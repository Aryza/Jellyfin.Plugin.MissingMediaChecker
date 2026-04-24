using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.MissingMediaChecker.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MissingMediaChecker.Services;

/// <summary>
/// Thin wrapper around the TMDB v3 REST API.
/// Uses the api_key query-param auth scheme (bearer tokens work too but require v4 endpoints).
/// </summary>
public sealed class TmdbService : IDisposable
{
    private const string BaseUrl         = "https://api.themoviedb.org/3";
    private const int    MaxAppendSlots  = 20;   // TMDB caps append_to_response at 20 endpoints.
    private const int    MaxRetries      = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient         _http;
    private readonly HttpClientHandler  _handler;
    private readonly string             _apiKey;
    private readonly ILogger            _logger;

    public TmdbService(string apiKey, ILogger logger)
    {
        _apiKey  = apiKey;
        _logger  = logger;

        // Enable transparent gzip/deflate/brotli: TMDB JSON compresses ~5-10×,
        // which cuts network time and deserialization buffering significantly.
        _handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip
                                   | DecompressionMethods.Deflate
                                   | DecompressionMethods.Brotli,
            MaxConnectionsPerServer = 32
        };

        _http = new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        // Advertise compression + accept JSON so the server skips negotiation.
        _http.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip, deflate, br");
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private string Url(string path)
    {
        var sep = path.Contains('?') ? '&' : '?';
        return $"{BaseUrl}{path}{sep}api_key={_apiKey}";
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // ResponseHeadersRead: start deserialization while the body is still
                // being received, instead of buffering the entire payload first.
                using var response = await _http
                    .GetAsync(Url(path), HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                // Honour TMDB's Retry-After on 429 (or 503) instead of silently
                // dropping the series. Bounded retries to avoid unbounded waits.
                if ((response.StatusCode == HttpStatusCode.TooManyRequests ||
                     response.StatusCode == HttpStatusCode.ServiceUnavailable) &&
                    attempt < MaxRetries)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta
                                   ?? TimeSpan.FromSeconds(1 << Math.Min(attempt, 4));  // 1, 2, 4, 8…
                    _logger.LogWarning(
                        "TMDB {Path} → {Status}, retrying after {Delay}s (attempt {Attempt})",
                        path, (int)response.StatusCode, retryAfter.TotalSeconds, attempt + 1);
                    await Task.Delay(retryAfter, ct).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("TMDB {Path} → {Status}", path, (int)response.StatusCode);
                    return default;
                }

                return await response.Content
                    .ReadFromJsonAsync<T>(JsonOptions, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "TMDB request failed for {Path}", path);
                return default;
            }
        }
    }

    // ── public API ────────────────────────────────────────────────────────────

    public Task<TmdbSeriesDetails?> GetSeriesAsync(string tmdbId, CancellationToken ct)
        => GetAsync<TmdbSeriesDetails>($"/tv/{tmdbId}", ct);

    public Task<TmdbSeasonDetails?> GetSeasonAsync(string tmdbId, int seasonNumber, CancellationToken ct)
        => GetAsync<TmdbSeasonDetails>($"/tv/{tmdbId}/season/{seasonNumber}", ct);

    public Task<TmdbMovieDetails?> GetMovieAsync(string tmdbId, CancellationToken ct)
        => GetAsync<TmdbMovieDetails>($"/movie/{tmdbId}", ct);

    public Task<TmdbCollectionDetails?> GetCollectionAsync(int collectionId, CancellationToken ct)
        => GetAsync<TmdbCollectionDetails>($"/collection/{collectionId}", ct);

    public Task<TmdbFindResult?> FindByExternalIdAsync(string externalId, string source, CancellationToken ct)
        => GetAsync<TmdbFindResult>($"/find/{Uri.EscapeDataString(externalId)}?external_source={source}", ct);

    /// <summary>Fetch TMDB weekly trending movies. Used by TrendingInLibraryChannel.</summary>
    public Task<TmdbTrendingMoviesResponse?> GetTrendingMoviesAsync(CancellationToken ct)
        => GetAsync<TmdbTrendingMoviesResponse>("/trending/movie/week", ct);

    /// <summary>
    /// Fetches a series plus the full episode list for every season in a single
    /// call (when possible) using TMDB's append_to_response parameter.
    ///
    /// The first request speculatively appends season/0..season/19. For series
    /// with ≤20 seasons this is the ONLY call needed (vs. 1 + N calls before).
    /// Seasons numbered ≥20 or outside the speculative range are fetched in
    /// follow-up batches of up to 20.
    /// </summary>
    public async Task<(TmdbSeriesDetails? details, Dictionary<int, TmdbSeasonDetails> seasons)>
        GetSeriesWithSeasonsAsync(string tmdbId, bool includeSpecials, CancellationToken ct)
    {
        var seasonMap = new Dictionary<int, TmdbSeasonDetails>();

        // Phase A: one optimistic call covering season/0..season/19.
        var speculativeAppend = BuildAppend(Enumerable.Range(0, MaxAppendSlots));
        var first = await GetAsync<TmdbSeriesWithExtras>(
            $"/tv/{tmdbId}?append_to_response={speculativeAppend}", ct).ConfigureAwait(false);
        if (first is null) return (null, seasonMap);

        HarvestSeasons(first.Extras, seasonMap);

        // Phase B: determine which seasons (from the canonical seasons array)
        // are still missing and fetch them in batches of 20.
        var missing = new List<int>();
        foreach (var s in first.Seasons)
        {
            if (!includeSpecials && s.SeasonNumber == 0) continue;
            if (!seasonMap.ContainsKey(s.SeasonNumber)) missing.Add(s.SeasonNumber);
        }

        for (int i = 0; i < missing.Count; i += MaxAppendSlots)
        {
            var slice = missing.Skip(i).Take(MaxAppendSlots);
            var append = BuildAppend(slice);
            var chunk = await GetAsync<TmdbSeriesWithExtras>(
                $"/tv/{tmdbId}?append_to_response={append}", ct).ConfigureAwait(false);
            if (chunk is null) continue;
            HarvestSeasons(chunk.Extras, seasonMap);
        }

        return (first, seasonMap);
    }

    private static string BuildAppend(IEnumerable<int> seasonNumbers)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var n in seasonNumbers)
        {
            if (!first) sb.Append(',');
            sb.Append("season/").Append(n);
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Pull valid <see cref="TmdbSeasonDetails"/> out of the dynamic
    /// "season/N" keys returned by append_to_response. Requests for
    /// non-existent season numbers come back as small error objects in the
    /// same slot; we detect those by the absence of an "episodes" property.
    /// </summary>
    private static void HarvestSeasons(
        Dictionary<string, JsonElement>? extras,
        Dictionary<int, TmdbSeasonDetails> target)
    {
        if (extras is null) return;
        foreach (var (key, value) in extras)
        {
            if (!key.StartsWith("season/", StringComparison.Ordinal)) continue;
            if (value.ValueKind != JsonValueKind.Object) continue;
            if (!value.TryGetProperty("episodes", out _)) continue;  // error payload

            var sd = value.Deserialize<TmdbSeasonDetails>(JsonOptions);
            if (sd is not null) target[sd.SeasonNumber] = sd;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }
}

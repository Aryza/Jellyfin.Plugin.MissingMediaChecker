using System;
using System.Net.Http;
using System.Net.Http.Json;
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
    private const string BaseUrl = "https://api.themoviedb.org/3";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly string     _apiKey;
    private readonly ILogger    _logger;

    public TmdbService(string apiKey, ILogger logger)
    {
        _apiKey = apiKey;
        _logger = logger;
        _http   = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private string Url(string path)
    {
        var sep = path.Contains('?') ? '&' : '?';
        return $"{BaseUrl}{path}{sep}api_key={_apiKey}";
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct)
    {
        try
        {
            var response = await _http.GetAsync(Url(path), ct).ConfigureAwait(false);
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

    public void Dispose() => _http.Dispose();
}

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MissingMediaChecker.Services;

/// <summary>
/// Tiny per-key TTL cache used by the channel implementations. Jellyfin calls
/// IChannel.GetChannelItems on every folder browse; without this, TMDB gets
/// hammered. Keys are channel-specific strings (e.g. "trending", "upcoming").
///
/// Thread-safe: concurrent misses on the same key serialise through a
/// SemaphoreSlim so only one loader fires.
/// </summary>
public static class ChannelContentCache
{
    private sealed class Entry
    {
        public object? Value;
        public DateTimeOffset ExpiresAt;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    private static readonly ConcurrentDictionary<string, Entry> _entries = new();

    public static async Task<T?> GetOrLoadAsync<T>(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T?>> loader,
        CancellationToken ct) where T : class
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());

        // Fast path: cached and still fresh.
        if (entry.Value is T cached && entry.ExpiresAt > DateTimeOffset.UtcNow)
            return cached;

        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the gate: another caller may have loaded.
            if (entry.Value is T fresh && entry.ExpiresAt > DateTimeOffset.UtcNow)
                return fresh;

            var value = await loader(ct).ConfigureAwait(false);
            if (value is not null)
            {
                entry.Value     = value;
                entry.ExpiresAt = DateTimeOffset.UtcNow.Add(ttl);
            }
            return value;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    public static void InvalidateAll()
    {
        foreach (var e in _entries.Values) { e.Value = null; e.ExpiresAt = DateTimeOffset.MinValue; }
    }
}

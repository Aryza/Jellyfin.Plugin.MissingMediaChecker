using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.MissingMediaChecker.Services;

/// <summary>
/// Per-key TTL cache used by the home-screen section handlers. The Home Screen
/// Sections plugin invokes our result method on every home render for every
/// user — without this, TMDB + ILibraryManager would get hammered.
///
/// Thread-safe: concurrent misses on the same key serialise through a
/// SemaphoreSlim so only one loader fires.
/// </summary>
public static class SectionContentCache
{
    private sealed class Entry
    {
        public object? Value;
        public DateTimeOffset ExpiresAt;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }

    private static readonly ConcurrentDictionary<string, Entry> _entries = new();

    /// <summary>Synchronous variant used by IHomeScreenSection handlers (HSS invokes via reflection and expects a sync return).</summary>
    public static T? GetOrLoad<T>(string key, TimeSpan ttl, Func<T?> loader) where T : class
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        if (entry.Value is T cached && entry.ExpiresAt > DateTimeOffset.UtcNow) return cached;

        entry.Gate.Wait();
        try
        {
            if (entry.Value is T fresh && entry.ExpiresAt > DateTimeOffset.UtcNow) return fresh;
            var value = loader();
            if (value is not null)
            {
                entry.Value     = value;
                entry.ExpiresAt = DateTimeOffset.UtcNow.Add(ttl);
            }
            return value;
        }
        finally { entry.Gate.Release(); }
    }

    public static async Task<T?> GetOrLoadAsync<T>(
        string key,
        TimeSpan ttl,
        Func<CancellationToken, Task<T?>> loader,
        CancellationToken ct) where T : class
    {
        var entry = _entries.GetOrAdd(key, _ => new Entry());
        if (entry.Value is T cached && entry.ExpiresAt > DateTimeOffset.UtcNow) return cached;

        await entry.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (entry.Value is T fresh && entry.ExpiresAt > DateTimeOffset.UtcNow) return fresh;
            var value = await loader(ct).ConfigureAwait(false);
            if (value is not null)
            {
                entry.Value     = value;
                entry.ExpiresAt = DateTimeOffset.UtcNow.Add(ttl);
            }
            return value;
        }
        finally { entry.Gate.Release(); }
    }

    public static void InvalidateAll()
    {
        foreach (var e in _entries.Values) { e.Value = null; e.ExpiresAt = DateTimeOffset.MinValue; }
    }

    public static void Invalidate(string key)
    {
        if (_entries.TryGetValue(key, out var e)) { e.Value = null; e.ExpiresAt = DateTimeOffset.MinValue; }
    }
}

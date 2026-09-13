using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ArrDashboard.Services;

/// <summary>
/// Tiny time-based cache. Sonarr and Radarr are cheap to query but the dashboard
/// polls, and several widgets share the same underlying calls, so a few seconds
/// of memoisation keeps load off the backends. Concurrent callers for the same
/// key await one in-flight request instead of stampeding.
/// </summary>
public class ResponseCache
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new ConcurrentDictionary<string, Entry>(StringComparer.Ordinal);

    public void Clear() => _entries.Clear();

    public async Task<T> GetOrAddAsync<T>(string key, int ttlSeconds, Func<Task<T>> factory)
    {
        if (ttlSeconds <= 0)
        {
            return await factory().ConfigureAwait(false);
        }

        var now = DateTime.UtcNow;

        if (_entries.TryGetValue(key, out var existing) && existing.Value is not null && existing.ExpiresAt > now)
        {
            return await CastAsync<T>(existing.Value).ConfigureAwait(false);
        }

        var gate = _entries.GetOrAdd(key, _ => new Entry()).Lock;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            now = DateTime.UtcNow;
            if (_entries.TryGetValue(key, out existing) && existing.Value is not null && existing.ExpiresAt > now)
            {
                return await CastAsync<T>(existing.Value).ConfigureAwait(false);
            }

            var value = await factory().ConfigureAwait(false);
            _entries[key] = new Entry
            {
                Value = value,
                ExpiresAt = now.AddSeconds(ttlSeconds),
                Lock = gate
            };

            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    private static Task<T> CastAsync<T>(object? value) => Task.FromResult((T)value!);

    private sealed class Entry
    {
        public object? Value { get; set; }

        public DateTime ExpiresAt { get; set; }

        public SemaphoreSlim Lock { get; set; } = new SemaphoreSlim(1, 1);
    }
}

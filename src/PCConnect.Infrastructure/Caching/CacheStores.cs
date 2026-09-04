using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using PCConnect.Core;
using StackExchange.Redis;

namespace PCConnect.Infrastructure.Caching;

/// <summary>Valkey (Redis-compatible) implementation used in every deployed environment.</summary>
public sealed class ValkeyCacheStore(IConnectionMultiplexer multiplexer, ILogger<ValkeyCacheStore> logger) : ICacheStore
{
    private readonly IDatabase _db = multiplexer.GetDatabase();

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        var value = await _db.StringGetAsync(key);
        return value.IsNull ? null : value.ToString();
    }

    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default) =>
        _db.StringSetAsync(key, value, ttl);

    public Task<bool> SetIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default) =>
        _db.StringSetAsync(key, value, ttl, When.NotExists);

    public Task RemoveAsync(string key, CancellationToken ct = default) =>
        _db.KeyDeleteAsync(key);

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        _db.KeyExistsAsync(key);

    public async Task<long> IncrementAsync(string key, TimeSpan window, CancellationToken ct = default)
    {
        var value = await _db.StringIncrementAsync(key);
        if (value == 1)
        {
            await _db.KeyExpireAsync(key, window);
        }

        return value;
    }

    public Task<TimeSpan?> TimeToLiveAsync(string key, CancellationToken ct = default) =>
        _db.KeyTimeToLiveAsync(key);

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            await _db.PingAsync();
            return true;
        }
        catch (RedisConnectionException ex)
        {
            logger.LogWarning(ex, "Valkey ping failed");
            return false;
        }
    }
}

/// <summary>
/// In-process fallback. Correct for a single instance and for tests; it does not
/// survive a restart and does not share state across instances, which is exactly
/// the S2-06 failure — so the API logs a warning at boot when it is selected and
/// <c>/readyz</c> reports the degraded mode.
/// </summary>
public sealed class InMemoryCacheStore : ICacheStore
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed record Entry(string Value, DateTimeOffset ExpiresAt, long Counter = 0);

    public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(TryGet(key, out var entry) ? entry.Value : null);

    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
    {
        _entries[key] = new Entry(value, DateTimeOffset.UtcNow.Add(ttl));
        return Task.CompletedTask;
    }

    public Task<bool> SetIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default)
    {
        Sweep();
        var added = _entries.TryAdd(key, new Entry(value, DateTimeOffset.UtcNow.Add(ttl)));
        return Task.FromResult(added);
    }

    public Task RemoveAsync(string key, CancellationToken ct = default)
    {
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(TryGet(key, out _));

    public Task<long> IncrementAsync(string key, TimeSpan window, CancellationToken ct = default)
    {
        var updated = _entries.AddOrUpdate(
            key,
            _ => new Entry("1", DateTimeOffset.UtcNow.Add(window), 1),
            (_, existing) => existing.ExpiresAt <= DateTimeOffset.UtcNow
                ? new Entry("1", DateTimeOffset.UtcNow.Add(window), 1)
                : existing with { Counter = existing.Counter + 1, Value = (existing.Counter + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) });

        return Task.FromResult(updated.Counter);
    }

    public Task<TimeSpan?> TimeToLiveAsync(string key, CancellationToken ct = default)
    {
        if (!TryGet(key, out var entry))
        {
            return Task.FromResult<TimeSpan?>(null);
        }

        var remaining = entry.ExpiresAt - DateTimeOffset.UtcNow;
        return Task.FromResult<TimeSpan?>(remaining > TimeSpan.Zero ? remaining : null);
    }

    public Task<bool> PingAsync(CancellationToken ct = default) => Task.FromResult(true);

    private bool TryGet(string key, out Entry entry)
    {
        if (_entries.TryGetValue(key, out var found) && found.ExpiresAt > DateTimeOffset.UtcNow)
        {
            entry = found;
            return true;
        }

        _entries.TryRemove(key, out _);
        entry = default!;
        return false;
    }

    private void Sweep()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, entry) in _entries)
        {
            if (entry.ExpiresAt <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }
    }
}

/// <summary>Cache key shapes, in one place so a typo cannot silently create a second namespace.</summary>
public static class CacheKeys
{
    public static string Presence(Guid deviceId) => $"presence:device:{deviceId:N}";

    public static string DenyListedJti(string jti) => $"deny:jti:{jti}";

    public static string RateLimit(string bucket, string subject) => $"rl:{bucket}:{subject}";

    public static string LoginLockout(string subject) => $"lockout:{subject}";

    public static string Idempotency(string scope, long userId, string key) => $"idem:{scope}:{userId}:{key}";

    public static string StepUpToken(string tokenHash) => $"stepup:{tokenHash}";

    public static string PairingAttempts(string codeHash) => $"pairattempt:{codeHash}";
}

/// <summary>Live presence with a TTL, refreshed by the realtime connection (05 §6).</summary>
public sealed class PresenceTracker(ICacheStore cache) : IPresenceTracker
{
    public Task MarkOnlineAsync(Guid deviceId, TimeSpan ttl, CancellationToken ct = default) =>
        cache.SetAsync(CacheKeys.Presence(deviceId), "1", ttl, ct);

    // Deleted immediately on disconnect rather than left to expire, so the phone's
    // indicator goes grey in about a second instead of ninety.
    public Task MarkOfflineAsync(Guid deviceId, CancellationToken ct = default) =>
        cache.RemoveAsync(CacheKeys.Presence(deviceId), ct);

    public Task<bool> IsOnlineAsync(Guid deviceId, CancellationToken ct = default) =>
        cache.ExistsAsync(CacheKeys.Presence(deviceId), ct);

    public async Task<IReadOnlyDictionary<Guid, bool>> AreOnlineAsync(
        IReadOnlyCollection<Guid> deviceIds,
        CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, bool>(deviceIds.Count);
        foreach (var id in deviceIds)
        {
            result[id] = await cache.ExistsAsync(CacheKeys.Presence(id), ct);
        }

        return result;
    }
}

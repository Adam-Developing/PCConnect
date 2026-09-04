using PCConnect.Core;
using PCConnect.Infrastructure.Caching;

namespace PCConnect.Infrastructure.Security;

/// <summary>One rate-limit budget: a count over a window (03 §6).</summary>
public sealed record RateBudget(string Name, int Limit, TimeSpan Window);

/// <summary>
/// The budgets that matter, named rather than scattered as magic numbers. The
/// destructive-command limit is deliberately tight: a legitimate user does not
/// shut a PC down four times a minute; an attacker with a stolen token wants to.
/// </summary>
public static class RateBudgets
{
    public static readonly RateBudget LoginPerAccount = new("login.account", 5, TimeSpan.FromMinutes(15));
    public static readonly RateBudget LoginPerIp = new("login.ip", 20, TimeSpan.FromMinutes(15));
    public static readonly RateBudget RefreshPerFamily = new("refresh.family", 60, TimeSpan.FromHours(1));
    public static readonly RateBudget PairClaimPerUser = new("pair.claim", 5, TimeSpan.FromMinutes(10));
    public static readonly RateBudget PairStartPerIp = new("pair.start", 10, TimeSpan.FromMinutes(10));
    public static readonly RateBudget CommandPerUser = new("command.user", 30, TimeSpan.FromMinutes(1));
    public static readonly RateBudget CommandPerDevice = new("command.device", 10, TimeSpan.FromMinutes(1));
    public static readonly RateBudget DestructiveCommand = new("command.destructive", 3, TimeSpan.FromMinutes(1));
    public static readonly RateBudget PasswordResetPerAccount = new("reset.account", 3, TimeSpan.FromHours(1));
    public static readonly RateBudget PasswordResetPerIp = new("reset.ip", 10, TimeSpan.FromHours(1));
    public static readonly RateBudget StepUpPerUser = new("stepup.user", 10, TimeSpan.FromMinutes(15));
    public static readonly RateBudget Default = new("default", 300, TimeSpan.FromMinutes(1));
}

public sealed class RateLimiter(ICacheStore cache)
{
    /// <summary>
    /// Consumes one unit of a budget. Throws 429 with <c>Retry-After</c> when the
    /// budget is exhausted; the caller does not have to remember to check a bool.
    /// </summary>
    public async Task ConsumeAsync(RateBudget budget, string subject, CancellationToken ct = default)
    {
        var key = CacheKeys.RateLimit(budget.Name, subject);
        var count = await cache.IncrementAsync(key, budget.Window, ct);

        if (count > budget.Limit)
        {
            var ttl = await cache.TimeToLiveAsync(key, ct) ?? budget.Window;
            throw AppException.TooManyRequests(
                "Too many requests. Try again shortly.",
                ttl <= TimeSpan.Zero ? budget.Window : ttl);
        }
    }

    /// <summary>Peeks without consuming; used where a failure should not itself cost budget.</summary>
    public async Task<bool> IsExhaustedAsync(RateBudget budget, string subject, CancellationToken ct = default)
    {
        var raw = await cache.GetAsync(CacheKeys.RateLimit(budget.Name, subject), ct);
        return long.TryParse(raw, out var count) && count > budget.Limit;
    }

    public Task ResetAsync(RateBudget budget, string subject, CancellationToken ct = default) =>
        cache.RemoveAsync(CacheKeys.RateLimit(budget.Name, subject), ct);
}

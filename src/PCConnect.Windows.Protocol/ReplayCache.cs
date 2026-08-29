using System.Collections.Concurrent;

namespace PCConnect.Windows.Protocol;

public sealed class ReplayCache(TimeSpan retention)
{
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> seen = new();

    public bool TryAccept(Guid key, DateTimeOffset now)
    {
        foreach (var item in seen)
            if (item.Value <= now - retention) seen.TryRemove(item.Key, out _);
        return seen.TryAdd(key, now);
    }
}

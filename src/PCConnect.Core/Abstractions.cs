namespace PCConnect.Core;

/// <summary>
/// The server is the sole authority on time (01 §6.4). Everything that reads
/// "now" goes through this, so a test can move time forward and a TTL can be
/// asserted rather than slept through.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Deterministic clock for tests and for the migration dry-run.</summary>
public sealed class FixedClock(DateTimeOffset now) : IClock
{
    private DateTimeOffset _now = now;

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);

    public void Set(DateTimeOffset to) => _now = to;
}

/// <summary>
/// Password and device-secret hashing. Argon2id in production; the interface
/// exists so the legacy verification path and the parameter tuning live in one
/// place and can be tested without the cost.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string phcString);

    /// <summary>True when the stored hash used weaker parameters than the current policy.</summary>
    bool NeedsRehash(string phcString);
}

/// <summary>
/// Envelope encryption (ADR-0004): a per-user DEK wrapped by a KEK that lives
/// outside the database, AES-256-GCM at both layers.
/// </summary>
public interface IEnvelopeEncryptor
{
    /// <summary>Generates a fresh 256-bit DEK and returns it wrapped under the current KEK.</summary>
    (byte[] Wrapped, string KekId) CreateDataKey();

    byte[] UnwrapDataKey(byte[] wrapped, string kekId);

    /// <summary>Returns <c>[12B nonce][ciphertext][16B tag]</c>.</summary>
    byte[] Encrypt(byte[] dataKey, string plaintext, string associatedData);

    string Decrypt(byte[] dataKey, byte[] ciphertext, string associatedData);

    string CurrentKekId { get; }
}

/// <summary>
/// Distributed state that must survive a process restart but not a machine
/// restart: presence, rate-limit windows, the access-token deny list, hot
/// idempotency lookups. Backed by Valkey in production.
/// </summary>
public interface ICacheStore
{
    Task<string?> GetAsync(string key, CancellationToken ct = default);

    Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default);

    Task<bool> SetIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken ct = default);

    Task RemoveAsync(string key, CancellationToken ct = default);

    Task<bool> ExistsAsync(string key, CancellationToken ct = default);

    /// <summary>Increments a counter, setting the TTL on first use. Returns the new value.</summary>
    Task<long> IncrementAsync(string key, TimeSpan window, CancellationToken ct = default);

    Task<TimeSpan?> TimeToLiveAsync(string key, CancellationToken ct = default);

    /// <summary>False when the backing store is unreachable; drives <c>/readyz</c>.</summary>
    Task<bool> PingAsync(CancellationToken ct = default);
}

/// <summary>
/// Outbound realtime fan-out, as seen by the application services. The transport
/// (SignalR) lives behind it so a service can be tested without a hub.
/// </summary>
public interface IRealtimeNotifier
{
    Task CommandIssuedAsync(Guid deviceId, Contracts.PendingCommand command, CancellationToken ct = default);

    Task CommandStatusAsync(Guid userId, Contracts.CommandStatusEvent status, CancellationToken ct = default);

    Task DevicePresenceAsync(Guid userId, Contracts.DevicePresenceEvent presence, CancellationToken ct = default);

    Task ReminderChangedAsync(Guid userId, Contracts.ReminderChangedEvent change, CancellationToken ct = default);

    Task ReminderDueAsync(Guid userId, Contracts.ReminderDueEvent due, CancellationToken ct = default);
}

/// <summary>Live device presence, which is cache-backed and advisory (05 §6).</summary>
public interface IPresenceTracker
{
    Task MarkOnlineAsync(Guid deviceId, TimeSpan ttl, CancellationToken ct = default);

    Task MarkOfflineAsync(Guid deviceId, CancellationToken ct = default);

    Task<bool> IsOnlineAsync(Guid deviceId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<Guid, bool>> AreOnlineAsync(IReadOnlyCollection<Guid> deviceIds, CancellationToken ct = default);
}

/// <summary>Sends account mail. A no-op logger implementation is used until SMTP is configured.</summary>
public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string body, CancellationToken ct = default);
}

/// <summary>
/// Checks a candidate password against the Pwned Passwords k-anonymity range API.
/// Fails open: an outage of a third-party service must not stop people
/// registering, but it is recorded when it happens.
/// </summary>
public interface IBreachedPasswordChecker
{
    Task<bool> IsBreachedAsync(string password, CancellationToken ct = default);
}

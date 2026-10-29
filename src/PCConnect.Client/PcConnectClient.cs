using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCConnect.Core;
using PCConnect.Core.Contracts;

namespace PCConnect.Client;

/// <summary>
/// Where a client's credentials live. Implementations must use the OS keychain —
/// Windows Credential Manager, Android Keystore, iOS Keychain. Never a plain
/// preferences file, which is S1-04 (06 §1).
/// </summary>
public interface ITokenStore
{
    Task<StoredTokens?> ReadAsync(CancellationToken ct = default);

    Task WriteAsync(StoredTokens tokens, CancellationToken ct = default);

    Task ClearAsync(CancellationToken ct = default);
}

/// <summary>
/// The refresh token and, for an agent, the device credential. The access token
/// is deliberately absent: it lives in memory only, for fifteen minutes.
/// </summary>
public sealed record StoredTokens(string? RefreshToken, string? DeviceId, string? DeviceSecret);

public sealed class InMemoryTokenStore : ITokenStore
{
    private StoredTokens? _tokens;

    public Task<StoredTokens?> ReadAsync(CancellationToken ct = default) => Task.FromResult(_tokens);

    public Task WriteAsync(StoredTokens tokens, CancellationToken ct = default)
    {
        _tokens = tokens;
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken ct = default)
    {
        _tokens = null;
        return Task.CompletedTask;
    }
}

public sealed class PcConnectClientOptions
{
    /// <summary>
    /// Resolved at runtime, never a compiled-in absolute constant (S3-08, 06 §1).
    /// </summary>
    public string BaseAddress { get; set; } = "http://localhost:5080";

    public string ClientKind { get; set; } = "desktop_agent";

    public string ClientVersion { get; set; } = "5.0.0";
}

/// <summary>
/// The .NET client for the v2 API, shared by the Windows agent, the WPF
/// companion and the integration tests.
///
/// It binds to the same contract records the server returns, so a field that
/// changes shape fails to compile here rather than failing at runtime on a
/// user's machine (C-5).
/// </summary>
public sealed class PcConnectClient(HttpClient http, PcConnectClientOptions options, ITokenStore tokens)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private (string Raw, Uri Parsed)? _cachedBaseUri;

    public PcConnectClientOptions Options => options;

    public string? AccessToken => _accessToken;

    /// <summary>Raised when the session ends and the user has to sign in again.</summary>
    public event Action? SignedOut;

    // ── discovery ────────────────────────────────────────────────────────────

    public Task<DiscoveryResponse?> GetDiscoveryAsync(CancellationToken ct = default) =>
        SendAsync<DiscoveryResponse>(HttpMethod.Get, "/v2/meta/discovery", null, authenticated: false, ct);

    /// <summary>
    /// Compares the running build against the server's minimum. Below it, the
    /// client is expected to show a blocking update prompt — the mechanism that
    /// eventually lets the legacy surface be switched off (04 §2).
    /// </summary>
    public static bool IsBelowMinimum(DiscoveryResponse discovery, string clientKey, string currentVersion)
    {
        if (!discovery.MinimumSupportedClient.TryGetValue(clientKey, out var minimum))
        {
            return false;
        }

        return Version.TryParse(minimum, out var required)
            && Version.TryParse(currentVersion, out var actual)
            && actual < required;
    }

    // ── authentication ───────────────────────────────────────────────────────

    public async Task<TokenPairResponse> LoginAsync(string login, string password, CancellationToken ct = default)
    {
        // The client sends the plaintext over TLS. It does not hash: while a
        // client hashes, the hash *is* the password (S1-03).
        var pair = await SendAsync<TokenPairResponse>(HttpMethod.Post, "/v2/auth/login",
            new LoginRequest(login, password, null, options.ClientKind, options.ClientVersion),
            authenticated: false, ct)
            ?? throw new InvalidOperationException("The server returned an empty login response.");

        await AdoptAsync(pair);
        return pair;
    }

    public async Task<TokenPairResponse> ExchangeDeviceSecretAsync(
        string deviceId, string deviceSecret, string osVersion = "", CancellationToken ct = default)
    {
        var pair = await SendAsync<TokenPairResponse>(HttpMethod.Post, "/v2/devices/token",
            new DeviceTokenRequest(deviceId, deviceSecret, options.ClientVersion, osVersion),
            authenticated: false, ct)
            ?? throw new InvalidOperationException("The server returned an empty device token response.");

        await AdoptAsync(pair, deviceId, deviceSecret);
        return pair;
    }

    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var stored = await tokens.ReadAsync(ct);
        if (stored?.RefreshToken is { } refresh)
        {
            try
            {
                await SendAsync<object>(HttpMethod.Post, "/v2/auth/logout",
                    new LogoutRequest(refresh), authenticated: false, ct);
            }
            catch (PcConnectApiException)
            {
                // Signing out locally matters more than the server's answer.
            }
        }

        _accessToken = null;
        _accessTokenExpiresAt = DateTimeOffset.MinValue;
        await tokens.ClearAsync(ct);
        SignedOut?.Invoke();
    }

    private async Task AdoptAsync(TokenPairResponse pair, string? deviceId = null, string? deviceSecret = null)
    {
        _accessToken = pair.AccessToken;
        _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(pair.ExpiresInSeconds);

        var existing = await tokens.ReadAsync(CancellationToken.None);
        await tokens.WriteAsync(
            new StoredTokens(
                pair.RefreshToken,
                deviceId ?? existing?.DeviceId,
                deviceSecret ?? existing?.DeviceSecret),
            CancellationToken.None);
    }

    private bool HasFreshToken(int marginSeconds) =>
        _accessToken is not null && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(marginSeconds);

    /// <summary>
    /// Returns a valid access token, refreshing if it is missing or close to
    /// expiry. Serialised, so a burst of parallel calls performs one refresh.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (HasFreshToken(30))
        {
            if (!HasFreshToken(120))
            {
                _ = WarmAccessTokenAsync();
            }

            return _accessToken;
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            return HasFreshToken(30) ? _accessToken : await RefreshCoreAsync(ct);
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Refreshes the access token in the background when it is missing or within
    /// two minutes of expiry, so interactive actions do not wait on a token round-trip.
    /// </summary>
    public async Task WarmAccessTokenAsync(CancellationToken ct = default)
    {
        if (HasFreshToken(120) || !await _refreshLock.WaitAsync(0, ct).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (!HasFreshToken(120))
            {
                await RefreshCoreAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is PcConnectApiException or HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Best-effort warmup; the next real API call will retry or surface the error.
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Keeps an active session's access token refreshed ahead of expiry while the
    /// host is running, avoiding synchronous refresh latency after idle periods.
    /// </summary>
    public async Task RunProactiveRefreshLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_accessToken is null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                    continue;
                }

                var remaining = (_accessTokenExpiresAt - TimeSpan.FromSeconds(90)) - DateTimeOffset.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    var sleep = remaining < TimeSpan.FromMinutes(2) ? remaining : TimeSpan.FromMinutes(2);
                    await Task.Delay(sleep, ct).ConfigureAwait(false);
                    continue;
                }

                var previousExpiry = _accessTokenExpiresAt;
                await WarmAccessTokenAsync(ct).ConfigureAwait(false);

                if (_accessTokenExpiresAt <= previousExpiry)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<string?> RefreshCoreAsync(CancellationToken ct)
    {
        var stored = await tokens.ReadAsync(ct);

        if (stored?.RefreshToken is { } refresh)
        {
            try
            {
                var pair = await SendAsync<TokenPairResponse>(HttpMethod.Post, "/v2/auth/refresh",
                    new RefreshRequest(refresh), authenticated: false, ct);

                if (pair is not null)
                {
                    await AdoptAsync(pair);
                    return _accessToken;
                }
            }
            catch (PcConnectApiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                // The refresh chain is dead. An agent can recover on its own
                // from its device secret below; a user session cannot.
                if (stored is not { DeviceId: not null, DeviceSecret: not null })
                {
                    await tokens.ClearAsync(CancellationToken.None);
                    _accessToken = null;
                    _accessTokenExpiresAt = DateTimeOffset.MinValue;
                    SignedOut?.Invoke();
                    return null;
                }
            }
        }

        if (stored is { DeviceId: not null, DeviceSecret: not null })
        {
            var pair = await ExchangeDeviceSecretAsync(stored.DeviceId, stored.DeviceSecret, ct: ct);
            return pair.AccessToken;
        }

        return null;
    }

    /// <summary>
    /// Adds the PC this app is running on to the signed-in account, and returns
    /// the ticket the agent redeems for its own credential (ADR-0013).
    /// </summary>
    public Task<DeviceProvisionResponse?> ProvisionDeviceAsync(
        string requestedName, string agentVersion = "", CancellationToken ct = default) =>
        SendAsync<DeviceProvisionResponse>(HttpMethod.Post, "/v2/devices/provision",
            new DeviceProvisionRequest(requestedName, "windows", agentVersion), authenticated: true, ct);

    public Task<DeviceProvisionCompleteResponse?> CompleteDeviceProvisioningAsync(
        string provisioningTicket, CancellationToken ct = default) =>
        SendAsync<DeviceProvisionCompleteResponse>(HttpMethod.Post, "/v2/devices/provision/complete",
            new DeviceProvisionCompleteRequest(provisioningTicket), authenticated: false, ct);

    // ── devices, commands, reminders ─────────────────────────────────────────

    public async Task<IReadOnlyList<DeviceResponse>> ListDevicesAsync(CancellationToken ct = default) =>
        (await SendAsync<Page<DeviceResponse>>(HttpMethod.Get, "/v2/devices", null, true, ct))?.Items ?? [];

    public Task<DeviceResponse?> UpdateDeviceAsync(string deviceId, UpdateDeviceRequest request, CancellationToken ct = default) =>
        SendAsync<DeviceResponse>(HttpMethod.Patch, $"/v2/devices/{deviceId}", request, true, ct);

    public Task RevokeDeviceAsync(string deviceId, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"/v2/devices/{deviceId}", null, true, ct);

    public Task HeartbeatAsync(string deviceId, string osVersion, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Post, $"/v2/devices/{deviceId}/heartbeat",
            new HeartbeatRequest(options.ClientVersion, osVersion), true, ct);

    public Task<StepUpChallengeResponse?> StartStepUpAsync(CancellationToken ct = default) =>
        SendAsync<StepUpChallengeResponse>(HttpMethod.Post, "/v2/auth/step-up/start", null, true, ct);

    public Task<StepUpTokenResponse?> VerifyStepUpWithPasswordAsync(
        string challengeId, string password, CancellationToken ct = default) =>
        SendAsync<StepUpTokenResponse>(HttpMethod.Post, "/v2/auth/step-up/verify",
            new StepUpVerifyRequest(challengeId, "password", password), true, ct);

    public Task<CommandResponse?> IssueCommandAsync(IssueCommandRequest request, CancellationToken ct = default) =>
        SendAsync<CommandResponse>(HttpMethod.Post, "/v2/commands", request, true, ct);

    public async Task<IReadOnlyList<CommandResponse>> ListCommandsAsync(int limit = 25, CancellationToken ct = default) =>
        (await SendAsync<Page<CommandResponse>>(HttpMethod.Get, $"/v2/commands?limit={limit}", null, true, ct))?.Items ?? [];

    public async Task<IReadOnlyList<PendingCommand>> ClaimPendingCommandsAsync(CancellationToken ct = default) =>
        (await SendAsync<Page<PendingCommand>>(HttpMethod.Get, "/v2/commands/pending", null, true, ct))?.Items ?? [];

    public Task<CommandResponse?> AckCommandAsync(
        string commandId, string outcome, string? resultCode = null, string? resultMessage = null, CancellationToken ct = default) =>
        SendAsync<CommandResponse>(HttpMethod.Post, $"/v2/commands/{commandId}/ack",
            new AckCommandRequest(outcome, resultCode, resultMessage), true, ct);

    public async Task<IReadOnlyList<ReminderResponse>> ListRemindersAsync(int limit = 50, CancellationToken ct = default) =>
        (await SendAsync<Page<ReminderResponse>>(HttpMethod.Get, $"/v2/reminders?limit={limit}", null, true, ct))?.Items ?? [];

    public Task<ReminderResponse?> CreateReminderAsync(CreateReminderRequest request, CancellationToken ct = default) =>
        SendAsync<ReminderResponse>(HttpMethod.Post, "/v2/reminders", request, true, ct);

    public Task<ReminderResponse?> GetReminderAsync(string reminderId, CancellationToken ct = default) =>
        SendAsync<ReminderResponse>(HttpMethod.Get, $"/v2/reminders/{reminderId}", null, true, ct);

    public Task<ReminderResponse?> UpdateReminderAsync(string reminderId, UpdateReminderRequest request, CancellationToken ct = default) =>
        SendAsync<ReminderResponse>(HttpMethod.Patch, $"/v2/reminders/{reminderId}", request, true, ct);

    public async Task DeleteReminderAsync(string reminderId, CancellationToken ct = default) =>
        await SendAsync<object>(HttpMethod.Delete, $"/v2/reminders/{reminderId}", null, true, ct);

    public Task<ReminderResponse?> RestoreReminderAsync(string reminderId, CancellationToken ct = default) =>
        SendAsync<ReminderResponse>(HttpMethod.Post, $"/v2/reminders/{reminderId}/restore", null, true, ct);

    public Task<ReminderResponse?> CompleteReminderAsync(string reminderId, bool completed = true, CancellationToken ct = default) =>
        SendAsync<ReminderResponse>(HttpMethod.Post, $"/v2/reminders/{reminderId}/complete",
            new CompleteReminderRequest(completed), true, ct);

    public Task<IReadOnlyList<ReminderOccurrenceResponse>?> GetReminderOccurrencesAsync(
        string reminderId, DateTimeOffset? from = null, DateTimeOffset? to = null, int limit = 50, CancellationToken ct = default)
    {
        var query = new List<string> { $"limit={limit}" };
        if (from.HasValue) query.Add($"from={Uri.EscapeDataString(from.Value.ToString("O"))}");
        if (to.HasValue) query.Add($"to={Uri.EscapeDataString(to.Value.ToString("O"))}");
        var qs = string.Join("&", query);
        return SendAsync<IReadOnlyList<ReminderOccurrenceResponse>>(
            HttpMethod.Get, $"/v2/reminders/{reminderId}/occurrences?{qs}", null, true, ct);
    }

    public Task<ReminderOccurrenceResponse?> UpdateReminderOccurrenceAsync(
        string reminderId, UpdateReminderOccurrenceRequest request, CancellationToken ct = default) =>
        SendAsync<ReminderOccurrenceResponse>(
            HttpMethod.Post, $"/v2/reminders/{reminderId}/occurrences", request, true, ct);

    public Task<ReminderOccurrenceResponse?> DeleteReminderOccurrenceAsync(
        string reminderId, DateTimeOffset occursAt, CancellationToken ct = default)
    {
        var timeStr = Uri.EscapeDataString(occursAt.ToString("O"));
        return SendAsync<ReminderOccurrenceResponse>(
            HttpMethod.Delete, $"/v2/reminders/{reminderId}/occurrences?occursAt={timeStr}", null, true, ct);
    }

    public Task<ProfileResponse?> GetProfileAsync(CancellationToken ct = default) =>
        SendAsync<ProfileResponse>(HttpMethod.Get, "/v2/account/profile", null, true, ct);

    // ── transport ────────────────────────────────────────────────────────────

    private async Task<T?> SendAsync<T>(
        HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
    {
        // Capture the token that will actually be sent so the 401 branch can
        // tell whether a concurrent caller has already refreshed it.
        var tokenUsed = authenticated ? _accessToken : null;
        var response = await SendOnceAsync(method, path, body, authenticated, ct);

        // Refresh once on a 401, then give up. Retrying a refresh that failed is
        // how a client ends up hammering an endpoint it can never satisfy.
        //
        // Use compare-and-swap: only invalidate the cached token if it is still
        // the same one that was rejected. When two requests race here, the first
        // one refreshes and writes a new _accessToken; the second's CAS is a
        // no-op, and GetAccessTokenAsync's inner double-check returns the token
        // the first caller already obtained — no second refresh, no reuse.
        if (response.StatusCode == HttpStatusCode.Unauthorized && authenticated)
        {
            response.Dispose();

            Interlocked.CompareExchange(ref _accessToken, null, tokenUsed);
            _accessTokenExpiresAt = DateTimeOffset.MinValue;

            if (await GetAccessTokenAsync(ct) is null)
            {
                throw new PcConnectApiException(HttpStatusCode.Unauthorized,
                    ErrorCodes.AuthTokenInvalid, "The session has ended. Sign in again.");
            }

            response = await SendOnceAsync(method, path, body, authenticated, ct);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
                {
                    return default;
                }

                // A missing Content-Length means chunked, not empty. Treating it
                // as empty silently returned null for every response the server
                // streamed, which looked like a server fault and was not one.
                var payload = await response.Content.ReadAsStringAsync(ct);

                return string.IsNullOrWhiteSpace(payload)
                    ? default
                    : JsonSerializer.Deserialize<T>(payload, Json);
            }

            ErrorEnvelope? envelope = null;
            try
            {
                var failureBody = await response.Content.ReadAsStringAsync(ct);
                if (!string.IsNullOrWhiteSpace(failureBody))
                {
                    envelope = JsonSerializer.Deserialize<ErrorEnvelope>(failureBody, Json);
                }
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
            {
                // A non-JSON error body (a proxy page, say) still has to surface
                // as a typed failure rather than a parse exception.
            }

            throw new PcConnectApiException(
                response.StatusCode,
                envelope?.Error.Code ?? "http." + (int)response.StatusCode,
                envelope?.Error.Message ?? response.ReasonPhrase ?? "The request failed.",
                response.Headers.RetryAfter?.Delta);
        }
    }

    private Uri ResolveUri(string path)
    {
        var raw = options.BaseAddress;
        var cached = _cachedBaseUri;
        if (cached is null || !string.Equals(cached.Value.Raw, raw, StringComparison.Ordinal))
        {
            cached = (raw, new Uri(raw));
            _cachedBaseUri = cached;
        }

        return new Uri(cached.Value.Parsed, path);
    }

    private async Task<HttpResponseMessage> SendOnceAsync(
        HttpMethod method, string path, object? body, bool authenticated, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, ResolveUri(path));

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        if (authenticated)
        {
            var token = await GetAccessTokenAsync(ct);
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
        }

        request.Headers.TryAddWithoutValidation("X-Request-Id", Guid.CreateVersion7().ToString("N"));

        return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }
}

/// <summary>
/// A failed API call, carrying the stable error code so a client can switch on
/// it rather than on the message (04 §3.1).
/// </summary>
public sealed class PcConnectApiException(
    HttpStatusCode statusCode, string code, string message, TimeSpan? retryAfter = null)
    : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string Code { get; } = code;

    public TimeSpan? RetryAfter { get; } = retryAfter;

    public bool IsStepUpRequired => Code == ErrorCodes.AuthStepUpRequired;
}

using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using PCConnect.Core.Domain;
using PCConnect.Infrastructure.Caching;
using PCConnect.Infrastructure.Contexts.Identity;
using PCConnect.Infrastructure.Data;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Infrastructure.Contexts.Devices;

/// <summary>
/// The devices bounded context: the device registry, pairing, presence and
/// heartbeat. It owns the answer to "does this user own this device", and it
/// executes nothing (01 §3.2).
/// </summary>
public sealed class DeviceService(
    Db db,
    Core.IPasswordHasher hasher,
    ITokenIssuer tokens,
    IEnvelopeEncryptor envelope,
    IClock clock,
    RateLimiter limiter,
    IPresenceTracker presence,
    ICacheStore cache,
    IRealtimeNotifier realtime,
    SecurityEventLog audit,
    IdentityService identity,
    ILogger<DeviceService> logger)
{
    private static readonly TimeSpan PairingTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PresenceTtl = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan HeartbeatCoalesce = TimeSpan.FromMinutes(1);
    private const int MaxPairingAttempts = 10;

    // ── pairing ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Step 1, from the agent, unauthenticated. Nothing is created here except a
    /// pending pairing: an unclaimed code is not a device and grants nothing.
    /// </summary>
    public async Task<PairStartResponse> StartPairingAsync(PairStartRequest request, RequestContext ctx, CancellationToken ct = default)
    {
        await limiter.ConsumeAsync(RateBudgets.PairStartPerIp, ctx.IpAddress ?? "unknown", ct);

        var requestedName = (request.RequestedName ?? string.Empty).Trim();
        if (requestedName.Length is 0 or > 128)
        {
            throw AppException.Validation("requestedName must be 1-128 characters.",
                new ErrorDetail("requestedName", "length"));
        }

        var platform = NormalisePlatform(request.Platform);
        var code = PairingCode.Generate();
        var (pollToken, pollHash) = tokens.CreateOpaqueToken();

        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO device_pairings (code_hash, poll_token_hash, requested_name, platform, expires_at)
            VALUES (@CodeHash, @PollHash, @Name, @Platform, @ExpiresAt)
            """,
            new
            {
                CodeHash = Sha256(code),
                PollHash = pollHash,
                Name = requestedName,
                Platform = platform,
                ExpiresAt = clock.UtcNow.Add(PairingTtl),
            }, cancellationToken: ct));

        await audit.WriteAsync(null, SecurityEventNames.DevicePairingStarted, true, ctx,
            new { requestedName, platform }, ct);

        return new PairStartResponse(code, pollToken, (int)PairingTtl.TotalSeconds);
    }

    /// <summary>
    /// Step 2, from the user's phone or the web dashboard. This is the moment the
    /// device becomes real, and it is the check that replaces "any PCName header
    /// auto-registers a device" (S1-08).
    /// </summary>
    public async Task<PairClaimResponse> ClaimPairingAsync(
        CallerIdentity caller, PairClaimRequest request, RequestContext ctx, CancellationToken ct = default)
    {
        caller.Require(Scopes.DeviceManage);
        await limiter.ConsumeAsync(RateBudgets.PairClaimPerUser, caller.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);

        var code = PairingCode.Normalise(request.PairingCode);
        if (code.Length == 0)
        {
            throw AppException.NotFound(ErrorCodes.PairingCodeInvalid, "That pairing code is not valid.");
        }

        var codeHash = Sha256(code);

        // Per-code attempt budget: with 25^8 combinations and ten attempts, the
        // code is not brute-forceable inside its ten-minute life.
        var attempts = await cache.IncrementAsync(CacheKeys.PairingAttempts(Convert.ToHexString(codeHash)), PairingTtl, ct);
        if (attempts > MaxPairingAttempts)
        {
            throw AppException.TooManyRequests("Too many attempts for that pairing code.", PairingTtl);
        }

        return await db.InTransactionAsync(async (connection, tx) =>
        {
            var pairing = await connection.QuerySingleOrDefaultAsync<PairingRow>(new CommandDefinition("""
                SELECT id, requested_name AS RequestedName, platform, expires_at AS ExpiresAt,
                       claimed_at AS ClaimedAt, device_id AS DeviceId
                  FROM device_pairings
                 WHERE code_hash = @CodeHash
                 FOR UPDATE
                """, new { CodeHash = codeHash }, tx, cancellationToken: ct));

            if (pairing is null || pairing.ExpiresAt <= clock.UtcNow)
            {
                throw AppException.NotFound(ErrorCodes.PairingCodeInvalid,
                    "That pairing code is not valid or has expired. Generate a new one on the PC.");
            }

            if (pairing.ClaimedAt is not null)
            {
                throw AppException.Conflict(ErrorCodes.PairingCodeInvalid, "That pairing code has already been used.");
            }

            var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
                ? pairing.RequestedName
                : request.DisplayName.Trim();

            displayName = await DeduplicateNameAsync(connection, tx, caller.UserId, displayName, ct);

            var deviceId = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO devices (user_id, display_name, platform)
                VALUES (@UserId, @DisplayName, @Platform)
                RETURNING id
                """,
                new { UserId = caller.UserId, DisplayName = displayName, Platform = pairing.Platform },
                tx, cancellationToken: ct));

            // The secret is generated here, hashed for storage, and held wrapped
            // under the KEK until the agent collects it exactly once.
            var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
            var (wrapped, kekId) = WrapSecret(secret);

            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO device_credentials (device_id, secret_hash) VALUES (@DeviceId, @Hash)
                """, new { DeviceId = deviceId, Hash = hasher.Hash(secret) }, tx, cancellationToken: ct));

            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE device_pairings
                   SET claimed_by_user_id = @UserId, device_id = @DeviceId, claimed_at = now(),
                       secret_wrapped = @Wrapped, secret_kek_id = @KekId
                 WHERE id = @Id
                """,
                new { UserId = caller.UserId, DeviceId = deviceId, Wrapped = wrapped, KekId = kekId, pairing.Id },
                tx, cancellationToken: ct));

            var publicId = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(
                "SELECT public_id FROM devices WHERE id = @Id", new { Id = deviceId }, tx, cancellationToken: ct));

            await audit.WriteInTransactionAsync(connection, tx, caller.UserId,
                SecurityEventNames.DevicePairingClaimed, true, ctx, new { deviceId = publicId, displayName }, ct);

            return new PairClaimResponse(publicId.ToString(), displayName);
        }, ct);
    }

    /// <summary>
    /// Step 3, from the agent. The device secret crosses the wire exactly once,
    /// here, and the wrapped copy is destroyed as it is released.
    /// </summary>
    public async Task<PairPollResponse> PollPairingAsync(PairPollRequest request, RequestContext ctx, CancellationToken ct = default)
    {
        var hash = tokens.HashOpaqueToken(request.PollToken ?? string.Empty);

        return await db.InTransactionAsync(async (connection, tx) =>
        {
            var row = await connection.QuerySingleOrDefaultAsync<PollRow>(new CommandDefinition("""
                SELECT p.id, p.expires_at AS ExpiresAt, p.claimed_at AS ClaimedAt,
                       p.secret_wrapped AS SecretWrapped, p.secret_kek_id AS SecretKekId,
                       p.secret_released_at AS SecretReleasedAt,
                       d.public_id AS DevicePublicId, d.display_name AS DisplayName
                  FROM device_pairings p
                  LEFT JOIN devices d ON d.id = p.device_id
                 WHERE p.poll_token_hash = @Hash
                 FOR UPDATE OF p
                """, new { Hash = hash }, tx, cancellationToken: ct));

            if (row is null)
            {
                throw AppException.NotFound(ErrorCodes.PairingCodeInvalid, "That pairing session is not recognised.");
            }

            if (row.ClaimedAt is null)
            {
                return row.ExpiresAt <= clock.UtcNow
                    ? new PairPollResponse("expired", null, null, null)
                    : new PairPollResponse("pending", null, null, null);
            }

            if (row.SecretReleasedAt is not null || row.SecretWrapped is null)
            {
                throw AppException.Conflict(ErrorCodes.PairingAlreadyCollected,
                    "That device secret has already been collected. Pair again to issue a new one.");
            }

            var secret = UnwrapSecret(row.SecretWrapped, row.SecretKekId!);

            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE device_pairings
                   SET secret_released_at = now(), secret_wrapped = NULL, secret_kek_id = NULL
                 WHERE id = @Id
                """, new { row.Id }, tx, cancellationToken: ct));

            await audit.WriteInTransactionAsync(connection, tx, null,
                SecurityEventNames.DevicePairingCollected, true, ctx, new { deviceId = row.DevicePublicId }, ct);

            return new PairPollResponse("paired", row.DevicePublicId?.ToString(), secret, row.DisplayName);
        }, ct);
    }

    /// <summary>
    /// The agent exchanges its long-lived secret for a short-lived device token
    /// carrying only <c>command:receive</c> and <c>command:ack</c>.
    /// </summary>
    public async Task<TokenPairResponse> IssueDeviceTokenAsync(DeviceTokenRequest request, RequestContext ctx, CancellationToken ct = default)
    {
        if (!Guid.TryParse(request.DeviceId, out var devicePublicId))
        {
            throw AppException.Unauthorized(ErrorCodes.AuthInvalidCredentials, "Device credentials are not valid.");
        }

        await limiter.ConsumeAsync(RateBudgets.LoginPerIp, ctx.IpAddress ?? "unknown", ct);

        return await db.InTransactionAsync(async (connection, tx) =>
        {
            var row = await connection.QuerySingleOrDefaultAsync<DeviceCredentialRow>(new CommandDefinition("""
                SELECT d.id AS DeviceId, d.user_id AS UserId, d.status, c.secret_hash AS SecretHash
                  FROM devices d
                  JOIN device_credentials c ON c.device_id = d.id
                  JOIN users u ON u.id = d.user_id
                 WHERE d.public_id = @PublicId AND u.deleted_at IS NULL
                """, new { PublicId = devicePublicId }, tx, cancellationToken: ct));

            if (row is null || !hasher.Verify(request.DeviceSecret ?? string.Empty, row.SecretHash))
            {
                await audit.WriteInTransactionAsync(connection, tx, row?.UserId,
                    SecurityEventNames.DeviceTokenIssued, false, ctx, new { deviceId = devicePublicId }, ct);
                throw AppException.Unauthorized(ErrorCodes.AuthInvalidCredentials, "Device credentials are not valid.");
            }

            if (row.Status != "active")
            {
                throw AppException.Forbidden(ErrorCodes.DeviceRevoked,
                    "This device has been removed from the account. Pair it again to reconnect.");
            }

            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE devices
                   SET agent_version = COALESCE(NULLIF(@AgentVersion, ''), agent_version),
                       os_version = COALESCE(NULLIF(@OsVersion, ''), os_version),
                       last_seen_at = now(), updated_at = now()
                 WHERE id = @DeviceId
                """,
                new { request.AgentVersion, request.OsVersion, row.DeviceId }, tx, cancellationToken: ct));

            var user = await IdentityService.LoadUserAsync(connection, tx, row.UserId, ct)
                ?? throw AppException.Unauthorized(ErrorCodes.AuthInvalidCredentials, "Device credentials are not valid.");

            var pair = await identity.MintPairAsync(connection, tx, user, ClientKinds.DesktopAgent,
                request.AgentVersion ?? string.Empty, row.DeviceId, Guid.CreateVersion7(),
                [.. Scopes.DeviceSession], ctx, ct);

            await audit.WriteInTransactionAsync(connection, tx, row.UserId,
                SecurityEventNames.DeviceTokenIssued, true, ctx, new { deviceId = devicePublicId }, ct);

            // A device session must not carry the user's profile back to the agent.
            return pair with { User = null };
        }, ct);
    }

    // ── registry ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DeviceResponse>> ListAsync(CallerIdentity caller, CancellationToken ct = default)
    {
        caller.Require(Scopes.DeviceRead);

        await using var connection = await db.OpenAsync(ct);
        var rows = (await connection.QueryAsync<DeviceRow>(new CommandDefinition(
            DeviceSelectSql + " WHERE d.user_id = @UserId AND d.status <> 'revoked' ORDER BY d.display_name",
            new { UserId = caller.UserId }, cancellationToken: ct))).ToList();

        var online = await presence.AreOnlineAsync(rows.Select(r => r.PublicId).ToList(), ct);
        return rows.Select(r => ToResponse(r, online.GetValueOrDefault(r.PublicId))).ToList();
    }

    public async Task<DeviceResponse> GetAsync(CallerIdentity caller, Guid deviceId, CancellationToken ct = default)
    {
        caller.Require(Scopes.DeviceRead);

        await using var connection = await db.OpenAsync(ct);
        var row = await LoadOwnedAsync(connection, null, caller.UserId, deviceId, ct);
        return ToResponse(row, await presence.IsOnlineAsync(row.PublicId, ct));
    }

    public async Task<DeviceResponse> UpdateAsync(
        CallerIdentity caller, Guid deviceId, UpdateDeviceRequest request, CancellationToken ct = default)
    {
        caller.Require(Scopes.DeviceManage);

        if (request.AllowedCommands is { } allowed)
        {
            foreach (var type in allowed)
            {
                if (!CommandTypes.All.Contains(type))
                {
                    throw AppException.Validation($"'{type}' is not a known command type.",
                        new ErrorDetail("allowedCommands", "unknown_type"));
                }
            }
        }

        return await db.InTransactionAsync(async (connection, tx) =>
        {
            var row = await LoadOwnedAsync(connection, tx, caller.UserId, deviceId, ct);

            var displayName = row.DisplayName;
            if (!string.IsNullOrWhiteSpace(request.DisplayName) &&
                !string.Equals(request.DisplayName.Trim(), row.DisplayName, StringComparison.Ordinal))
            {
                displayName = request.DisplayName.Trim();
                if (displayName.Length > 128)
                {
                    throw AppException.Validation("displayName must be at most 128 characters.",
                        new ErrorDetail("displayName", "length"));
                }
            }

            try
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE devices
                       SET display_name = @DisplayName,
                           allowed_commands = COALESCE(@Allowed::jsonb, allowed_commands),
                           updated_at = now()
                     WHERE id = @Id
                    """,
                    new
                    {
                        DisplayName = displayName,
                        Allowed = request.AllowedCommands is null ? null : DbJson.Serialise(request.AllowedCommands),
                        row.Id,
                    }, tx, cancellationToken: ct));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw AppException.Conflict(ErrorCodes.DeviceNameConflict,
                    "You already have a device with that name.");
            }

            var updated = await LoadOwnedAsync(connection, tx, caller.UserId, deviceId, ct);
            return ToResponse(updated, await presence.IsOnlineAsync(deviceId, ct));
        }, ct);
    }

    /// <summary>
    /// Revoking a device kills its credential and every session minted from it in
    /// one step, so a stolen device secret stops working immediately rather than
    /// when its access token happens to expire.
    /// </summary>
    public async Task RevokeAsync(CallerIdentity caller, Guid deviceId, RequestContext ctx, CancellationToken ct = default)
    {
        caller.Require(Scopes.DeviceManage);

        await db.InTransactionAsync(async (connection, tx) =>
        {
            var row = await LoadOwnedAsync(connection, tx, caller.UserId, deviceId, ct);

            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE devices SET status = 'revoked', revoked_at = now(), updated_at = now() WHERE id = @Id
                """, new { row.Id }, tx, cancellationToken: ct));

            await connection.ExecuteAsync(new CommandDefinition("""
                DELETE FROM device_credentials WHERE device_id = @Id
                """, new { row.Id }, tx, cancellationToken: ct));

            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE refresh_tokens SET revoked_at = now(), revoked_reason = 'device_revoked'
                 WHERE device_id = @Id AND revoked_at IS NULL
                """, new { row.Id }, tx, cancellationToken: ct));

            // Anything already in flight for this device is abandoned rather than
            // left to be executed by a machine that is no longer trusted.
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE commands SET status = 'cancelled', terminal_at = now()
                 WHERE device_id = @Id AND status IN ('issued','delivered')
                """, new { row.Id }, tx, cancellationToken: ct));

            await audit.WriteInTransactionAsync(connection, tx, caller.UserId,
                SecurityEventNames.DeviceRevoked, true, ctx, new { deviceId }, ct);
        }, ct);

        await presence.MarkOfflineAsync(deviceId, ct);
        await realtime.DevicePresenceAsync(caller.UserPublicId, new DevicePresenceEvent(deviceId.ToString(), false), ct);
    }

    // ── legacy compatibility (dies with the shim) ────────────────────────────

    /// <summary>
    /// Resolves a device by the display name a legacy client asserts in its
    /// <c>PCName</c> header, creating it if it does not exist.
    ///
    /// This is the one place the old weak model survives (04 §5, ADR-0008): the
    /// installed clients cannot pair, so for them a name still auto-registers.
    /// It is confined here, only reachable with a <c>client_kind='legacy'</c>
    /// credential, writes a security event every time, and dies with the shim.
    /// </summary>
    public async Task<DeviceRow> ResolveLegacyDeviceAsync(
        CallerIdentity caller, string? pcName, RequestContext ctx, bool createIfMissing, CancellationToken ct = default)
    {
        if (caller.ClientKind != ClientKinds.Legacy)
        {
            throw AppException.Forbidden(ErrorCodes.AuthScopeInsufficient,
                "Device names are not an identity outside the compatibility shim.");
        }

        var name = (pcName ?? string.Empty).Trim();
        if (name.Length is 0 or > 128)
        {
            throw AppException.NotFound(ErrorCodes.DeviceNotFound, "No such device.");
        }

        await using var connection = await db.OpenAsync(ct);
        var existing = await connection.QuerySingleOrDefaultAsync<DeviceRow>(new CommandDefinition(
            DeviceSelectSql + " WHERE d.user_id = @UserId AND d.display_name = @Name AND d.status <> 'revoked'",
            new { UserId = caller.UserId, Name = name }, cancellationToken: ct));

        if (existing is not null)
        {
            return existing;
        }

        if (!createIfMissing)
        {
            throw AppException.NotFound(ErrorCodes.DeviceNotFound, "No such device.");
        }

        return await db.InTransactionAsync(async (txConnection, tx) =>
        {
            var deviceId = await txConnection.ExecuteScalarAsync<long>(new CommandDefinition("""
                INSERT INTO devices (user_id, display_name, platform)
                VALUES (@UserId, @Name, 'windows')
                ON CONFLICT (user_id, display_name) DO UPDATE SET updated_at = now()
                RETURNING id
                """, new { UserId = caller.UserId, Name = name }, tx, cancellationToken: ct));

            // A legacy device has no secret of its own: it is reached only through
            // the compatibility token, and it cannot obtain a device token.
            await txConnection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO device_credentials (device_id, secret_hash)
                VALUES (@DeviceId, @Hash)
                ON CONFLICT (device_id) DO NOTHING
                """,
                new { DeviceId = deviceId, Hash = hasher.Hash(Base64Url(RandomNumberGenerator.GetBytes(32))) },
                tx, cancellationToken: ct));

            await audit.WriteInTransactionAsync(txConnection, tx, caller.UserId,
                SecurityEventNames.LegacyAutoPair, true, ctx, new { displayName = name }, ct);

            return await txConnection.QuerySingleAsync<DeviceRow>(new CommandDefinition(
                DeviceSelectSql + " WHERE d.id = @Id", new { Id = deviceId }, tx, cancellationToken: ct));
        }, ct);
    }

    /// <summary>
    /// Builds the caller a legacy request acts as when it addresses one device:
    /// the same user identity, narrowed to that device so the command context's
    /// ownership and receive checks work unchanged.
    /// </summary>
    public static CallerIdentity AsLegacyDeviceCaller(CallerIdentity caller, DeviceRow device) =>
        new()
        {
            UserId = caller.UserId,
            UserPublicId = caller.UserPublicId,
            DeviceId = device.Id,
            DevicePublicId = device.PublicId,
            ClientKind = ClientKinds.Legacy,
            Scopes = caller.Scopes,
            TokenId = caller.TokenId,
        };

    // ── presence ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Coalesced heartbeat: live presence is refreshed every time, the durable
    /// <c>last_seen_at</c> at most once a minute. Writing a heartbeat to the
    /// database on every ping is what made <c>pcnames.Time</c> write-hot.
    /// </summary>
    public async Task HeartbeatAsync(CallerIdentity caller, HeartbeatRequest request, CancellationToken ct = default)
    {
        caller.Require(Scopes.CommandReceive);

        if (caller.DeviceId is not { } deviceId || caller.DevicePublicId is not { } devicePublicId)
        {
            throw AppException.Forbidden(ErrorCodes.AuthScopeInsufficient, "Only a device token can send a heartbeat.");
        }

        await presence.MarkOnlineAsync(devicePublicId, PresenceTtl, ct);

        var coalesceKey = $"hb:{devicePublicId:N}";
        if (await cache.SetIfAbsentAsync(coalesceKey, "1", HeartbeatCoalesce, ct))
        {
            await using var connection = await db.OpenAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE devices
                   SET last_seen_at = now(),
                       agent_version = COALESCE(NULLIF(@AgentVersion, ''), agent_version),
                       os_version = COALESCE(NULLIF(@OsVersion, ''), os_version)
                 WHERE id = @DeviceId
                """, new { request.AgentVersion, request.OsVersion, DeviceId = deviceId }, cancellationToken: ct));
        }

        await realtime.DevicePresenceAsync(caller.UserPublicId,
            new DevicePresenceEvent(devicePublicId.ToString(), true), ct);
    }

    public async Task MarkConnectedAsync(Guid userPublicId, Guid devicePublicId, CancellationToken ct = default)
    {
        await presence.MarkOnlineAsync(devicePublicId, PresenceTtl, ct);
        await realtime.DevicePresenceAsync(userPublicId, new DevicePresenceEvent(devicePublicId.ToString(), true), ct);
    }

    public async Task MarkDisconnectedAsync(Guid userPublicId, Guid devicePublicId, CancellationToken ct = default)
    {
        await presence.MarkOfflineAsync(devicePublicId, ct);
        await realtime.DevicePresenceAsync(userPublicId, new DevicePresenceEvent(devicePublicId.ToString(), false), ct);
    }

    // ── shared helpers used by other contexts ────────────────────────────────

    internal const string DeviceSelectSql = """
        SELECT d.id AS Id, d.public_id AS PublicId, d.user_id AS UserId, d.display_name AS DisplayName,
               d.platform, d.os_version AS OsVersion, d.agent_version AS AgentVersion, d.status,
               d.last_seen_at AS LastSeenAt, d.paired_at AS PairedAt, d.allowed_commands AS AllowedCommands
          FROM devices d
        """;

    /// <summary>
    /// The ownership check, in one place. A plain equality on an authenticated
    /// id — never a header, never a name (03 §3, check 3). Not-owned and
    /// not-found are the same 404 on purpose.
    /// </summary>
    internal static async Task<DeviceRow> LoadOwnedAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, long userId, Guid devicePublicId, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<DeviceRow>(new CommandDefinition(
            DeviceSelectSql + " WHERE d.public_id = @PublicId AND d.user_id = @UserId AND d.status <> 'revoked'",
            new { PublicId = devicePublicId, UserId = userId }, tx, cancellationToken: ct));

        return row ?? throw AppException.NotFound(ErrorCodes.DeviceNotFound, "No such device.");
    }

    internal static DeviceResponse ToResponse(DeviceRow row, bool isOnline) => new(
        row.PublicId.ToString(),
        row.DisplayName,
        row.Platform,
        row.OsVersion,
        row.AgentVersion,
        row.Status,
        isOnline,
        row.LastSeenAt,
        row.PairedAt,
        DbJson.StringArray(row.AllowedCommands));

    private async Task<string> DeduplicateNameAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, long userId, string displayName, CancellationToken ct)
    {
        var candidate = displayName;
        for (var suffix = 2; suffix < 100; suffix++)
        {
            var taken = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM devices WHERE user_id = @UserId AND display_name = @Name)",
                new { UserId = userId, Name = candidate }, tx, cancellationToken: ct));

            if (!taken)
            {
                return candidate;
            }

            candidate = $"{displayName} ({suffix})";
        }

        logger.LogWarning("Could not find a free device name for user {UserId} based on {Name}", userId, displayName);
        throw AppException.Conflict(ErrorCodes.DeviceNameConflict, "You already have a device with that name.");
    }

    private static string NormalisePlatform(string? platform)
    {
        var value = (platform ?? "windows").Trim().ToLowerInvariant();
        return value is "windows" or "macos" or "linux" or "android" or "ios" ? value : "other";
    }

    private (byte[] Wrapped, string KekId) WrapSecret(string secret)
    {
        var (wrappedKey, kekId) = envelope.CreateDataKey();
        var dek = envelope.UnwrapDataKey(wrappedKey, kekId);
        var ciphertext = envelope.Encrypt(dek, secret, "pcconnect:device-secret");

        // The wrapped DEK travels with the ciphertext: [2B length][wrapped DEK][ciphertext].
        var output = new byte[2 + wrappedKey.Length + ciphertext.Length];
        output[0] = (byte)(wrappedKey.Length >> 8);
        output[1] = (byte)(wrappedKey.Length & 0xFF);
        wrappedKey.CopyTo(output, 2);
        ciphertext.CopyTo(output, 2 + wrappedKey.Length);

        CryptographicOperations.ZeroMemory(dek);
        return (output, kekId);
    }

    private string UnwrapSecret(byte[] stored, string kekId)
    {
        var wrappedLength = (stored[0] << 8) | stored[1];
        var wrappedKey = stored[2..(2 + wrappedLength)];
        var ciphertext = stored[(2 + wrappedLength)..];

        var dek = envelope.UnwrapDataKey(wrappedKey, kekId);
        try
        {
            return envelope.Decrypt(dek, ciphertext, "pcconnect:device-secret");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private static byte[] Sha256(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── row shapes ───────────────────────────────────────────────────────────

    public sealed record DeviceRow
    {
        public long Id { get; init; }
        public Guid PublicId { get; init; }
        public long UserId { get; init; }
        public string DisplayName { get; init; } = string.Empty;
        public string Platform { get; init; } = "windows";
        public string OsVersion { get; init; } = string.Empty;
        public string AgentVersion { get; init; } = string.Empty;
        public string Status { get; init; } = "active";
        public DateTimeOffset? LastSeenAt { get; init; }
        public DateTimeOffset PairedAt { get; init; }
        public string AllowedCommands { get; init; } = "[]";
    }

    private sealed record PairingRow
    {
        public long Id { get; init; }
        public string RequestedName { get; init; } = string.Empty;
        public string Platform { get; init; } = "windows";
        public DateTimeOffset ExpiresAt { get; init; }
        public DateTimeOffset? ClaimedAt { get; init; }
        public long? DeviceId { get; init; }
    }

    private sealed record PollRow
    {
        public long Id { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public DateTimeOffset? ClaimedAt { get; init; }
        public byte[]? SecretWrapped { get; init; }
        public string? SecretKekId { get; init; }
        public DateTimeOffset? SecretReleasedAt { get; init; }
        public Guid? DevicePublicId { get; init; }
        public string? DisplayName { get; init; }
    }

    private sealed record DeviceCredentialRow
    {
        public long DeviceId { get; init; }
        public long UserId { get; init; }
        public string Status { get; init; } = "active";
        public string SecretHash { get; init; } = string.Empty;
    }
}

using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using PCConnect.Core.Domain;
using PCConnect.Infrastructure.Caching;
using PCConnect.Infrastructure.Data;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Infrastructure.Contexts.Identity;

public sealed class IdentityOptions
{
    /// <summary>
    /// While true, <c>POST /v2/auth/login</c> still accepts the unsalted SHA-256
    /// the installed clients send. It is switched off at the sunset date; after
    /// that, remaining legacy accounts go through password reset (07 Phase 6.5).
    /// </summary>
    public bool AcceptLegacyPasswordHash { get; set; } = true;

    public bool RequireVerifiedEmailToLogIn { get; set; }

    public int MaxFailedAttemptsBeforeLockout { get; set; } = 10;

    public int LockoutBaseSeconds { get; set; } = 30;

    public int LockoutMaxSeconds { get; set; } = 3600;

    public int PasswordResetTtlMinutes { get; set; } = 30;

    public int EmailVerifyTtlHours { get; set; } = 48;

    public string PasswordResetUrlTemplate { get; set; } = "https://pcconnect.example/password-reset?token={0}";

    public string EmailVerifyUrlTemplate { get; set; } = "https://pcconnect.example/verify-email?token={0}";
}

/// <summary>
/// The identity bounded context: users, credentials, sessions, tokens, resets.
/// It knows nothing about devices, commands or reminders (01 §3.2).
/// </summary>
public sealed class IdentityService(
    Db db,
    Core.IPasswordHasher hasher,
    ITokenIssuer tokens,
    IClock clock,
    RateLimiter limiter,
    SecurityEventLog audit,
    IBreachedPasswordChecker breachedPasswords,
    IEmailSender email,
    IOptions<IdentityOptions> options,
    ILogger<IdentityService> logger)
{
    private readonly IdentityOptions _options = options.Value;

    /// <summary>
    /// A well-formed Argon2id hash of a value nobody holds. Verifying against it
    /// when the account does not exist keeps the "unknown user" and "wrong
    /// password" branches within the same order of magnitude, so response time is
    /// not a username oracle (03 §2.5).
    /// </summary>
    private readonly Lazy<string> _decoyHash = new(() => hasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

    // ── registration ─────────────────────────────────────────────────────────

    public async Task<TokenPairResponse> RegisterAsync(RegisterRequest request, RequestContext ctx, CancellationToken ct = default)
    {
        var username = request.Username?.Trim() ?? string.Empty;
        var emailAddress = request.Email?.Trim() ?? string.Empty;

        if (username.Length is < 3 or > 64)
        {
            throw AppException.Validation("Username must be between 3 and 64 characters.",
                new ErrorDetail("username", "length"));
        }

        if (!Normalise.LooksLikeEmail(emailAddress) || emailAddress.Length > 320)
        {
            throw AppException.Validation("A valid email address is required.",
                new ErrorDetail("email", "format"));
        }

        PasswordPolicy.Validate(request.Password, username, emailAddress);
        await AssertNotBreachedAsync(request.Password, ct);

        var timezone = Normalise.IanaTimeZoneOrDefault(request.Timezone, "Etc/UTC");
        var passwordHash = hasher.Hash(request.Password);

        var user = await db.InTransactionAsync(async (connection, tx) =>
        {
            long userId;
            try
            {
                userId = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
                    INSERT INTO users (email, email_normalised, username, username_normalised,
                                       display_name, timezone, status, is_marketing_opt_in)
                    VALUES (@Email, @EmailNormalised, @Username, @UsernameNormalised,
                            @DisplayName, @Timezone, @Status, @Marketing)
                    RETURNING id
                    """,
                    new
                    {
                        Email = emailAddress,
                        EmailNormalised = Normalise.Email(emailAddress),
                        Username = username,
                        UsernameNormalised = Normalise.Username(username),
                        DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? username : request.DisplayName!.Trim(),
                        Timezone = timezone,
                        Status = _options.RequireVerifiedEmailToLogIn ? UserStatuses.PendingVerification : UserStatuses.Active,
                        Marketing = request.MarketingOptIn,
                    }, tx, cancellationToken: ct));
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                // Deliberately the same message for either collision: telling the
                // caller which one matched turns registration into an account
                // enumeration endpoint.
                throw AppException.Conflict(ErrorCodes.AccountExists,
                    "An account with that username or email address already exists.");
            }

            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO user_credentials (user_id, algo, password_hash)
                VALUES (@UserId, @Algo, @Hash)
                """,
                new { UserId = userId, Algo = PasswordAlgorithms.Argon2id, Hash = passwordHash },
                tx, cancellationToken: ct));

            await audit.WriteInTransactionAsync(connection, tx, userId,
                SecurityEventNames.AccountRegistered, true, ctx, null, ct);

            return await LoadUserAsync(connection, tx, userId, ct)
                ?? throw new InvalidOperationException("The account was inserted but could not be read back.");
        }, ct);

        await IssueEmailVerificationAsync(user.Id, user.Email, ctx, ct);

        return await IssueSessionAsync(user, request: null, ClientKinds.Mobile, string.Empty, ctx, ct);
    }

    // ── login ────────────────────────────────────────────────────────────────

    public async Task<TokenPairResponse> LoginAsync(LoginRequest request, RequestContext ctx, CancellationToken ct = default)
    {
        var login = request.Login?.Trim() ?? string.Empty;
        if (login.Length == 0)
        {
            throw AppException.Validation("A username or email address is required.",
                new ErrorDetail("login", "required"));
        }

        if (!ClientKinds.IsValid(request.ClientKind))
        {
            throw AppException.Validation("Unknown clientKind.", new ErrorDetail("clientKind", "unsupported"));
        }

        await limiter.ConsumeAsync(RateBudgets.LoginPerIp, ctx.IpAddress ?? "unknown", ct);
        await limiter.ConsumeAsync(RateBudgets.LoginPerAccount, Normalise.Username(login), ct);

        await using var connection = await db.OpenAsync(ct);
        var user = await FindUserByLoginAsync(connection, null, login, ct);

        if (user is null)
        {
            // Burn the same work an existing account would have cost.
            _ = hasher.Verify(request.Password ?? request.LegacyPasswordHash ?? "x", _decoyHash.Value);
            await audit.WriteAsync(null, SecurityEventNames.LoginFailed, false, ctx, new { reason = "unknown_account" }, ct);
            throw InvalidCredentials();
        }

        if (user.LockedUntil is { } lockedUntil && lockedUntil > clock.UtcNow)
        {
            await audit.WriteAsync(user.Id, SecurityEventNames.LoginLocked, false, ctx, null, ct);
            throw new AppException(ErrorCodes.AuthAccountLocked,
                "Too many failed attempts. Try again later.",
                System.Net.HttpStatusCode.Locked, null, lockedUntil - clock.UtcNow);
        }

        if (user.Status == UserStatuses.Suspended)
        {
            await audit.WriteAsync(user.Id, SecurityEventNames.LoginFailed, false, ctx, new { reason = "suspended" }, ct);
            throw InvalidCredentials();
        }

        var verification = await VerifyPasswordAsync(connection, user, request, ctx, ct);
        if (!verification.Ok)
        {
            await RecordFailedAttemptAsync(user.Id, ct);
            await audit.WriteAsync(user.Id, SecurityEventNames.LoginFailed, false, ctx,
                new { reason = verification.Reason }, ct);
            throw verification.Error ?? InvalidCredentials();
        }

        if (_options.RequireVerifiedEmailToLogIn && !user.IsEmailVerified)
        {
            throw AppException.Forbidden(ErrorCodes.AuthEmailUnverified,
                "Verify your email address before signing in.");
        }

        await ClearFailedAttemptsAsync(user.Id, ct);
        await limiter.ResetAsync(RateBudgets.LoginPerAccount, Normalise.Username(login), ct);
        await audit.WriteAsync(user.Id, SecurityEventNames.LoginSucceeded, true, ctx,
            new { clientKind = request.ClientKind, legacy = verification.UsedLegacyPath }, ct);

        return await IssueSessionAsync(user, request, request.ClientKind, request.ClientVersion, ctx, ct);
    }

    /// <summary>
    /// Authenticates without minting a session.
    ///
    /// The legacy shim needs exactly this: it verifies the credential and then
    /// issues its own long-lived compatibility key. Routing it through
    /// <see cref="LoginAsync"/> would leave an unused token pair in the session
    /// list for every legacy sign-in, which makes that list — the thing a user
    /// checks when they suspect a compromise — noisier and less trustworthy.
    /// </summary>
    public async Task<long> VerifyCredentialsAsync(LoginRequest request, RequestContext ctx, CancellationToken ct = default)
    {
        var login = request.Login?.Trim() ?? string.Empty;

        await limiter.ConsumeAsync(RateBudgets.LoginPerIp, ctx.IpAddress ?? "unknown", ct);
        await limiter.ConsumeAsync(RateBudgets.LoginPerAccount, Normalise.Username(login), ct);

        await using var connection = await db.OpenAsync(ct);
        var user = await FindUserByLoginAsync(connection, null, login, ct);

        if (user is null)
        {
            _ = hasher.Verify(request.Password ?? request.LegacyPasswordHash ?? "x", _decoyHash.Value);
            await audit.WriteAsync(null, SecurityEventNames.LoginFailed, false, ctx, new { reason = "unknown_account" }, ct);
            throw InvalidCredentials();
        }

        if (user.LockedUntil is { } lockedUntil && lockedUntil > clock.UtcNow)
        {
            throw new AppException(ErrorCodes.AuthAccountLocked,
                "Too many failed attempts. Try again later.",
                System.Net.HttpStatusCode.Locked, null, lockedUntil - clock.UtcNow);
        }

        if (user.Status == UserStatuses.Suspended)
        {
            throw InvalidCredentials();
        }

        var verification = await VerifyPasswordAsync(connection, user, request, ctx, ct);
        if (!verification.Ok)
        {
            await RecordFailedAttemptAsync(user.Id, ct);
            await audit.WriteAsync(user.Id, SecurityEventNames.LoginFailed, false, ctx,
                new { reason = verification.Reason }, ct);
            throw verification.Error ?? InvalidCredentials();
        }

        await ClearFailedAttemptsAsync(user.Id, ct);
        await audit.WriteAsync(user.Id, SecurityEventNames.LegacyLogin, true, ctx, null, ct);
        return user.Id;
    }

    private sealed record PasswordVerification(bool Ok, string Reason, bool UsedLegacyPath, AppException? Error = null);

    /// <summary>
    /// The two accepted paths, and the asymmetry between them that matters: the
    /// legacy path can authenticate but can never upgrade the stored hash,
    /// because on that path the server never sees the real password (02 §6).
    /// </summary>
    private async Task<PasswordVerification> VerifyPasswordAsync(
        NpgsqlConnection connection,
        UserRow user,
        LoginRequest request,
        RequestContext ctx,
        CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(request.Password))
        {
            if (user.Algo == PasswordAlgorithms.Argon2id)
            {
                if (!hasher.Verify(request.Password, user.PasswordHash))
                {
                    return new PasswordVerification(false, "bad_password", false);
                }

                if (hasher.NeedsRehash(user.PasswordHash))
                {
                    await UpgradeHashAsync(user.Id, hasher.Hash(request.Password), ctx, "parameters", ct);
                }

                return new PasswordVerification(true, "ok", false);
            }

            // Legacy row, real password supplied: verify against the unsalted
            // SHA-256 and then upgrade in place. This is the only path that can
            // retire a legacy hash, which is why shipping the clients comes first.
            var digest = Normalise.Sha256Hex(request.Password);
            if (!FixedTimeEquals(digest, user.PasswordHash))
            {
                return new PasswordVerification(false, "bad_password", false);
            }

            await UpgradeHashAsync(user.Id, hasher.Hash(request.Password), ctx, "legacy_sha256", ct);
            return new PasswordVerification(true, "ok", false);
        }

        if (!string.IsNullOrEmpty(request.LegacyPasswordHash))
        {
            if (!_options.AcceptLegacyPasswordHash)
            {
                return new PasswordVerification(false, "legacy_disabled", true,
                    AppException.Unauthorized(ErrorCodes.AuthLegacyHashRejected,
                        "This client version is no longer supported. Install the current client to sign in."));
            }

            if (user.Algo != PasswordAlgorithms.LegacySha256Unsalted)
            {
                // The account has already been upgraded; the pre-hashed value can
                // no longer authenticate it. Say so with the generic error.
                return new PasswordVerification(false, "already_upgraded", true);
            }

            if (!FixedTimeEquals(request.LegacyPasswordHash.Trim().ToLowerInvariant(), user.PasswordHash))
            {
                return new PasswordVerification(false, "bad_password", true);
            }

            await audit.WriteAsync(user.Id, SecurityEventNames.LoginLegacyHash, true, ctx, null, ct);
            return new PasswordVerification(true, "ok", true);
        }

        _ = connection;
        return new PasswordVerification(false, "no_credential", false,
            AppException.Validation("A password is required.", new ErrorDetail("password", "required")));
    }

    private async Task UpgradeHashAsync(long userId, string newHash, RequestContext ctx, string reason, CancellationToken ct)
    {
        await db.InTransactionAsync(async (connection, tx) =>
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE user_credentials
                   SET password_hash = @Hash,
                       algo = @Algo,
                       must_rehash = false,
                       password_changed_at = now(),
                       updated_at = now()
                 WHERE user_id = @UserId
                """,
                new { Hash = newHash, Algo = PasswordAlgorithms.Argon2id, UserId = userId },
                tx, cancellationToken: ct));

            // An upgraded credential invalidates every session minted under the old
            // one: if the legacy hash leaked, the sessions it created are suspect.
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE refresh_tokens
                   SET revoked_at = now(), revoked_reason = 'password_change'
                 WHERE user_id = @UserId AND revoked_at IS NULL
                """, new { UserId = userId }, tx, cancellationToken: ct));

            await audit.WriteInTransactionAsync(connection, tx, userId,
                SecurityEventNames.PasswordUpgraded, true, ctx, new { reason }, ct);
        }, ct);
    }

    // ── sessions ─────────────────────────────────────────────────────────────

    public async Task<TokenPairResponse> RefreshAsync(string refreshToken, RequestContext ctx, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw AppException.Validation("refreshToken is required.", new ErrorDetail("refreshToken", "required"));
        }

        var hash = tokens.HashOpaqueToken(refreshToken);

        // Reuse detection revokes the whole family and must survive the failure
        // it reports. Throwing from inside the transaction would roll the
        // revocation back, leaving a known-leaked chain live — so the branch
        // commits and the exception is raised afterwards.
        var outcome = await db.InTransactionAsync(async (connection, tx) =>
        {
            var row = await connection.QuerySingleOrDefaultAsync<RefreshRow>(new CommandDefinition("""
                SELECT id, token_hash AS TokenHash, family_id AS FamilyId, user_id AS UserId, device_id AS DeviceId,
                       client_kind AS ClientKind, client_version AS ClientVersion,
                       expires_at AS ExpiresAt, revoked_at AS RevokedAt, revoked_reason AS RevokedReason
                  FROM refresh_tokens
                 WHERE token_hash = @Hash
                 FOR UPDATE
                """, new { Hash = hash }, tx, cancellationToken: ct));

            if (row is null)
            {
                throw AppException.Unauthorized(ErrorCodes.AuthTokenInvalid, "That session is no longer valid.");
            }

            await limiter.ConsumeAsync(RateBudgets.RefreshPerFamily, row.FamilyId.ToString(), ct);

            if (row.RevokedAt is not null)
            {
                // A revoked token being presented means a copy leaked and both
                // parties are now walking the chain. Kill the family, not just
                // this link (03 §2.4).
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE refresh_tokens
                       SET revoked_at = now(), revoked_reason = 'reuse_detected'
                     WHERE family_id = @FamilyId AND revoked_at IS NULL
                    """, new { row.FamilyId }, tx, cancellationToken: ct));

                await audit.WriteInTransactionAsync(connection, tx, row.UserId,
                    SecurityEventNames.TokenReuseDetected, false, ctx,
                    new { familyId = row.FamilyId, previousReason = row.RevokedReason }, ct);

                return new RefreshOutcome(null, ReuseDetected: true);
            }

            if (row.ExpiresAt <= clock.UtcNow)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE refresh_tokens SET revoked_at = now(), revoked_reason = 'expired' WHERE id = @Id
                    """, new { row.Id }, tx, cancellationToken: ct));
                throw AppException.Unauthorized(ErrorCodes.AuthTokenExpired, "That session has expired. Sign in again.");
            }

            var user = await LoadUserAsync(connection, tx, row.UserId, ct)
                ?? throw AppException.Unauthorized(ErrorCodes.AuthTokenInvalid, "That session is no longer valid.");

            if (user.DeletedAt is not null || user.Status == UserStatuses.Suspended)
            {
                throw AppException.Unauthorized(ErrorCodes.AuthTokenInvalid, "That session is no longer valid.");
            }

            // Rotate: the presented token dies, a new one takes its place, and the
            // family id stays constant so reuse of any link is detectable.
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE refresh_tokens
                   SET revoked_at = now(), revoked_reason = 'rotated', last_used_at = now()
                 WHERE id = @Id
                """, new { row.Id }, tx, cancellationToken: ct));

            var scopes = ScopesFor(row.ClientKind, row.DeviceId is not null);
            var pair = await MintPairAsync(connection, tx, user, row.ClientKind, row.ClientVersion,
                row.DeviceId, row.FamilyId, scopes, ctx, ct);

            await audit.WriteInTransactionAsync(connection, tx, user.Id,
                SecurityEventNames.TokenRefreshed, true, ctx, new { familyId = row.FamilyId }, ct);

            return new RefreshOutcome(pair, ReuseDetected: false);
        }, ct);

        if (outcome.ReuseDetected)
        {
            throw AppException.Unauthorized(ErrorCodes.AuthTokenReused,
                "That session was already used and has been ended for your security. Sign in again.");
        }

        return outcome.Pair!;
    }

    private sealed record RefreshOutcome(TokenPairResponse? Pair, bool ReuseDetected);

    public async Task LogoutAsync(string refreshToken, RequestContext ctx, CancellationToken ct = default)
    {
        var hash = tokens.HashOpaqueToken(refreshToken ?? string.Empty);

        await using var connection = await db.OpenAsync(ct);
        var userId = await connection.ExecuteScalarAsync<long?>(new CommandDefinition("""
            UPDATE refresh_tokens
               SET revoked_at = now(), revoked_reason = 'logout'
             WHERE token_hash = @Hash AND revoked_at IS NULL
            RETURNING user_id
            """, new { Hash = hash }, cancellationToken: ct));

        if (userId is not null)
        {
            await audit.WriteAsync(userId, SecurityEventNames.SessionRevoked, true, ctx, null, ct);
        }
    }

    public async Task LogoutAllAsync(long userId, RequestContext ctx, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE refresh_tokens
               SET revoked_at = now(), revoked_reason = 'logout_all'
             WHERE user_id = @UserId AND revoked_at IS NULL
            """, new { UserId = userId }, cancellationToken: ct));

        await audit.WriteAsync(userId, SecurityEventNames.AllSessionsRevoked, true, ctx, null, ct);
    }

    public async Task RevokeSessionFamilyAsync(long userId, Guid familyId, RequestContext ctx, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var affected = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE refresh_tokens
               SET revoked_at = now(), revoked_reason = 'logout'
             WHERE user_id = @UserId AND family_id = @FamilyId AND revoked_at IS NULL
            """, new { UserId = userId, FamilyId = familyId }, cancellationToken: ct));

        if (affected == 0)
        {
            throw AppException.NotFound(ErrorCodes.AccountNotFound, "No such session.");
        }

        await audit.WriteAsync(userId, SecurityEventNames.SessionRevoked, true, ctx, new { familyId }, ct);
    }

    public async Task<IReadOnlyList<SessionResponse>> ListSessionsAsync(long userId, string? currentFamilyId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<SessionRow>(new CommandDefinition("""
            SELECT DISTINCT ON (r.family_id)
                   r.family_id AS FamilyId, r.client_kind AS ClientKind, r.client_version AS ClientVersion,
                   d.public_id AS DevicePublicId, host(r.ip_first_seen) AS Ip,
                   r.issued_at AS IssuedAt, r.last_used_at AS LastUsedAt, r.expires_at AS ExpiresAt
              FROM refresh_tokens r
              LEFT JOIN devices d ON d.id = r.device_id
             WHERE r.user_id = @UserId AND r.revoked_at IS NULL AND r.expires_at > now()
             ORDER BY r.family_id, r.issued_at DESC
            """, new { UserId = userId }, cancellationToken: ct));

        return rows.Select(r => new SessionResponse(
            r.FamilyId.ToString(),
            r.ClientKind,
            r.ClientVersion,
            r.DevicePublicId?.ToString(),
            r.Ip,
            r.IssuedAt,
            r.LastUsedAt,
            r.ExpiresAt,
            string.Equals(r.FamilyId.ToString(), currentFamilyId, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    // ── passwords ────────────────────────────────────────────────────────────

    public async Task ChangePasswordAsync(long userId, ChangePasswordRequest request, RequestContext ctx, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var user = await LoadUserAsync(connection, null, userId, ct)
            ?? throw AppException.NotFound(ErrorCodes.AccountNotFound, "No such account.");

        var currentOk = user.Algo == PasswordAlgorithms.Argon2id
            ? hasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash)
            : FixedTimeEquals(Normalise.Sha256Hex(request.CurrentPassword ?? string.Empty), user.PasswordHash);

        if (!currentOk)
        {
            await audit.WriteAsync(userId, SecurityEventNames.PasswordChanged, false, ctx, new { reason = "bad_current" }, ct);
            throw AppException.Unauthorized(ErrorCodes.AuthInvalidCredentials, "Your current password is incorrect.");
        }

        // The same policy module as registration and reset. S1-10 was that this
        // call did not exist on the change path.
        PasswordPolicy.Validate(request.NewPassword, user.Username, user.Email);
        await AssertNotBreachedAsync(request.NewPassword, ct);

        await ApplyNewPasswordAsync(userId, request.NewPassword, SecurityEventNames.PasswordChanged, ctx, ct);
    }

    /// <summary>
    /// Always succeeds from the caller's point of view, whether or not the
    /// account exists, and takes a similar amount of time either way (03 §6).
    /// </summary>
    public async Task ForgotPasswordAsync(string emailAddress, RequestContext ctx, CancellationToken ct = default)
    {
        await limiter.ConsumeAsync(RateBudgets.PasswordResetPerIp, ctx.IpAddress ?? "unknown", ct);

        var normalised = Normalise.Email(emailAddress ?? string.Empty);
        await limiter.ConsumeAsync(RateBudgets.PasswordResetPerAccount, normalised, ct);

        await using var connection = await db.OpenAsync(ct);
        var user = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            UserSelectSql + " WHERE u.email_normalised = @Email AND u.deleted_at IS NULL",
            new { Email = normalised }, cancellationToken: ct));

        if (user is null)
        {
            logger.LogInformation("Password reset requested for an address with no account");
            return;
        }

        var (token, hash) = tokens.CreateOpaqueToken();
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO auth_challenges (user_id, purpose, code_hash, expires_at, requested_ip)
            VALUES (@UserId, 'password_reset', @Hash, @ExpiresAt, @Ip::inet)
            """,
            new
            {
                UserId = user.Id,
                Hash = hash,
                ExpiresAt = clock.UtcNow.AddMinutes(_options.PasswordResetTtlMinutes),
                Ip = ctx.IpAddress,
            }, cancellationToken: ct));

        await audit.WriteAsync(user.Id, SecurityEventNames.PasswordResetRequested, true, ctx, null, ct);

        await email.SendAsync(user.Email, "Reset your PCConnect password",
            $"Use this link within {_options.PasswordResetTtlMinutes} minutes to set a new password:\n\n" +
            string.Format(System.Globalization.CultureInfo.InvariantCulture, _options.PasswordResetUrlTemplate, token) +
            "\n\nIf you did not ask for this, you can ignore this message.", ct);
    }

    public async Task ResetPasswordAsync(string token, string newPassword, RequestContext ctx, CancellationToken ct = default)
    {
        var hash = tokens.HashOpaqueToken(token ?? string.Empty);

        await using var connection = await db.OpenAsync(ct);
        var challenge = await connection.QuerySingleOrDefaultAsync<ChallengeRow>(new CommandDefinition("""
            SELECT id, user_id AS UserId, expires_at AS ExpiresAt, consumed_at AS ConsumedAt
              FROM auth_challenges
             WHERE code_hash = @Hash AND purpose = 'password_reset'
            """, new { Hash = hash }, cancellationToken: ct));

        if (challenge is null || challenge.ConsumedAt is not null || challenge.ExpiresAt <= clock.UtcNow)
        {
            throw AppException.Unauthorized(ErrorCodes.AuthChallengeInvalid,
                "That reset link is invalid or has expired. Request a new one.");
        }

        var user = await LoadUserAsync(connection, null, challenge.UserId, ct)
            ?? throw AppException.Unauthorized(ErrorCodes.AuthChallengeInvalid, "That reset link is no longer valid.");

        PasswordPolicy.Validate(newPassword, user.Username, user.Email);
        await AssertNotBreachedAsync(newPassword, ct);

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE auth_challenges SET consumed_at = now() WHERE id = @Id AND consumed_at IS NULL",
            new { challenge.Id }, cancellationToken: ct));

        await ApplyNewPasswordAsync(challenge.UserId, newPassword, SecurityEventNames.PasswordResetCompleted, ctx, ct);
    }

    private async Task ApplyNewPasswordAsync(long userId, string newPassword, string auditEvent, RequestContext ctx, CancellationToken ct)
    {
        var newHash = hasher.Hash(newPassword);

        await db.InTransactionAsync(async (connection, tx) =>
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE user_credentials
                   SET password_hash = @Hash, algo = @Algo, must_rehash = false,
                       password_changed_at = now(), failed_attempts = 0, locked_until = NULL, updated_at = now()
                 WHERE user_id = @UserId
                """, new { Hash = newHash, Algo = PasswordAlgorithms.Argon2id, UserId = userId }, tx, cancellationToken: ct));

            // Every session ends: a password change is the one signal that says
            // "assume the old credential is compromised".
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE refresh_tokens SET revoked_at = now(), revoked_reason = 'password_change'
                 WHERE user_id = @UserId AND revoked_at IS NULL
                """, new { UserId = userId }, tx, cancellationToken: ct));

            // An account that was parked pending a reset becomes usable again.
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE users SET status = 'active', updated_at = now()
                 WHERE id = @UserId AND status = 'pending_verification'
                """, new { UserId = userId }, tx, cancellationToken: ct));

            await audit.WriteInTransactionAsync(connection, tx, userId, auditEvent, true, ctx, null, ct);
        }, ct);
    }

    // ── email verification ───────────────────────────────────────────────────

    public async Task IssueEmailVerificationAsync(long userId, string emailAddress, RequestContext ctx, CancellationToken ct = default)
    {
        var (token, hash) = tokens.CreateOpaqueToken();

        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO auth_challenges (user_id, purpose, code_hash, expires_at, requested_ip)
            VALUES (@UserId, 'email_verify', @Hash, @ExpiresAt, @Ip::inet)
            """,
            new
            {
                UserId = userId,
                Hash = hash,
                ExpiresAt = clock.UtcNow.AddHours(_options.EmailVerifyTtlHours),
                Ip = ctx.IpAddress,
            }, cancellationToken: ct));

        await email.SendAsync(emailAddress, "Verify your PCConnect email address",
            "Confirm your email address:\n\n" +
            string.Format(System.Globalization.CultureInfo.InvariantCulture, _options.EmailVerifyUrlTemplate, token), ct);
    }

    public async Task VerifyEmailAsync(string token, RequestContext ctx, CancellationToken ct = default)
    {
        var hash = tokens.HashOpaqueToken(token ?? string.Empty);

        await db.InTransactionAsync(async (connection, tx) =>
        {
            var challenge = await connection.QuerySingleOrDefaultAsync<ChallengeRow>(new CommandDefinition("""
                SELECT id, user_id AS UserId, expires_at AS ExpiresAt, consumed_at AS ConsumedAt
                  FROM auth_challenges
                 WHERE code_hash = @Hash AND purpose = 'email_verify'
                 FOR UPDATE
                """, new { Hash = hash }, tx, cancellationToken: ct));

            if (challenge is null || challenge.ConsumedAt is not null || challenge.ExpiresAt <= clock.UtcNow)
            {
                throw AppException.Unauthorized(ErrorCodes.AuthChallengeInvalid,
                    "That verification link is invalid or has expired.");
            }

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE auth_challenges SET consumed_at = now() WHERE id = @Id",
                new { challenge.Id }, tx, cancellationToken: ct));

            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE users
                   SET is_email_verified = true,
                       status = CASE WHEN status = 'pending_verification' THEN 'active' ELSE status END,
                       updated_at = now()
                 WHERE id = @UserId
                """, new { challenge.UserId }, tx, cancellationToken: ct));

            _ = ctx;
        }, ct);
    }

    // ── token minting ────────────────────────────────────────────────────────

    public static string[] ScopesFor(string clientKind, bool isDevice) => clientKind switch
    {
        ClientKinds.Legacy => [.. Scopes.LegacyCompat],
        ClientKinds.DesktopAgent when isDevice => [.. Scopes.DeviceSession],
        _ => [.. Scopes.UserSession],
    };

    /// <summary>
    /// Issues a session for a user whose identity has already been proven by a
    /// means other than a password — today, a passkey assertion (ADR-0010).
    /// </summary>
    public async Task<TokenPairResponse> IssueSessionForVerifiedUserAsync(
        long userId, string clientKind, string clientVersion, RequestContext ctx, CancellationToken ct = default)
    {
        if (!ClientKinds.IsValid(clientKind))
        {
            throw AppException.Validation("Unknown clientKind.", new ErrorDetail("clientKind", "unsupported"));
        }

        await using var connection = await db.OpenAsync(ct);
        var user = await LoadUserAsync(connection, null, userId, ct)
            ?? throw AppException.Unauthorized(ErrorCodes.AuthInvalidCredentials, "That account is not available.");

        if (user.DeletedAt is not null || user.Status == UserStatuses.Suspended)
        {
            throw AppException.Unauthorized(ErrorCodes.AuthInvalidCredentials, "That account is not available.");
        }

        await audit.WriteAsync(userId, SecurityEventNames.LoginSucceeded, true, ctx,
            new { clientKind, method = "passkey" }, ct);

        return await IssueSessionAsync(user, null, clientKind, clientVersion, ctx, ct);
    }

    private async Task<TokenPairResponse> IssueSessionAsync(
        UserRow user, LoginRequest? request, string clientKind, string clientVersion, RequestContext ctx, CancellationToken ct)
    {
        _ = request;
        return await db.InTransactionAsync((connection, tx) =>
            MintPairAsync(connection, tx, user, clientKind, clientVersion, null, Guid.CreateVersion7(),
                ScopesFor(clientKind, false), ctx, ct), ct);
    }

    /// <summary>
    /// Mints an access/refresh pair inside an existing transaction. Device
    /// sessions pass <paramref name="deviceId"/>, which is what constrains the
    /// scopes to <c>command:receive</c> and <c>command:ack</c>.
    /// </summary>
    public async Task<TokenPairResponse> MintPairAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? tx,
        UserRow user,
        string clientKind,
        string clientVersion,
        long? deviceId,
        Guid familyId,
        string[] scopes,
        RequestContext ctx,
        CancellationToken ct)
    {
        Guid? devicePublicId = null;
        if (deviceId is { } id)
        {
            devicePublicId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT public_id FROM devices WHERE id = @Id", new { Id = id }, tx, cancellationToken: ct));
        }

        var access = tokens.IssueAccessToken(new AccessTokenRequest
        {
            Subject = user.PublicId,
            ClientKind = clientKind,
            Scopes = scopes,
            DeviceId = devicePublicId,
            FamilyId = familyId.ToString(),
        });

        var (refreshToken, refreshHash) = tokens.CreateOpaqueToken();

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO refresh_tokens
                (token_hash, family_id, user_id, device_id, client_kind, client_version, user_agent, ip_first_seen, expires_at)
            VALUES
                (@Hash, @FamilyId, @UserId, @DeviceId, @ClientKind, @ClientVersion, @UserAgent, @Ip::inet, @ExpiresAt)
            """,
            new
            {
                Hash = refreshHash,
                FamilyId = familyId,
                UserId = user.Id,
                DeviceId = deviceId,
                ClientKind = clientKind,
                ClientVersion = clientVersion.Length > 32 ? clientVersion[..32] : clientVersion,
                UserAgent = ctx.UserAgent.Length > 255 ? ctx.UserAgent[..255] : ctx.UserAgent,
                Ip = ctx.IpAddress,
                ExpiresAt = clock.UtcNow.Add(tokens.RefreshTokenLifetime),
            }, tx, cancellationToken: ct));

        return new TokenPairResponse(
            access.Token,
            (int)tokens.AccessTokenLifetime.TotalSeconds,
            refreshToken,
            "Bearer",
            scopes,
            ToProfile(user));
    }

    /// <summary>
    /// Mints the long-lived compatibility credential the legacy shim hands back
    /// as an "API key". It lives in <c>refresh_tokens</c> with
    /// <c>client_kind='legacy'</c> so it is visible in the session list,
    /// revocable, countable, and dies with the shim (ADR-0008).
    /// </summary>
    public async Task<string> IssueLegacyCompatTokenAsync(long userId, RequestContext ctx, CancellationToken ct = default)
    {
        var (token, hash) = tokens.CreateOpaqueToken();

        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO refresh_tokens
                (token_hash, family_id, user_id, client_kind, client_version, user_agent, ip_first_seen, expires_at)
            VALUES
                (@Hash, @FamilyId, @UserId, 'legacy', 'shim', @UserAgent, @Ip::inet, @ExpiresAt)
            """,
            new
            {
                Hash = hash,
                FamilyId = Guid.CreateVersion7(),
                UserId = userId,
                UserAgent = ctx.UserAgent.Length > 255 ? ctx.UserAgent[..255] : ctx.UserAgent,
                Ip = ctx.IpAddress,
                ExpiresAt = clock.UtcNow.AddDays(365),
            }, cancellationToken: ct));

        return token;
    }

    /// <summary>Resolves a legacy API key to a caller, for the shim only.</summary>
    public async Task<CallerIdentity?> ResolveLegacyApiKeyAsync(string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var hash = tokens.HashOpaqueToken(apiKey);

        await using var connection = await db.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<LegacyKeyRow>(new CommandDefinition("""
            SELECT r.user_id AS UserId, u.public_id AS UserPublicId, r.family_id AS FamilyId
              FROM refresh_tokens r
              JOIN users u ON u.id = r.user_id
             WHERE r.token_hash = @Hash
               AND r.client_kind = 'legacy'
               AND r.revoked_at IS NULL
               AND r.expires_at > now()
               AND u.deleted_at IS NULL
               AND u.status <> 'suspended'
            """, new { Hash = hash }, cancellationToken: ct));

        if (row is null)
        {
            return null;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE refresh_tokens SET last_used_at = now() WHERE token_hash = @Hash",
            new { Hash = hash }, cancellationToken: ct));

        return new CallerIdentity
        {
            UserId = row.UserId,
            UserPublicId = row.UserPublicId,
            ClientKind = ClientKinds.Legacy,
            Scopes = Scopes.LegacyCompat,
            TokenId = $"legacy:{row.FamilyId}",
        };
    }

    // ── lookups used by the auth middleware and other contexts ───────────────

    public async Task<CallerIdentity?> ResolveUserTokenAsync(Guid userPublicId, string clientKind, IReadOnlyList<string> scopes, string jti, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<(long Id, string Status, DateTimeOffset? DeletedAt)>(
            new CommandDefinition("SELECT id, status, deleted_at FROM users WHERE public_id = @PublicId",
                new { PublicId = userPublicId }, cancellationToken: ct));

        if (row.Id == 0 || row.DeletedAt is not null || row.Status == UserStatuses.Suspended)
        {
            return null;
        }

        return new CallerIdentity
        {
            UserId = row.Id,
            UserPublicId = userPublicId,
            ClientKind = clientKind,
            Scopes = scopes,
            TokenId = jti,
        };
    }

    /// <summary>Used by the legacy shim, which identifies an account by the login it was given.</summary>
    public async Task<long?> FindUserIdByLoginAsync(string login, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var user = await FindUserByLoginAsync(connection, null, login.Trim(), ct);
        return user?.Id;
    }

    public async Task<ProfileResponse?> GetProfileAsync(long userId, CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        var user = await LoadUserAsync(connection, null, userId, ct);
        return user is null ? null : ToProfile(user);
    }

    public async Task<ProfileResponse> UpdateProfileAsync(long userId, UpdateProfileRequest request, CancellationToken ct = default)
    {
        if (request.Timezone is not null &&
            Normalise.IanaTimeZoneOrDefault(request.Timezone, " ") == " ")
        {
            throw AppException.Unprocessable(ErrorCodes.ReminderTimezoneInvalid,
                $"'{request.Timezone}' is not a known IANA timezone.");
        }

        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE users
               SET display_name = COALESCE(@DisplayName, display_name),
                   timezone = COALESCE(@Timezone, timezone),
                   locale = COALESCE(@Locale, locale),
                   is_marketing_opt_in = COALESCE(@Marketing, is_marketing_opt_in),
                   updated_at = now()
             WHERE id = @UserId AND deleted_at IS NULL
            """,
            new
            {
                request.DisplayName,
                request.Timezone,
                request.Locale,
                Marketing = request.MarketingOptIn,
                UserId = userId,
            }, cancellationToken: ct));

        var user = await LoadUserAsync(connection, null, userId, ct)
            ?? throw AppException.NotFound(ErrorCodes.AccountNotFound, "No such account.");
        return ToProfile(user);
    }

    /// <summary>
    /// Soft-deletes now and schedules the hard delete for 30 days later. Every
    /// session ends immediately, so the account is unusable from this moment even
    /// though the rows survive the grace period (03 §8).
    /// </summary>
    public async Task DeleteAccountAsync(long userId, RequestContext ctx, CancellationToken ct = default)
    {
        await db.InTransactionAsync(async (connection, tx) =>
        {
            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE users SET deleted_at = now(), status = 'suspended', updated_at = now()
                 WHERE id = @UserId AND deleted_at IS NULL
                """, new { UserId = userId }, tx, cancellationToken: ct));

            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE refresh_tokens SET revoked_at = now(), revoked_reason = 'admin'
                 WHERE user_id = @UserId AND revoked_at IS NULL
                """, new { UserId = userId }, tx, cancellationToken: ct));

            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE devices SET status = 'revoked', revoked_at = now(), updated_at = now()
                 WHERE user_id = @UserId AND status <> 'revoked'
                """, new { UserId = userId }, tx, cancellationToken: ct));

            await audit.WriteInTransactionAsync(connection, tx, userId,
                SecurityEventNames.AccountDeleted, true, ctx, null, ct);
        }, ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    internal const string UserSelectSql = """
        SELECT u.id AS Id, u.public_id AS PublicId, u.email AS Email, u.email_normalised AS EmailNormalised,
               u.is_email_verified AS IsEmailVerified, u.username AS Username, u.display_name AS DisplayName,
               u.timezone AS Timezone, u.locale AS Locale, u.status AS Status,
               u.is_marketing_opt_in AS IsMarketingOptIn, u.created_at AS CreatedAt, u.deleted_at AS DeletedAt,
               c.algo AS Algo, c.password_hash AS PasswordHash, c.locked_until AS LockedUntil,
               c.failed_attempts AS FailedAttempts
          FROM users u
          LEFT JOIN user_credentials c ON c.user_id = u.id
        """;

    internal static async Task<UserRow?> LoadUserAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, long userId, CancellationToken ct) =>
        await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            UserSelectSql + " WHERE u.id = @Id", new { Id = userId }, tx, cancellationToken: ct));

    private static async Task<UserRow?> FindUserByLoginAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, string login, CancellationToken ct)
    {
        var normalised = Normalise.LooksLikeEmail(login) ? Normalise.Email(login) : Normalise.Username(login);
        return await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
            UserSelectSql + """
             WHERE (u.username_normalised = @Value OR u.email_normalised = @Value)
               AND u.deleted_at IS NULL
            """, new { Value = normalised }, tx, cancellationToken: ct));
    }

    public static ProfileResponse ToProfile(UserRow user) => new(
        user.PublicId.ToString(),
        user.Username,
        user.Email,
        user.IsEmailVerified,
        user.DisplayName,
        user.Timezone,
        user.Locale,
        user.Status,
        user.IsMarketingOptIn,
        user.CreatedAt);

    private async Task RecordFailedAttemptAsync(long userId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var attempts = await connection.ExecuteScalarAsync<int>(new CommandDefinition("""
            UPDATE user_credentials
               SET failed_attempts = failed_attempts + 1, updated_at = now()
             WHERE user_id = @UserId
            RETURNING failed_attempts
            """, new { UserId = userId }, cancellationToken: ct));

        if (attempts >= _options.MaxFailedAttemptsBeforeLockout)
        {
            // Exponential, capped. Doubling from the base means a bot's tenth
            // attempt costs it half an hour and a person's typo costs 30 seconds.
            var over = attempts - _options.MaxFailedAttemptsBeforeLockout;
            var seconds = Math.Min(_options.LockoutMaxSeconds, _options.LockoutBaseSeconds * Math.Pow(2, over));

            await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE user_credentials SET locked_until = now() + make_interval(secs => @Seconds) WHERE user_id = @UserId
                """, new { Seconds = seconds, UserId = userId }, cancellationToken: ct));
        }
    }

    private async Task ClearFailedAttemptsAsync(long userId, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE user_credentials SET failed_attempts = 0, locked_until = NULL, updated_at = now()
             WHERE user_id = @UserId AND (failed_attempts <> 0 OR locked_until IS NOT NULL)
            """, new { UserId = userId }, cancellationToken: ct));
    }

    private async Task AssertNotBreachedAsync(string? password, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(password))
        {
            return;
        }

        if (await breachedPasswords.IsBreachedAsync(password, ct))
        {
            throw new AppException(ErrorCodes.AuthPasswordPolicy,
                "That password has appeared in a public data breach. Choose a different one.",
                System.Net.HttpStatusCode.UnprocessableEntity,
                [new ErrorDetail("password", "breached")]);
        }
    }

    private static AppException InvalidCredentials() =>
        AppException.Unauthorized(ErrorCodes.AuthInvalidCredentials, "That username or password is not correct.");

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a ?? string.Empty),
            Encoding.UTF8.GetBytes(b ?? string.Empty));

    // ── row shapes ───────────────────────────────────────────────────────────

    public sealed record UserRow
    {
        public long Id { get; init; }
        public Guid PublicId { get; init; }
        public string Email { get; init; } = string.Empty;
        public string EmailNormalised { get; init; } = string.Empty;
        public bool IsEmailVerified { get; init; }
        public string Username { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public string Timezone { get; init; } = "Etc/UTC";
        public string Locale { get; init; } = "en-GB";
        public string Status { get; init; } = UserStatuses.Active;
        public bool IsMarketingOptIn { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? DeletedAt { get; init; }
        public string Algo { get; init; } = PasswordAlgorithms.Argon2id;
        public string PasswordHash { get; init; } = string.Empty;
        public DateTimeOffset? LockedUntil { get; init; }
        public int FailedAttempts { get; init; }
    }

    private sealed record RefreshRow
    {
        public long Id { get; init; }
        public byte[] TokenHash { get; init; } = [];
        public Guid FamilyId { get; init; }
        public long UserId { get; init; }
        public long? DeviceId { get; init; }
        public string ClientKind { get; init; } = ClientKinds.Mobile;
        public string ClientVersion { get; init; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; init; }
        public DateTimeOffset? RevokedAt { get; init; }
        public string? RevokedReason { get; init; }
    }

    private sealed record ChallengeRow
    {
        public long Id { get; init; }
        public long UserId { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
        public DateTimeOffset? ConsumedAt { get; init; }
    }

    private sealed record SessionRow
    {
        public Guid FamilyId { get; init; }
        public string ClientKind { get; init; } = string.Empty;
        public string ClientVersion { get; init; } = string.Empty;
        public Guid? DevicePublicId { get; init; }
        public string? Ip { get; init; }
        public DateTimeOffset IssuedAt { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }
        public DateTimeOffset ExpiresAt { get; init; }
    }

    private sealed record LegacyKeyRow
    {
        public long UserId { get; init; }
        public Guid UserPublicId { get; init; }
        public Guid FamilyId { get; init; }
    }
}

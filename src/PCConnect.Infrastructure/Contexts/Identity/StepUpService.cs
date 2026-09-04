using System.Security.Cryptography;
using Dapper;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using PCConnect.Core.Domain;
using PCConnect.Infrastructure.Caching;
using PCConnect.Infrastructure.Data;
using PCConnect.Infrastructure.Security;

namespace PCConnect.Infrastructure.Contexts.Identity;

public static class StepUpMethods
{
    public const string Passkey = "passkey";
    public const string Password = "password";
}

/// <summary>
/// Risk-tiered step-up (ADR-0011).
///
/// A valid access token is enough to lock a screen. It is not enough to power a
/// machine off: a destructive command requires a fresh proof of the human, made
/// within the last five minutes, single-use, and bound to the account.
/// </summary>
public sealed class StepUpService(
    Db db,
    Core.IPasswordHasher hasher,
    ITokenIssuer tokens,
    IClock clock,
    ICacheStore cache,
    RateLimiter limiter,
    WebAuthnService webAuthn,
    SecurityEventLog audit,
    TokenService tokenService)
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    public async Task<StepUpChallengeResponse> BeginAsync(CallerIdentity caller, CancellationToken ct = default)
    {
        await limiter.ConsumeAsync(RateBudgets.StepUpPerUser, caller.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);

        var methods = new List<string>();
        PasskeyAssertionOptions? passkeyOptions = null;

        if (await webAuthn.HasPasskeyAsync(caller.UserId, ct))
        {
            methods.Add(StepUpMethods.Passkey);
            passkeyOptions = await webAuthn.BeginAssertionAsync(caller.UserId, "step_up", ct);
        }

        methods.Add(StepUpMethods.Password);

        var challengeId = passkeyOptions?.ChallengeId ?? $"pw.{Guid.CreateVersion7():N}";

        return new StepUpChallengeResponse(
            challengeId,
            methods,
            passkeyOptions,
            (int)TokenLifetime.TotalSeconds);
    }

    public async Task<StepUpTokenResponse> VerifyAsync(
        CallerIdentity caller, StepUpVerifyRequest request, RequestContext ctx, CancellationToken ct = default)
    {
        await limiter.ConsumeAsync(RateBudgets.StepUpPerUser, caller.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);

        switch (request.Method)
        {
            case StepUpMethods.Passkey:
            {
                if (request.Passkey is null)
                {
                    throw AppException.Validation("A passkey assertion is required for this method.",
                        new ErrorDetail("passkey", "required"));
                }

                var result = await webAuthn.CompleteAssertionAsync(request.Passkey, "step_up", caller.UserId, ctx, ct);
                if (result.UserId != caller.UserId)
                {
                    throw AppException.Forbidden(ErrorCodes.AuthStepUpInvalid, "That passkey belongs to another account.");
                }

                return await MintAsync(caller, StepUpMethods.Passkey, ctx, ct);
            }

            case StepUpMethods.Password:
            {
                if (string.IsNullOrEmpty(request.Password))
                {
                    throw AppException.Validation("A password is required for this method.",
                        new ErrorDetail("password", "required"));
                }

                await using var connection = await db.OpenAsync(ct);
                var user = await IdentityService.LoadUserAsync(connection, null, caller.UserId, ct)
                    ?? throw AppException.NotFound(ErrorCodes.AccountNotFound, "No such account.");

                var ok = user.Algo == PasswordAlgorithms.Argon2id
                    ? hasher.Verify(request.Password, user.PasswordHash)
                    : CryptographicOperations.FixedTimeEquals(
                        System.Text.Encoding.UTF8.GetBytes(Normalise.Sha256Hex(request.Password)),
                        System.Text.Encoding.UTF8.GetBytes(user.PasswordHash));

                if (!ok)
                {
                    await audit.WriteAsync(caller.UserId, SecurityEventNames.StepUpFailed, false, ctx,
                        new { method = StepUpMethods.Password }, ct);
                    throw AppException.Unauthorized(ErrorCodes.AuthStepUpInvalid, "That password is not correct.");
                }

                return await MintAsync(caller, StepUpMethods.Password, ctx, ct);
            }

            default:
                throw AppException.Validation("Unknown step-up method.", new ErrorDetail("method", "unsupported"));
        }
    }

    private async Task<StepUpTokenResponse> MintAsync(CallerIdentity caller, string method, RequestContext ctx, CancellationToken ct)
    {
        var issued = tokens.IssueStepUpToken(caller.UserPublicId, method);

        // Recorded as unused so that redeeming it is one-shot: a step-up proves a
        // human confirmed one action, not that a token may confirm many.
        await cache.SetAsync(CacheKeys.StepUpToken(issued.TokenId), method, TokenLifetime, ct);
        await audit.WriteAsync(caller.UserId, SecurityEventNames.StepUpSucceeded, true, ctx, new { method }, ct);

        return new StepUpTokenResponse(issued.Token, (int)TokenLifetime.TotalSeconds, method);
    }

    /// <summary>
    /// Redeems a step-up token for one destructive command. Returns the method
    /// that satisfied it. Throws if the token is missing, wrong, for another
    /// account, expired, or already spent.
    /// </summary>
    public async Task<string> RedeemAsync(CallerIdentity caller, string? stepUpToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(stepUpToken))
        {
            throw AppException.Forbidden(ErrorCodes.AuthStepUpRequired,
                "This action needs to be confirmed. Start a step-up challenge and try again.");
        }

        var handler = new JsonWebTokenHandler();
        var parameters = tokenService.CreateValidationParameters();

        var validation = await handler.ValidateTokenAsync(stepUpToken, parameters);
        if (!validation.IsValid)
        {
            throw AppException.Forbidden(ErrorCodes.AuthStepUpInvalid, "That confirmation is not valid. Confirm again.");
        }

        var jwt = (JsonWebToken)validation.SecurityToken;

        if (!jwt.TryGetPayloadValue<string>(PcConnectClaims.Purpose, out var purpose) ||
            !string.Equals(purpose, PcConnectClaims.StepUpPurpose, StringComparison.Ordinal))
        {
            // An access token is not a confirmation. Without this check, holding a
            // session would silently satisfy step-up and the control would be
            // decorative.
            throw AppException.Forbidden(ErrorCodes.AuthStepUpInvalid, "That token is not a confirmation token.");
        }

        if (!string.Equals(jwt.Subject, caller.UserPublicId.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            throw AppException.Forbidden(ErrorCodes.AuthStepUpInvalid, "That confirmation belongs to another account.");
        }

        var jti = jwt.Id;
        var method = await cache.GetAsync(CacheKeys.StepUpToken(jti), ct);
        if (method is null)
        {
            throw AppException.Forbidden(ErrorCodes.AuthStepUpInvalid,
                "That confirmation has already been used or has expired. Confirm again.");
        }

        await cache.RemoveAsync(CacheKeys.StepUpToken(jti), ct);
        return method;
    }

    /// <summary>
    /// Housekeeping for expired ceremonies; called by the worker's retention job
    /// rather than left to accumulate.
    /// </summary>
    public async Task<int> PurgeExpiredChallengesAsync(CancellationToken ct = default)
    {
        await using var connection = await db.OpenAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM webauthn_challenges WHERE expires_at < now() - interval '1 day'
            """, cancellationToken: ct)) +
            await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM auth_challenges WHERE expires_at < now() - interval '7 days'
            """, cancellationToken: ct));
    }

    private readonly IClock _clock = clock;
}

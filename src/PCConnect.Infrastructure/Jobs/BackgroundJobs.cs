using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using PCConnect.Infrastructure.Contexts.Commands;
using PCConnect.Infrastructure.Contexts.Identity;
using PCConnect.Infrastructure.Contexts.Reminders;
using PCConnect.Infrastructure.Data;

namespace PCConnect.Infrastructure.Jobs;

public sealed class WorkerOptions
{
    /// <summary>
    /// How often expired commands are swept. This is the interval between a
    /// command's TTL passing and the phone being told it was not delivered, so
    /// it is short — the work is one indexed query against a partial index.
    /// </summary>
    public int CommandSweepSeconds { get; set; } = 5;

    public int ReminderTickSeconds { get; set; } = 30;

    public int RecurrenceHorizonMinutes { get; set; } = 60;

    public int RetentionHours { get; set; } = 24;

    public int CommandEventRetentionDays { get; set; } = 90;

    public int SecurityEventRetentionDays { get; set; } = 90;

    public int SoftDeleteGraceDays { get; set; } = 30;

    /// <summary>
    /// A leader lock so two worker replicas do not both sweep. Cache-backed, so
    /// it degrades to "both run" if the cache is down — which is safe, because
    /// every job is idempotent, but noisier.
    /// </summary>
    public bool UseLeaderLock { get; set; } = true;
}

/// <summary>
/// Shared plumbing: a fixed-interval loop that survives a failing tick, logs it,
/// and keeps running. A background job that dies silently on the first bad row
/// is worse than no background job.
/// </summary>
public abstract class IntervalJob(IServiceProvider services, ILogger logger) : BackgroundService
{
    protected abstract TimeSpan Interval { get; }

    protected abstract string Name { get; }

    protected abstract Task RunOnceAsync(IServiceProvider scope, CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("{Job} starting with a {Interval} interval", Name, Interval);

        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                await RunOnceAsync(scope.ServiceProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Job} tick failed; continuing", Name);
            }

            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("{Job} stopped", Name);
    }
}

/// <summary>
/// Marks commands past their TTL as expired and tells the issuing client.
///
/// This job is what turns "a command is never executed after its TTL" from an
/// intention into an observable fact: without it, a command sits `issued`
/// forever and V4 fails (S2-03).
/// </summary>
public sealed class CommandExpiryJob(
    IServiceProvider services,
    IOptions<WorkerOptions> options,
    ILogger<CommandExpiryJob> logger) : IntervalJob(services, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(options.Value.CommandSweepSeconds);

    protected override string Name => "command-expiry";

    protected override async Task RunOnceAsync(IServiceProvider scope, CancellationToken ct)
    {
        var commands = scope.GetRequiredService<CommandService>();
        var expired = await commands.ExpireDueAsync(500, ct);

        if (expired.Count > 0)
        {
            logger.LogInformation("Expired {Count} command(s)", expired.Count);
        }
    }
}

/// <summary>
/// Delivers reminders that have come due, and keeps the materialised recurrence
/// horizon topped up.
/// </summary>
public sealed class ReminderSchedulerJob(
    IServiceProvider services,
    IOptions<WorkerOptions> options,
    ILogger<ReminderSchedulerJob> logger) : IntervalJob(services, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromSeconds(options.Value.ReminderTickSeconds);

    protected override string Name => "reminder-scheduler";

    protected override async Task RunOnceAsync(IServiceProvider scope, CancellationToken ct)
    {
        var reminders = scope.GetRequiredService<ReminderService>();
        var realtime = scope.GetRequiredService<IRealtimeNotifier>();

        // A horizon slightly ahead of the tick, so a reminder due between ticks
        // is delivered on time rather than one tick late.
        var due = await reminders.ClaimDueAsync(Interval, 200, ct);

        foreach (var reminder in due)
        {
            await realtime.ReminderDueAsync(reminder.UserPublicId,
                new ReminderDueEvent(reminder.PublicId.ToString(), reminder.Body, reminder.DueAt), ct);
        }

        if (due.Count > 0)
        {
            logger.LogInformation("Delivered {Count} due reminder(s)", due.Count);
        }
    }
}

public sealed class RecurrenceHorizonJob(
    IServiceProvider services,
    IOptions<WorkerOptions> options,
    ILogger<RecurrenceHorizonJob> logger) : IntervalJob(services, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(options.Value.RecurrenceHorizonMinutes);

    protected override string Name => "recurrence-horizon";

    protected override async Task RunOnceAsync(IServiceProvider scope, CancellationToken ct)
    {
        var written = await scope.GetRequiredService<ReminderService>().ExtendRecurrenceHorizonAsync(ct);

        if (written > 0)
        {
            logger.LogInformation("Materialised {Count} new recurrence occurrence(s)", written);
        }
    }
}

/// <summary>
/// Retention. Every table that grows without bound has a line here; the
/// alternative is a database that fills up quietly two years from now.
/// </summary>
public sealed class RetentionJob(
    IServiceProvider services,
    IOptions<WorkerOptions> options,
    ILogger<RetentionJob> logger) : IntervalJob(services, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromHours(options.Value.RetentionHours);

    protected override string Name => "retention";

    protected override async Task RunOnceAsync(IServiceProvider scope, CancellationToken ct)
    {
        var settings = options.Value;
        var db = scope.GetRequiredService<Db>();
        var commands = scope.GetRequiredService<CommandService>();
        var stepUp = scope.GetRequiredService<StepUpService>();

        var events = await commands.PurgeOldEventsAsync(settings.CommandEventRetentionDays, ct);
        var challenges = await stepUp.PurgeExpiredChallengesAsync(ct);

        await using var connection = await db.OpenAsync(ct);

        var tokens = await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM refresh_tokens
             WHERE expires_at < now() - interval '30 days'
                OR (revoked_at IS NOT NULL AND revoked_at < now() - interval '30 days')
            """, cancellationToken: ct));

        var idempotency = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM idempotency_keys WHERE expires_at < now()", cancellationToken: ct));

        // Security events keep their shape but lose the IP: the audit value is
        // in the decision and the account, not in an address kept indefinitely.
        var anonymised = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE security_events
               SET source_ip = NULL, user_agent = ''
             WHERE occurred_at < now() - make_interval(days => @Days)
               AND (source_ip IS NOT NULL OR user_agent <> '')
            """, new { Days = settings.SecurityEventRetentionDays }, cancellationToken: ct));

        // The grace period on account deletion expires here. The cascade is
        // asserted by a test: an account deletion that leaves rows behind is a
        // GDPR failure, not an untidiness (03 §8).
        var purgedAccounts = await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM users
             WHERE deleted_at IS NOT NULL
               AND deleted_at < now() - make_interval(days => @Days)
            """, new { Days = settings.SoftDeleteGraceDays }, cancellationToken: ct));

        var purgedReminders = await connection.ExecuteAsync(new CommandDefinition("""
            DELETE FROM reminders
             WHERE deleted_at IS NOT NULL
               AND deleted_at < now() - make_interval(days => @Days)
            """, new { Days = settings.SoftDeleteGraceDays }, cancellationToken: ct));

        logger.LogInformation(
            "Retention: {Events} command events, {Challenges} challenges, {Tokens} tokens, " +
            "{Idempotency} idempotency keys, {Anonymised} security events anonymised, " +
            "{Accounts} accounts and {Reminders} reminders hard-deleted",
            events, challenges, tokens, idempotency, anonymised, purgedAccounts, purgedReminders);
    }
}

/// <summary>
/// Runs the verification gates continuously rather than only at migration time.
///
/// V4 — "no command outlives its TTL unresolved" — must be zero forever, not
/// just during the migration; if it is ever non-zero, the expiry sweep has
/// stopped and a stale shutdown is waiting to be executed.
/// </summary>
public sealed class VerificationJob(
    IServiceProvider services,
    ILogger<VerificationJob> logger) : IntervalJob(services, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(15);

    protected override string Name => "verification";

    protected override async Task RunOnceAsync(IServiceProvider scope, CancellationToken ct)
    {
        var db = scope.GetRequiredService<Db>();
        await using var connection = await db.OpenAsync(ct);

        var stale = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT count(*) FROM commands
             WHERE status IN ('issued','delivered') AND expires_at < now() - interval '1 minute'
            """, cancellationToken: ct));

        if (stale > 0)
        {
            logger.LogError(
                "VERIFICATION V4 FAILED: {Count} command(s) are past their TTL and still pending. " +
                "The expiry sweep is not running.", stale);
        }

        var staleExecutions = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT count(*) FROM commands
             WHERE status = 'succeeded' AND acked_at IS NOT NULL AND acked_at > expires_at
            """, cancellationToken: ct));

        if (staleExecutions > 0)
        {
            logger.LogCritical(
                "VERIFICATION V5 FAILED: {Count} command(s) were executed after expiry. " +
                "A computer may have been powered off without its owner asking.", staleExecutions);
        }

        var unconfirmed = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            SELECT count(*) FROM commands WHERE risk_tier = 'destructive' AND step_up_verified_at IS NULL
            """, cancellationToken: ct));

        if (unconfirmed > 0)
        {
            logger.LogCritical(
                "VERIFICATION V6 FAILED: {Count} destructive command(s) exist with no recorded step-up.",
                unconfirmed);
        }
    }
}

public static class JobRegistration
{
    public static IServiceCollection AddPcConnectJobs(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<WorkerOptions>(configuration.GetSection("Worker"));
        services.AddHostedService<CommandExpiryJob>();
        services.AddHostedService<ReminderSchedulerJob>();
        services.AddHostedService<RecurrenceHorizonJob>();
        services.AddHostedService<RetentionJob>();
        services.AddHostedService<VerificationJob>();
        return services;
    }
}

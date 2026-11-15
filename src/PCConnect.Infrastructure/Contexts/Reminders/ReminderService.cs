using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Security.Cryptography;
using Dapper;
using Npgsql;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using PCConnect.Core.Domain;
using PCConnect.Infrastructure.Data;
using PCConnect.Infrastructure.Recurrence;

namespace PCConnect.Infrastructure.Contexts.Reminders;

/// <summary>
/// The reminders bounded context. It owns the encrypted body, the UTC instant
/// and the recurrence; it does not send notifications (that is the worker's job
/// through <see cref="IRealtimeNotifier"/>).
/// </summary>
public sealed class ReminderService(
    Db db,
    IEnvelopeEncryptor envelope,
    IClock clock,
    IRealtimeNotifier realtime,
    RecurrenceExpander recurrence,
    ILogger<ReminderService> logger)
{
    public const int MaxBodyLength = 2000;
    private const int MaxPageSize = 200;

    // ── read ─────────────────────────────────────────────────────────────────

    public async Task<Page<ReminderResponse>> ListAsync(
        CallerIdentity caller,
        DateTimeOffset? from,
        DateTimeOffset? to,
        bool? completed,
        string? cursor,
        int limit,
        CancellationToken ct = default)
    {
        caller.Require(Scopes.ReminderRead);

        var take = Math.Clamp(limit <= 0 ? 50 : limit, 1, MaxPageSize);
        var before = Contexts.Commands.Cursor.Decode(cursor);

        await using var connection = await db.OpenAsync(ct);
        var rows = (await connection.QueryAsync<ReminderRow>(new CommandDefinition(
            SelectSql + """
             WHERE r.user_id = @UserId
               AND r.deleted_at IS NULL
               AND (@From::timestamptz IS NULL OR r.due_at_utc >= @From::timestamptz)
               AND (@To::timestamptz   IS NULL OR r.due_at_utc <= @To::timestamptz)
               AND (@Completed::boolean IS NULL OR r.is_completed = @Completed::boolean)
               AND (@BeforeId::bigint IS NULL OR r.id < @BeforeId::bigint)
             ORDER BY r.id DESC
             LIMIT @Take
            """,
            new
            {
                UserId = caller.UserId,
                From = from,
                To = to,
                Completed = completed,
                BeforeId = before,
                Take = take + 1,
            }, cancellationToken: ct))).ToList();

        var hasMore = rows.Count > take;
        var page = rows.Take(take).ToList();
        var dek = page.Count == 0 ? null : await TryLoadDataKeyAsync(connection, caller.UserId, ct);

        // One query for the whole page. Asking per row is the N+1 that turns a
        // fifty-item list into fifty-one round trips.
        var targets = await LoadTargetsAsync(connection, null, page.Select(r => r.Id).ToList(), ct);

        try
        {
            return new Page<ReminderResponse>(
                page.Select(r => ToResponse(r, dek, targets.GetValueOrDefault(r.Id))).ToList(),
                hasMore ? Contexts.Commands.Cursor.Encode(page[^1].Id) : null);
        }
        finally
        {
            ZeroKey(dek);
        }
    }

    public async Task<ReminderResponse> GetAsync(CallerIdentity caller, Guid reminderId, CancellationToken ct = default)
    {
        caller.Require(Scopes.ReminderRead);

        await using var connection = await db.OpenAsync(ct);
        var row = await LoadOwnedAsync(connection, null, caller.UserId, reminderId, ct);
        var dek = await TryLoadDataKeyAsync(connection, caller.UserId, ct);
        var targets = await LoadTargetsAsync(connection, null, [row.Id], ct);

        try
        {
            return ToResponse(row, dek, targets.GetValueOrDefault(row.Id));
        }
        finally
        {
            ZeroKey(dek);
        }
    }

    // ── write ────────────────────────────────────────────────────────────────

    public async Task<ReminderResponse> CreateAsync(
        CallerIdentity caller, CreateReminderRequest request, CancellationToken ct = default)
    {
        caller.Require(Scopes.ReminderWrite);
        ValidateBody(request.Body);
        ValidateTimezoneIfProvided(request.Timezone);
        var wantedDevices = ParseTargetDevices(request.DeviceIds);
        var rrule = ValidateRrule(request.Rrule, request.DueAt);

        var response = await db.InTransactionAsync(async (connection, tx) =>
        {
            var timezone = await ResolveTimezoneAsync(connection, tx, caller.UserId, request.Timezone, ct);

            var dek = await LoadDataKeyAsync(connection, tx, caller.UserId, create: true, ct)!;
            try
            {
                var dueAt = request.DueAt.ToUniversalTime();
                var localTime = ToLocalTime(dueAt, timezone);

                var row = await connection.QuerySingleAsync<ReminderRow>(new CommandDefinition($"""
                    INSERT INTO reminders
                        (user_id, body_ciphertext, body_dek_id, due_at_utc, due_local_time, timezone, rrule, recurrence_until)
                    VALUES
                        (@UserId, @Body, @DekId, @DueAt, @LocalTime, @Timezone, @Rrule, @Until)
                    RETURNING {ReturningColumnsSql}
                    """,
                    new
                    {
                        UserId = caller.UserId,
                        Body = envelope.Encrypt(dek!, request.Body, AssociatedData(caller.UserId)),
                        DekId = envelope.CurrentKekId,
                        DueAt = dueAt,
                        LocalTime = localTime,
                        Timezone = timezone,
                        Rrule = rrule,
                        Until = request.RecurrenceUntil?.ToUniversalTime(),
                    }, tx, cancellationToken: ct));

                var targetIds = await ReplaceTargetsAsync(connection, tx, caller.UserId, row.Id, wantedDevices, ct, isNew: true);

                if (rrule is not null)
                {
                    await MaterialiseOccurrencesAsync(connection, tx, row, ct);
                }

                return ToResponse(row, dek, targetIds);
            }
            finally
            {
                ZeroKey(dek);
            }
        }, ct);

        await realtime.ReminderChangedAsync(caller.UserPublicId,
            new ReminderChangedEvent("created", response, response.Id), ct);

        return response;
    }

    public async Task<ReminderResponse> UpdateAsync(
        CallerIdentity caller, Guid reminderId, UpdateReminderRequest request, CancellationToken ct = default)
    {
        caller.Require(Scopes.ReminderWrite);

        if (request.Body is not null)
        {
            ValidateBody(request.Body);
        }

        ValidateTimezoneIfProvided(request.Timezone);
        var wantedDevices = ParseTargetDevices(request.DeviceIds);

        var response = await db.InTransactionAsync(async (connection, tx) =>
        {
            var row = await LoadOwnedAsync(connection, tx, caller.UserId, reminderId, ct);
            var dek = await LoadDataKeyAsync(connection, tx, caller.UserId, create: true, ct);

            try
            {
                var timezone = request.Timezone is null
                    ? row.Timezone
                    : await ResolveTimezoneAsync(connection, tx, caller.UserId, request.Timezone, ct);

                var dueAt = (request.DueAt ?? row.DueAtUtc).ToUniversalTime();
                var rrule = request.Rrule is null ? row.Rrule : ValidateRrule(request.Rrule, dueAt);
                var until = request.Rrule is null
                    ? (request.RecurrenceUntil?.ToUniversalTime() ?? row.RecurrenceUntil)
                    : (rrule is null ? null : request.RecurrenceUntil?.ToUniversalTime());

                var updated = await connection.QuerySingleAsync<ReminderRow>(new CommandDefinition($"""
                    UPDATE reminders
                       SET body_ciphertext = COALESCE(@Body, body_ciphertext),
                           body_dek_id = CASE WHEN @Body IS NULL THEN body_dek_id ELSE @DekId END,
                           due_at_utc = @DueAt,
                           due_local_time = @LocalTime,
                           timezone = @Timezone,
                           rrule = @Rrule,
                           recurrence_until = @Until,
                           updated_at = now()
                     WHERE id = @Id
                    RETURNING {ReturningColumnsSql}
                    """,
                    new
                    {
                        Body = request.Body is null ? null : envelope.Encrypt(dek!, request.Body, AssociatedData(caller.UserId)),
                        DekId = envelope.CurrentKekId,
                        DueAt = dueAt,
                        LocalTime = ToLocalTime(dueAt, timezone),
                        Timezone = timezone,
                        Rrule = rrule,
                        Until = until,
                        row.Id,
                    }, tx, cancellationToken: ct));

                IReadOnlyList<string>? targetIds;
                if (wantedDevices is null && request.ClearDeviceIds == true)
                {
                    await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM reminder_devices WHERE reminder_id = @ReminderId",
                        new { ReminderId = row.Id }, tx, cancellationToken: ct));
                    targetIds = null;
                }
                else
                {
                    var replacedTargets = await ReplaceTargetsAsync(connection, tx, caller.UserId, row.Id, wantedDevices, ct);
                    targetIds = replacedTargets
                        ?? (await LoadTargetsAsync(connection, tx, [updated.Id], ct)).GetValueOrDefault(updated.Id);
                }

                // The horizon is rebuilt rather than patched: an edited series
                // whose old occurrences survive is how a reminder fires twice.
                await connection.ExecuteAsync(new CommandDefinition("""
                    DELETE FROM reminder_occurrences
                     WHERE reminder_id = @Id AND status = 'pending' AND occurs_at_utc > now()
                    """, new { row.Id }, tx, cancellationToken: ct));

                if (updated.Rrule is not null)
                {
                    await MaterialiseOccurrencesAsync(connection, tx, updated, ct);
                }

                return ToResponse(updated, dek, targetIds);
            }
            finally
            {
                ZeroKey(dek);
            }
        }, ct);

        await realtime.ReminderChangedAsync(caller.UserPublicId,
            new ReminderChangedEvent("updated", response, response.Id), ct);

        return response;
    }

    public async Task<ReminderResponse> CompleteAsync(
        CallerIdentity caller, Guid reminderId, CompleteReminderRequest request, CancellationToken ct = default)
    {
        caller.Require(Scopes.ReminderWrite);

        var response = await db.InTransactionAsync(async (connection, tx) =>
        {
            var row = await LoadOwnedAsync(connection, tx, caller.UserId, reminderId, ct);
            ReminderRow updated;

            if (request.OccurrenceAt is { } occurrenceAt && row.Rrule is not null)
            {
                // Completing one occurrence of a series leaves the series running.
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO reminder_occurrences (reminder_id, occurs_at_utc, status, completed_at)
                    VALUES (@Id, @OccursAt, @Status, @CompletedAt)
                    ON CONFLICT (reminder_id, occurs_at_utc)
                    DO UPDATE SET status = EXCLUDED.status, completed_at = EXCLUDED.completed_at
                    """,
                    new
                    {
                        row.Id,
                        OccursAt = occurrenceAt.ToUniversalTime(),
                        Status = request.Completed ? "completed" : "pending",
                        CompletedAt = request.Completed ? clock.UtcNow : (DateTimeOffset?)null,
                    }, tx, cancellationToken: ct));
                updated = row;
            }
            else
            {
                updated = await connection.QuerySingleAsync<ReminderRow>(new CommandDefinition($"""
                    UPDATE reminders
                       SET is_completed = @Completed,
                           completed_at = CASE WHEN @Completed THEN now() ELSE NULL END,
                           updated_at = now()
                     WHERE id = @Id
                    RETURNING {ReturningColumnsSql}
                    """,
                    new { Completed = request.Completed, row.Id }, tx, cancellationToken: ct));
            }

            var dek = await LoadDataKeyAsync(connection, tx, caller.UserId, create: false, ct);
            try
            {
                var targets = await LoadTargetsAsync(connection, tx, [updated.Id], ct);
                return ToResponse(updated, dek, targets.GetValueOrDefault(updated.Id));
            }
            finally
            {
                ZeroKey(dek);
            }
        }, ct);

        await realtime.ReminderChangedAsync(caller.UserPublicId,
            new ReminderChangedEvent("updated", response, response.Id), ct);

        return response;
    }

    public async Task DeleteAsync(CallerIdentity caller, Guid reminderId, CancellationToken ct = default)
    {
        caller.Require(Scopes.ReminderWrite);

        var (action, updatedReminder) = await db.InTransactionAsync(async (connection, tx) =>
        {
            var row = await LoadOwnedAsync(connection, tx, caller.UserId, reminderId, ct);

            if (string.IsNullOrWhiteSpace(row.Rrule))
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE reminders SET deleted_at = now(), updated_at = now()
                     WHERE id = @Id AND deleted_at IS NULL
                    """, new { row.Id }, tx, cancellationToken: ct));

                return ("deleted", (ReminderResponse?)null);
            }

            var now = clock.UtcNow;

            var storedPastOccurrences = (await connection.QueryAsync<DateTimeOffset>(new CommandDefinition("""
                SELECT occurs_at_utc FROM reminder_occurrences
                 WHERE reminder_id = @Id AND occurs_at_utc < @Now
                 ORDER BY occurs_at_utc ASC
                """, new { row.Id, Now = now }, tx, cancellationToken: ct))).ToList();

            var expandedPastOccurrences = row.DueAtUtc < now
                ? recurrence.Expand(row.Rrule, row.DueAtUtc, row.Timezone, row.DueAtUtc, now.AddSeconds(-1), row.RecurrenceUntil)
                    .Select(static o => o.ToUniversalTime()).ToList()
                : [];

            var allPastInstants = new SortedSet<DateTimeOffset>(
                storedPastOccurrences.Concat(expandedPastOccurrences));

            var hasFutureStored = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("""
                SELECT EXISTS(
                    SELECT 1 FROM reminder_occurrences
                     WHERE reminder_id = @Id AND occurs_at_utc >= @Now
                )
                """, new { row.Id, Now = now }, tx, cancellationToken: ct));

            var hasFutureExpanded = (row.RecurrenceUntil is null || row.RecurrenceUntil >= now) &&
                recurrence.Expand(row.Rrule, row.DueAtUtc, row.Timezone, now, now.AddDays(RecurrenceExpander.HorizonDays), row.RecurrenceUntil).Count > 0;

            var hasFuture = hasFutureStored || hasFutureExpanded;

            if (allPastInstants.Count == 0 || !hasFuture)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    DELETE FROM reminder_occurrences WHERE reminder_id = @Id AND occurs_at_utc >= @Now;
                    UPDATE reminders SET deleted_at = now(), updated_at = now()
                     WHERE id = @Id AND deleted_at IS NULL;
                    """, new { row.Id, Now = now }, tx, cancellationToken: ct));

                return ("deleted", (ReminderResponse?)null);
            }

            // Removing recurring reminders removes future occurrences, but keeps past occurrences.
            await connection.ExecuteAsync(new CommandDefinition("""
                DELETE FROM reminder_occurrences
                 WHERE reminder_id = @Id AND occurs_at_utc >= @Now
                """, new { row.Id, Now = now }, tx, cancellationToken: ct));

            if (allPastInstants.Count > 0)
            {
                await using var command = new NpgsqlCommand("""
                    INSERT INTO reminder_occurrences (reminder_id, occurs_at_utc)
                    SELECT $1, unnest($2)
                    ON CONFLICT (reminder_id, occurs_at_utc) DO NOTHING
                    """, connection, tx);
                command.Parameters.Add(new NpgsqlParameter<long> { Value = row.Id });
                command.Parameters.Add(new NpgsqlParameter<DateTimeOffset[]>
                {
                    NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.TimestampTz,
                    Value = allPastInstants.ToArray(),
                });
                await command.ExecuteNonQueryAsync(ct);
            }

            var untilUtc = allPastInstants.Max;
            if (row.RecurrenceUntil is { } existingUntil && existingUntil < untilUtc)
            {
                untilUtc = existingUntil;
            }

            var updated = await connection.QuerySingleAsync<ReminderRow>(new CommandDefinition($"""
                UPDATE reminders
                   SET recurrence_until = @Until,
                       is_completed = true,
                       completed_at = COALESCE(completed_at, @Now),
                       updated_at = now()
                 WHERE id = @Id
                RETURNING {ReturningColumnsSql}
                """, new { row.Id, Until = untilUtc, Now = now }, tx, cancellationToken: ct));

            var dek = await TryLoadDataKeyAsync(connection, caller.UserId, ct, tx);
            try
            {
                var targets = await LoadTargetsAsync(connection, tx, [updated.Id], ct);
                return ("updated", (ReminderResponse?)ToResponse(updated, dek, targets.GetValueOrDefault(updated.Id)));
            }
            finally
            {
                ZeroKey(dek);
            }
        }, ct);

        if (action == "deleted")
        {
            await realtime.ReminderChangedAsync(caller.UserPublicId,
                new ReminderChangedEvent("deleted", null, reminderId.ToString()), ct);
        }
        else if (updatedReminder is not null)
        {
            await realtime.ReminderChangedAsync(caller.UserPublicId,
                new ReminderChangedEvent("updated", updatedReminder, reminderId.ToString()), ct);
        }
    }

    public async Task<ReminderResponse> RestoreAsync(
        CallerIdentity caller, Guid reminderId, CancellationToken ct = default)
    {
        caller.Require(Scopes.ReminderWrite);

        var response = await db.InTransactionAsync(async (connection, tx) =>
        {
            var restored = await connection.QuerySingleOrDefaultAsync<ReminderRow>(new CommandDefinition($"""
                UPDATE reminders
                   SET deleted_at = NULL,
                       updated_at = now()
                 WHERE public_id = @PublicId AND user_id = @UserId
                RETURNING {ReturningColumnsSql}
                """,
                new { PublicId = reminderId, UserId = caller.UserId }, tx, cancellationToken: ct))
                ?? throw AppException.NotFound(ErrorCodes.ReminderNotFound, "No such reminder.");

            if (restored.Rrule is not null)
            {
                await MaterialiseOccurrencesAsync(connection, tx, restored, ct);
            }

            var dek = await TryLoadDataKeyAsync(connection, caller.UserId, ct, tx);
            try
            {
                var targets = await LoadTargetsAsync(connection, tx, [restored.Id], ct);
                return ToResponse(restored, dek, targets.GetValueOrDefault(restored.Id));
            }
            finally
            {
                ZeroKey(dek);
            }
        }, ct);

        await realtime.ReminderChangedAsync(caller.UserPublicId,
            new ReminderChangedEvent("created", response, response.Id), ct);

        return response;
    }

    public async Task<IReadOnlyList<ReminderOccurrenceResponse>> GetOccurrencesAsync(
        CallerIdentity caller,
        Guid reminderId,
        DateTimeOffset? from = null,
        DateTimeOffset? to = null,
        int limit = 50,
        CancellationToken ct = default)
    {
        caller.Require(Scopes.ReminderRead);

        return await db.InTransactionAsync(async (connection, tx) =>
        {
            var row = await LoadOwnedAsync(connection, tx, caller.UserId, reminderId, ct);
            if (row.Rrule is null)
            {
                return (IReadOnlyList<ReminderOccurrenceResponse>)[];
            }

            var targetCount = RecurrenceExpander.TargetOccurrenceCount(row.Rrule);
            var effectiveLimit = Math.Min(limit, targetCount);
            var defaultToUtc = DefaultOccurrenceHorizon(row.Rrule, clock.UtcNow);

            var toUtc = to?.ToUniversalTime() ?? defaultToUtc;
            if (row.RecurrenceUntil is { } until && until < toUtc)
            {
                toUtc = until;
            }

            var lookbackBase = toUtc < clock.UtcNow ? toUtc : clock.UtcNow;
            var fromUtc = from?.ToUniversalTime() ?? (row.DueAtUtc > lookbackBase.AddDays(-14) ? row.DueAtUtc : lookbackBase.AddDays(-14));
            if (fromUtc > toUtc)
            {
                fromUtc = row.DueAtUtc <= toUtc ? row.DueAtUtc : toUtc;
            }

            var dek = await TryLoadDataKeyAsync(connection, caller.UserId, ct, tx);
            try
            {
                var seriesBody = DecryptBodyOrPlaceholder(dek, row.BodyCiphertext, caller.UserId, row.PublicId);

                var stored = (await connection.QueryAsync<StoredOccurrenceRow>(new CommandDefinition("""
                    SELECT id AS Id, occurs_at_utc AS OccursAtUtc, status AS Status,
                           completed_at AS CompletedAt, custom_body_ciphertext AS CustomBodyCiphertext
                      FROM reminder_occurrences
                     WHERE reminder_id = @ReminderId
                       AND occurs_at_utc >= @From AND occurs_at_utc <= @To
                     ORDER BY occurs_at_utc ASC
                    """, new { ReminderId = row.Id, From = fromUtc, To = toUtc }, tx, cancellationToken: ct))).ToList();

                var storedDict = stored.ToDictionary(s => s.OccursAtUtc.ToUniversalTime());

                var expandedInstants = recurrence
                    .Expand(row.Rrule!, row.DueAtUtc, row.Timezone, fromUtc, toUtc, row.RecurrenceUntil)
                    .Select(static i => i.ToUniversalTime());

                var allInstants = new SortedSet<DateTimeOffset>(
                    stored.Select(static s => s.OccursAtUtc.ToUniversalTime()).Concat(expandedInstants));

                SortedSet<DateTimeOffset> selectedInstants;
                if (from.HasValue && to.HasValue)
                {
                    var rangeLimit = Math.Clamp(limit, 1, 200);
                    selectedInstants = new SortedSet<DateTimeOffset>(
                        allInstants.Where(i => i >= fromUtc && i <= toUtc).Take(rangeLimit));
                }
                else
                {
                    var cutoff = clock.UtcNow.AddMinutes(-5);
                    var upcomingInstants = allInstants.Where(i => i >= cutoff).Take(effectiveLimit).ToList();
                    if (upcomingInstants.Count == 0)
                    {
                        upcomingInstants = allInstants.TakeLast(effectiveLimit).ToList();
                    }

                    var pastStored = allInstants.Where(i => i < cutoff && storedDict.TryGetValue(i, out var s) && s.Status != "pending").TakeLast(3).ToList();
                    selectedInstants = new SortedSet<DateTimeOffset>(pastStored.Concat(upcomingInstants));
                }

                var results = new List<ReminderOccurrenceResponse>(selectedInstants.Count);
                foreach (var instant in selectedInstants)
                {
                    if (storedDict.TryGetValue(instant, out var storedRow))
                    {
                        string? customBody = null;
                        if (storedRow.CustomBodyCiphertext is not null && dek is not null)
                        {
                            try
                            {
                                customBody = envelope.Decrypt(dek, storedRow.CustomBodyCiphertext, AssociatedData(caller.UserId));
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "Could not decrypt custom occurrence body for reminder {ReminderId}", row.PublicId);
                            }
                        }

                        results.Add(new ReminderOccurrenceResponse(
                            storedRow.Id,
                            row.PublicId.ToString(),
                            storedRow.OccursAtUtc.ToUniversalTime(),
                            storedRow.Status,
                            storedRow.CompletedAt,
                            customBody,
                            customBody ?? seriesBody));
                    }
                    else
                    {
                        results.Add(new ReminderOccurrenceResponse(
                            0,
                            row.PublicId.ToString(),
                            instant.ToUniversalTime(),
                            "pending",
                            null,
                            null,
                            seriesBody));
                    }
                }

                return (IReadOnlyList<ReminderOccurrenceResponse>)results
                    .OrderBy(static o => o.OccursAt.ToUniversalTime())
                    .ToList();
            }
            finally
            {
                ZeroKey(dek);
            }
        }, ct);
    }

    public async Task<ReminderOccurrenceResponse> UpdateOccurrenceAsync(
        CallerIdentity caller,
        Guid reminderId,
        UpdateReminderOccurrenceRequest request,
        CancellationToken ct = default)
    {
        caller.Require(Scopes.ReminderWrite);

        var (response, reminderResponse) = await db.InTransactionAsync(async (connection, tx) =>
        {
            var row = await LoadOwnedAsync(connection, tx, caller.UserId, reminderId, ct);
            if (row.Rrule is null)
            {
                throw AppException.Validation("Occurrences only apply to recurring reminders.",
                    new ErrorDetail("rrule", "not_recurring"));
            }

            var occursAt = request.OccursAt.ToUniversalTime();
            var validStatuses = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pending", "completed", "skipped", "notified" };
            if (request.Status is not null && !validStatuses.Contains(request.Status))
            {
                throw AppException.Validation("Invalid occurrence status.", new ErrorDetail("status", "invalid"));
            }

            var dek = await LoadDataKeyAsync(connection, tx, caller.UserId, create: true, ct)!;
            try
            {
                byte[]? customBodyCiphertext = null;
                if (!string.IsNullOrWhiteSpace(request.CustomBody))
                {
                    customBodyCiphertext = envelope.Encrypt(dek!, request.CustomBody, AssociatedData(caller.UserId));
                }

                DateTimeOffset? completedAt = request.Status?.ToLowerInvariant() switch
                {
                    "completed" => clock.UtcNow,
                    "pending" => null,
                    "skipped" => null,
                    _ => null
                };

                var updated = await connection.QuerySingleAsync<StoredOccurrenceRow>(new CommandDefinition("""
                    INSERT INTO reminder_occurrences (reminder_id, occurs_at_utc, status, completed_at, custom_body_ciphertext)
                    VALUES (@ReminderId, @OccursAt, COALESCE(@Status, 'pending'), @CompletedAt, @CustomBodyCiphertext)
                    ON CONFLICT (reminder_id, occurs_at_utc)
                    DO UPDATE SET
                        status = COALESCE(@Status, reminder_occurrences.status),
                        completed_at = CASE
                            WHEN @Status = 'completed' THEN COALESCE(@CompletedAt, now())
                            WHEN @Status IS NOT NULL AND @Status != 'completed' THEN NULL
                            ELSE reminder_occurrences.completed_at
                        END,
                        custom_body_ciphertext = CASE
                            WHEN @ClearCustomBody = true THEN NULL
                            WHEN @CustomBodyCiphertext IS NOT NULL THEN @CustomBodyCiphertext
                            ELSE reminder_occurrences.custom_body_ciphertext
                        END
                    RETURNING id AS Id, occurs_at_utc AS OccursAtUtc, status AS Status,
                              completed_at AS CompletedAt, custom_body_ciphertext AS CustomBodyCiphertext
                    """,
                    new
                    {
                        ReminderId = row.Id,
                        OccursAt = occursAt,
                        Status = request.Status?.ToLowerInvariant(),
                        CompletedAt = completedAt,
                        CustomBodyCiphertext = customBodyCiphertext,
                        ClearCustomBody = request.ClearCustomBody ?? false,
                    }, tx, cancellationToken: ct));

                var refreshedParent = await connection.QuerySingleAsync<ReminderRow>(new CommandDefinition($"""
                    UPDATE reminders
                       SET is_completed = CASE
                               WHEN recurrence_until IS NULL OR recurrence_until >= @Now THEN false
                               ELSE is_completed
                           END,
                           completed_at = CASE
                               WHEN @Status = 'completed' THEN COALESCE(@CompletedAt, now())
                               ELSE completed_at
                           END,
                           updated_at = now()
                     WHERE id = @Id
                    RETURNING {ReturningColumnsSql}
                    """,
                    new
                    {
                        row.Id,
                        Now = clock.UtcNow,
                        Status = request.Status?.ToLowerInvariant(),
                        CompletedAt = completedAt,
                    }, tx, cancellationToken: ct));

                var seriesBody = DecryptBodyOrPlaceholder(dek, refreshedParent.BodyCiphertext, caller.UserId, refreshedParent.PublicId);
                string? effectiveCustom = null;
                if (updated.CustomBodyCiphertext is not null)
                {
                    effectiveCustom = envelope.Decrypt(dek!, updated.CustomBodyCiphertext, AssociatedData(caller.UserId));
                }

                var occurrenceResponse = new ReminderOccurrenceResponse(
                    updated.Id,
                    refreshedParent.PublicId.ToString(),
                    updated.OccursAtUtc,
                    updated.Status,
                    updated.CompletedAt,
                    effectiveCustom,
                    effectiveCustom ?? seriesBody);

                var targets = await LoadTargetsAsync(connection, tx, [refreshedParent.Id], ct);
                return (occurrenceResponse, ToResponse(refreshedParent, dek, targets.GetValueOrDefault(refreshedParent.Id)));
            }
            finally
            {
                ZeroKey(dek);
            }
        }, ct);

        await realtime.ReminderChangedAsync(caller.UserPublicId,
            new ReminderChangedEvent("updated", reminderResponse, reminderResponse.Id), ct);

        return response;
    }

    public async Task<ReminderOccurrenceResponse> SkipOccurrenceAsync(
        CallerIdentity caller,
        Guid reminderId,
        DateTimeOffset occursAt,
        CancellationToken ct = default)
    {
        return await UpdateOccurrenceAsync(caller, reminderId, new UpdateReminderOccurrenceRequest(
            OccursAt: occursAt,
            Status: "skipped"), ct);
    }


    // ── legacy compatibility (dies with the shim) ────────────────────────────

    /// <summary>
    /// The shape the installed VB.NET and Java clients expect: an integer id and
    /// a decrypted body. The integer is the internal row id, which v2 never
    /// exposes — the shim is the one place that keeps v1's weaker identifier,
    /// because those clients parse it as an int and cannot be changed (ADR-0008).
    /// </summary>
    public sealed record LegacyReminder(long Id, DateTimeOffset DueAt, string Timezone, string Body, bool IsCompleted);

    public async Task<IReadOnlyList<LegacyReminder>> ListLegacyAsync(
        CallerIdentity caller, bool includeCompleted, CancellationToken ct = default)
    {
        caller.Require(Scopes.ReminderRead);

        await using var connection = await db.OpenAsync(ct);
        var rows = (await connection.QueryAsync<ReminderRow>(new CommandDefinition(
            SelectSql + """
             WHERE r.user_id = @UserId
               AND r.deleted_at IS NULL
               AND (@IncludeCompleted OR r.is_completed = false)
             ORDER BY r.due_at_utc
             LIMIT 500
            """, new { UserId = caller.UserId, IncludeCompleted = includeCompleted }, cancellationToken: ct))).ToList();

        if (rows.Count == 0)
        {
            return [];
        }

        var dek = await TryLoadDataKeyAsync(connection, caller.UserId, ct);
        if (dek is null)
        {
            return [];
        }

        try
        {
            return rows.Select(r => new LegacyReminder(
                r.Id, r.DueAtUtc, r.Timezone,
                envelope.Decrypt(dek, r.BodyCiphertext, AssociatedData(r.UserId)),
                r.IsCompleted)).ToList();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public async Task<LegacyReminder?> NextDueLegacyAsync(CallerIdentity caller, CancellationToken ct = default)
    {
        var all = await ListLegacyAsync(caller, includeCompleted: false, ct);
        return all.Count == 0 ? null : all[0];
    }

    public async Task CompleteLegacyAsync(CallerIdentity caller, long legacyId, CancellationToken ct = default)
    {
        caller.Require(Scopes.ReminderWrite);

        await using var connection = await db.OpenAsync(ct);
        var publicId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition("""
            SELECT public_id FROM reminders
             WHERE id = @Id AND user_id = @UserId AND deleted_at IS NULL
            """, new { Id = legacyId, UserId = caller.UserId }, cancellationToken: ct));

        if (publicId is null)
        {
            throw AppException.NotFound(ErrorCodes.ReminderNotFound, "No such reminder.");
        }

        await CompleteAsync(caller, publicId.Value, new CompleteReminderRequest(true), ct);
    }

    // ── scheduling (worker) ──────────────────────────────────────────────────

    /// <summary>
    /// A reminder that has come due. <paramref name="DeviceIds"/> is null when it
    /// shows on every PC, which is what a reminder with no chosen targets means.
    /// </summary>
    public sealed record DueReminder(
        Guid PublicId,
        Guid UserPublicId,
        string Body,
        DateTimeOffset DueAt,
        IReadOnlyList<string>? DeviceIds);

    /// <summary>
    /// Everything due in this tick, decrypted for delivery. Marked as notified in
    /// the same transaction so a restart does not re-notify.
    /// </summary>
    public async Task<IReadOnlyList<DueReminder>> ClaimDueAsync(TimeSpan horizon, int batchSize = 200, CancellationToken ct = default)
    {
        var until = clock.UtcNow.Add(horizon);

        return await db.InTransactionAsync(async (connection, tx) =>
        {
            var singles = (await connection.QueryAsync<DueRow>(new CommandDefinition("""
                WITH due AS (
                    UPDATE reminders
                       SET notified_at = now()
                     WHERE id IN (
                         SELECT id FROM reminders
                          WHERE deleted_at IS NULL
                            AND is_completed = false
                            AND rrule IS NULL
                            AND due_at_utc <= @Until
                            AND notified_at IS NULL
                          ORDER BY due_at_utc
                          LIMIT @BatchSize
                          FOR UPDATE SKIP LOCKED
                     )
                    RETURNING id, public_id, user_id, body_ciphertext, body_dek_id, due_at_utc
                )
                SELECT due.id AS ReminderId, due.public_id AS PublicId,
                       u.public_id AS UserPublicId, u.id AS UserId,
                       due.body_ciphertext AS BodyCiphertext, due.due_at_utc AS DueAtUtc
                  FROM due JOIN users u ON u.id = due.user_id
                """, new { Until = until, BatchSize = batchSize }, tx, cancellationToken: ct))).ToList();

            var occurrences = (await connection.QueryAsync<DueRow>(new CommandDefinition("""
                WITH due AS (
                    UPDATE reminder_occurrences o
                       SET status = 'notified', notified_at = now()
                     WHERE o.id IN (
                         SELECT o2.id FROM reminder_occurrences o2
                           JOIN reminders r ON r.id = o2.reminder_id
                          WHERE o2.status = 'pending'
                            AND o2.occurs_at_utc <= @Until
                            AND r.deleted_at IS NULL
                          ORDER BY o2.occurs_at_utc
                          LIMIT @BatchSize
                          FOR UPDATE OF o2 SKIP LOCKED
                     )
                    RETURNING o.reminder_id, o.occurs_at_utc, o.custom_body_ciphertext
                )
                SELECT r.id AS ReminderId, r.public_id AS PublicId,
                       u.public_id AS UserPublicId, u.id AS UserId,
                       COALESCE(due.custom_body_ciphertext, r.body_ciphertext) AS BodyCiphertext, due.occurs_at_utc AS DueAtUtc
                  FROM due
                  JOIN reminders r ON r.id = due.reminder_id
                  JOIN users u ON u.id = r.user_id
                """, new { Until = until, BatchSize = batchSize }, tx, cancellationToken: ct))).ToList();

            var results = new List<DueReminder>(singles.Count + occurrences.Count);
            // Nullable: a null value is a remembered failure, so a user whose
            // key will not unwrap is looked up once per tick rather than once
            // per reminder.
            var keyCache = new Dictionary<long, byte[]?>();
            var targets = await LoadTargetsAsync(connection, tx,
                singles.Concat(occurrences).Select(r => r.ReminderId).Distinct().ToList(), ct);

            foreach (var row in singles.Concat(occurrences))
            {
                // One account must not stop the sweep for every other account.
                //
                // Letting the exception out rolled the transaction back, which
                // undid the notified marking for the whole batch and left the
                // rows to be claimed again on the next tick — so a single user
                // whose data key predates a KEK change stopped reminders for
                // everybody, and did it again every thirty seconds, for ever.
                // The read path has degraded to a placeholder since it was
                // written; the schedule is not encrypted and must still fire.
                if (!keyCache.TryGetValue(row.UserId, out var dek))
                {
                    dek = await TryLoadDataKeyAsync(connection, row.UserId, ct, tx);
                    keyCache[row.UserId] = dek;
                }

                results.Add(new DueReminder(row.PublicId, row.UserPublicId,
                    DecryptBodyOrPlaceholder(dek, row.BodyCiphertext, row.UserId, row.PublicId), row.DueAtUtc,
                    targets.GetValueOrDefault(row.ReminderId)));
            }

            foreach (var key in keyCache.Values)
            {
                if (key is not null)
                {
                    CryptographicOperations.ZeroMemory(key);
                }
            }

            return results;
        }, ct);
    }

    /// <summary>
    /// Keeps the materialised horizon topped up for every live series. Expanding
    /// an RRULE on every scheduler tick does not scale; a rolling window does.
    /// </summary>
    public async Task<int> ExtendRecurrenceHorizonAsync(CancellationToken ct = default)
    {
        return await db.InTransactionAsync(async (connection, tx) =>
        {
            var series = (await connection.QueryAsync<ReminderRow>(new CommandDefinition(
                SelectSql + """
                 WHERE r.deleted_at IS NULL
                   AND r.rrule IS NOT NULL
                   AND (r.recurrence_until IS NULL OR r.recurrence_until >= @Now)
                 LIMIT 1000
                """, new { Now = clock.UtcNow }, transaction: tx, cancellationToken: ct))).ToList();

            var written = 0;
            foreach (var row in series)
            {
                written += await MaterialiseOccurrencesAsync(connection, tx, row, ct);
            }

            return written;
        }, ct);
    }

    private async Task<int> MaterialiseOccurrencesAsync(
        NpgsqlConnection connection, NpgsqlTransaction tx, ReminderRow row, CancellationToken ct)
    {
        if (row.Rrule is null)
        {
            return 0;
        }

        var occurrences = recurrence.Expand(row.Rrule, row.DueAtUtc, row.Timezone,
            clock.UtcNow, clock.UtcNow.AddDays(RecurrenceExpander.HorizonDays), row.RecurrenceUntil);
        if (occurrences.Count == 0)
        {
            return 0;
        }

        await using var command = new NpgsqlCommand("""
            INSERT INTO reminder_occurrences (reminder_id, occurs_at_utc)
            SELECT $1, unnest($2)
            ON CONFLICT (reminder_id, occurs_at_utc) DO NOTHING
            """, connection, tx);
        command.Parameters.Add(new NpgsqlParameter<long> { Value = row.Id });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset[]>
        {
            NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.TimestampTz,
            Value = occurrences.Select(static o => o.ToUniversalTime()).ToArray(),
        });

        return await command.ExecuteNonQueryAsync(ct);
    }

    // ── envelope encryption ──────────────────────────────────────────────────

    /// <summary>
    /// Loads (and, when asked, creates) the user's data key. The DEK exists only
    /// in memory and only for the duration of a call; the wrapped copy in the
    /// database is useless without the KEK, which is not in the database.
    /// </summary>
    /// <summary>
    /// Loads the user's data key, treating "cannot unwrap it" as "cannot read
    /// the bodies" rather than as a failed request or a failed sweep.
    ///
    /// The unwrap throws when the KEK that wrapped this user's data key is not
    /// the one configured — the exact state a mis-ordered rotation leaves behind
    /// (09 §2.11), and the one a restore across a key change produces. Letting it
    /// out returned 500 for `GET /v2/reminders`, so every reminder the person
    /// had disappeared behind a server error, along with any way to see or
    /// delete them. The schedules are not encrypted and are still perfectly
    /// readable; only the bodies are lost, and the list says so per row.
    /// </summary>
    private async Task<byte[]?> TryLoadDataKeyAsync(
        NpgsqlConnection connection, long userId, CancellationToken ct, NpgsqlTransaction? tx = null)
    {
        try
        {
            return await LoadDataKeyAsync(connection, tx, userId, create: false, ct);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            logger.LogError(ex,
                "The data key for user {UserId} could not be unwrapped. The configured KEK is not the one " +
                "that wrapped it; reminder bodies will read as unavailable until it is restored.", userId);

            return null;
        }
    }

    internal async Task<byte[]?> LoadDataKeyAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, long userId, bool create, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<(byte[]? DekWrapped, string? DekKekId)>(
            new CommandDefinition("SELECT dek_wrapped, dek_kek_id FROM users WHERE id = @Id",
                new { Id = userId }, tx, cancellationToken: ct));

        if (row.DekWrapped is { Length: > 0 } wrapped && row.DekKekId is { } kekId)
        {
            return envelope.UnwrapDataKey(wrapped, kekId);
        }

        if (!create)
        {
            return null;
        }

        var (newWrapped, newKekId) = envelope.CreateDataKey();
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE users SET dek_wrapped = @Wrapped, dek_kek_id = @KekId, updated_at = now()
             WHERE id = @Id AND dek_wrapped IS NULL
            """, new { Wrapped = newWrapped, KekId = newKekId, Id = userId }, tx, cancellationToken: ct));

        // Re-read: a concurrent writer may have won the race, and using its key
        // rather than ours is what keeps a user's rows readable with one key.
        var confirmed = await connection.QuerySingleAsync<(byte[] DekWrapped, string DekKekId)>(
            new CommandDefinition("SELECT dek_wrapped, dek_kek_id FROM users WHERE id = @Id",
                new { Id = userId }, tx, cancellationToken: ct));

        return envelope.UnwrapDataKey(confirmed.DekWrapped, confirmed.DekKekId);
    }

    /// <summary>
    /// Binds a ciphertext to its owner. A row moved between users fails to
    /// authenticate rather than decrypting into someone else's list.
    /// </summary>
    internal static string AssociatedData(long userId) =>
        string.Create(CultureInfo.InvariantCulture, $"pcconnect:reminder:{userId}");

    // ── helpers ──────────────────────────────────────────────────────────────

    internal const string ReturningColumnsSql = """
        id AS Id, public_id AS PublicId, user_id AS UserId,
        body_ciphertext AS BodyCiphertext, body_dek_id AS BodyDekId,
        due_at_utc AS DueAtUtc, due_local_time AS DueLocalTime, timezone AS Timezone,
        rrule AS Rrule, recurrence_until AS RecurrenceUntil,
        is_completed AS IsCompleted, completed_at AS CompletedAt,
        created_at AS CreatedAt, updated_at AS UpdatedAt
        """;

    internal const string SelectSql = $"SELECT {ReturningColumnsSql} FROM reminders r";

    /// <summary>
    /// The devices each of these reminders shows on, keyed by reminder.
    ///
    /// A reminder with no rows is absent from the dictionary, which is how "every
    /// device" is represented all the way out to the client: null, not an empty
    /// list. An empty list would mean "no PCs at all", which nothing can produce.
    /// </summary>
    private static async Task<IReadOnlyDictionary<long, IReadOnlyList<string>>> LoadTargetsAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, IReadOnlyList<long> reminderIds, CancellationToken ct)
    {
        if (reminderIds.Count == 0)
        {
            return new Dictionary<long, IReadOnlyList<string>>();
        }

        var rows = await connection.QueryAsync<TargetRow>(new CommandDefinition("""
            SELECT rd.reminder_id AS ReminderId, d.public_id AS DeviceId
              FROM reminder_devices rd
              JOIN devices d ON d.id = rd.device_id
             WHERE rd.reminder_id = ANY(@Ids)
             ORDER BY rd.reminder_id, d.public_id
            """, new { Ids = reminderIds.ToArray() }, tx, cancellationToken: ct));

        return rows
            .GroupBy(r => r.ReminderId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(r => r.DeviceId.ToString()).ToList());
    }

    /// <summary>
    /// Replaces a reminder's targets, rejecting any device that is not this
    /// user's.
    ///
    /// Without the ownership check a reminder could name someone else's PC, and
    /// the id would come straight back out on the reminder it was written to —
    /// which is the same shape of defect as trusting a caller-supplied PCName
    /// (S1-08).
    /// </summary>
    private static Guid[]? ParseTargetDevices(IReadOnlyList<string>? deviceIds)
    {
        if (deviceIds is null)
        {
            return null;
        }

        if (deviceIds.Count == 0)
        {
            // Well formed and impossible, so 422 with a code of its own rather
            // than a generic 400 — the client has to tell "you sent nonsense"
            // apart from "nobody would ever see that" to say anything useful.
            throw AppException.Unprocessable(ErrorCodes.ReminderTargetsEmpty,
                "Choose at least one PC, or send no list at all to show it on every PC.");
        }

        var parsed = new HashSet<Guid>(deviceIds.Count);
        foreach (var id in deviceIds)
        {
            if (!Guid.TryParse(id, out var guid))
            {
                throw AppException.Validation("That is not a device id.", new ErrorDetail("deviceIds", "invalid"));
            }

            parsed.Add(guid);
        }

        return [.. parsed];
    }

    private static async Task<IReadOnlyList<string>?> ReplaceTargetsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction tx,
        long userId,
        long reminderId,
        Guid[]? wanted,
        CancellationToken ct,
        bool isNew = false)
    {
        if (wanted is null)
        {
            return null;
        }

        var owned = (await connection.QueryAsync<long>(new CommandDefinition("""
            SELECT id FROM devices
             WHERE user_id = @UserId AND public_id = ANY(@PublicIds) AND revoked_at IS NULL
            """, new { UserId = userId, PublicIds = wanted }, tx, cancellationToken: ct))).ToList();

        if (owned.Count != wanted.Length)
        {
            // Deliberately the same answer for "not yours" and "does not
            // exist": a different one would let a caller test ids for
            // existence. Revoked PCs are excluded above, so choosing one is
            // this error and not a reminder aimed at a machine that is gone.
            throw AppException.Unprocessable(ErrorCodes.ReminderTargetUnknown,
                "One of those PCs is not on this account.");
        }

        if (!isNew)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM reminder_devices WHERE reminder_id = @ReminderId",
                new { ReminderId = reminderId }, tx, cancellationToken: ct));
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO reminder_devices (reminder_id, device_id)
            SELECT @ReminderId, unnest(@DeviceIds)
            """, new { ReminderId = reminderId, DeviceIds = owned.ToArray() }, tx, cancellationToken: ct));

        return wanted
            .Select(static g => g.ToString())
            .OrderBy(static s => s, StringComparer.Ordinal)
            .ToArray();
    }

    private sealed record TargetRow
    {
        public long ReminderId { get; init; }

        public Guid DeviceId { get; init; }
    }

    private static async Task<ReminderRow> LoadOwnedAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, long userId, Guid publicId, CancellationToken ct)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ReminderRow>(new CommandDefinition(
            SelectSql + " WHERE r.public_id = @PublicId AND r.user_id = @UserId AND r.deleted_at IS NULL",
            new { PublicId = publicId, UserId = userId }, tx, cancellationToken: ct));

        return row ?? throw AppException.NotFound(ErrorCodes.ReminderNotFound, "No such reminder.");
    }

    /// <summary>
    /// Text that stands in for a body this server cannot read. Deliberately not
    /// an empty string: the row is still there, its schedule still fires, and
    /// the owner needs to be able to see it in order to delete or replace it.
    /// </summary>
    internal const string UnreadableBody = "(this reminder could not be read)";

    /// <summary>
    /// The body, or the placeholder standing in for one this server cannot read.
    ///
    /// One rule for both paths that read a body, because a row that will not
    /// decrypt must take neither of them down. Letting the exception out
    /// returned 500 for `GET /v2/reminders` — one bad row removed the entire
    /// feature, with no list, no way to find that row and no way to delete it —
    /// and on the scheduler it abandoned the sweep for every other user in the
    /// batch.
    ///
    /// This happens for real after a KEK rotation done wrongly (09 §2.11), after
    /// a restore that mixes eras, and in development after the key file is lost.
    /// The schedule, the recurrence and the completion state are all still
    /// readable; only the body is lost, and saying so is far more useful than
    /// failing.
    /// </summary>
    private string DecryptBodyOrPlaceholder(byte[]? dek, byte[] ciphertext, long userId, Guid reminderId)
    {
        // No key means no body on this account at all — see TryLoadDataKeyAsync,
        // which has already logged why.
        if (dek is null)
        {
            return UnreadableBody;
        }

        try
        {
            return envelope.Decrypt(dek, ciphertext, AssociatedData(userId));
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            logger.LogError(ex,
                "Reminder {ReminderId} could not be decrypted. Its key encryption key is wrong or the row is damaged.",
                reminderId);

            return UnreadableBody;
        }
    }

    private ReminderResponse ToResponse(
        ReminderRow row, byte[]? dek, IReadOnlyList<string>? targetIds = null)
    {
        var body = DecryptBodyOrPlaceholder(dek, row.BodyCiphertext, row.UserId, row.PublicId);

        return new ReminderResponse(
            row.PublicId.ToString(),
            body,
            row.DueAtUtc,
            row.DueLocalTime.ToString(@"hh\:mm", CultureInfo.InvariantCulture),
            row.Timezone,
            row.Rrule,
            row.RecurrenceUntil,
            row.IsCompleted,
            row.CompletedAt,
            row.CreatedAt,
            row.UpdatedAt,
            targetIds);
    }

    private static void ZeroKey(byte[]? dek)
    {
        if (dek is not null)
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    private static DateTimeOffset DefaultOccurrenceHorizon(string rrule, DateTimeOffset now) =>
        rrule switch
        {
            _ when rrule.Contains("FREQ=HOURLY", StringComparison.OrdinalIgnoreCase) => now.AddDays(2),
            _ when rrule.Contains("FREQ=DAILY", StringComparison.OrdinalIgnoreCase) => now.AddDays(14),
            _ when rrule.Contains("FREQ=WEEKLY", StringComparison.OrdinalIgnoreCase) => now.AddDays(60),
            _ when rrule.Contains("FREQ=MONTHLY", StringComparison.OrdinalIgnoreCase) => now.AddMonths(12),
            _ when rrule.Contains("FREQ=YEARLY", StringComparison.OrdinalIgnoreCase) => now.AddYears(6),
            _ => now.AddYears(3),
        };

    private static void ValidateBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw AppException.Validation("A reminder needs some text.", new ErrorDetail("body", "required"));
        }

        if (body.Length > MaxBodyLength)
        {
            throw AppException.Unprocessable(ErrorCodes.ReminderBodyTooLong,
                $"A reminder can be at most {MaxBodyLength} characters.");
        }
    }

    private string? ValidateRrule(string? rrule, DateTimeOffset dueAt)
    {
        if (string.IsNullOrWhiteSpace(rrule))
        {
            return null;
        }

        if (!recurrence.TryValidate(rrule, dueAt, out var error))
        {
            throw AppException.Unprocessable(ErrorCodes.ReminderRruleInvalid, error);
        }

        return rrule.Trim();
    }

    private static void ValidateTimezoneIfProvided(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested) && Normalise.IanaTimeZoneOrDefault(requested, " ") == " ")
        {
            throw AppException.Unprocessable(ErrorCodes.ReminderTimezoneInvalid,
                $"'{requested}' is not a known IANA timezone.");
        }
    }

    private static async Task<string> ResolveTimezoneAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, long userId, string? requested, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            ValidateTimezoneIfProvided(requested);
            return requested;
        }

        return await connection.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT timezone FROM users WHERE id = @Id", new { Id = userId }, tx, cancellationToken: ct))
            ?? "Etc/UTC";
    }

    private static TimeSpan ToLocalTime(DateTimeOffset instant, string timezone)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            return TimeZoneInfo.ConvertTime(instant, tz).TimeOfDay;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return instant.UtcDateTime.TimeOfDay;
        }
    }

    internal sealed record ReminderRow
    {
        public long Id { get; init; }
        public Guid PublicId { get; init; }
        public long UserId { get; init; }
        public byte[] BodyCiphertext { get; init; } = [];
        public string BodyDekId { get; init; } = string.Empty;
        public DateTimeOffset DueAtUtc { get; init; }
        public TimeSpan DueLocalTime { get; init; }
        public string Timezone { get; init; } = "Etc/UTC";
        public string? Rrule { get; init; }
        public DateTimeOffset? RecurrenceUntil { get; init; }
        public bool IsCompleted { get; init; }
        public DateTimeOffset? CompletedAt { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
    }

    private sealed record StoredOccurrenceRow
    {
        public long Id { get; init; }
        public DateTimeOffset OccursAtUtc { get; init; }
        public string Status { get; init; } = "pending";
        public DateTimeOffset? CompletedAt { get; init; }
        public byte[]? CustomBodyCiphertext { get; init; }
    }

    private sealed record DueRow
    {
        public long ReminderId { get; init; }
        public Guid PublicId { get; init; }
        public Guid UserPublicId { get; init; }
        public long UserId { get; init; }
        public byte[] BodyCiphertext { get; init; } = [];
        public DateTimeOffset DueAtUtc { get; init; }
    }
}

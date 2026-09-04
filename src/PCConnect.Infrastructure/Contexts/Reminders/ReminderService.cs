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

        try
        {
            return new Page<ReminderResponse>(
                page.Select(r => ToResponse(r, dek)).ToList(),
                hasMore ? Contexts.Commands.Cursor.Encode(page[^1].Id) : null);
        }
        finally
        {
            if (dek is not null)
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        }
    }

    public async Task<ReminderResponse> GetAsync(CallerIdentity caller, Guid reminderId, CancellationToken ct = default)
    {
        caller.Require(Scopes.ReminderRead);

        await using var connection = await db.OpenAsync(ct);
        var row = await LoadOwnedAsync(connection, null, caller.UserId, reminderId, ct);
        var dek = await TryLoadDataKeyAsync(connection, caller.UserId, ct);

        try
        {
            return ToResponse(row, dek);
        }
        finally
        {
            if (dek is not null)
            {
                CryptographicOperations.ZeroMemory(dek);
            }
        }
    }

    // ── write ────────────────────────────────────────────────────────────────

    public async Task<ReminderResponse> CreateAsync(
        CallerIdentity caller, CreateReminderRequest request, CancellationToken ct = default)
    {
        caller.Require(Scopes.ReminderWrite);
        ValidateBody(request.Body);

        var response = await db.InTransactionAsync(async (connection, tx) =>
        {
            var timezone = await ResolveTimezoneAsync(connection, tx, caller.UserId, request.Timezone, ct);
            var rrule = ValidateRrule(request.Rrule, request.DueAt);

            var dek = await LoadDataKeyAsync(connection, tx, caller.UserId, create: true, ct)!;
            try
            {
                var dueAt = request.DueAt.ToUniversalTime();
                var localTime = ToLocalTime(dueAt, timezone);

                var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
                    INSERT INTO reminders
                        (user_id, body_ciphertext, body_dek_id, due_at_utc, due_local_time, timezone, rrule, recurrence_until)
                    VALUES
                        (@UserId, @Body, @DekId, @DueAt, @LocalTime, @Timezone, @Rrule, @Until)
                    RETURNING id
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

                var row = await connection.QuerySingleAsync<ReminderRow>(new CommandDefinition(
                    SelectSql + " WHERE r.id = @Id", new { Id = id }, tx, cancellationToken: ct));

                if (rrule is not null)
                {
                    await MaterialiseOccurrencesAsync(connection, tx, row, ct);
                }

                return ToResponse(row, dek);
            }
            finally
            {
                if (dek is not null)
                {
                    CryptographicOperations.ZeroMemory(dek);
                }
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

                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE reminders
                       SET body_ciphertext = COALESCE(@Body, body_ciphertext),
                           body_dek_id = CASE WHEN @Body IS NULL THEN body_dek_id ELSE @DekId END,
                           due_at_utc = @DueAt,
                           due_local_time = @LocalTime,
                           timezone = @Timezone,
                           rrule = @Rrule,
                           recurrence_until = COALESCE(@Until, recurrence_until),
                           updated_at = now()
                     WHERE id = @Id
                    """,
                    new
                    {
                        Body = request.Body is null ? null : envelope.Encrypt(dek!, request.Body, AssociatedData(caller.UserId)),
                        DekId = envelope.CurrentKekId,
                        DueAt = dueAt,
                        LocalTime = ToLocalTime(dueAt, timezone),
                        Timezone = timezone,
                        Rrule = rrule,
                        Until = request.RecurrenceUntil?.ToUniversalTime(),
                        row.Id,
                    }, tx, cancellationToken: ct));

                var updated = await connection.QuerySingleAsync<ReminderRow>(new CommandDefinition(
                    SelectSql + " WHERE r.id = @Id", new { row.Id }, tx, cancellationToken: ct));

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

                return ToResponse(updated, dek);
            }
            finally
            {
                if (dek is not null)
                {
                    CryptographicOperations.ZeroMemory(dek);
                }
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
            }
            else
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE reminders
                       SET is_completed = @Completed,
                           completed_at = CASE WHEN @Completed THEN now() ELSE NULL END,
                           updated_at = now()
                     WHERE id = @Id
                    """, new { Completed = request.Completed, row.Id }, tx, cancellationToken: ct));
            }

            var updated = await connection.QuerySingleAsync<ReminderRow>(new CommandDefinition(
                SelectSql + " WHERE r.id = @Id", new { row.Id }, tx, cancellationToken: ct));

            var dek = await LoadDataKeyAsync(connection, tx, caller.UserId, create: false, ct);
            try
            {
                return ToResponse(updated, dek);
            }
            finally
            {
                if (dek is not null)
                {
                    CryptographicOperations.ZeroMemory(dek);
                }
            }
        }, ct);

        await realtime.ReminderChangedAsync(caller.UserPublicId,
            new ReminderChangedEvent("updated", response, response.Id), ct);

        return response;
    }

    public async Task DeleteAsync(CallerIdentity caller, Guid reminderId, CancellationToken ct = default)
    {
        caller.Require(Scopes.ReminderWrite);

        await using var connection = await db.OpenAsync(ct);
        var affected = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE reminders SET deleted_at = now(), updated_at = now()
             WHERE public_id = @PublicId AND user_id = @UserId AND deleted_at IS NULL
            """, new { PublicId = reminderId, UserId = caller.UserId }, cancellationToken: ct));

        if (affected == 0)
        {
            throw AppException.NotFound(ErrorCodes.ReminderNotFound, "No such reminder.");
        }

        await realtime.ReminderChangedAsync(caller.UserPublicId,
            new ReminderChangedEvent("deleted", null, reminderId.ToString()), ct);
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

    public sealed record DueReminder(Guid PublicId, Guid UserPublicId, string Body, DateTimeOffset DueAt);

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
                SELECT due.public_id AS PublicId, u.public_id AS UserPublicId, u.id AS UserId,
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
                    RETURNING o.reminder_id, o.occurs_at_utc
                )
                SELECT r.public_id AS PublicId, u.public_id AS UserPublicId, u.id AS UserId,
                       r.body_ciphertext AS BodyCiphertext, due.occurs_at_utc AS DueAtUtc
                  FROM due
                  JOIN reminders r ON r.id = due.reminder_id
                  JOIN users u ON u.id = r.user_id
                """, new { Until = until, BatchSize = batchSize }, tx, cancellationToken: ct))).ToList();

            var results = new List<DueReminder>(singles.Count + occurrences.Count);
            var keyCache = new Dictionary<long, byte[]>();

            foreach (var row in singles.Concat(occurrences))
            {
                if (!keyCache.TryGetValue(row.UserId, out var dek))
                {
                    var loaded = await LoadDataKeyAsync(connection, tx, row.UserId, create: false, ct);
                    if (loaded is null)
                    {
                        continue;
                    }

                    dek = loaded;
                    keyCache[row.UserId] = dek;
                }

                results.Add(new DueReminder(row.PublicId, row.UserPublicId,
                    envelope.Decrypt(dek, row.BodyCiphertext, AssociatedData(row.UserId)), row.DueAtUtc));
            }

            foreach (var key in keyCache.Values)
            {
                CryptographicOperations.ZeroMemory(key);
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
                 WHERE r.deleted_at IS NULL AND r.rrule IS NOT NULL AND r.is_completed = false
                 LIMIT 1000
                """, transaction: tx, cancellationToken: ct))).ToList();

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

        var written = 0;
        foreach (var occursAt in occurrences)
        {
            written += await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO reminder_occurrences (reminder_id, occurs_at_utc)
                VALUES (@Id, @OccursAt)
                ON CONFLICT (reminder_id, occurs_at_utc) DO NOTHING
                """, new { row.Id, OccursAt = occursAt }, tx, cancellationToken: ct));
        }

        return written;
    }

    // ── envelope encryption ──────────────────────────────────────────────────

    /// <summary>
    /// Loads (and, when asked, creates) the user's data key. The DEK exists only
    /// in memory and only for the duration of a call; the wrapped copy in the
    /// database is useless without the KEK, which is not in the database.
    /// </summary>
    /// <summary>
    /// Loads the user's data key for a read, treating "cannot unwrap it" as
    /// "cannot read the bodies" rather than as a failed request.
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
        NpgsqlConnection connection, long userId, CancellationToken ct)
    {
        try
        {
            return await LoadDataKeyAsync(connection, null, userId, create: false, ct);
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

    internal const string SelectSql = """
        SELECT r.id AS Id, r.public_id AS PublicId, r.user_id AS UserId,
               r.body_ciphertext AS BodyCiphertext, r.body_dek_id AS BodyDekId,
               r.due_at_utc AS DueAtUtc, r.due_local_time AS DueLocalTime, r.timezone AS Timezone,
               r.rrule AS Rrule, r.recurrence_until AS RecurrenceUntil,
               r.is_completed AS IsCompleted, r.completed_at AS CompletedAt,
               r.created_at AS CreatedAt, r.updated_at AS UpdatedAt
          FROM reminders r
        """;

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

    private ReminderResponse ToResponse(ReminderRow row, byte[]? dek)
    {
        // A row that will not decrypt must not take the list down with it.
        //
        // Letting the exception out returned 500 for `GET /v2/reminders`, so one
        // unreadable row removed the entire feature — no list, no way to find
        // the bad row, no way to delete it. That happens for real after a KEK
        // rotation done wrongly (09 §2.11), after a restore that mixes eras, or
        // if a ciphertext is damaged. The schedule, the recurrence and the
        // completion state are all still readable; only the body is lost, and
        // saying so is far more useful than failing the request.
        // No key means the bodies cannot be read at all — see TryLoadDataKeyAsync.
        var body = UnreadableBody;

        if (dek is not null)
        {
            try
            {
                body = envelope.Decrypt(dek, row.BodyCiphertext, AssociatedData(row.UserId));
            }
            catch (System.Security.Cryptography.CryptographicException ex)
            {
                logger.LogError(ex,
                    "Reminder {ReminderId} could not be decrypted. Its key encryption key is wrong or the row is damaged.",
                    row.PublicId);
                body = UnreadableBody;
            }
        }

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
            row.UpdatedAt);
    }

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

    private static async Task<string> ResolveTimezoneAsync(
        NpgsqlConnection connection, NpgsqlTransaction? tx, long userId, string? requested, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (Normalise.IanaTimeZoneOrDefault(requested, " ") == " ")
            {
                throw AppException.Unprocessable(ErrorCodes.ReminderTimezoneInvalid,
                    $"'{requested}' is not a known IANA timezone.");
            }

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

    private sealed record DueRow
    {
        public Guid PublicId { get; init; }
        public Guid UserPublicId { get; init; }
        public long UserId { get; init; }
        public byte[] BodyCiphertext { get; init; } = [];
        public DateTimeOffset DueAtUtc { get; init; }
    }
}

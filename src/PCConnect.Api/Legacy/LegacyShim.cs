using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PCConnect.Api.Configuration;
using PCConnect.Api.Http;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using PCConnect.Core.Domain;
using PCConnect.Infrastructure.Contexts;
using PCConnect.Infrastructure.Contexts.Commands;
using PCConnect.Infrastructure.Contexts.Devices;
using PCConnect.Infrastructure.Contexts.Identity;
using PCConnect.Infrastructure.Contexts.Reminders;
using PCConnect.Infrastructure.Telemetry;

namespace PCConnect.Api.Legacy;

/// <summary>
/// The v1 compatibility shim (04 §5).
///
/// It reproduces the exact wire format the installed VB.NET and Java clients
/// expect, over the v2 services — it has no database access of its own, so there
/// is one implementation of every rule. Every response carries `Deprecation` and
/// `Sunset`, and every request increments a labelled counter. That counter, not
/// a guess, is what decides when this file can be deleted.
///
/// The shapes below were derived from the clients themselves, not from
/// `api/api_spec.md`, which documents a gateway that was never deployed. Where
/// the two disagree the client wins, because the client is what is installed.
/// </summary>
public static class LegacyShim
{
    private const string ApiKeyHeader = "X-API-Key";
    private const string PcNameHeader = "PCName";

    /// <summary>
    /// v1 emitted property names exactly as its PHP arrays spelled them -
    /// `PCNames`, `ID`, `Reminder`, `message`. The v2 API is camelCase; this
    /// serialiser keeps the shim on v1's spelling, because the installed clients
    /// index these payloads by name.
    /// </summary>
    private static readonly JsonSerializerOptions LegacyJson = new()
    {
        PropertyNamingPolicy = null,
        DictionaryKeyPolicy = null,
    };

    public static IEndpointRouteBuilder MapLegacyShim(this IEndpointRouteBuilder app)
    {
        // Mounted twice: `/api/*` is what the installed clients call, `/legacy/*`
        // is the documented name for the same surface.
        foreach (var prefix in new[] { "/api", "/legacy" })
        {
            var group = app.MapGroup(prefix).WithTags("Legacy").ExcludeFromDescription();
            MapAll(group);
        }

        return app;
    }

    private static void MapAll(RouteGroupBuilder group)
    {
        // ── auth ─────────────────────────────────────────────────────────────

        group.MapPost("/login.php", async (HttpContext http, IdentityService identity, CommandMetrics metrics, CancellationToken ct) =>
        {
            metrics.LegacyRequest("login");
            Deprecate(http);

            var form = await http.Request.ReadFormAsync(ct);
            var username = form["loginUsername"].FirstOrDefault() ?? string.Empty;
            var password = form["loginPassword"].FirstOrDefault() ?? string.Empty;

            try
            {
                // The installed clients send an unsalted SHA-256, so the request
                // is routed down the legacy verification path. That path can
                // authenticate but can never upgrade the stored hash (02 §6).
                var request = Normalise.LooksLikeLegacyHash(password)
                    ? new LoginRequest(username, null, password, ClientKinds.Legacy, "v1")
                    : new LoginRequest(username, password, null, ClientKinds.Legacy, "v1");

                var ctx = http.RequestContext();

                // Verify without minting a session: the compatibility key below
                // is the only credential this client gets, and an extra unused
                // token pair would clutter the user's session list.
                var userId = await identity.VerifyCredentialsAsync(request, ctx, ct);
                var apiKey = await identity.IssueLegacyCompatTokenAsync(userId, ctx, ct);

                return Text(apiKey);
            }
            catch (AppException)
            {
                // Byte-for-byte the string the VB client compares against.
                return Text("Invalid username or password.");
            }
        });

        // ── time and connectivity ────────────────────────────────────────────

        group.MapGet("/time.php", (HttpContext http, IClock clock, CommandMetrics metrics) =>
        {
            metrics.LegacyRequest("time");
            Deprecate(http);

            // The VB client parses this as a dictionary of strings and posts the
            // value straight back as its heartbeat.
            return Results.Json(
                new Dictionary<string, string>
                {
                    ["time"] = clock.UtcNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                },
                LegacyJson);
        });

        group.MapGet("/pcconnect/checkinternet.php", (HttpContext http, CommandMetrics metrics) =>
        {
            metrics.LegacyRequest("checkinternet");
            Deprecate(http);

            // `PCClient.vb:380` treats anything other than "yes" as offline and
            // greys out its indicator. `api/api_spec.md` documents "Pong" for the
            // never-deployed Node gateway; the installed client wins (see 04 §5).
            return Text("yes");
        });

        group.MapGet("/pcconnect/checkinternet", (HttpContext http, CommandMetrics metrics) =>
        {
            metrics.LegacyRequest("checkinternet");
            Deprecate(http);
            return Text("yes");
        });

        // ── devices ──────────────────────────────────────────────────────────

        foreach (var path in new[] { "/pcclient/PCNames.php", "/pcconnect/PCNames.php" })
        {
            group.MapGet(path, async (HttpContext http, DeviceService devices, IdentityService identity, CommandMetrics metrics, CancellationToken ct) =>
            {
                metrics.LegacyRequest("pcnames");
                Deprecate(http);

                var caller = await AuthenticateAsync(http, identity, ct);
                var list = await devices.ListAsync(caller, ct);

                return Results.Json(new LegacyPcNames(list.Select(d => d.DisplayName).ToArray()), LegacyJson);
            });
        }

        group.MapGet("/pcclient/addpc.php", async (HttpContext http, DeviceService devices, IdentityService identity, CommandMetrics metrics, CancellationToken ct) =>
        {
            metrics.LegacyRequest("addpc");
            Deprecate(http);

            var caller = await AuthenticateAsync(http, identity, ct);
            var device = await devices.ResolveLegacyDeviceAsync(
                caller, http.Request.Headers[PcNameHeader].FirstOrDefault(), http.RequestContext(), createIfMissing: true, ct);

            return Text($"PC {device.DisplayName} added successfully");
        });

        group.MapPost("/pcclient/updatepctimedatabase.php", async (HttpContext http, DeviceService devices, IdentityService identity, CommandMetrics metrics, CancellationToken ct) =>
        {
            metrics.LegacyRequest("heartbeat");
            Deprecate(http);

            var caller = await AuthenticateAsync(http, identity, ct);
            var device = await devices.ResolveLegacyDeviceAsync(
                caller, http.Request.Headers[PcNameHeader].FirstOrDefault(), http.RequestContext(), createIfMissing: true, ct);

            await devices.HeartbeatAsync(DeviceService.AsLegacyDeviceCaller(caller, device),
                new HeartbeatRequest("v1", string.Empty), ct);

            return Text("Time updated successfully");
        });

        // ── commands ─────────────────────────────────────────────────────────

        group.MapGet("/pcclient/findrequests.php", async (HttpContext http, DeviceService devices, IdentityService identity, CommandService commands, CommandMetrics metrics, CancellationToken ct) =>
        {
            metrics.LegacyRequest("findrequests");
            Deprecate(http);

            var caller = await AuthenticateAsync(http, identity, ct);
            var device = await devices.ResolveLegacyDeviceAsync(
                caller, http.Request.Headers[PcNameHeader].FirstOrDefault(), http.RequestContext(), createIfMissing: true, ct);

            var pending = await commands.ClaimPendingAsync(
                DeviceService.AsLegacyDeviceCaller(caller, device), http.RequestContext(), ct);

            // The v1 client dispatches on a bare capitalised string and has room
            // for exactly one pending command; the rest stay delivered and expire
            // on their TTL rather than being executed later out of order.
            return Text(pending.Count == 0 ? string.Empty : ToLegacyCommand(pending[0].Type));
        });

        foreach (var method in new[] { "GET", "POST" })
        {
            group.MapMethods("/pcclient/updaterequest.php", [method],
                async (HttpContext http, DeviceService devices, IdentityService identity, CommandService commands, CommandMetrics metrics, CancellationToken ct) =>
            {
                metrics.LegacyRequest("updaterequest");
                Deprecate(http);

                var caller = await AuthenticateAsync(http, identity, ct);
                var device = await devices.ResolveLegacyDeviceAsync(
                    caller, http.Request.Headers[PcNameHeader].FirstOrDefault(), http.RequestContext(), createIfMissing: true, ct);

                var deviceCaller = DeviceService.AsLegacyDeviceCaller(caller, device);

                // v1 has no per-command ack: it clears the mailbox. The nearest
                // honest translation is to acknowledge everything currently
                // delivered to this device, which is what "cleared" meant.
                var recent = await commands.ListAsync(caller, null, 20, device.PublicId, ct);
                foreach (var command in recent.Items.Where(c => c.Status == CommandStatuses.Delivered))
                {
                    try
                    {
                        await commands.AckAsync(deviceCaller, Guid.Parse(command.Id),
                            new AckCommandRequest(CommandOutcomes.Ok, "legacy", "acknowledged by v1 client"),
                            http.RequestContext(), ct);
                    }
                    catch (AppException)
                    {
                        // Already terminal: another path acknowledged it first.
                    }
                }

                return Text("Request cleared properly");
            });
        }

        group.MapPost("/pcconnect/exchange.php", async (HttpContext http, DeviceService devices, IdentityService identity, CommandService commands, CommandMetrics metrics, CancellationToken ct) =>
        {
            metrics.LegacyRequest("exchange");
            Deprecate(http);

            var caller = await AuthenticateAsync(http, identity, ct);
            var form = await http.Request.ReadFormAsync(ct);
            var requested = form["Request"].FirstOrDefault() ?? string.Empty;

            var device = await devices.ResolveLegacyDeviceAsync(
                caller, http.Request.Headers[PcNameHeader].FirstOrDefault(), http.RequestContext(), createIfMissing: true, ct);

            if (!CommandTypes.TryNormalise(requested, out var commandType))
            {
                return Text("Invalid request");
            }

            // v1 has no client-generated id, so the shim generates one. That
            // loses idempotency for these clients — a retry from a v1 client can
            // issue a second command. Recorded as a known limitation of the shim
            // rather than papered over.
            await commands.IssueAsync(caller, new IssueCommandRequest(
                Guid.CreateVersion7().ToString(),
                device.PublicId.ToString(),
                commandType,
                null,
                (int)CommandTtl.Default.TotalSeconds), http.RequestContext(), ct);

            return Results.Json(new LegacyMessage("Success"), LegacyJson);
        });

        // ── reminders ────────────────────────────────────────────────────────

        foreach (var path in new[] { "/pcclient/listreminders.php", "/pcconnect/listreminders.php" })
        {
            group.MapGet(path, async (HttpContext http, IdentityService identity, ReminderService reminders, CommandMetrics metrics, CancellationToken ct) =>
            {
                metrics.LegacyRequest("listreminders");
                Deprecate(http);

                var caller = await AuthenticateAsync(http, identity, ct);
                var list = await reminders.ListLegacyAsync(caller, includeCompleted: true, ct);

                return Results.Json(list.Select(r => ToLegacyReminder(r, caller.UserId)).ToArray(), LegacyJson);
            });
        }

        group.MapGet("/pcclient/getreminder.php", async (HttpContext http, IdentityService identity, ReminderService reminders, CommandMetrics metrics, CancellationToken ct) =>
        {
            metrics.LegacyRequest("getreminder");
            Deprecate(http);

            var caller = await AuthenticateAsync(http, identity, ct);
            var next = await reminders.NextDueLegacyAsync(caller, ct);

            if (next is null)
            {
                // The VB client parses this into a dictionary and catches the
                // failure, which is how v1 signalled "nothing due".
                return Results.Json(new Dictionary<string, string>(), LegacyJson);
            }

            var local = ToLocal(next.DueAt, next.Timezone);
            return Results.Json(new Dictionary<string, string>
            {
                ["id"] = next.Id.ToString(CultureInfo.InvariantCulture),
                ["date"] = local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["time"] = local.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                ["reminder"] = next.Body,
            }, LegacyJson);
        });

        group.MapPost("/pcclient/completereminder.php", async (HttpContext http, IdentityService identity, ReminderService reminders, CommandMetrics metrics, CancellationToken ct) =>
        {
            metrics.LegacyRequest("completereminder");
            Deprecate(http);

            var caller = await AuthenticateAsync(http, identity, ct);
            var form = await http.Request.ReadFormAsync(ct);

            if (!long.TryParse(form["id"].FirstOrDefault(), CultureInfo.InvariantCulture, out var id))
            {
                return Text("Invalid id");
            }

            await reminders.CompleteLegacyAsync(caller, id, ct);
            return Text("Reminder completed");
        });

        foreach (var path in new[] { "/pcconnect/reminder.php", "/pcclient/reminder.php" })
        {
            group.MapPost(path, async (HttpContext http, IdentityService identity, ReminderService reminders, CommandMetrics metrics, CancellationToken ct) =>
            {
                metrics.LegacyRequest("createreminder");
                Deprecate(http);

                var caller = await AuthenticateAsync(http, identity, ct);
                var form = await http.Request.ReadFormAsync(ct);

                var date = form["date"].FirstOrDefault() ?? string.Empty;
                var time = form["time"].FirstOrDefault() ?? "00:00";
                var body = form["reminder"].FirstOrDefault() ?? string.Empty;

                var profile = await identity.GetProfileAsync(caller.UserId, ct)
                    ?? throw AppException.NotFound(ErrorCodes.AccountNotFound, "No such account.");

                if (!TryParseLegacyLocal(date, time, profile.Timezone, out var dueAt))
                {
                    return Text("Invalid date or time");
                }

                await reminders.CreateAsync(caller,
                    new CreateReminderRequest(body, dueAt, profile.Timezone), ct);

                return Results.Json(new LegacyMessage("Reminder added"), LegacyJson);
            });
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static async Task<CallerIdentity> AuthenticateAsync(HttpContext http, IdentityService identity, CancellationToken ct)
    {
        var apiKey = http.Request.Headers[ApiKeyHeader].FirstOrDefault()
            ?? http.Request.Headers["X-API-KEY"].FirstOrDefault();

        var caller = await identity.ResolveLegacyApiKeyAsync(apiKey ?? string.Empty, ct);

        return caller ?? throw AppException.Unauthorized(ErrorCodes.AuthTokenInvalid, "Invalid API key.");
    }

    /// <summary>
    /// Every shim response says it is deprecated and when it goes away. Machines
    /// that never read a header still get counted by the Prometheus label; the
    /// header is for whoever reads a trace or a proxy log.
    /// </summary>
    private static void Deprecate(HttpContext http)
    {
        var options = http.RequestServices.GetRequiredService<IOptions<DiscoveryOptions>>().Value;

        http.Response.Headers["Deprecation"] = "true";
        http.Response.Headers["Link"] = "<https://pcconnect.example/download>; rel=\"successor-version\"";

        if (options.LegacySunsetAt is { } sunset)
        {
            http.Response.Headers["Sunset"] = sunset.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    private static IResult Text(string value) => Results.Text(value, "text/plain; charset=utf-8");

    private static string ToLegacyCommand(string commandType) => commandType switch
    {
        CommandTypes.Shutdown => "Shutdown",
        CommandTypes.Restart => "Restart",
        CommandTypes.SignOut => "Signout",
        CommandTypes.Lock => "Lock",
        CommandTypes.Sleep => "Sleep",
        CommandTypes.Hibernate => "Hibernate",
        _ => string.Empty,
    };

    private static LegacyReminderDto ToLegacyReminder(ReminderService.LegacyReminder reminder, long userId)
    {
        var local = ToLocal(reminder.DueAt, reminder.Timezone);
        return new LegacyReminderDto(
            reminder.Id,
            userId,
            local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            local.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            reminder.Body,
            reminder.IsCompleted ? 1 : 0);
    }

    private static DateTimeOffset ToLocal(DateTimeOffset instant, string timezone)
    {
        try
        {
            return TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.FindSystemTimeZoneById(timezone));
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return instant;
        }
    }

    private static bool TryParseLegacyLocal(string date, string time, string timezone, out DateTimeOffset dueAt)
    {
        dueAt = default;

        string[] dateFormats = ["yyyy-MM-dd", "dd/MM/yyyy", "dd/MM/yy", "MM/dd/yyyy"];
        if (!DateTime.TryParseExact(date.Trim(), dateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedDate))
        {
            return false;
        }

        if (!TimeSpan.TryParse(time.Trim(), CultureInfo.InvariantCulture, out var parsedTime))
        {
            return false;
        }

        var local = DateTime.SpecifyKind(parsedDate.Date.Add(parsedTime), DateTimeKind.Unspecified);

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(timezone);
            dueAt = new DateTimeOffset(local, tz.GetUtcOffset(local)).ToUniversalTime();
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            dueAt = new DateTimeOffset(local, TimeSpan.Zero);
        }

        return true;
    }

    // The v1 payload shapes, with v1's casing. `PCNames`, `ID` and `Reminder`
    // are wrong by every convention in 04 §3 and are reproduced exactly, because
    // the installed clients parse these names.
    private sealed record LegacyPcNames(string[] PCNames);

    private sealed record LegacyMessage(string message);

    private sealed record LegacyReminderDto(long ID, long Username, string Date, string Time, string Reminder, int Completed);
}

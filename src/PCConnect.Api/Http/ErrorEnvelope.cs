using System.Net;
using Microsoft.AspNetCore.Diagnostics;
using PCConnect.Core;
using PCConnect.Core.Contracts;
using PCConnect.Infrastructure.Contexts;

namespace PCConnect.Api.Http;

/// <summary>
/// One error envelope for every failure, replacing the four shapes the previous
/// generations returned (bare text, {error,message}, {success,data}, raw arrays).
/// 5xx never carries an internal message or a stack trace (04 §3.1).
/// </summary>
public sealed class ErrorEnvelopeHandler(ILogger<ErrorEnvelopeHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var requestId = context.TraceIdentifier;

        switch (exception)
        {
            case AppException app:
            {
                if (app.RetryAfter is { } retryAfter)
                {
                    context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds))
                        .ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                // Deliberate failures are business outcomes, not incidents: they
                // are logged at information so a wrong password does not page
                // anyone, while 5xx below is logged at error.
                logger.LogInformation("{Code} on {Method} {Path}: {Message}",
                    app.Code, context.Request.Method, context.Request.Path, app.Message);

                await WriteAsync(context, app.Status, app.Code, app.Message, requestId,
                    app.Details.Select(d => new ErrorDetailDto(d.Field, d.Issue)).ToList(), ct);
                return true;
            }

            case BadHttpRequestException bad:
            {
                await WriteAsync(context, HttpStatusCode.BadRequest, ErrorCodes.Malformed,
                    "The request body could not be read.", requestId, null, ct);
                logger.LogInformation(bad, "Malformed request on {Path}", context.Request.Path);
                return true;
            }

            case OperationCanceledException when context.RequestAborted.IsCancellationRequested:
                return true;

            default:
            {
                logger.LogError(exception, "Unhandled exception on {Method} {Path} ({RequestId})",
                    context.Request.Method, context.Request.Path, requestId);

                await WriteAsync(context, HttpStatusCode.InternalServerError, ErrorCodes.Internal,
                    "Something went wrong. If it keeps happening, quote the request id.", requestId, null, ct);
                return true;
            }
        }
    }

    private static async Task WriteAsync(
        HttpContext context, HttpStatusCode status, string code, string message,
        string requestId, IReadOnlyList<ErrorDetailDto>? details, CancellationToken ct)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = (int)status;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(
            new ErrorEnvelope(new ErrorBody(code, message, requestId, details)), ct);
    }
}

/// <summary>
/// Correlation and the ambient request facts the contexts need. The id is echoed
/// on every response and appears in every log line, so a user quoting it is
/// enough to find the request (01 §6.5).
/// </summary>
public sealed class RequestContextMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Request-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();

        // An id supplied by the caller is used only if it is short and printable:
        // it ends up in logs, and an unbounded caller-controlled value in a log
        // line is a log-injection vector.
        context.TraceIdentifier = IsUsable(incoming) ? incoming! : Guid.CreateVersion7().ToString("N");

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = context.TraceIdentifier;
            return Task.CompletedTask;
        });

        await next(context);
    }

    private static bool IsUsable(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}

public static class HttpContextExtensions
{
    public static RequestContext RequestContext(this HttpContext context) =>
        new(context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.FirstOrDefault() ?? string.Empty,
            context.TraceIdentifier);
}

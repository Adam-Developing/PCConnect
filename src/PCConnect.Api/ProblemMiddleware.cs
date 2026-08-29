using System.Diagnostics;
using System.Text.Json;
using PCConnect.Contracts.V2;
using PCConnect.Domain.Commands;
using PCConnect.Infrastructure.Identity;
using PCConnect.Infrastructure.Observability;

namespace PCConnect.Api;

public sealed class ProblemMiddleware(RequestDelegate next, ILogger<ProblemMiddleware> logger)
{
    private static readonly Action<ILogger, string, Exception?> UnhandledFailure = LoggerMessage.Define<string>(LogLevel.Error, new EventId(5001, nameof(UnhandledFailure)), "Unhandled request failure {CorrelationId}");
    private static readonly Action<ILogger, string, string, Exception?> RequestRejected = LoggerMessage.Define<string, string>(LogLevel.Information, new EventId(4001, nameof(RequestRejected)), "Request rejected with {Code} ({CorrelationId})");

    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers["X-Correlation-ID"] = CorrelationId(context);
        try { await next(context); }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var (status, code, title, log) = exception switch
            {
                AuthenticationFailureException authentication => (StatusCodes.Status401Unauthorized, authentication.Code, "Authentication failed", false),
                ConflictException conflict => (StatusCodes.Status409Conflict, conflict.Code, "Conflict", false),
                ResourceNotFoundException notFound => (StatusCodes.Status404NotFound, notFound.Code, "Not found", false),
                ResourceGoneException gone => (StatusCodes.Status410Gone, gone.Code, "Gone", false),
                RequestRateLimitedException limited => (StatusCodes.Status429TooManyRequests, limited.Code, "Too many requests", false),
                DomainException domain => (StatusCodes.Status422UnprocessableEntity, domain.Code, "Request rejected", false),
                BadHttpRequestException badRequest => (badRequest.StatusCode, badRequest.StatusCode == StatusCodes.Status413PayloadTooLarge ? "request_too_large" : "validation_failed", "Request body rejected", false),
                ArgumentException => (StatusCodes.Status400BadRequest, "validation_failed", "Validation failed", false),
                _ => (StatusCodes.Status500InternalServerError, "internal_error", "An unexpected error occurred", true)
            };
            if (log) UnhandledFailure(logger, CorrelationId(context), exception);
            else RequestRejected(logger, code, CorrelationId(context), null);
            if (code == "refresh_token_reuse") PCConnectTelemetry.RecordTokenReuse();
            await WriteProblemAsync(
                context,
                status,
                code,
                title,
                status < 500 && exception is not BadHttpRequestException ? exception.Message : null);
        }
        finally
        {
            var path = context.Request.Path.Value ?? string.Empty;
            if (path.StartsWith("/api/v2/auth/", StringComparison.Ordinal))
                PCConnectTelemetry.RecordAuthentication(context.Response.StatusCode);
            if (!path.StartsWith("/api/v2/", StringComparison.Ordinal))
                PCConnectTelemetry.RecordCompatibilityRequest(path, context.Response.StatusCode);
        }
    }

    public static string CorrelationId(HttpContext context) =>
        Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

    public static async Task WriteProblemAsync(HttpContext context, int status, string code, string title, string? detail = null)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await JsonSerializer.SerializeAsync(context.Response.Body, new Problem(
            $"https://pcconnect.adamdeveloping.co.uk/problems/{code}",
            title,
            status,
            code,
            CorrelationId(context),
            detail,
            context.Request.Path),
            JsonSerializerOptions.Web,
            context.RequestAborted);
    }
}

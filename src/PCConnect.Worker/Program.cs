using PCConnect.Infrastructure;
using PCConnect.Worker;
using PCConnect.Infrastructure.Observability;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);
if (Directory.Exists("/run/secrets")) builder.Configuration.AddKeyPerFile("/run/secrets", optional: false);
builder.Services.AddPCConnectInfrastructure(builder.Configuration);
var release = builder.Configuration["Release"] ?? "unknown";
builder.Logging.AddJsonConsole();
builder.Logging.AddOpenTelemetry(options =>
{
    options.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("pcconnect-worker", serviceVersion: release));
    options.IncludeFormattedMessage = true;
    options.IncludeScopes = true;
    options.AddOtlpExporter();
});
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService("pcconnect-worker", serviceVersion: release))
    .WithTracing(tracing => tracing
        .AddSource(PCConnectTelemetry.ActivitySourceName, "Npgsql")
        .AddHttpClientInstrumentation(options => options.RecordException = true)
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddMeter(PCConnectTelemetry.MeterName, "System.Net.Http", "Npgsql")
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddOtlpExporter());
builder.Services.AddHostedService<CommandExpiryWorker>();
builder.Services.AddHostedService<ReminderSchedulerWorker>();
builder.Services.AddHostedService<PresenceWorker>();
builder.Services.AddHostedService<OutboxPublisherWorker>();
builder.Services.AddHostedService<EmailDeliveryWorker>();
builder.Services.AddHostedService<AccountDeletionWorker>();
builder.Services.AddHostedService<DataExportWorker>();
builder.Services.AddHostedService<RetentionWorker>();
builder.Services.AddHostedService<OperationalMetricsWorker>();
var host = builder.Build();
await host.RunAsync();

using System.Net;
using Foundry.Broker;
using Foundry.Services;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine("State", "logs", "foundry-broker-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14,
        fileSizeLimitBytes: 10_485_760,
        shared: true)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

var configuredHost = builder.Configuration["Broker:Host"] ?? FoundryBrokerDefaults.Host;
var configuredPort =
    builder.Configuration.GetValue<int?>("Broker:Port") is { } parsedPort && parsedPort > 0
        ? parsedPort
        : FoundryBrokerDefaults.Port;
if (!IPAddress.TryParse(configuredHost, out var ipAddress))
{
    ipAddress = IPAddress.Loopback;
    configuredHost = FoundryBrokerDefaults.Host;
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(ipAddress, configuredPort);
});

var baseUrl = FoundryBrokerDefaults.BuildBaseUrl(configuredHost, configuredPort);
builder.Services.AddSingleton(
    new FoundryBrokerRuntimeMetadata
    {
        Host = configuredHost,
        Port = configuredPort,
        BaseUrl = baseUrl,
        StartedAt = DateTimeOffset.Now,
        LoopbackOnly = true,
    }
);
builder.Services.AddSingleton<FoundryOrchestrator>();
builder.Services.AddHostedService<FoundryJobWorker>();
builder.Services.AddHostedService<JobRetentionWorker>();
builder.Services.AddHostedService<JobSchedulerWorker>();

var app = builder.Build();
var logger = app.Logger;

app.UseSerilogRequestLogging();

app.MapHealthEndpoints(logger);
app.MapMLEndpoints(logger);
app.MapKnowledgeEndpoints(logger);
app.MapScheduleEndpoints(logger);

app.Run();

// Expose the entry-point type so WebApplicationFactory<Program> can reference it from tests.
public partial class Program { }

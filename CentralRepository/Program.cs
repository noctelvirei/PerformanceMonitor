using Microsoft.Extensions.Options;
using ModelContextProtocol.AspNetCore;
using PerformanceMonitor.CentralRepository.Mcp;
using PerformanceMonitor.CentralRepository.Models;
using PerformanceMonitor.CentralRepository.Services;
using PerformanceMonitor.CentralRepository.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();
builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection("Monitor"));
builder.Services.AddSingleton<CentralRepositoryStore>();
builder.Services.AddSingleton<SqlServerCentralRepositoryStore>();
builder.Services.AddSingleton<ICentralRepositoryStore, RoutingCentralRepositoryStore>();
builder.Services.AddSingleton<ICentralRepository>(serviceProvider => serviceProvider.GetRequiredService<ICentralRepositoryStore>());
builder.Services.AddSingleton<IEstateTelemetryReader>(serviceProvider => serviceProvider.GetRequiredService<ICentralRepositoryStore>());
builder.Services.AddSingleton<MonitorSettingsService>();
builder.Services.AddSingleton<MonitorSettingsConfigurationPersistence>();
builder.Services.AddSingleton<SqlInstanceDiscoveryService>();
builder.Services.AddSingleton<CentralToolService>();
builder.Services.AddSingleton<McpAccessService>();
builder.Services.AddSingleton<CollectionSnapshotIntakeService>();
builder.Services.AddSingleton<CollectionRunScheduler>();
builder.Services.AddSingleton<SqlCollectorExecutor>();
builder.Services.AddHostedService<SqlEstateCollectorService>();
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "PerformanceMonitorCentralRepository",
            Version = "2.10.0"
        };
        options.ServerInstructions = CentralMcpInstructions.Text;
    })
    .WithHttpTransport()
    .WithTools<CentralMcpTools>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", generated_at = DateTime.UtcNow }));

app.MapGet("/api/storage", (ICentralRepository repository) => Results.Ok(repository.GetStorageInfo()));

app.MapGet("/api/summary", async (IEstateTelemetryReader reader, CancellationToken cancellationToken)
    => Results.Ok(await reader.GetEstateSummaryAsync(cancellationToken)));

app.MapGet("/api/servers", async (IEstateTelemetryReader reader, CancellationToken cancellationToken)
    => Results.Ok(await reader.GetServersAsync(cancellationToken)));

app.MapGet("/api/alerts", async (IEstateTelemetryReader reader, CancellationToken cancellationToken)
    => Results.Ok(await reader.GetEstateActiveAlertsAsync(cancellationToken)));

app.MapGet("/api/settings", (MonitorSettingsService settings)
    => Results.Ok(settings.GetSettings()));

app.MapPut("/api/settings", async (
    CentralRepositorySettingsDto request,
    MonitorSettingsService settings,
    CancellationToken cancellationToken) =>
{
    await settings.SaveSettingsAsync(request, cancellationToken);
    return Results.Ok(settings.GetSettings());
});

app.MapPost("/api/settings/test-connection", async (
    TestConnectionRequest request,
    MonitorSettingsService settings,
    CancellationToken cancellationToken) =>
{
    var result = await settings.TestConnectionAsync(request.Server, cancellationToken);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/settings/test-repository", async (
    TestRepositoryRequest request,
    MonitorSettingsService settings,
    CancellationToken cancellationToken) =>
{
    var result = await settings.TestRepositoryAsync(request.Repository, cancellationToken);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/settings/discover-servers", async (
    SqlInstanceDiscoveryRequest request,
    SqlInstanceDiscoveryService discovery,
    CancellationToken cancellationToken) =>
{
    var result = await discovery.DiscoverAsync(request, cancellationToken);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapPost("/api/settings/discovery-jobs", (
    SqlInstanceDiscoveryRequest request,
    SqlInstanceDiscoveryService discovery) =>
{
    var job = discovery.StartDiscovery(request);
    return Results.Accepted($"/api/settings/discovery-jobs/{job.JobId}", job);
});

app.MapGet("/api/settings/discovery-jobs/{jobId}", (
    string jobId,
    SqlInstanceDiscoveryService discovery) =>
{
    var job = discovery.GetDiscoveryJob(jobId);
    return job is null
        ? Results.NotFound(new { message = "Discovery job not found." })
        : Results.Ok(job);
});

app.MapGet("/api/collection-log", async (IEstateTelemetryReader reader, int? limit, CancellationToken cancellationToken)
    => Results.Ok(await reader.GetCollectionLogAsync(limit ?? 200, cancellationToken)));

app.MapGet("/api/servers/{serverId}/waits", async (
    string serverId,
    IEstateTelemetryReader reader,
    int? hours,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var rows = await reader.GetTopWaitsAsync(serverId, hours ?? 1, limit ?? 20, cancellationToken);
    return Results.Ok(rows);
});

app.MapGet("/api/servers/{serverId}/cpu", async (
    string serverId,
    IEstateTelemetryReader reader,
    int? hours,
    CancellationToken cancellationToken) =>
{
    var rows = await reader.GetCpuSamplesAsync(serverId, hours ?? 1, cancellationToken);
    return Results.Ok(rows);
});

app.MapGet("/api/servers/{serverId}/waiting-tasks", async (
    string serverId,
    IEstateTelemetryReader reader,
    int? hours,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var rows = await reader.GetWaitingTasksAsync(serverId, hours ?? 1, limit ?? 50, cancellationToken);
    return Results.Ok(rows);
});

app.MapGet("/api/servers/{serverId}/collectors/{collectorName}/samples", async (
    string serverId,
    string collectorName,
    IEstateTelemetryReader reader,
    int? hours,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var rows = await reader.GetCollectorSamplesAsync(serverId, collectorName, hours ?? 1, limit ?? 100, cancellationToken);
    return Results.Ok(rows);
});

app.MapGet("/api/servers/{serverId}/experience", async (
    string serverId,
    IEstateTelemetryReader reader,
    int? hours,
    CancellationToken cancellationToken) =>
{
    var experience = await reader.GetServerExperienceAsync(serverId, hours ?? 1, cancellationToken);
    return Results.Ok(experience);
});

app.MapPost("/api/ingest/snapshot", async (
    IngestSnapshotDto request,
    HttpRequest httpRequest,
    IOptionsMonitor<MonitorOptions> options,
    CollectionSnapshotIntakeService intake,
    CancellationToken cancellationToken) =>
{
    var configuredApiKey = options.CurrentValue.IngestApiKey;
    if (string.IsNullOrWhiteSpace(configuredApiKey))
    {
        return Results.Problem("Ingest API is disabled until an API key is configured in Settings.", statusCode: StatusCodes.Status403Forbidden);
    }

    if (!httpRequest.Headers.TryGetValue("X-PerformanceMonitor-Key", out var providedApiKey)
        || !string.Equals(providedApiKey.ToString(), configuredApiKey, StringComparison.Ordinal))
    {
        return Results.Problem("Invalid ingest API key.", statusCode: StatusCodes.Status401Unauthorized);
    }

    if (string.IsNullOrWhiteSpace(request.Server.Id))
    {
        return Results.BadRequest("Server id is required.");
    }

    return Results.Ok(await intake.AcceptRemoteAsync(request, cancellationToken));
});

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp"))
    {
        var access = context.RequestServices.GetRequiredService<McpAccessService>();
        if (!await access.AuthorizeMcpRequestAsync(context))
        {
            return;
        }
    }

    await next();
});

app.MapMcp("/mcp");

app.MapFallbackToFile("index.html");

app.Run();

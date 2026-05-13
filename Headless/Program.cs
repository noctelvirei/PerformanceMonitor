using Microsoft.Extensions.Options;
using PerformanceMonitor.Headless.Models;
using PerformanceMonitor.Headless.Services;
using PerformanceMonitor.Headless.Storage;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService();
builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection("Monitor"));
builder.Services.AddSingleton<HeadlessStore>();
builder.Services.AddSingleton<SqlServerHeadlessStore>();
builder.Services.AddSingleton<IHeadlessStore, RoutingHeadlessStore>();
builder.Services.AddSingleton<MonitorSettingsService>();
builder.Services.AddHostedService<SqlEstateCollectorService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", generated_at = DateTime.UtcNow }));

app.MapGet("/api/storage", (IHeadlessStore store) => Results.Ok(store.GetStorageInfo()));

app.MapGet("/api/summary", async (IHeadlessStore store, CancellationToken cancellationToken)
    => Results.Ok(await store.GetEstateSummaryAsync(cancellationToken)));

app.MapGet("/api/servers", async (IHeadlessStore store, CancellationToken cancellationToken)
    => Results.Ok(await store.GetServersAsync(cancellationToken)));

app.MapGet("/api/alerts", async (IHeadlessStore store, CancellationToken cancellationToken)
    => Results.Ok(await store.GetActiveAlertsAsync(cancellationToken)));

app.MapGet("/api/settings", (MonitorSettingsService settings)
    => Results.Ok(settings.GetSettings()));

app.MapPut("/api/settings", async (
    HeadlessSettingsDto request,
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

app.MapGet("/api/collection-log", async (IHeadlessStore store, int? limit, CancellationToken cancellationToken)
    => Results.Ok(await store.GetCollectionLogAsync(limit ?? 200, cancellationToken)));

app.MapGet("/api/servers/{serverId}/waits", async (
    string serverId,
    IHeadlessStore store,
    int? hours,
    int? limit,
    CancellationToken cancellationToken) =>
{
    var rows = await store.GetTopWaitsAsync(serverId, hours ?? 1, limit ?? 20, cancellationToken);
    return Results.Ok(rows);
});

app.MapGet("/api/servers/{serverId}/cpu", async (
    string serverId,
    IHeadlessStore store,
    int? hours,
    CancellationToken cancellationToken) =>
{
    var rows = await store.GetCpuSamplesAsync(serverId, hours ?? 1, cancellationToken);
    return Results.Ok(rows);
});

app.MapPost("/api/ingest/snapshot", async (
    IngestSnapshotDto request,
    HttpRequest httpRequest,
    IOptionsMonitor<MonitorOptions> options,
    IHeadlessStore store,
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

    var server = new MonitoredServerOptions
    {
        Id = request.Server.Id.Trim(),
        DisplayName = string.IsNullOrWhiteSpace(request.Server.DisplayName) ? request.Server.Id.Trim() : request.Server.DisplayName.Trim(),
        Purpose = string.IsNullOrWhiteSpace(request.Server.Purpose) ? "Unassigned" : request.Server.Purpose.Trim(),
        Enabled = request.Server.Enabled
    };

    var collectionTime = request.CollectionTime ?? DateTime.UtcNow;
    await store.InitializeAsync(cancellationToken);
    await store.UpsertConfiguredServersAsync([server], cancellationToken);
    await store.SetServerStatusAsync(server, request.Status, request.ErrorMessage, request.ServerProperties, cancellationToken);

    var serverPropertiesRows = 0;
    if (request.ServerProperties is not null)
    {
        await store.InsertServerPropertiesAsync(server, collectionTime, request.ServerProperties, cancellationToken);
        serverPropertiesRows = 1;
    }

    await store.InsertWaitStatsAsync(server, collectionTime, request.WaitStats, cancellationToken);
    await store.InsertCpuSamplesAsync(server, collectionTime, request.CpuSamples, cancellationToken);

    foreach (var log in request.CollectionLog)
    {
        await store.InsertCollectionLogAsync(
            server,
            log.CollectorName,
            log.CollectionTime,
            log.DurationMs,
            log.Status,
            log.ErrorMessage,
            log.RowsCollected,
            0,
            0,
            cancellationToken);
    }

    return Results.Ok(new IngestResultDto(
        true,
        serverPropertiesRows,
        request.WaitStats.Count,
        request.CpuSamples.Count,
        request.CollectionLog.Count));
});

app.MapFallbackToFile("index.html");

app.Run();

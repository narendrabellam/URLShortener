using System.Text.Json;
using LinkForge;

var builder = WebApplication.CreateBuilder(args);
var dataDirectory = builder.Configuration["DATA_DIRECTORY"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
var baseUrl = builder.Configuration["BASE_URL"]?.TrimEnd('/');
if (string.IsNullOrWhiteSpace(baseUrl))
{
    var configuredUrl = builder.Configuration["ASPNETCORE_URLS"]?.Split(';').FirstOrDefault();
    baseUrl = (configuredUrl ?? "http://localhost:3000").TrimEnd('/');
}
if (string.IsNullOrWhiteSpace(builder.Configuration["ASPNETCORE_URLS"]))
    builder.WebHost.UseUrls("http://localhost:3000");

var connectionString = builder.Configuration.GetConnectionString("Sqlite")
    ?? PersistenceServiceCollectionExtensions.BuildConnectionString(Path.Combine(dataDirectory, "linkforge.db"));
builder.Services.AddLinkForgeData(connectionString);
builder.Services.AddEngine(builder.Configuration);
builder.Services.AddSingleton<RunDispatcher>();
builder.Services.AddOpenApi();

var app = builder.Build();
await app.Services.InitializeLinkForgeDataAsync();
await app.Services.GetRequiredService<PipelineCoordinator>().InitializeAsync();

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Exception error) when (error is LinkStoreException or WorkflowException or AgentException)
    {
        var statusCode = error switch
        {
            LinkStoreException storeError => storeError.StatusCode,
            WorkflowException workflowError => workflowError.StatusCode,
            _ => StatusCodes.Status503ServiceUnavailable
        };
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RequestFailure");
        logger.LogWarning("Request failed with status {StatusCode}: {Message}", statusCode, error.Message);
        if (context.Response.HasStarted) throw;
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new { error = error.Message });
    }
});

app.Use(async (context, next) =>
{
    if (context.Request.ContentLength is > 16_384)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await context.Response.WriteAsJsonAsync(new { error = "Request body exceeds the 16 KB limit." });
        return;
    }
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "no-store";
    await next(context);
});

app.MapOpenApi();
app.MapGet("/health", (IConfiguration configuration) => Results.Ok(new
{
    status = "ok",
    aiConfigured = !string.IsNullOrWhiteSpace(configuration["OPENAI_API_KEY"])
}));
var api = app.MapGroup("/api");

api.MapPost("/urls", async (HttpContext context, SqliteLinkStore store) =>
{
    var body = await ReadJsonAsync<CreateUrlRequest>(context);
    if (body is null)
        return Results.BadRequest(new { error = "Request body must be a JSON object containing url." });
    var record = await store.CreateAsync(body.Url ?? "", body.Alias, context.RequestAborted);
    return Results.Created($"/api/urls/{record.Code}", new
    {
        record.Code, record.Url, record.CreatedAt, record.Clicks, shortUrl = $"{baseUrl}/{record.Code}"
    });
});

api.MapGet("/urls", async (SqliteLinkStore store, CancellationToken cancellationToken) =>
    Results.Ok(new { urls = await store.ListAsync(cancellationToken) }));

api.MapGet("/urls/{code}", async (string code, SqliteLinkStore store, CancellationToken cancellationToken) =>
{
    var record = await store.GetAsync(code, cancellationToken);
    return record is null
        ? Results.NotFound(new { error = "Short URL not found." })
        : Results.Ok(new
        {
            record.Code, record.Url, record.CreatedAt, record.Clicks, record.LastAccessedAt, record.Referrers,
            shortUrl = $"{baseUrl}/{record.Code}"
        });
});

api.MapGet("/scenarios", () => Results.Ok(new
{
    scenarios = ScenarioLibrary.All.Select(scenario => new
    {
        scenario.Id, scenario.Title, scenario.Requirements, scenario.Assumptions, scenario.AcceptanceCriteria,
        dependencyGraph = scenario.Nodes.Select(node => new
        {
            node.Id, node.Stage, node.DependsOn, node.EntryGate, node.ExitGate, node.RetryLimit,
            node.ApprovalRequired, node.HighImpact
        })
    })
}));

api.MapPost("/runs", async (HttpContext context, PipelineCoordinator coordinator, RunDispatcher runner) =>
{
    var body = await ReadJsonAsync<StartRunRequest>(context);
    if (body is null || string.IsNullOrWhiteSpace(body.ScenarioId))
        return Results.BadRequest(new { error = "scenarioId is required." });
    var run = await coordinator.CreateRunAsync(body.ScenarioId, body.ForceTestFailure, context.RequestAborted);
    runner.Launch(token => coordinator.ContinueAsync(run.Id, token));
    return Results.Accepted($"/api/runs/{run.Id}", run);
});

api.MapGet("/runs", (PipelineCoordinator coordinator) => Results.Ok(new { runs = coordinator.List() }));
api.MapGet("/runs/{runId}", (string runId, PipelineCoordinator coordinator) =>
{
    var run = coordinator.Get(runId);
    return run is null ? Results.NotFound(new { error = "Run not found." }) : Results.Ok(run);
});
api.MapGet("/runs/{runId}/summary", async (string runId, PipelineCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.GetSummaryAsync(runId, cancellationToken)));
api.MapGet("/runs/{runId}/evidence", async (string runId, PipelineCoordinator coordinator, CancellationToken cancellationToken) =>
    Results.Ok(await coordinator.GetEvidenceAsync(runId, cancellationToken)));
api.MapGet("/runs/{runId}/audit/verify", async (string runId, PipelineCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var (valid, broken, events) = await coordinator.VerifyAuditAsync(runId, cancellationToken);
    return Results.Ok(new { valid, brokenAtSequence = broken, events });
});
api.MapGet("/metrics", (PipelineCoordinator coordinator) => Results.Ok(coordinator.GetMetrics()));

api.MapPost("/runs/{runId}/approval", async (
    string runId, HttpContext context, PipelineCoordinator coordinator, RunDispatcher runner) =>
{
    var body = await ReadJsonAsync<ApprovalDecisionRequest>(context);
    if (body is null)
        return Results.BadRequest(new { error = "Approval requires approved, optional reason and changeSetDigest fields." });
    var run = await coordinator.DecideAsync(
        runId, new ApprovalRequest(body.Approved, ActorOrDefault(body.Actor), body.Reason, body.ChangeSetDigest),
        context.RequestAborted);
    if (run.Status == "running") runner.Launch(token => coordinator.ContinueAsync(runId, token));
    return Results.Ok(run);
});

api.MapPost("/runs/{runId}/replan", async (
    string runId, HttpContext context, PipelineCoordinator coordinator, RunDispatcher runner) =>
{
    var body = await ReadJsonAsync<ReplanRequest>(context);
    if (body is null)
        return Results.BadRequest(new { error = "Updated requirements are required." });
    var run = await coordinator.PrepareReplanAsync(runId, body, context.RequestAborted);
    runner.Launch(token => coordinator.ContinueAsync(runId, token));
    return Results.Accepted($"/api/runs/{runId}", run);
});

api.MapPost("/runs/{runId}/stop", async (string runId, HttpContext context, PipelineCoordinator coordinator) =>
{
    var body = await ReadJsonAsync<StopRequest>(context);
    return Results.Ok(await coordinator.StopAsync(runId, ActorOrDefault(body?.Actor), body?.Reason, context.RequestAborted));
});

app.MapGet("/{code}", async (string code, HttpContext context, SqliteLinkStore store) =>
{
    if (!SqliteLinkStore.IsValidCode(code))
        return Results.NotFound(new { error = "Short URL not found." });
    var referer = context.Request.Headers.Referer.ToString();
    var referrer = Uri.TryCreate(referer, UriKind.Absolute, out var referrerUri) ? referrerUri.Host : "direct";
    var destination = await store.ResolveAsync(code, referrer, context.RequestAborted);
    return destination is null ? Results.NotFound(new { error = "Short URL not found." }) : Results.Redirect(destination);
});

app.Run();

// The actor is audit metadata supplied by the caller, not an authenticated identity.
static string ActorOrDefault(string? actor) =>
    string.IsNullOrWhiteSpace(actor) ? "reviewer" : (actor.Trim().Length > 100 ? actor.Trim()[..100] : actor.Trim());

static async Task<T?> ReadJsonAsync<T>(HttpContext context)
{
    using var memory = new MemoryStream();
    var buffer = new byte[4096];
    int read;
    while ((read = await context.Request.Body.ReadAsync(buffer, context.RequestAborted)) > 0)
    {
        if (memory.Length + read > 16_384)
            throw new LinkStoreException(413, "Request body exceeds the 16 KB limit.");
        await memory.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
    }
    if (memory.Length == 0) return default;
    try
    {
        return JsonSerializer.Deserialize<T>(memory.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }
    catch (JsonException)
    {
        throw new LinkStoreException(400, "Request body must be valid JSON.");
    }
}

public sealed record CreateUrlRequest(string? Url, string? Alias);
public sealed record StartRunRequest(string? ScenarioId, bool ForceTestFailure = false);
public sealed record ApprovalDecisionRequest(bool Approved, string? Reason, string? ChangeSetDigest = null, string? Actor = null);
public sealed record StopRequest(string? Reason, string? Actor = null);

public partial class Program;

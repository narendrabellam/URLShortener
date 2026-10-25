using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LinkForge.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "linkforge-api-" + Guid.NewGuid().ToString("N"));
    public FakeEngineerProvider Agent { get; } = new();
    public FakeChangeVerifier Validator { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("DATA_DIRECTORY", dataDirectory);
        builder.UseSetting("Workflow:AllowFaultInjection", "true");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IEngineerProvider>();
            services.RemoveAll<IChangeVerifier>();
            services.AddSingleton<IEngineerProvider>(Agent);
            services.AddSingleton<IChangeVerifier>(Validator);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(dataDirectory, true); } catch (IOException) { }
    }
}

public sealed class ApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;
    private readonly HttpClient client;

    public ApiTests(ApiFactory factory)
    {
        this.factory = factory;
        client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    private static HttpRequestMessage Post(string url, object body) =>
        new(HttpMethod.Post, url) { Content = JsonContent.Create(body) };

    private async Task<JsonElement> GetJsonAsync(string url)
    {
        var response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> WaitForRunAsync(string runId, Func<JsonElement, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var run = await GetJsonAsync($"/api/runs/{runId}");
            if (done(run)) return run;
            await Task.Delay(25);
        }
        throw new TimeoutException($"Run {runId} did not reach the expected state.");
    }

    private async Task<string> StartRunAsync(string scenario = "greenfield", bool fault = false)
    {
        var response = await client.SendAsync(Post("/api/runs", new { scenarioId = scenario, forceTestFailure = fault }));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Health_ReportsStatusWithoutLeakingConfiguration()
    {
        var text = await client.GetStringAsync("/health");
        Assert.Contains("\"status\":\"ok\"", text);
        Assert.Contains("aiConfigured", text);
        Assert.DoesNotContain("sk-", text);
    }

    [Fact]
    public async Task UrlLifecycle_CreateRedirectAnalytics()
    {
        var created = await client.SendAsync(Post("/api/urls", new { url = "https://example.com/a", alias = "api-demo" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var duplicate = await client.SendAsync(Post("/api/urls", new { url = "https://example.com/b", alias = "api-demo" }));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);

        var request = new HttpRequestMessage(HttpMethod.Get, "/api-demo");
        request.Headers.Referrer = new Uri("https://news.example/story");
        var redirect = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("https://example.com/a", redirect.Headers.Location!.ToString());

        var details = await GetJsonAsync("/api/urls/api-demo");
        Assert.Equal(1, details.GetProperty("clicks").GetInt32());
        Assert.Equal(1, details.GetProperty("referrers").GetProperty("news.example").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/does-not-exist")).StatusCode);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com")]
    public async Task CreateUrl_RejectsUnsafeDestinations(string url)
    {
        var response = await client.SendAsync(Post("/api/urls", new { url }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OversizedBody_IsRejectedWith413()
    {
        var body = new StringContent("{\"url\":\"https://example.com/" + new string('a', 20_000) + "\"}", Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/urls", body);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task MalformedJson_Returns400()
    {
        var response = await client.PostAsync("/api/urls", new StringContent("{nope", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Scenarios_ExposeDependencyGraphWithGates()
    {
        var scenarios = (await GetJsonAsync("/api/scenarios")).GetProperty("scenarios");
        Assert.Equal(3, scenarios.GetArrayLength());
        var graph = scenarios[0].GetProperty("dependencyGraph");
        Assert.Contains(graph.EnumerateArray(), node => node.GetProperty("approvalRequired").GetBoolean());
        Assert.All(graph.EnumerateArray(), node => Assert.False(string.IsNullOrEmpty(node.GetProperty("exitGate").GetString())));
    }

    [Fact]
    public async Task Run_FlowsThroughApprovalsAndExposesEvidenceAuditAndMetrics()
    {
        var id = await StartRunAsync();
        var waiting = await WaitForRunAsync(id, run => run.GetProperty("status").GetString() == "waiting_approval");
        var digest = waiting.GetProperty("nodes").GetProperty("change-approval").GetProperty("output").GetProperty("changeSetDigest").GetString();

        var noDigest = await client.SendAsync(Post($"/api/runs/{id}/approval", new { approved = true }));
        Assert.Equal(HttpStatusCode.BadRequest, noDigest.StatusCode);

        var approved = await client.SendAsync(Post($"/api/runs/{id}/approval", new { approved = true, actor = "alice", reason = "Reviewed diff.", changeSetDigest = digest }));
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        await WaitForRunAsync(id, run => run.GetProperty("nodes").GetProperty("release-approval").GetProperty("status").GetString() == "waiting_approval");

        var release = await client.SendAsync(Post($"/api/runs/{id}/approval", new { approved = true, reason = "Ready." }));
        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        var done = await WaitForRunAsync(id, run => run.GetProperty("status").GetString() == "succeeded");
        Assert.Equal(7, done.GetProperty("metrics").GetProperty("agentCalls").GetInt32());

        var evidence = await GetJsonAsync($"/api/runs/{id}/evidence");
        Assert.True(evidence.GetProperty("agentCalls").GetArrayLength() >= 7);
        Assert.Equal(2, evidence.GetProperty("approvals").GetArrayLength());
        Assert.Contains(evidence.GetProperty("approvals").EnumerateArray(), a => a.GetProperty("actor").GetString() == "alice");
        Assert.True(evidence.GetProperty("validations").GetArrayLength() > 0);

        var audit = await GetJsonAsync($"/api/runs/{id}/audit/verify");
        Assert.True(audit.GetProperty("valid").GetBoolean());

        var metrics = await GetJsonAsync("/api/metrics");
        Assert.True(metrics.GetProperty("succeeded").GetInt32() >= 1);

        var summary = await GetJsonAsync($"/api/runs/{id}/summary");
        Assert.Equal("succeeded", summary.GetProperty("status").GetString());
        Assert.True(summary.GetProperty("validation").GetProperty("allPassed").GetBoolean());
        Assert.Equal(2, summary.GetProperty("approvals").GetArrayLength());
        Assert.Equal(0, summary.GetProperty("incompleteNodes").GetArrayLength());
        Assert.True(summary.GetProperty("plan").GetProperty("graph").GetArrayLength() > 0);
        Assert.True(summary.GetProperty("artifacts").GetArrayLength() > 0);
        Assert.True(summary.GetProperty("limitations").GetArrayLength() > 0);
    }

    [Fact]
    public async Task Rejection_SafeStopsTheRun()
    {
        var id = await StartRunAsync("brownfield");
        await WaitForRunAsync(id, run => run.GetProperty("status").GetString() == "waiting_approval");
        var response = await client.SendAsync(Post($"/api/runs/{id}/approval", new { approved = false, reason = "No." }));
        Assert.Equal("safe_stopped", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task InjectedValidationFailure_RollsBackAndSafeStops()
    {
        var id = await StartRunAsync(fault: true);
        var waiting = await WaitForRunAsync(id, run => run.GetProperty("status").GetString() == "waiting_approval");
        var digest = waiting.GetProperty("nodes").GetProperty("change-approval").GetProperty("output").GetProperty("changeSetDigest").GetString();
        await client.SendAsync(Post($"/api/runs/{id}/approval", new { approved = true, changeSetDigest = digest }));
        var stopped = await WaitForRunAsync(id, run => run.GetProperty("status").GetString() == "safe_stopped");
        Assert.Equal(1, stopped.GetProperty("metrics").GetProperty("rollbacks").GetInt32());

        var summary = await GetJsonAsync($"/api/runs/{id}/summary");
        Assert.Equal("safe_stopped", summary.GetProperty("status").GetString());
        Assert.False(summary.GetProperty("validation").GetProperty("allPassed").GetBoolean());
        Assert.True(summary.GetProperty("failedNodes").GetArrayLength() > 0);
        Assert.True(summary.GetProperty("auditChain").GetProperty("verified").GetBoolean());
    }

    [Fact]
    public async Task StopAndReplan_Work()
    {
        var id = await StartRunAsync("ambiguous");
        await WaitForRunAsync(id, run => run.GetProperty("status").GetString() == "waiting_approval");

        var replan = await client.SendAsync(Post($"/api/runs/{id}/replan", new { requirements = "Narrower scope: aliases only." }));
        Assert.Equal(HttpStatusCode.Accepted, replan.StatusCode);
        var replanned = await WaitForRunAsync(id, run =>
            run.GetProperty("requirementVersion").GetInt32() == 2 && run.GetProperty("status").GetString() == "waiting_approval");
        Assert.Equal("Narrower scope: aliases only.", replanned.GetProperty("requirements").GetString());

        var stop = await client.SendAsync(Post($"/api/runs/{id}/stop", new { reason = "Operator abort." }));
        Assert.Equal(HttpStatusCode.OK, stop.StatusCode);
        Assert.Equal("safe_stopped", (await GetJsonAsync($"/api/runs/{id}")).GetProperty("status").GetString());
    }

    [Fact]
    public async Task UnknownScenarioAndRun_Return404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Post("/api/runs", new { scenarioId = "nope" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/runs/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Post("/api/runs", new { }))).StatusCode);
    }

    [Fact]
    public async Task ProviderOutage_SafeStopsRunWithClassifiedError()
    {
        using var outage = new ApiFactory();
        outage.Agent.Fault = (_, _) => new AgentException(AgentErrorKind.QuotaExceeded, "quota");
        var local = outage.CreateClient();
        var response = await local.SendAsync(Post("/api/runs", new { scenarioId = "greenfield" }));
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        JsonElement run;
        do
        {
            run = await local.GetFromJsonAsync<JsonElement>($"/api/runs/{id}");
            if (run.GetProperty("status").GetString() == "safe_stopped") break;
            await Task.Delay(25);
        } while (DateTime.UtcNow < deadline);
        Assert.Equal("safe_stopped", run.GetProperty("status").GetString());
        Assert.Equal("QuotaExceeded", run.GetProperty("nodes").GetProperty("requirements").GetProperty("errorKind").GetString());
    }
}

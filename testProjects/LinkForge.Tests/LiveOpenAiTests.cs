using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LinkForge.Tests;

/// <summary>
/// Opt-in tests that call the real OpenAI API (billable). They run only when RUN_LIVE_OPENAI_TESTS=1 and
/// OPENAI_API_KEY are both set; otherwise they are reported as skipped, never as passed.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_LIVE_OPENAI_TESTS") != "1")
            Skip = "Set RUN_LIVE_OPENAI_TESTS=1 to run live OpenAI tests (incurs API charges).";
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OPENAI_API_KEY")))
            Skip = "OPENAI_API_KEY is not set.";
    }
}

public sealed class LiveOpenAiTests : IDisposable
{
    private readonly TestDatabase db = new();
    public void Dispose() => db.Dispose();

    private static IConfiguration Configuration() => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    private static OpenAiOptions Settings()
    {
        var options = new OpenAiOptions();
        if (Environment.GetEnvironmentVariable("OpenAI__Model") is { Length: > 0 } model) options.Model = model;
        return options;
    }

    private static IEngineerProvider RealAgent()
    {
        var options = Options.Create(Settings());
        return new ResilientEngineerProvider(
            new OpenAiEngineerProvider(options, Configuration(), NullLogger<OpenAiEngineerProvider>.Instance),
            options, NullLogger<ResilientEngineerProvider>.Instance);
    }

    [LiveFact]
    public async Task RealProvider_ReturnsAValidArtifactWithRealMetadata()
    {
        var result = await RealAgent().GenerateAsync(new EngineerProviderRequest(
            "greenfield", "Link expiry", "requirements",
            "Add optional link expiry to a URL shortener.", 1, ["SQLite storage."], ["Expired links return 404."],
            new Dictionary<string, System.Text.Json.JsonElement>()));

        Assert.False(string.IsNullOrWhiteSpace(result.Artifact.Summary));
        Assert.Equal("openai", result.Metadata.Provider);
        Assert.False(string.IsNullOrWhiteSpace(result.Metadata.Model));
        Assert.False(string.IsNullOrWhiteSpace(result.Metadata.ResponseId));
        Assert.True(result.Metadata.InputTokens > 0);
        Assert.True(result.Metadata.OutputTokens > 0);
    }

    [LiveFact]
    public async Task RealWorkflow_ProducesGovernedEvidenceAndIndependentValidation()
    {
        var workspace = new WorkspaceOptions { CommandTimeoutSeconds = 900 };
        var validator = new SandboxChangeVerifier(Options.Create(workspace), new AllowlistedProcessRunner(),
            NullLogger<SandboxChangeVerifier>.Instance);
        var coordinator = new PipelineCoordinator(db.Repository(), RealAgent(), validator,
            new SourceCodebaseContext(Options.Create(workspace)), Options.Create(new WorkflowOptions()),
            NullLogger<PipelineCoordinator>.Instance);

        var run = await coordinator.StartAsync("greenfield");
        Assert.Equal("waiting_approval", run.Status);
        run = await coordinator.ApproveAsync(run.Id, Harness.Approve(run, "Live test approval."));

        // A model-generated change may legitimately fail independent validation; what must hold is that the
        // outcome reflects real build/test evidence rather than the model's say-so.
        var validations = await db.Repository().GetValidationsAsync(run.Id);
        Assert.Contains(validations, v => v.Check == "build.dotnet-build");
        if (run.Nodes[ScenarioLibrary.ValidationNode].Status == "succeeded")
            Assert.Contains(validations, v => v.Check == "test.dotnet-test" && v.Passed);

        var calls = await db.Repository().GetAgentCallsAsync(run.Id);
        Assert.All(calls.Where(c => c.Outcome == "completed"), call => Assert.Equal("openai", call.Provider));
        Assert.True((await coordinator.VerifyAuditAsync(run.Id)).Valid);
    }
}

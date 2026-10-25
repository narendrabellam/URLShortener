using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace LinkForge.Tests;

/// <summary>Real SQLite file per test, so persistence behaviour is exercised for real.</summary>
public sealed class TestDatabase : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "linkforge-tests-" + Guid.NewGuid().ToString("N"));

    public TestDatabase()
    {
        Directory.CreateDirectory(directory);
        ConnectionString = PersistenceServiceCollectionExtensions.BuildConnectionString(Path.Combine(directory, "test.db"));
        Factory = new PooledFactory(ConnectionString);
        using var db = Factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    public string Directory_ => directory;
    public string ConnectionString { get; }
    public IDbContextFactory<LinkForgeDbContext> Factory { get; }

    public SqliteRunRepository Repository() => new(Factory);
    public SqliteLinkStore UrlStore() => new(Factory);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
    }

    private sealed class PooledFactory(string connectionString) : IDbContextFactory<LinkForgeDbContext>
    {
        public LinkForgeDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<LinkForgeDbContext>().UseSqlite(connectionString).Options);
    }
}

/// <summary>
/// Deterministic test double. It exists only in the test project and is never registered by the application;
/// real AI execution is covered by the opt-in live tests.
/// </summary>
public sealed class FakeEngineerProvider : IEngineerProvider
{
    private int calls;
    public int Calls => calls;
    public List<string> Stages { get; } = [];
    public Func<EngineerProviderRequest, int, Exception?>? Fault { get; set; }
    public Func<EngineerProviderRequest, EngineeringArtifact?>? Override { get; set; }
    public Func<Task>? Gate { get; set; }

    public async Task<AgentResult> GenerateAsync(EngineerProviderRequest request, CancellationToken cancellationToken = default)
    {
        var attempt = Interlocked.Increment(ref calls);
        lock (Stages) Stages.Add(request.Stage);
        if (Gate is not null) await Gate().WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (Fault?.Invoke(request, attempt) is { } fault) throw fault;
        var artifact = Override?.Invoke(request) ?? Default(request);
        return new AgentResult(artifact, new AgentCallMetadata("fake", "fake-model-1", $"resp-{attempt}", 100, 50, 5));
    }

    public static ProposedFile GoodFile(string name) => new($"source/LinkForge.Persistence/{name}.cs",
        $"namespace LinkForge;\n\ninternal static class {name}\n{{\n    public const int Value = 1;\n}}\n");

    public static EngineeringArtifact Default(EngineerProviderRequest request) => request.Stage switch
    {
        "implementation" => new("Add a small constant holder.", ["ImplFile"], [], [], ["Add GeneratedImpl"], [], ["Additive only"],
            [GoodFile("GeneratedImpl")]),
        "testing" => new("Propose checks.", [], [], [], [], ["Value is 1"], [], []),
        _ => new($"Output for {request.Stage}", [], ["A risk"], request.ScenarioId == "ambiguous" ? ["Retention?"] : [], [], ["a check"], ["a decision"], [])
    };
}

public sealed class FakeChangeVerifier : IChangeVerifier
{
    public bool Pass { get; set; } = true;
    public Func<int, bool>? PassOnCall { get; set; }
    public string FailureDetails { get; set; } = "error CS1002";
    public int Calls { get; private set; }
    public List<ProposedFile> LastFiles { get; private set; } = [];

    public Task<ValidationReport> ValidateAsync(IReadOnlyList<ProposedFile> files, CancellationToken cancellationToken = default)
    {
        Calls++;
        LastFiles = [.. files];
        var pass = PassOnCall?.Invoke(Calls) ?? Pass;
        return Task.FromResult(new ValidationReport(
        [
            new ValidationCheck("policy.path-allowlist", true, "ok"),
            new ValidationCheck("build.dotnet-build", pass, pass ? "Exit code 0." : FailureDetails),
            new ValidationCheck("test.dotnet-test", pass, pass ? "Exit code 0." : "Skipped")
        ]));
    }
}

public sealed class NullCodebaseContext : ICodebaseContext
{
    public string Describe() => "test workload";
}

public static class Harness
{
    public static PipelineCoordinator Create(
        TestDatabase db, IEngineerProvider agent, IChangeVerifier? validator = null, bool faultInjection = false) =>
        new(db.Repository(), agent, validator ?? new FakeChangeVerifier(), new NullCodebaseContext(),
            Options.Create(new WorkflowOptions { AllowFaultInjection = faultInjection }),
            NullLogger<PipelineCoordinator>.Instance);

    public static string Digest(WorkflowRun run) =>
        run.Nodes[ScenarioLibrary.ChangeApprovalNode].Output!.Value.GetProperty("changeSetDigest").GetString()!;

    public static ApprovalRequest Approve(WorkflowRun run, string reason = "Reviewed.") =>
        new(true, "reviewer", reason, run.Nodes.TryGetValue(ScenarioLibrary.ChangeApprovalNode, out var node) && node.Status == "waiting_approval"
            ? Digest(run) : null);

    public static async Task<WorkflowRun> WaitAsync(Func<WorkflowRun?> get, Func<WorkflowRun, bool> done, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            var run = get();
            if (run is not null && done(run)) return run;
            await Task.Delay(25);
        }
        throw new TimeoutException("Condition not reached in time.");
    }

    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);
}

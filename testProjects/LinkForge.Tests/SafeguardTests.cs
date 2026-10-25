using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LinkForge.Tests;

public sealed class ChangeRulesTests
{
    private static ProposedFile File(string path, string content = "namespace LinkForge;\npublic sealed class X {}\n") => new(path, content);

    private static string Failed(IEnumerable<ValidationCheck> checks) =>
        string.Join(",", checks.Where(c => !c.Passed).Select(c => c.Name));

    [Fact]
    public void CompliantChangeSet_Passes() =>
        Assert.Empty(Failed(ChangeRules.Evaluate([File("source/LinkForge.Persistence/X.cs"), File("testProjects/LinkForge.Tests/XTests.cs")])));

    [Theory]
    [InlineData("../evil.cs")]
    [InlineData("source/LinkForge.Persistence/../../evil.cs")]
    [InlineData("/etc/passwd.cs")]
    [InlineData("C:/x.cs")]
    [InlineData("source\\LinkForge.Persistence\\X.cs")]
    [InlineData("source/LinkForge.Persistence/X.csproj")]
    [InlineData("source/LinkForge.Engine/X.cs")]
    [InlineData("Dockerfile")]
    [InlineData("")]
    public void DisallowedPaths_AreRejected(string path) =>
        Assert.Contains("policy.path-allowlist", Failed(ChangeRules.Evaluate([File(path)])));

    [Fact]
    public void OverwritingExistingFile_IsRejectedUnlessAllowed()
    {
        var files = new[] { File("source/LinkForge.Persistence/Existing.cs") };
        Assert.Contains("policy.path-allowlist", Failed(ChangeRules.Evaluate(files, false, _ => true)));
        Assert.Empty(Failed(ChangeRules.Evaluate(files, true, _ => true)));
    }

    [Fact]
    public void OverwritingExistingTestFile_IsRejectedEvenWhenOverwriteAllowed()
    {
        var files = new[] { File("testProjects/LinkForge.Tests/Existing.cs") };
        Assert.Contains("policy.path-allowlist", Failed(ChangeRules.Evaluate(files, true, _ => true)));
    }

    [Fact]
    public void OverwriteThatDropsExistingMembers_IsRejectedButFullReplacementPasses()
    {
        const string existing = "namespace LinkForge;\npublic sealed class Run\n{\n    public int Id { get; set; }\n    public DateTimeOffset StartedAt { get; set; }\n    public void Stop() { }\n}\n";
        const string partial = "namespace LinkForge;\npublic sealed class Run\n{\n    public int Id { get; set; }\n}\n";
        const string full = existing + "// extended\n";
        var path = "source/LinkForge.Persistence/Run.cs";

        var rejected = ChangeRules.Evaluate([new(path, partial)], true, _ => true, _ => existing);
        Assert.Contains("policy.preserves-existing-members", Failed(rejected));
        Assert.Contains("StartedAt", rejected.Single(check => check.Name == "policy.preserves-existing-members").Details);
        Assert.Empty(Failed(ChangeRules.Evaluate([new(path, full)], true, _ => true, _ => existing)));
    }

    [Fact]
    public void TooManyFiles_DuplicatesAndEmptySets_AreRejected()
    {
        var many = Enumerable.Range(0, 7).Select(i => File($"source/LinkForge.Persistence/F{i}.cs")).ToArray();
        Assert.Contains("policy.change-set-size", Failed(ChangeRules.Evaluate(many)));
        Assert.Contains("policy.change-set-size", Failed(ChangeRules.Evaluate([])));
        Assert.Contains("policy.path-allowlist",
            Failed(ChangeRules.Evaluate([File("source/LinkForge.Persistence/A.cs"), File("source/LinkForge.Persistence/A.cs")])));
    }

    [Fact]
    public void OversizedFile_IsRejected() =>
        Assert.Contains("policy.file-size",
            Failed(ChangeRules.Evaluate([File("source/LinkForge.Persistence/Big.cs", new string('a', ChangeRules.MaximumFileBytes + 1))])));

    [Fact]
    public void EmbeddedSecret_IsRejected() =>
        Assert.Contains("security.secret-scan",
            Failed(ChangeRules.Evaluate([File("source/LinkForge.Persistence/S.cs", "const string K = \"sk-abcdefghijklmnopqrstuvwxyz123456\";")])));

    [Theory]
    [InlineData("System.Diagnostics.Process.Start(\"cmd\");")]
    [InlineData("System.IO.File.Delete(path);")]
    [InlineData("Environment.GetEnvironmentVariable(\"OPENAI_API_KEY\");")]
    [InlineData("db.Database.ExecuteSqlRaw(sql);")]
    public void ForbiddenApis_AreRejected(string code) =>
        Assert.Contains("security.forbidden-apis", Failed(ChangeRules.Evaluate([File("source/LinkForge.Persistence/F.cs", code)])));

    [Fact]
    public void Digest_IsStableOrderIndependentAndContentSensitive()
    {
        var a = File("source/LinkForge.Persistence/A.cs", "a");
        var b = File("source/LinkForge.Persistence/B.cs", "b");
        Assert.Equal(ChangeRules.Digest([a, b]), ChangeRules.Digest([b, a]));
        Assert.NotEqual(ChangeRules.Digest([a, b]), ChangeRules.Digest([a, b with { Content = "c" }]));
    }
}

public sealed class SecretScrubberTests
{
    [Theory]
    [InlineData("key sk-abcdefghijklmnopqrstuvwxyz123456 end")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789")]
    public void Secrets_AreDetectedAndMasked(string text)
    {
        Assert.True(SecretScrubber.ContainsSecret(text));
        var redacted = SecretScrubber.Redact(text);
        Assert.Contains(SecretScrubber.Mask, redacted);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz", redacted);
    }

    [Fact]
    public void OrdinaryText_IsUntouched()
    {
        Assert.False(SecretScrubber.ContainsSecret("Add link expiry to the shortener."));
        Assert.Equal("Add link expiry to the shortener.", SecretScrubber.Redact("Add link expiry to the shortener."));
        Assert.Equal("", SecretScrubber.Redact(null));
    }
}

public sealed class ProcessRunnerTests
{
    [Theory]
    [InlineData("cmd", "/c")]
    [InlineData("powershell", "-c")]
    [InlineData("git", "push")]
    [InlineData("dotnet", "publish")]
    [InlineData("dotnet", "run")]
    public async Task Allowlist_RejectsEverythingExceptDotnetBuildAndTest(string file, string verb) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AllowlistedProcessRunner().RunAsync(file, [verb], Path.GetTempPath(), TimeSpan.FromSeconds(5)));

    [Fact]
    public async Task Allowlist_RejectsNoArguments() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AllowlistedProcessRunner().RunAsync("dotnet", [], Path.GetTempPath(), TimeSpan.FromSeconds(5)));
}

public sealed class SandboxValidatorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "linkforge-src-" + Guid.NewGuid().ToString("N"));

    private sealed class RecordingRunner(int buildExit = 0, int testExit = 0, bool snapshot = true) : ICommandRunner
    {
        public List<string> Commands { get; } = [];
        public List<string> WorkingDirectories { get; } = [];
        public bool AppliedFileSeen { get; private set; }
        public bool SecretsAbsent { get; private set; } = true;

        public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
            TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Commands.Add(fileName + " " + arguments[0]);
            WorkingDirectories.Add(workingDirectory);
            if (snapshot) AppliedFileSeen = File.Exists(Path.Combine(workingDirectory, "source", "LinkForge.Persistence", "New.cs"));
            if (File.Exists(Path.Combine(workingDirectory, ".env"))) SecretsAbsent = false;
            return Task.FromResult(new CommandResult(arguments[0] == "build" ? buildExit : testExit, "output", false));
        }
    }

    public SandboxValidatorTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "source", "LinkForge.Persistence"));
        Directory.CreateDirectory(Path.Combine(root, "source", "LinkForge.Persistence", "bin"));
        File.WriteAllText(Path.Combine(root, "LinkForge.sln"), "");
        File.WriteAllText(Path.Combine(root, ".env"), "OPENAI_API_KEY=should-not-copy");
        File.WriteAllText(Path.Combine(root, "source", "LinkForge.Persistence", "Existing.cs"), "// existing");
    }

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    private SandboxChangeVerifier Create(ICommandRunner runner, string? sourceRoot = null) =>
        new(Options.Create(new WorkspaceOptions { SourceRoot = sourceRoot ?? root, CommandTimeoutSeconds = 5 }),
            runner, NullLogger<SandboxChangeVerifier>.Instance);

    private static readonly ProposedFile[] Good = [new("source/LinkForge.Persistence/New.cs", "namespace LinkForge;\ninternal sealed class New {}\n")];

    [Fact]
    public async Task Pass_RunsBuildThenTestInAnIsolatedCopyAndLeavesRealWorkspaceUntouched()
    {
        var runner = new RecordingRunner();
        var report = await Create(runner).ValidateAsync(Good);

        Assert.True(report.Passed);
        Assert.Equal(["dotnet build", "dotnet test"], runner.Commands);
        Assert.True(runner.AppliedFileSeen);
        Assert.True(runner.SecretsAbsent);
        Assert.All(runner.WorkingDirectories, dir => Assert.NotEqual(root, dir));
        Assert.False(File.Exists(Path.Combine(root, "source", "LinkForge.Persistence", "New.cs")));
        Assert.All(runner.WorkingDirectories.Distinct(), dir => Assert.False(Directory.Exists(dir)));
    }

    [Fact]
    public async Task BuildFailure_SkipsTestsAndFails()
    {
        var runner = new RecordingRunner(buildExit: 1);
        var report = await Create(runner).ValidateAsync(Good);
        Assert.False(report.Passed);
        Assert.Equal(["dotnet build"], runner.Commands);
        Assert.Contains(report.Checks, c => c.Name == "test.dotnet-test" && !c.Passed);
    }

    [Fact]
    public async Task TestFailure_Fails()
    {
        var report = await Create(new RecordingRunner(testExit: 1)).ValidateAsync(Good);
        Assert.False(report.Passed);
        Assert.Contains(report.Checks, c => c.Name == "test.dotnet-test" && !c.Passed);
    }

    [Fact]
    public async Task PolicyViolation_FailsBeforeAnyCommandRuns()
    {
        var runner = new RecordingRunner();
        var report = await Create(runner).ValidateAsync([new("source/LinkForge.Persistence/Existing.cs", "x")]);
        Assert.False(report.Passed);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task MissingWorkspace_FailsClosed()
    {
        var runner = new RecordingRunner();
        var report = await Create(runner, Path.Combine(root, "nowhere")).ValidateAsync(Good);
        Assert.False(report.Passed);
        Assert.Empty(runner.Commands);
    }
}

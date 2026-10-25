using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LinkForge.Tests;

/// <summary>
/// Runs the real sandbox (copy, apply, dotnet build, dotnet test). Slow, so opt-in via RUN_SANDBOX_TESTS=1.
/// The child process environment strips RUN_* variables, so the nested test run cannot recurse.
/// </summary>
public sealed class SandboxFactAttribute : FactAttribute
{
    public SandboxFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_SANDBOX_TESTS") != "1")
            Skip = "Set RUN_SANDBOX_TESTS=1 to run the real sandbox build/test integration test (slow).";
    }
}

public sealed class SandboxIntegrationTests
{
    private static SandboxChangeVerifier Create() => new(
        Options.Create(new WorkspaceOptions { CommandTimeoutSeconds = 900 }),
        new AllowlistedProcessRunner(), NullLogger<SandboxChangeVerifier>.Instance);

    [SandboxFact]
    public async Task ValidChange_BuildsAndTestsInTheRealSandbox()
    {
        var report = await Create().ValidateAsync(
            [new("source/LinkForge.Persistence/SandboxProbe.cs", "namespace LinkForge;\n\ninternal static class SandboxProbe\n{\n    public const int Value = 1;\n}\n")]);

        Assert.True(report.Passed, string.Join("\n", report.Checks.Select(c => $"{c.Name}: {c.Passed} {c.Details}")));
        Assert.Contains(report.Checks, c => c.Name == "test.dotnet-test" && c.Passed);
    }

    [SandboxFact]
    public async Task BrokenChange_IsCaughtByTheRealCompiler()
    {
        var report = await Create().ValidateAsync(
            [new("source/LinkForge.Persistence/Broken.cs", "namespace LinkForge;\n\ninternal static class Broken\n{\n    public const int Value = \"not an int\";\n}\n")]);

        Assert.False(report.Passed);
        Assert.Contains(report.Checks, c => c.Name == "build.dotnet-build" && !c.Passed && c.Details.Contains("error"));
        Assert.False(File.Exists(Path.Combine(AppContext.BaseDirectory, "Broken.cs")));
    }
}

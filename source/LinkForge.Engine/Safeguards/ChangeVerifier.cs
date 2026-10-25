using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinkForge;

public sealed class WorkspaceOptions
{
    public const string SectionName = "Workspace";

    /// <summary>Directory containing LinkForge.sln. Auto-detected when empty.</summary>
    public string? SourceRoot { get; set; }
    public string? TempRoot { get; set; }
    public int CommandTimeoutSeconds { get; set; } = 900;
    public bool AllowOverwrite { get; set; }
}

public sealed record CommandResult(int ExitCode, string Output, bool TimedOut);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        TimeSpan timeout, CancellationToken cancellationToken = default);
}

public interface IChangeVerifier
{
    /// <summary>Independently validates a proposed change set. Never modifies the real workspace.</summary>
    Task<ValidationReport> ValidateAsync(IReadOnlyList<ProposedFile> files, CancellationToken cancellationToken = default);
}

/// <summary>Runs only allowlisted `dotnet build|test` commands, with no shell and a scrubbed environment.</summary>
public sealed class AllowlistedProcessRunner : ICommandRunner
{
    private static readonly HashSet<string> AllowedVerbs = new(StringComparer.Ordinal) { "build", "test" };

    public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments,
        string workingDirectory, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (fileName != "dotnet" || arguments.Count == 0 || !AllowedVerbs.Contains(arguments[0]))
            throw new InvalidOperationException("Command is not on the execution allowlist.");

        var info = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var key in info.Environment.Keys
                     .Where(key => IsSensitiveVariable(key) || key.StartsWith("RUN_", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        // Long-lived MSBuild/compiler servers inherit the redirected pipes and would block completion.
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        info.Environment["UseSharedCompilation"] = "false";

        using var process = new Process { StartInfo = info };
        var output = new StringBuilder();
        var sync = new object();
        void Append(string? line)
        {
            if (line is null) return;
            lock (sync) { if (output.Length < 200_000) output.AppendLine(line); }
        }
        process.OutputDataReceived += (_, args) => Append(args.Data);
        process.ErrorDataReceived += (_, args) => Append(args.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
            // Bounded flush of redirected output; never wait indefinitely on inherited pipes.
            process.WaitForExit(TimeSpan.FromSeconds(5));
            return new CommandResult(process.ExitCode, output.ToString(), false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (cancellationToken.IsCancellationRequested) throw;
            return new CommandResult(-1, output.ToString(), true);
        }
    }

    private static bool IsSensitiveVariable(string name) =>
        name.Contains("API_KEY", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("OPENAI", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Copies the workload into a throw-away directory, applies the proposed files there, and runs
/// policy checks, `dotnet build` and `dotnet test`. A model response is never trusted as evidence of correctness.
/// </summary>
public sealed class SandboxChangeVerifier(
    IOptions<WorkspaceOptions> options,
    ICommandRunner runner,
    ILogger<SandboxChangeVerifier> logger) : IChangeVerifier
{
    private static readonly SemaphoreSlim SandboxGate = new(1, 1);
    private static readonly string[] ExcludedDirectories = ["bin", "obj", ".git", ".vs", "data", "node_modules", "workspace"];

    public async Task<ValidationReport> ValidateAsync(
        IReadOnlyList<ProposedFile> files, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var sourceRoot = ResolveSourceRoot(settings);
        var checks = new List<ValidationCheck>();
        checks.AddRange(ChangeRules.Evaluate(files, settings.AllowOverwrite,
            sourceRoot is null ? null : path => File.Exists(Path.Combine(sourceRoot, path)),
            sourceRoot is null ? null : path => File.Exists(Path.Combine(sourceRoot, path)) ? File.ReadAllText(Path.Combine(sourceRoot, path)) : null));
        if (checks.Any(check => !check.Passed)) return new ValidationReport(checks);
        if (sourceRoot is null)
        {
            checks.Add(new("sandbox.workspace", false, "Workload source root (LinkForge.sln) was not found; failing closed."));
            return new ValidationReport(checks);
        }

        await SandboxGate.WaitAsync(cancellationToken);
        var sandbox = Path.Combine(settings.TempRoot ?? Path.GetTempPath(), "linkforge-sandbox-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyWorkload(sourceRoot, sandbox);
            foreach (var file in files)
            {
                var target = Path.GetFullPath(Path.Combine(sandbox, file.Path));
                if (!target.StartsWith(Path.GetFullPath(sandbox) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new InvalidOperationException("Resolved path escaped the sandbox.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                await File.WriteAllTextAsync(target, file.Content, cancellationToken);
            }
            checks.Add(new("sandbox.apply", true, $"Applied {files.Count} file(s) to an isolated copy; the real workspace is untouched."));

            var timeout = TimeSpan.FromSeconds(settings.CommandTimeoutSeconds);
            var solution = Path.Combine(sandbox, "LinkForge.sln");
            var build = await RunCheckAsync("build.dotnet-build", ["build", solution, "--nologo", "-v", "minimal"],
                sandbox, timeout, cancellationToken);
            checks.Add(build);
            if (build.Passed)
                checks.Add(await RunCheckAsync("test.dotnet-test",
                    ["test", solution, "--no-build", "--nologo", "-v", "minimal"], sandbox, timeout, cancellationToken));
            else
                checks.Add(new("test.dotnet-test", false, "Skipped because the build failed."));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger.LogWarning(error, "Sandbox validation failed to run.");
            checks.Add(new("sandbox.execution", false, SecretScrubber.Redact(error.Message)));
        }
        finally
        {
            TryDelete(sandbox);
            SandboxGate.Release();
        }
        return new ValidationReport(checks);
    }

    private async Task<ValidationCheck> RunCheckAsync(string name, string[] arguments, string workingDirectory,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await runner.RunAsync("dotnet", arguments, workingDirectory, timeout, cancellationToken);
        var summary = result.TimedOut
            ? $"Timed out after {timeout.TotalSeconds:0}s."
            : $"Exit code {result.ExitCode}. {Tail(result.Output)}";
        return new ValidationCheck(name, !result.TimedOut && result.ExitCode == 0, summary, stopwatch.ElapsedMilliseconds);
    }

    private static string Tail(string output)
    {
        var redacted = SecretScrubber.Redact(output).Trim();
        return redacted.Length <= 3000 ? redacted : "..." + redacted[^3000..];
    }

    private static string? ResolveSourceRoot(WorkspaceOptions settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.SourceRoot))
            return File.Exists(Path.Combine(settings.SourceRoot, "LinkForge.sln")) ? Path.GetFullPath(settings.SourceRoot) : null;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "LinkForge.sln"))) return directory.FullName;
        return null;
    }

    private static void CopyWorkload(string source, string destination)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            if (IsExcluded(relative)) continue;
            Directory.CreateDirectory(Path.Combine(destination, relative));
        }
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (IsExcluded(relative) || Path.GetFileName(file).StartsWith(".env", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(destination, relative), overwrite: true);
        }
    }

    private static bool IsExcluded(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar).Any(segment => ExcludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase));

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

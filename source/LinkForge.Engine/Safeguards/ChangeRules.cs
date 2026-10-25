using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LinkForge;

public sealed record ValidationCheck(string Name, bool Passed, string Details, long DurationMs = 0);

public sealed record ValidationReport(IReadOnlyList<ValidationCheck> Checks)
{
    public bool Passed => Checks.Count > 0 && Checks.All(check => check.Passed);
}

/// <summary>
/// Change-control policy for model-proposed files. These rules are deterministic and independent of the model:
/// a persuasive response cannot relax them.
/// </summary>
public static partial class ChangeRules
{
    public const int MaximumFiles = 6;
    public const int MaximumFileBytes = 64 * 1024;

    private static readonly string[] AllowedPrefixes =
        ["source/LinkForge.Service/", "source/LinkForge.Persistence/", "testProjects/LinkForge.Tests/"];

    private static readonly string[] ForbiddenFragments =
    [
        "System.Diagnostics.Process", "Process.Start", "File.Delete", "Directory.Delete",
        "Assembly.Load", "DllImport", "LibraryImport", "Environment.Exit", "Environment.GetEnvironmentVariable",
        "ExecuteSqlRaw", "ExecuteSqlInterpolated", "FromSqlRaw", "unsafe ", "#pragma warning disable"
    ];

    public static IReadOnlyList<ValidationCheck> Evaluate(IReadOnlyList<ProposedFile> files, bool allowOverwrite = false,
        Func<string, bool>? exists = null, Func<string, string?>? readExisting = null)
    {
        var checks = new List<ValidationCheck>();
        checks.Add(new("policy.change-set-size", files.Count is > 0 and <= MaximumFiles,
            $"{files.Count} file(s); allowed 1-{MaximumFiles}."));

        var pathProblems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var overwritten = new List<string>();
        foreach (var file in files)
        {
            var problem = CheckPath(file.Path);
            if (problem is null && !seen.Add(file.Path)) problem = "duplicate path";
            if (problem is null && exists?.Invoke(file.Path) == true)
            {
                // Overwrites are opt-in, limited to production code; tests stay additive.
                if (!allowOverwrite || file.Path.StartsWith("testProjects/", StringComparison.Ordinal)) problem = "overwrites an existing file";
                else overwritten.Add(file.Path);
            }
            if (problem is not null) pathProblems.Add($"{Sanitize(file.Path)}: {problem}");
        }
        checks.Add(new("policy.path-allowlist", pathProblems.Count == 0,
            pathProblems.Count == 0
                ? "All paths are .cs files under allowed source directories." + (overwritten.Count == 0 ? " All are new files." : " Overwrites existing: " + string.Join(", ", overwritten))
                : string.Join("; ", pathProblems)));

        if (readExisting is not null && overwritten.Count > 0)
        {
            var dropped = overwritten.SelectMany(path => MissingMembers(readExisting(path), files.First(file => file.Path == path).Content)
                .Select(name => $"{path} drops '{name}'")).Take(12).ToArray();
            checks.Add(new("policy.preserves-existing-members", dropped.Length == 0,
                dropped.Length == 0 ? "Overwritten files keep every existing public/internal member."
                    : "Overwrites must preserve every existing member (return the COMPLETE file): " + string.Join("; ", dropped)));
        }

        var oversized = files.Where(file => Encoding.UTF8.GetByteCount(file.Content) > MaximumFileBytes)
            .Select(file => Sanitize(file.Path)).ToArray();
        checks.Add(new("policy.file-size", oversized.Length == 0,
            oversized.Length == 0 ? $"All files <= {MaximumFileBytes / 1024} KB." : "Oversized: " + string.Join(", ", oversized)));

        var secretFiles = files.Where(file => SecretScrubber.ContainsSecret(file.Content)).Select(file => Sanitize(file.Path)).ToArray();
        checks.Add(new("security.secret-scan", secretFiles.Length == 0,
            secretFiles.Length == 0 ? "No credential-shaped content found." : "Credential-shaped content in: " + string.Join(", ", secretFiles)));

        var forbidden = files.SelectMany(file => ForbiddenFragments
            .Where(fragment => file.Content.Contains(fragment, StringComparison.Ordinal))
            .Select(fragment => $"{Sanitize(file.Path)} uses '{fragment}'")).ToArray();
        checks.Add(new("security.forbidden-apis", forbidden.Length == 0,
            forbidden.Length == 0 ? "No forbidden API usage found." : string.Join("; ", forbidden)));
        return checks;
    }

    public static string? CheckPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 200) return "empty or too long";
        if (path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.Contains("//"))
            return "must be a relative forward-slash path";
        if (path.Split('/').Any(segment => segment is ".." or "." or ""))
            return "path traversal segment";
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return "only .cs files are allowed";
        if (!AllowedPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.Ordinal)))
            return "outside allowed directories";
        if (path.Any(character => char.IsControl(character))) return "control characters";
        return null;
    }

    /// <summary>Names of public/internal types and members declared in the old text that no longer appear in the new text.</summary>
    internal static IEnumerable<string> MissingMembers(string? existing, string replacement)
    {
        if (string.IsNullOrEmpty(existing)) return [];
        return MemberDeclaration().Matches(existing).Select(match => match.Groups[1].Value).Distinct()
            .Where(name => !Regex.IsMatch(replacement, $@"\b{Regex.Escape(name)}\b"));
    }

    [GeneratedRegex(@"^\s*(?:public|internal)\s+(?:(?:sealed|static|abstract|partial|readonly|required|override|virtual|async|const|new)\s+)*[\w<>\[\],.?]+\s+(\w+)\s*(?:\(|\{|=|;|:)", RegexOptions.Multiline)]
    private static partial Regex MemberDeclaration();

    /// <summary>Stable digest of a change set; an approval is bound to exactly this content.</summary>
    public static string Digest(IEnumerable<ProposedFile> files)
    {
        var builder = new StringBuilder();
        foreach (var file in files.OrderBy(file => file.Path, StringComparer.Ordinal))
            builder.Append(file.Path).Append('\n').Append(file.Content.Length).Append('\n').Append(file.Content).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string Sanitize(string value) =>
        new(value.Where(character => !char.IsControl(character)).Take(120).ToArray());
}

using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace LinkForge;

/// <summary>Gives agents a compact, bounded map of the existing codebase (brownfield reasoning input).</summary>
public interface ICodebaseContext
{
    string Describe();

    BlastRadiusReport Impact(IReadOnlyCollection<string> changedPaths) => BlastRadiusReport.Empty;
}

public sealed partial class SourceCodebaseContext(IOptions<WorkspaceOptions> options) : ICodebaseContext
{
    private const int MaximumCharacters = 7000;
    private readonly Lazy<string> map = new(() => Build(options.Value));

    public string Describe() => map.Value;

    public BlastRadiusReport Impact(IReadOnlyCollection<string> changedPaths)
    {
        var root = ResolveRoot(options.Value);
        var src = root is null ? null : Path.Combine(root, "source");
        if (src is null || !Directory.Exists(src)) return BlastRadiusReport.Empty;
        var sources = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(root!, file).Replace('\\', '/'))
            .Where(relative => !relative.Contains("/obj/") && !relative.Contains("/bin/"))
            .ToDictionary(relative => relative, relative => File.ReadAllText(Path.Combine(root!, relative)), StringComparer.Ordinal);
        return BlastRadiusAnalyzer.Analyze(sources, changedPaths);
    }

    private static string? ResolveRoot(WorkspaceOptions settings)
    {
        var root = settings.SourceRoot;
        if (string.IsNullOrWhiteSpace(root))
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "LinkForge.sln"))) { root = directory.FullName; break; }
        return root;
    }

    [GeneratedRegex(@"^\s*(public|internal)\s+(sealed\s+|static\s+|partial\s+|abstract\s+)*(class|record|interface|enum)\s+\w+", RegexOptions.Multiline)]
    private static partial Regex TypePattern();

    [GeneratedRegex(@"\.Map(Get|Post|Put|Delete)\(""[^""]*""")]
    private static partial Regex EndpointPattern();

    private static string Build(WorkspaceOptions settings)
    {
        var root = ResolveRoot(settings);
        var src = root is null ? null : Path.Combine(root, "source");
        if (src is null || !Directory.Exists(src)) return "Workload source is not available in this environment.";

        var builder = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root!, file).Replace('\\', '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/")) continue;
            var text = File.ReadAllText(file);
            var types = TypePattern().Matches(text).Select(match => match.Value.Trim()).Take(12);
            var endpoints = EndpointPattern().Matches(text).Select(match => match.Value.TrimStart('.')).Take(20);
            builder.Append(relative).Append(" (").Append(text.Split('\n').Length).AppendLine(" lines)");
            foreach (var item in types.Concat(endpoints)) builder.Append("  ").AppendLine(item);
            if (builder.Length > MaximumCharacters) break;
        }
        var map = builder.Length > MaximumCharacters ? builder.ToString(0, MaximumCharacters) : builder.ToString();

        // Real signatures matter: without them the model invents members that do not compile.
        var source = new StringBuilder(map).AppendLine().AppendLine("Key source files (verbatim):");
        foreach (var key in KeySourceFiles)
        {
            var path = Path.Combine(root!, key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) continue;
            var text = File.ReadAllText(path);
            if (text.Length > MaximumKeyFileCharacters) text = text[..MaximumKeyFileCharacters] + "\n// ...truncated";
            source.Append("--- ").Append(key).AppendLine(" ---").AppendLine(text);
        }
        return source.ToString();
    }

    private const int MaximumKeyFileCharacters = 12000;

    private static readonly string[] KeySourceFiles =
    [
        "source/LinkForge.Persistence/PersistenceModels.cs",
        "source/LinkForge.Persistence/SqliteLinkStore.cs",
        "source/LinkForge.Persistence/LinkForgeDbContext.cs",
        "source/LinkForge.Service/Program.cs"
    ];
}

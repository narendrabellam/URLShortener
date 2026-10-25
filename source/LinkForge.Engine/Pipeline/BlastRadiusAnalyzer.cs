using System.Text.RegularExpressions;

namespace LinkForge;

public sealed record BlastRadiusReport(
    string[] ModifiedFiles,
    string[] NewFiles,
    string[] DirectDependents,
    string[] TransitiveDependents,
    string[] AffectedEndpoints,
    string[] DataEntities)
{
    public static BlastRadiusReport Empty { get; } = new([], [], [], [], [], []);
}

/// <summary>
/// Static, name-based impact analysis over the existing source: which files depend on the files a change set touches,
/// and which HTTP endpoints and persisted entities sit in that blast radius. It is a heuristic (no semantic model),
/// so it reports what a reviewer should look at rather than proving what is safe.
/// </summary>
public static partial class BlastRadiusAnalyzer
{
    [GeneratedRegex(@"\b(?:class|record|interface|enum|struct)\s+(\w+)")]
    private static partial Regex TypePattern();

    [GeneratedRegex(@"\.Map(?:Get|Post|Put|Delete)\(""([^""]*)""")]
    private static partial Regex EndpointPattern();

    [GeneratedRegex(@"DbSet<(\w+)>")]
    private static partial Regex DbSetPattern();

    public static BlastRadiusReport Analyze(IReadOnlyDictionary<string, string> existingSources, IEnumerable<string> changedPaths)
    {
        var changed = changedPaths.Select(Normalize).Distinct(StringComparer.Ordinal).ToArray();
        var modified = changed.Where(existingSources.ContainsKey).Order(StringComparer.Ordinal).ToArray();
        var added = changed.Where(path => !existingSources.ContainsKey(path)).Order(StringComparer.Ordinal).ToArray();

        var declared = existingSources.ToDictionary(
            file => file.Key,
            file => TypePattern().Matches(file.Value).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal));

        // dependents[x] = files that mention a type declared in x.
        var dependents = existingSources.Keys.ToDictionary(path => path, path => new HashSet<string>(StringComparer.Ordinal));
        foreach (var (owner, types) in declared)
            foreach (var (user, text) in existingSources)
                if (user != owner && types.Any(type => Regex.IsMatch(text, $@"\b{Regex.Escape(type)}\b")))
                    dependents[owner].Add(user);

        var direct = modified.SelectMany(path => dependents[path]).Except(modified).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(modified.Concat(direct), StringComparer.Ordinal);
        var frontier = new Queue<string>(direct);
        var transitive = new HashSet<string>(StringComparer.Ordinal);
        while (frontier.TryDequeue(out var current))
            foreach (var next in dependents[current])
                if (seen.Add(next)) { transitive.Add(next); frontier.Enqueue(next); }

        var blastRadius = modified.Concat(direct).Concat(transitive).ToArray();
        var endpoints = blastRadius.SelectMany(path => EndpointPattern().Matches(existingSources[path]).Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var entities = existingSources.Values.SelectMany(text => DbSetPattern().Matches(text).Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Where(entity => blastRadius.Any(path => declared[path].Contains(entity)))
            .Order(StringComparer.Ordinal).ToArray();

        return new BlastRadiusReport(
            modified, added,
            [.. direct.Order(StringComparer.Ordinal)], [.. transitive.Order(StringComparer.Ordinal)],
            endpoints, entities);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}

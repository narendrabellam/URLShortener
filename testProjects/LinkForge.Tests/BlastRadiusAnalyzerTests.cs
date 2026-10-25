using Xunit;

namespace LinkForge.Tests;

public sealed class BlastRadiusAnalyzerTests
{
    private static readonly Dictionary<string, string> Sources = new()
    {
        ["source/Data/Entities.cs"] = "public class ShortUrl { }",
        ["source/Data/Store.cs"] = "public class Store { ShortUrl Get() => null!; DbSet<ShortUrl> Urls; }",
        ["source/Api/Program.cs"] = "app.MapGet(\"/{code}\", (Store s) => 1); app.MapPost(\"/api/urls\", (Store s) => 2);",
        ["source/Api/Other.cs"] = "public class Unrelated { }"
    };

    [Fact]
    public void ChangingAnEntity_ReportsTransitiveDependentsEndpointsAndEntities()
    {
        var report = BlastRadiusAnalyzer.Analyze(Sources, ["source/Data/Entities.cs"]);

        Assert.Equal(["source/Data/Entities.cs"], report.ModifiedFiles);
        Assert.Equal(["source/Data/Store.cs"], report.DirectDependents);
        Assert.Equal(["source/Api/Program.cs"], report.TransitiveDependents);
        Assert.Equal(["/api/urls", "/{code}"], report.AffectedEndpoints);
        Assert.Equal(["ShortUrl"], report.DataEntities);
    }

    [Fact]
    public void NewFilesAndUnrelatedFiles_HaveNoBlastRadius()
    {
        var report = BlastRadiusAnalyzer.Analyze(Sources, ["source/Api\\Brand.cs", "source/Api/Other.cs"]);

        Assert.Equal(["source/Api/Brand.cs"], report.NewFiles);
        Assert.Empty(report.DirectDependents);
        Assert.Empty(report.AffectedEndpoints);
    }
}

public sealed class CodebaseContextTests
{
    [Fact]
    public void Describe_IncludesEveryKeySourceFileVerbatim()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "LinkForge.sln"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);

        var context = new SourceCodebaseContext(Microsoft.Extensions.Options.Options.Create(new WorkspaceOptions { SourceRoot = root }));
        var description = context.Describe();

        foreach (var file in new[]
        {
            "source/LinkForge.Persistence/PersistenceModels.cs", "source/LinkForge.Persistence/SqliteLinkStore.cs",
            "source/LinkForge.Persistence/LinkForgeDbContext.cs", "source/LinkForge.Service/Program.cs"
        })
            Assert.Contains($"--- {file} ---", description);
    }
}

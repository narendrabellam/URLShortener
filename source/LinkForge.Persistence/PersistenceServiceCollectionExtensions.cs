using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LinkForge;

public static class PersistenceServiceCollectionExtensions
{
    /// <summary>Registers SQLite-backed EF Core persistence using the given connection string.</summary>
    public static IServiceCollection AddLinkForgeData(this IServiceCollection services, string connectionString)
    {
        services.AddDbContextFactory<LinkForgeDbContext>(options => options.UseSqlite(connectionString));
        services.AddSingleton<IRunRepository, SqliteRunRepository>();
        services.AddSingleton<SqliteLinkStore>();
        return services;
    }

    public static string BuildConnectionString(string databasePath)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 30
        }.ToString();
    }

    public static async Task InitializeLinkForgeDataAsync(
        this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<LinkForgeDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        // WAL lets readers proceed while the single writer commits; the setting persists in the file.
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
    }
}

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace LinkForge;

public sealed partial class SqliteLinkStore(IDbContextFactory<LinkForgeDbContext> dbFactory)
{
    private const int MaximumReferrersPerLink = 100;

    [GeneratedRegex("^[A-Za-z0-9_-]{4,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    public static string ValidateDestination(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 ||
            !Uri.TryCreate(value, UriKind.Absolute, out var parsed) ||
            parsed.Scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(parsed.Host))
            throw new LinkStoreException(400, "URL must be an absolute HTTP or HTTPS address no longer than 2048 characters.");
        return parsed.ToString();
    }

    public static bool IsValidCode(string code) => CodePattern().IsMatch(code);

    public async Task<ShortUrl> CreateAsync(string url, string? alias, CancellationToken cancellationToken = default)
    {
        var destination = ValidateDestination(url);
        var code = alias ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(6))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (!IsValidCode(code))
            throw new LinkStoreException(400, "Alias must be 4-32 letters, numbers, hyphens, or underscores.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var record = new ShortUrlEntity { Code = code, Destination = destination, CreatedAt = DateTimeOffset.UtcNow };
        db.ShortUrls.Add(record);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException error) when (IsUniqueViolation(error))
        {
            throw new LinkStoreException(409, "That short code is already in use.");
        }
        return new ShortUrl(record.Code, record.Destination, record.CreatedAt, 0, null,
            new Dictionary<string, long>());
    }

    /// <summary>Resolves a code and records the click. Increments are atomic SQL updates, so concurrent clicks are not lost.</summary>
    public async Task<string?> ResolveAsync(string code, string referrer, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var url = await db.ShortUrls.AsNoTracking().Where(record => record.Code == code)
            .Select(record => new { record.Id, record.Destination }).SingleOrDefaultAsync(cancellationToken);
        if (url is null) return null;

        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.ShortUrls.Where(record => record.Id == url.Id).ExecuteUpdateAsync(update => update
            .SetProperty(record => record.Clicks, record => record.Clicks + 1)
            .SetProperty(record => record.LastAccessedAt, now), cancellationToken);

        var safeReferrer = string.IsNullOrWhiteSpace(referrer) ? "direct" : referrer[..Math.Min(referrer.Length, 253)];
        var updated = await IncrementReferrerAsync(db, url.Id, safeReferrer, cancellationToken);
        if (updated == 0)
        {
            var count = await db.ShortUrlReferrers.CountAsync(item => item.ShortUrlId == url.Id, cancellationToken);
            if (count >= MaximumReferrersPerLink)
            {
                safeReferrer = "other";
                updated = await IncrementReferrerAsync(db, url.Id, safeReferrer, cancellationToken);
            }
            if (updated == 0)
            {
                db.ShortUrlReferrers.Add(new ShortUrlReferrerEntity
                    { ShortUrlId = url.Id, Referrer = safeReferrer, Clicks = 1 });
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        await transaction.CommitAsync(cancellationToken);
        return url.Destination;
    }

    public async Task<ShortUrl?> GetAsync(string code, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var record = await db.ShortUrls.AsNoTracking().Include(value => value.Referrers)
            .SingleOrDefaultAsync(value => value.Code == code, cancellationToken);
        return record is null ? null : ToModel(record);
    }

    public async Task<IReadOnlyList<ShortUrl>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var records = await db.ShortUrls.AsNoTracking().Include(value => value.Referrers)
            .OrderByDescending(value => value.CreatedAt).Take(500).ToArrayAsync(cancellationToken);
        return records.Select(ToModel).ToArray();
    }

    private static Task<int> IncrementReferrerAsync(
        LinkForgeDbContext db, long urlId, string referrer, CancellationToken cancellationToken) =>
        db.ShortUrlReferrers.Where(item => item.ShortUrlId == urlId && item.Referrer == referrer)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.Clicks, item => item.Clicks + 1),
                cancellationToken);

    private static ShortUrl ToModel(ShortUrlEntity record) => new(
        record.Code, record.Destination, record.CreatedAt, record.Clicks, record.LastAccessedAt,
        record.Referrers.ToDictionary(value => value.Referrer, value => value.Clicks));

    private static bool IsUniqueViolation(Exception error) =>
        error.GetBaseException() is SqliteException { SqliteErrorCode: 19 };
}

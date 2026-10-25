using Microsoft.EntityFrameworkCore;

namespace LinkForge.Tests;

public sealed class LinkStoreTests : IDisposable
{
    private readonly TestDatabase db = new();
    public void Dispose() => db.Dispose();

    [Fact]
    public async Task Create_ValidatesAliasAndRejectsDuplicates()
    {
        var store = db.UrlStore();
        var created = await store.CreateAsync("https://example.com/path", "demo1");
        Assert.Equal("demo1", created.Code);
        Assert.Equal(409, (await Assert.ThrowsAsync<LinkStoreException>(() => store.CreateAsync("https://other.example", "demo1"))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<LinkStoreException>(() => store.CreateAsync("https://example.com", "x"))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<LinkStoreException>(() => store.CreateAsync("https://example.com", "bad alias!"))).StatusCode);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/file")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Create_RejectsNonHttpDestinations(string url)
    {
        var error = await Assert.ThrowsAsync<LinkStoreException>(() => db.UrlStore().CreateAsync(url, null));
        Assert.Equal(400, error.StatusCode);
    }

    [Fact]
    public async Task Create_GeneratesValidCodeWhenAliasOmitted()
    {
        var created = await db.UrlStore().CreateAsync("https://example.com", null);
        Assert.True(SqliteLinkStore.IsValidCode(created.Code));
    }

    [Fact]
    public async Task Resolve_ConcurrentClicksAreNotLostAndSurviveRestart()
    {
        var store = db.UrlStore();
        await store.CreateAsync("https://example.com/path", "demo1");
        await Task.WhenAll(Enumerable.Range(0, 25).Select(i => store.ResolveAsync("demo1", i % 2 == 0 ? "a.example" : "b.example")));

        var reopened = db.UrlStore();
        var link = (await reopened.GetAsync("demo1"))!;
        Assert.Equal(25, link.Clicks);
        Assert.Equal(13, link.Referrers["a.example"]);
        Assert.Equal(12, link.Referrers["b.example"]);
        Assert.NotNull(link.LastAccessedAt);
    }

    [Fact]
    public async Task Resolve_UnknownCodeReturnsNullWithoutSideEffects()
    {
        var store = db.UrlStore();
        Assert.Null(await store.ResolveAsync("missing", "direct"));
        await using var context = await db.Factory.CreateDbContextAsync();
        Assert.Equal(0, await context.ShortUrlReferrers.CountAsync());
    }

    [Fact]
    public async Task Resolve_BoundsReferrerCardinality()
    {
        var store = db.UrlStore();
        await store.CreateAsync("https://example.com", "bounded");
        for (var i = 0; i < 103; i++) await store.ResolveAsync("bounded", $"host{i}.example");
        var link = (await store.GetAsync("bounded"))!;
        Assert.True(link.Referrers.Count <= 101);
        Assert.Equal(3, link.Referrers["other"]);
        Assert.Equal(103, link.Clicks);
    }
}

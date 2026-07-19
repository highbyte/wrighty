using Highbyte.Wrighty.Caching;

namespace Highbyte.Wrighty.UnitTests.Caching;

public sealed class JsonSessionRecordCacheTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"wrighty-session-cache-{Guid.NewGuid():N}");

    private JsonSessionRecordCache Cache() => new(new CachePaths(directory));

    private static CachedSessionRecord Record(string sessionId) => new(
        "claude",
        sessionId,
        "/tmp/workspace",
        new DateTimeOffset(2026, 7, 19, 10, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 7, 19, 11, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Missing_cache_returns_null()
    {
        Assert.Null(await Cache().GetAsync("github:owner/repo#1", CancellationToken.None));
    }

    [Fact]
    public async Task Put_persists_and_a_new_instance_reads_the_record()
    {
        await Cache().PutAsync("github:owner/repo#1", Record("session-one"), CancellationToken.None);

        var reread = await Cache().GetAsync("github:owner/repo#1", CancellationToken.None);

        Assert.NotNull(reread);
        Assert.Equal("claude", reread!.AgentType);
        Assert.Equal("session-one", reread.SessionId);
        Assert.Equal("/tmp/workspace", reread.WorkspacePath);
        Assert.NotNull(reread.LastClaimExpiresAt);
        Assert.Null(await Cache().GetAsync("github:owner/repo#2", CancellationToken.None));
        Assert.True(File.Exists(new CachePaths(directory).SessionCachePath));
    }

    [Fact]
    public async Task Put_overwrites_the_existing_record_per_key()
    {
        var cache = Cache();
        await cache.PutAsync("github:owner/repo#1", Record("session-old"), CancellationToken.None);
        await cache.PutAsync("github:owner/repo#1", Record("session-new"), CancellationToken.None);
        await cache.PutAsync("github:owner/repo#2", Record("session-other"), CancellationToken.None);

        Assert.Equal("session-new",
            (await cache.GetAsync("github:owner/repo#1", CancellationToken.None))!.SessionId);
        Assert.Equal("session-other",
            (await cache.GetAsync("github:owner/repo#2", CancellationToken.None))!.SessionId);
    }

    [Fact]
    public async Task Corrupt_cache_file_is_treated_as_empty_and_recovered_by_put()
    {
        var paths = new CachePaths(directory);
        Directory.CreateDirectory(paths.Root);
        await File.WriteAllTextAsync(paths.SessionCachePath, "{ not json");

        Assert.Null(await Cache().GetAsync("github:owner/repo#1", CancellationToken.None));

        await Cache().PutAsync("github:owner/repo#1", Record("session-one"), CancellationToken.None);
        Assert.Equal("session-one",
            (await Cache().GetAsync("github:owner/repo#1", CancellationToken.None))!.SessionId);
    }

    [Fact]
    public async Task Unsupported_schema_version_is_treated_as_empty()
    {
        var paths = new CachePaths(directory);
        Directory.CreateDirectory(paths.Root);
        await File.WriteAllTextAsync(
            paths.SessionCachePath,
            """{ "version": 99, "entries": { "github:owner/repo#1": {} } }""");

        Assert.Null(await Cache().GetAsync("github:owner/repo#1", CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

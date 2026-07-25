using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Caching;

public sealed class JsonWorkItemRuntimeStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(), $"wrighty-session-cache-{Guid.NewGuid():N}");

    private JsonWorkItemRuntimeStore Cache() => new(new CachePaths(directory));

    private static StoredWorkItemRuntime Record(string sessionId) => new(
        new SessionAddress("claude", sessionId, "/tmp/workspace"),
        null,
        null,
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
        Assert.Equal("claude", reread!.Session?.Agent);
        Assert.Equal("session-one", reread.Session?.Id);
        Assert.Equal("/tmp/workspace", reread.Session?.WorkspacePath);
        Assert.NotNull(reread.LastClaimExpiresAt);
        Assert.Null(await Cache().GetAsync("github:owner/repo#2", CancellationToken.None));
        Assert.True(File.Exists(new CachePaths(directory).WorkItemRuntimePath));
    }

    [Fact]
    public async Task Put_overwrites_the_existing_record_per_key()
    {
        var cache = Cache();
        await cache.PutAsync("github:owner/repo#1", Record("session-old"), CancellationToken.None);
        await cache.PutAsync("github:owner/repo#1", Record("session-new"), CancellationToken.None);
        await cache.PutAsync("github:owner/repo#2", Record("session-other"), CancellationToken.None);

        Assert.Equal("session-new",
            (await cache.GetAsync("github:owner/repo#1", CancellationToken.None))!.Session?.Id);
        Assert.Equal("session-other",
            (await cache.GetAsync("github:owner/repo#2", CancellationToken.None))!.Session?.Id);
    }

    [Fact]
    public async Task Structured_failure_round_trips_with_stable_wire_names()
    {
        var failure = new AgentFailure(
            AgentFailureKind.UsageExhausted,
            "usage_limit_reached",
            new DateTimeOffset(2026, 7, 24, 4, 0, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(30),
            true,
            AgentFailureConfidence.Authoritative,
            "Usage limit reached.");
        var record = Record("session-one") with
        {
            LastRun = new LastRunRecord(
                RunOutcome.Failed,
                new DateTimeOffset(2026, 7, 24, 3, 0, 0, TimeSpan.Zero),
                Failure: failure)
        };

        await Cache().PutAsync("github:owner/repo#1", record, CancellationToken.None);

        var reread = await Cache().GetAsync("github:owner/repo#1", CancellationToken.None);
        var json = await File.ReadAllTextAsync(new CachePaths(directory).WorkItemRuntimePath);
        Assert.Equal(failure, reread?.LastRun?.Failure);
        Assert.Contains("\"kind\": \"usage-exhausted\"", json);
        Assert.Contains("\"confidence\": \"authoritative\"", json);
    }

    [Fact]
    public async Task Corrupt_runtime_file_fails_closed()
    {
        var paths = new CachePaths(directory);
        Directory.CreateDirectory(paths.Root);
        await File.WriteAllTextAsync(paths.WorkItemRuntimePath, "{ not json");

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(
            () => Cache().GetAsync("github:owner/repo#1", CancellationToken.None));
        Assert.Equal("WORK_ITEM_RUNTIME_CORRUPT", exception.Code);
    }

    [Fact]
    public async Task Unsupported_schema_version_fails_closed()
    {
        var paths = new CachePaths(directory);
        Directory.CreateDirectory(paths.Root);
        await File.WriteAllTextAsync(
            paths.WorkItemRuntimePath,
            """{ "version": 99, "entries": { "github:owner/repo#1": {} } }""");

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(
            () => Cache().GetAsync("github:owner/repo#1", CancellationToken.None));
        Assert.Equal("WORK_ITEM_RUNTIME_CORRUPT", exception.Code);
    }

    [Fact]
    public async Task Pre_overhaul_cache_is_rejected_without_migration()
    {
        var paths = new CachePaths(directory);
        Directory.CreateDirectory(paths.Root);
        await File.WriteAllTextAsync(
            Path.Combine(paths.Root, "sessions-v1.json"),
            """{ "version": 1, "entries": {} }""");

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(
            () => Cache().GetAsync("github:owner/repo#1", CancellationToken.None));

        Assert.Equal("STORE_SCHEMA_UNSUPPORTED", exception.Code);
        Assert.Contains("sessions-v1.json", exception.Message);
        Assert.Contains("Remove or rename", exception.Message);
        Assert.False(File.Exists(paths.WorkItemRuntimePath));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

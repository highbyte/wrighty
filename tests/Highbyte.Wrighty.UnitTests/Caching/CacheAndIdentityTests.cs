using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Identity;

namespace Highbyte.Wrighty.UnitTests.Caching;

public sealed class CacheAndIdentityTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"wrighty-cache-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Node_cache_round_trips_and_invalidates_metadata()
    {
        var cache = new JsonNodeIdCache(new CachePaths(directory));
        var metadata = new ProjectMetadata(
            "project-id",
            "status-field-id",
            new Dictionary<string, string> { ["Todo"] = "todo-id" },
            "priority-field-id");

        await cache.PutAsync("github.com/owner/1", metadata, CancellationToken.None);
        var loaded = await cache.GetAsync("github.com/owner/1", CancellationToken.None);
        await cache.InvalidateAsync("github.com/owner/1", CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(metadata.ProjectId, loaded.ProjectId);
        Assert.Equal(metadata.StatusFieldId, loaded.StatusFieldId);
        Assert.Equal(metadata.PriorityFieldId, loaded.PriorityFieldId);
        Assert.Equal("todo-id", loaded.StatusOptions["Todo"]);
        Assert.Null(await cache.GetAsync("github.com/owner/1", CancellationToken.None));
    }

    [Fact]
    public async Task Worker_identity_is_stable_and_does_not_expose_the_install_uuid()
    {
        var paths = new CachePaths(directory);
        var first = await new WorkerIdentityProvider(paths).GetIdentityAsync(CancellationToken.None);
        var second = await new WorkerIdentityProvider(paths).GetIdentityAsync(CancellationToken.None);
        var identityFile = await File.ReadAllTextAsync(paths.IdentityPath);

        Assert.Equal(first, second);
        Assert.Matches("^[0-9a-f]{12}$", first);
        Assert.DoesNotContain(first, identityFile);
    }

    [Fact]
    public async Task Concurrent_worker_identity_initialization_converges_on_one_identity()
    {
        var paths = new CachePaths(directory);
        var identities = await Task.WhenAll(
            Enumerable.Range(0, 10)
                .Select(_ => new WorkerIdentityProvider(paths)
                    .GetIdentityAsync(CancellationToken.None)));

        Assert.Single(identities.Distinct());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

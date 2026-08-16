using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Storage;

namespace Highbyte.Wrighty.UnitTests.Storage;

public sealed class StorageLocationCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(),
        $"wrighty-storage-location-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Local_markdown_catalog_separates_content_runtime_cache_and_credentials()
    {
        var repository = Path.Combine(root, "repository");
        var cache = new CachePaths(Path.Combine(root, "cache"));
        var configPath = Path.Combine(repository, TrackerConfigLoader.FileName);
        var userPath = Path.Combine(root, "config", "settings-v2.json");
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            SourcePath = configPath,
            LocalMarkdown = new LocalMarkdownBackendConfig { Path = ".wrighty" }
        };

        var locations = new StorageLocationCatalog(cache).Describe(
            configPath,
            config,
            userPath);

        Assert.Equal(
            Path.Combine(repository, ".wrighty"),
            ById(locations, "local.store").Path);
        Assert.Equal(
            Path.Combine(repository, ".wrighty", ".wrighty-runtime-v1.json"),
            ById(locations, "local.runtime").Path);
        Assert.Equal(StorageLifecycle.Container, ById(locations, "local.store").Lifecycle);
        Assert.Equal(
            Path.Combine(repository, ".wrighty.json.edit.lock"),
            ById(locations, "repository.configuration.edit-lock").Path);
        Assert.Equal(
            StorageLocationKind.Pattern,
            ById(locations, "repository.configuration.temporary").Kind);
        Assert.Equal(StorageLifecycle.RuntimeState, ById(locations, "local.runtime").Lifecycle);
        Assert.Equal(StorageLifecycle.Container, ById(locations, "cache.root").Lifecycle);
        Assert.Equal(StorageLifecycle.RuntimeState, ById(locations, "cache.identity").Lifecycle);
        Assert.Equal(StorageLifecycle.RuntimeState, ById(locations, "cache.work-items").Lifecycle);
        Assert.Equal("WRIGHTY_CACHE_DIR", ById(locations, "cache.root").Source);
        Assert.Equal(StorageLifecycle.Credential, ById(locations, "web.managed-token").Lifecycle);
        Assert.DoesNotContain(locations, location => location.Id == "github.issue-form");
    }

    [Fact]
    public void GitHub_catalog_names_every_fixed_cache_file_and_repository_artifact()
    {
        var repository = Path.Combine(root, "repository");
        var cache = new CachePaths(Path.Combine(root, "cache"));
        var configPath = Path.Combine(repository, TrackerConfigLoader.FileName);
        var config = new TrackerConfig
        {
            Backend = "github",
            SourcePath = configPath,
            GitHub = new GitHubBackendConfig
            {
                Repository = "owner/repository",
                ProjectOwner = "owner",
                ProjectNumber = 1
            }
        };

        var locations = new StorageLocationCatalog(cache).Describe(
            configPath,
            config,
            Path.Combine(root, "config", "settings-v2.json"));

        Assert.Equal(cache.NodeCachePath, ById(locations, "cache.nodes").Path);
        Assert.Equal(cache.IdentityPath, ById(locations, "cache.identity").Path);
        Assert.Equal(cache.WorkItemRuntimePath, ById(locations, "cache.work-items").Path);
        Assert.Equal(cache.ProviderCapacityPath, ById(locations, "cache.provider-capacity").Path);
        Assert.Equal(cache.ProviderCapacityLockPath, ById(locations, "cache.provider-capacity-lock").Path);
        Assert.Equal(cache.WorkerInstancesRoot, ById(locations, "cache.worker-instances").Path);
        Assert.Equal(cache.HandoffRoot, ById(locations, "cache.handoffs").Path);
        Assert.Equal(
            StorageLocationKind.Pattern,
            ById(locations, "cache.handoff-files").Kind);
        Assert.Equal(cache.CopilotSharesRoot, ById(locations, "cache.copilot-shares").Path);
        Assert.Equal(
            Path.Combine(repository, ".github", "ISSUE_TEMPLATE", "wrighty-task.yml"),
            ById(locations, "github.issue-form").Path);
        Assert.Equal(
            StorageLocationKind.Pattern,
            ById(locations, "github.import-manifest-files").Kind);
        Assert.DoesNotContain(locations, location => location.Id == "local.store");
    }

    private static StorageLocationDescriptor ById(
        IEnumerable<StorageLocationDescriptor> locations,
        string id) => Assert.Single(locations, location => location.Id == id);

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}

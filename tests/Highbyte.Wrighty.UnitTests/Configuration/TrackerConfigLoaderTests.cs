using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using System.Text.Json;

namespace Highbyte.Wrighty.UnitTests.Configuration;

public sealed class TrackerConfigLoaderTests : IDisposable
{
    private readonly string directory = Path.Combine(
        Path.GetTempPath(),
        $"wrighty-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task LoadAsync_finds_config_in_a_parent_directory()
    {
        var child = Path.Combine(directory, "a", "b");
        Directory.CreateDirectory(child);
        await File.WriteAllTextAsync(
            Path.Combine(directory, TrackerConfigLoader.FileName),
            """
            {
              "backend": "github",
              "github": {
                "repository": "owner/repo",
                "projectNumber": 12
              }
            }
            """);

        var config = await new TrackerConfigLoader().LoadAsync(child, CancellationToken.None);

        Assert.Equal("owner/repo", config.Repository);
        Assert.Equal("owner", config.EffectiveProjectOwner);
        Assert.Equal(12, config.ProjectNumber);
        Assert.Equal(60, config.LeaseMinutes);
        Assert.Equal("Done", config.DefaultFinishTo);
        Assert.Equal(10, config.ClaimHistoryLimit);
        Assert.Equal("Current agent type", config.AgentTypeField);
        Assert.Equal("Current session ID", config.SessionIdField);
        Assert.Equal("github", config.Backend);
    }

    [Fact]
    public async Task LoadAsync_uses_explicit_config_path_override_from_another_workspace()
    {
        var trackerRoot = Path.Combine(directory, "tracker");
        var worktree = Path.Combine(directory, "worktree");
        Directory.CreateDirectory(trackerRoot);
        Directory.CreateDirectory(worktree);
        var configPath = Path.Combine(trackerRoot, TrackerConfigLoader.FileName);
        await File.WriteAllTextAsync(configPath, """
            {
              "backend": "local-markdown",
              "localMarkdown": { "path": ".wrighty" }
            }
            """);

        var config = await new TrackerConfigLoader(() => configPath)
            .LoadAsync(worktree, CancellationToken.None);

        Assert.Equal(Path.GetFullPath(configPath), config.SourcePath);
        Assert.Equal(".wrighty", config.LocalMarkdown!.Path);
    }

    [Fact]
    public async Task LoadAsync_rejects_an_invalid_repository()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, TrackerConfigLoader.FileName),
            """{ "backend": "github", "github": { "repository": "invalid", "projectNumber": 1 } }""");

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None));

        Assert.Equal("CONFIG_INVALID", exception.Code);
        Assert.Equal(3, exception.ExitCode);
    }

    [Fact]
    public async Task LoadAsync_rejects_an_invalid_claim_history_limit()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, TrackerConfigLoader.FileName),
            """{ "backend": "github", "github": { "repository": "owner/repo", "projectNumber": 1, "claimHistoryLimit": -1 } }""");

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None));

        Assert.Equal("CONFIG_INVALID", exception.Code);
        Assert.Equal(3, exception.ExitCode);
    }

    [Fact]
    public async Task LoadAsync_rejects_an_unknown_backend()
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, TrackerConfigLoader.FileName),
            """{ "backend": "other" }""");

        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None));

        Assert.Equal("CONFIG_INVALID", exception.Code);
        Assert.Contains("Unsupported backend", exception.Message);
    }

    [Fact]
    public async Task SaveAsync_writes_an_atomic_public_configuration_without_computed_properties()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, TrackerConfigLoader.FileName);
        var store = new TrackerConfigLoader();

        await store.SaveAsync(
            path,
            new TrackerConfig
            {
                Repository = "owner/repo",
                ProjectOwner = "owner",
                ProjectNumber = 12,
                LinkRepository = false
            },
            CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = document.RootElement;
        var github = root.GetProperty("github");
        Assert.Equal("owner/repo", github.GetProperty("repository").GetString());
        Assert.False(github.GetProperty("linkRepository").GetBoolean());
        Assert.False(root.TryGetProperty("repository", out _));
        Assert.False(root.TryGetProperty("repositoryOwner", out _));
        Assert.False(root.TryGetProperty("repositoryName", out _));
        Assert.False(root.TryGetProperty("effectiveProjectOwner", out _));
        Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
    }

    [Fact]
    public async Task TryLoadPathAsync_never_replaces_an_invalid_existing_file()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, TrackerConfigLoader.FileName);
        const string invalid = "{ invalid";
        await File.WriteAllTextAsync(path, invalid);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            new TrackerConfigLoader().TryLoadPathAsync(path, CancellationToken.None));

        Assert.Equal("CONFIG_INVALID", exception.Code);
        Assert.Equal(invalid, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task LoadAsync_reports_missing_and_empty_configuration_files()
    {
        Directory.CreateDirectory(directory);
        var loader = new TrackerConfigLoader();

        var missing = await Assert.ThrowsAsync<TrackerException>(() =>
            loader.LoadAsync(directory, CancellationToken.None));
        Assert.Equal("CONFIG_NOT_FOUND", missing.Code);

        await File.WriteAllTextAsync(Path.Combine(directory, TrackerConfigLoader.FileName), string.Empty);
        var empty = await Assert.ThrowsAsync<TrackerException>(() =>
            loader.LoadAsync(directory, CancellationToken.None));
        Assert.Equal("CONFIG_INVALID", empty.Code);
    }

    [Fact]
    public async Task ResolvePath_handles_explicit_existing_and_default_paths()
    {
        var loader = new TrackerConfigLoader();
        var child = Path.Combine(directory, "child");
        Directory.CreateDirectory(child);

        Assert.Equal(
            Path.Combine(child, "custom.json"),
            loader.ResolvePath(child, "custom.json"));
        Assert.Equal(
            Path.Combine(child, TrackerConfigLoader.FileName),
            loader.ResolvePath(child, null));

        var parentPath = Path.Combine(directory, TrackerConfigLoader.FileName);
        await File.WriteAllTextAsync(parentPath, "{}");
        Assert.Equal(parentPath, loader.ResolvePath(child, null));
    }

    [Fact]
    public async Task TryLoadPath_returns_null_or_valid_configuration()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, TrackerConfigLoader.FileName);
        var loader = new TrackerConfigLoader();

        Assert.Null(await loader.TryLoadPathAsync(path, CancellationToken.None));

        await File.WriteAllTextAsync(path, """
            {
              "backend": "local-markdown",
              "localMarkdown": {
                "path": "items",
                "statuses": ["Todo", "Done"],
                "priorities": []
              },
              "archive": { "onStatuses": ["Done"] }
            }
            """);
        var config = await loader.TryLoadPathAsync(path, CancellationToken.None);

        Assert.NotNull(config);
        Assert.Equal("local-markdown", config.Backend);
        Assert.Equal(path, config.SourcePath);
        Assert.Equal(["Todo", "Done"], config.LocalMarkdown!.Statuses);
        Assert.True(config.EffectiveWeb.ProtectNonHumanClaims);
    }

    [Fact]
    public async Task Web_non_human_claim_protection_can_be_disabled_explicitly()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, TrackerConfigLoader.FileName);
        await File.WriteAllTextAsync(path, """
            {
              "backend": "local-markdown",
              "localMarkdown": {},
              "web": { "protectNonHumanClaims": false }
            }
            """);

        var config = await new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None);

        Assert.False(config.EffectiveWeb.ProtectNonHumanClaims);
    }

    [Fact]
    public async Task Worker_workspace_mode_is_loaded_from_configuration()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, TrackerConfigLoader.FileName);
        await File.WriteAllTextAsync(path, """
            {
              "backend": "local-markdown",
              "localMarkdown": {},
              "worker": { "workspaceMode": "shared" }
            }
            """);

        var config = await new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None);

        Assert.Equal("shared", config.EffectiveWorker.WorkspaceMode);
    }

    [Fact]
    public async Task TryLoadPath_wraps_configuration_validation_with_path_details()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, TrackerConfigLoader.FileName);
        await File.WriteAllTextAsync(path, """{"backend":"local-markdown"}""");

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            new TrackerConfigLoader().TryLoadPathAsync(path, CancellationToken.None));

        Assert.Equal("CONFIG_INVALID", exception.Code);
        Assert.Equal(path, exception.Details["configPath"]);
        Assert.Contains("localMarkdown section", exception.Message);
    }

    [Fact]
    public async Task SaveAsync_persists_local_markdown_configuration()
    {
        var path = Path.Combine(directory, "nested", TrackerConfigLoader.FileName);
        var config = ValidLocal() with
        {
            Archive = new ArchiveConfig { OnStatuses = ["Done"] }
        };

        await new TrackerConfigLoader().SaveAsync(path, config, CancellationToken.None);

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var root = document.RootElement;
        Assert.Equal("local-markdown", root.GetProperty("backend").GetString());
        Assert.Equal("items", root.GetProperty("localMarkdown").GetProperty("path").GetString());
        Assert.False(root.TryGetProperty("github", out _));
    }

    [Fact]
    public async Task SaveAsync_maps_directory_creation_failure()
    {
        Directory.CreateDirectory(directory);
        var blocker = Path.Combine(directory, "blocker");
        await File.WriteAllTextAsync(blocker, "file blocks directory creation");
        var path = Path.Combine(blocker, TrackerConfigLoader.FileName);

        var exception = await Assert.ThrowsAsync<TrackerException>(() =>
            new TrackerConfigLoader().SaveAsync(path, ValidGitHub(), CancellationToken.None));

        Assert.Equal("CONFIG_WRITE_FAILED", exception.Code);
        Assert.Equal(Path.GetFullPath(path), exception.Details["configPath"]);
    }

    [Fact]
    public async Task SaveAsync_allows_GitHub_destination_with_Local_Markdown_source()
    {
        var path = Path.Combine(directory, TrackerConfigLoader.FileName);
        var config = ValidGitHub() with
        {
            LocalMarkdown = new LocalMarkdownBackendConfig
            {
                Path = "source-store",
                Statuses = ["Todo", "Done"],
                Priorities = ["P1"]
            }
        };

        await new TrackerConfigLoader().SaveAsync(path, config, CancellationToken.None);
        var loaded = await new TrackerConfigLoader().TryLoadPathAsync(path, CancellationToken.None);

        Assert.Equal("github", loaded!.Backend);
        Assert.Equal("owner/repo", loaded.Repository);
        Assert.Equal("source-store", loaded.LocalMarkdown!.Path);
    }

    [Fact]
    public async Task SaveAsync_rejects_invalid_backend_configurations()
    {
        var cases = new (TrackerConfig Config, string Message)[]
        {
            (new TrackerConfig { Backend = "other" }, "Unsupported backend"),
            (ValidGitHub() with
            {
                LocalMarkdown = new LocalMarkdownBackendConfig { Statuses = [] }
            }, "statuses cannot be empty"),
            (ValidGitHub() with { Repository = "invalid" }, "owner/name"),
            (ValidGitHub() with { Repository = "owner/" }, "owner/name"),
            (ValidGitHub() with { ProjectNumber = 0 }, "projectNumber"),
            (ValidGitHub() with { ProjectOwner = " " }, "projectOwner"),
            (ValidGitHub() with { ClaimHistoryLimit = 1001 }, "claimHistoryLimit"),
            (ValidGitHub() with { StatusField = " " }, "statusField"),
            (ValidGitHub() with { PriorityField = " " }, "statusField"),
            (ValidGitHub() with { AgentTypeField = " " }, "statusField"),
            (ValidGitHub() with { SessionIdField = " " }, "statusField"),
            (ValidGitHub() with { CreationAttemptIdField = " " }, "statusField"),
            (ValidGitHub() with { GitHubHost = " " }, "statusField"),
            (ValidGitHub() with { LeaseMinutes = 4 }, "leaseMinutes"),
            (ValidGitHub() with { LeaseMinutes = 1441 }, "leaseMinutes"),
            (ValidGitHub() with { Archive = new ArchiveConfig { OnStatuses = ["Done", "done"] } }, "archive.onStatuses"),
            (ValidGitHub() with { DefaultPickFrom = " " }, "defaultPickFrom"),
            (ValidGitHub() with { DefaultPickTo = " " }, "defaultPickFrom"),
            (ValidGitHub() with { DefaultFinishTo = " " }, "defaultPickFrom"),
            (ValidGitHub() with
            {
                Worker = new WorkerConfig { WorkspaceMode = "parallel" }
            }, "worker.workspaceMode"),
            (ValidGitHub() with
            {
                Worker = new WorkerConfig
                {
                    Completion = new WorkerCompletionConfig { Commit = "auto" }
                }
            }, "worker.completion.commit"),
            (ValidGitHub() with
            {
                Worker = new WorkerConfig
                {
                    Completion = new WorkerCompletionConfig { Integration = "merge" }
                }
            }, "worker.completion.integration"),
            (ValidGitHub() with
            {
                Worker = new WorkerConfig { WorktreeRoot = "{repository}/trees" }
            }, "worker.worktreeRoot"),
            (ValidGitHub() with
            {
                Worker = new WorkerConfig { BranchFormat = "feature/{slug}" }
            }, "worker.branchFormat"),
            (ValidGitHub() with
            {
                Worker = new WorkerConfig { WorktreeNameFormat = " " }
            }, "worker.worktreeNameFormat"),
            (ValidLocal() with { GitHub = ValidGitHub().EffectiveGitHub }, "cannot also contain a github"),
            (new TrackerConfig { Backend = "local-markdown" }, "requires a localMarkdown"),
            (ValidLocal() with { LocalMarkdown = ValidLocal().LocalMarkdown! with { Statuses = [] } }, "statuses cannot be empty"),
            (ValidLocal() with { LocalMarkdown = ValidLocal().LocalMarkdown! with { Statuses = ["Todo", "todo"] } }, "statuses cannot contain"),
            (ValidLocal() with { LocalMarkdown = ValidLocal().LocalMarkdown! with { Priorities = ["P1", " "] } }, "priorities cannot contain"),
            (ValidLocal() with { LocalMarkdown = ValidLocal().LocalMarkdown! with { Path = " " } }, "path cannot be empty"),
            (ValidLocal() with { Archive = new ArchiveConfig { OnStatuses = ["Missing"] } }, "not present")
        };

        foreach (var (config, message) in cases)
        {
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N"), TrackerConfigLoader.FileName);
            var exception = await Assert.ThrowsAsync<TrackerException>(() =>
                new TrackerConfigLoader().SaveAsync(path, config, CancellationToken.None));
            Assert.Equal("CONFIG_INVALID", exception.Code);
            Assert.Contains(message, exception.Message);
        }
    }

    private static TrackerConfig ValidGitHub() => new()
    {
        Repository = "owner/repo",
        ProjectNumber = 1
    };

    private static TrackerConfig ValidLocal() => new()
    {
        Backend = "local-markdown",
        LocalMarkdown = new LocalMarkdownBackendConfig
        {
            Path = "items",
            Statuses = ["Todo", "Done"],
            Priorities = ["P1"]
        }
    };

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

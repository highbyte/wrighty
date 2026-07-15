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

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

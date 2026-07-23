using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Configuration;

public sealed partial class TrackerConfigLoader(Func<string?>? configPathOverride = null) : ITrackerConfigStore
{
    public const string FileName = ".wrighty.json";
    public const string ConfigPathEnvironmentVariable = "WRIGHTY_CONFIG_PATH";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public async Task<TrackerConfig> LoadAsync(
        string startDirectory,
        CancellationToken cancellationToken)
    {
        var overridePath = (configPathOverride ?? (() =>
            Environment.GetEnvironmentVariable(ConfigPathEnvironmentVariable)))();
        var path = string.IsNullOrWhiteSpace(overridePath)
            ? FindConfig(startDirectory)
            : Path.GetFullPath(overridePath, startDirectory);
        if (path is null)
        {
            throw new TrackerException(
                "CONFIG_NOT_FOUND",
                $"Could not find {FileName} in the current directory or any parent directory.",
                3);
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var config = await JsonSerializer.DeserializeAsync<TrackerConfig>(
                stream,
                JsonOptions,
                cancellationToken);

            if (config is null)
            {
                throw new JsonException("The configuration file is empty.");
            }

            config = config with { SourcePath = path };
            Validate(config);
            return config;
        }
        catch (TrackerException exception) when (exception.Code == "CONFIG_INVALID")
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                $"Could not read configuration from {path}: {exception.Message}",
                3,
                new Dictionary<string, object?> { ["configPath"] = path },
                exception);
        }
        catch (TrackerException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                $"Could not read configuration from {path}: {exception.Message}",
                3,
                innerException: exception);
        }
    }

    public string ResolvePath(string startDirectory, string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath, startDirectory);
        }

        // Honor WRIGHTY_CONFIG_PATH with the same precedence as LoadAsync so init resolves the same
        // config every data command does. Without this, a worker-spawned agent whose worktree lives
        // outside the repo (the config env var is set, but upward discovery cannot reach the repo's
        // .wrighty.json) sees `init --check` report "not initialized" while `get`/`finish` succeed.
        var overridePath = (configPathOverride ?? (() =>
            Environment.GetEnvironmentVariable(ConfigPathEnvironmentVariable)))();
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath, startDirectory);
        }

        return FindConfig(startDirectory)
            ?? Path.Combine(Path.GetFullPath(startDirectory), FileName);
    }

    public async Task<TrackerConfig?> TryLoadPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var config = await JsonSerializer.DeserializeAsync<TrackerConfig>(
                stream,
                JsonOptions,
                cancellationToken);
            if (config is null)
            {
                throw new JsonException("The configuration file is empty.");
            }

            config = config with { SourcePath = path };
            Validate(config);
            return config;
        }
        catch (TrackerException exception) when (exception.Code == "CONFIG_INVALID")
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                $"Could not read configuration from {path}: {exception.Message}",
                3,
                new Dictionary<string, object?> { ["configPath"] = path },
                exception);
        }
        catch (TrackerException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                $"Could not read configuration from {path}: {exception.Message}",
                3,
                new Dictionary<string, object?> { ["configPath"] = path },
                exception);
        }
    }

    public async Task SaveAsync(
        string path,
        TrackerConfig config,
        CancellationToken cancellationToken)
    {
        config = NormalizeForPersistence(config, path);
        Validate(config);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    config,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web)
                    {
                        WriteIndented = true,
                        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                    },
                    cancellationToken);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new TrackerException(
                "CONFIG_WRITE_FAILED",
                $"Could not write configuration to {fullPath}: {exception.Message}",
                3,
                new Dictionary<string, object?> { ["configPath"] = fullPath },
                exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A failed best-effort cleanup must not mask the original write result.
                }
            }
        }
    }

    private static string? FindConfig(string startDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private static TrackerConfig NormalizeForPersistence(TrackerConfig config, string path) =>
        string.Equals(config.Backend, "github", StringComparison.OrdinalIgnoreCase) &&
        config.GitHub is null
            ? config with { GitHub = config.EffectiveGitHub, SourcePath = path }
            : config with { SourcePath = path };

    private static void Validate(TrackerConfig config)
    {
        if (string.Equals(config.Backend, "local-markdown", StringComparison.OrdinalIgnoreCase))
        {
            ValidateLocalMarkdown(config);
            return;
        }

        if (!string.Equals(config.Backend, "github", StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                $"Unsupported backend '{config.Backend}'. Available backends are 'github' and 'local-markdown'.",
                3);
        }

        ValidateGitHub(config);
    }

    private static void ValidateLocalMarkdown(TrackerConfig config)
    {
        ValidateCommon(config);
        if (config.GitHub is not null)
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                "A local-markdown configuration cannot also contain a github section.",
                3);
        }
        if (config.LocalMarkdown is null)
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                "The local-markdown backend requires a localMarkdown section.",
                3);
        }

        ValidateLocalMarkdownSection(config.LocalMarkdown);
        ValidateArchiveStatuses(config);
    }

    private static void ValidateLocalMarkdownSection(LocalMarkdownBackendConfig localMarkdown)
    {
        ValidateNames(localMarkdown.Statuses, "localMarkdown.statuses", required: true);
        ValidateNames(localMarkdown.Priorities, "localMarkdown.priorities", required: false);
        if (string.IsNullOrWhiteSpace(localMarkdown.Path))
        {
            throw new TrackerException("CONFIG_INVALID", "localMarkdown.path cannot be empty.", 3);
        }
    }

    private static void ValidateArchiveStatuses(TrackerConfig config)
    {
        foreach (var status in config.Archive.OnStatuses)
        {
            if (!config.LocalMarkdown!.Statuses.Contains(status, StringComparer.OrdinalIgnoreCase))
            {
                throw new TrackerException(
                    "CONFIG_INVALID",
                    $"Archive status '{status}' is not present in localMarkdown.statuses.",
                    3);
            }
        }
    }

    private static void ValidateGitHub(TrackerConfig config)
    {
        ValidateCommon(config);

        if (config.LocalMarkdown is not null)
        {
            ValidateLocalMarkdownSection(config.LocalMarkdown);
        }

        if (config.GitHub is null && string.IsNullOrWhiteSpace(config.Repository))
        {
            throw new TrackerException("CONFIG_INVALID", "The github backend requires a github section.", 3);
        }

        var repositoryParts = config.Repository?.Split('/') ?? [];
        if (repositoryParts.Length != 2 || repositoryParts.Any(string.IsNullOrWhiteSpace))
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                "The repository must use the owner/name format.",
                3);
        }

        if (config.ProjectNumber <= 0)
        {
            throw new TrackerException("CONFIG_INVALID", "projectNumber must be positive.", 3);
        }

        if (config.ProjectOwner is not null && string.IsNullOrWhiteSpace(config.ProjectOwner))
        {
            throw new TrackerException("CONFIG_INVALID", "projectOwner cannot be empty.", 3);
        }

        if (config.ClaimHistoryLimit is < 0 or > 1000)
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                "claimHistoryLimit must be between 0 and 1000.",
                3);
        }

        ValidateGitHubNames(config);
    }

    private static void ValidateGitHubNames(TrackerConfig config)
    {
        var values = new[]
        {
            config.StatusField,
            config.PriorityField,
            config.AgentTypeField,
            config.SessionIdField,
            config.CreationAttemptIdField,
            config.GitHubHost
        };
        if (values.Any(string.IsNullOrWhiteSpace))
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                "statusField, priorityField, agentTypeField, sessionIdField, creationAttemptIdField, and gitHubHost cannot be empty.",
                3);
        }
    }

    private static void ValidateCommon(TrackerConfig config)
    {
        if (config.LeaseMinutes is < 5 or > 1440)
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                "leaseMinutes must be between 5 and 1440.",
                3);
        }

        ValidateNames(config.Archive.OnStatuses, "archive.onStatuses", required: false);
        if (string.IsNullOrWhiteSpace(config.DefaultPickFrom) ||
            string.IsNullOrWhiteSpace(config.DefaultPickTo) ||
            string.IsNullOrWhiteSpace(config.DefaultFinishTo))
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                "defaultPickFrom, defaultPickTo, and defaultFinishTo cannot be empty.",
                3);
        }

        ValidateChoice(config.Worker?.WorkspaceMode,
            "worker.workspaceMode must be current, shared, or worktree.",
            "current", "shared", "worktree");
        ValidateChoice(config.Worker?.Completion?.Commit,
            "worker.completion.commit must be inspect or agent.",
            "inspect", "agent");
        ValidateChoice(config.Worker?.Completion?.Integration,
            "worker.completion.integration must be none, merge-local, or push-pr.",
            "none", "merge-local", "push-pr");
        ValidateChoice(config.Worker?.HandoverComment,
            "worker.handoverComment must be full, minimal, or off.",
            "full", "minimal", "off");

        ValidateTemplate(config.Worker?.WorktreeRoot, "worker.worktreeRoot",
            ["repo", "repoParent", "home", "repoPathHash"]);
        ValidateTemplate(config.Worker?.BranchFormat, "worker.branchFormat",
            ["id", "number", "title", "unique", "agent", "date"]);
        ValidateTemplate(config.Worker?.WorktreeNameFormat, "worker.worktreeNameFormat",
            ["id", "number", "title", "unique", "agent", "date"]);
    }

    private static void ValidateChoice(string? value, string message, params string[] allowed)
    {
        if (value is { } candidate && !allowed.Contains(candidate.ToLowerInvariant()))
            throw new TrackerException("CONFIG_INVALID", message, 3);
    }

    private static void ValidateTemplate(
        string? template,
        string property,
        IReadOnlyList<string> placeholders)
    {
        if (template is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(template))
        {
            throw new TrackerException("CONFIG_INVALID", $"{property} cannot be empty.", 3);
        }

        var unknown = TemplatePlaceholder().Matches(template)
            .Select(match => match.Groups[1].Value)
            .FirstOrDefault(name => !placeholders.Contains(name, StringComparer.Ordinal));
        if (unknown is not null)
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                $"{property} contains unknown placeholder '{{{unknown}}}'. " +
                $"Supported: {string.Join(", ", placeholders.Select(name => $"{{{name}}}"))}.",
                3);
        }
    }

    [GeneratedRegex(@"\{([^{}]*)\}", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TemplatePlaceholder();

    private static void ValidateNames(
        IReadOnlyList<string> values,
        string property,
        bool required)
    {
        if (required && values.Count == 0)
        {
            throw new TrackerException("CONFIG_INVALID", $"{property} cannot be empty.", 3);
        }

        if (values.Any(string.IsNullOrWhiteSpace) ||
            values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count)
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                $"{property} cannot contain empty or duplicate values.",
                3);
        }
    }
}

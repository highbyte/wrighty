using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Configuration;

public sealed partial class TrackerConfigLoader(Func<string?>? configPathOverride = null) : ITrackerConfigStore
{
    public const string FileName = ".wrighty.json";
    public const string ConfigPathEnvironmentVariable = "WRIGHTY_CONFIG_PATH";
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
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
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return DeserializeExact(bytes, path);
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
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
            return DeserializeExact(bytes, path);
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

    private static string Revision(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static TrackerConfig DeserializeExact(
        ReadOnlySpan<byte> bytes,
        string sourcePath)
    {
        var config = JsonSerializer.Deserialize<TrackerConfig>(bytes, JsonOptions);
        if (config is null)
            throw new JsonException("The configuration file is empty.");
        config = config with
        {
            SourcePath = sourcePath,
            SourceRevision = Revision(bytes)
        };
        Validate(config);
        return config;
    }

    private static TrackerConfig NormalizeForPersistence(TrackerConfig config, string path) =>
        string.Equals(config.Backend, "github", StringComparison.OrdinalIgnoreCase) &&
        config.GitHub is null
            ? config with
            {
                SchemaVersion = CurrentSchemaVersion,
                GitHub = config.EffectiveGitHub,
                SourcePath = path
            }
            : config with
            {
                SchemaVersion = CurrentSchemaVersion,
                SourcePath = path
            };

    internal static void Validate(TrackerConfig config)
    {
        if (config.SchemaVersion is <= 0)
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                "schemaVersion must be a positive integer.",
                3);
        }
        if (config.SchemaVersion is > CurrentSchemaVersion)
        {
            throw new TrackerException(
                "CONFIG_VERSION_UNSUPPORTED",
                $"Configuration schema version {config.SchemaVersion} is newer than this Wrighty " +
                $"build supports ({CurrentSchemaVersion}). Upgrade Wrighty before using this configuration.",
                3,
                new Dictionary<string, object?>
                {
                    ["schemaVersion"] = config.SchemaVersion,
                    ["supportedSchemaVersion"] = CurrentSchemaVersion
                });
        }

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
        ValidateWorkflowStatuses(config);
    }

    /// <summary>
    /// The same contract archive statuses already follow: a workflow default naming a status the
    /// board does not have is a broken setup — a silently missing column and an empty pick pool —
    /// and must fail at load rather than at first use. Matters doubly for the worker-queue idiom,
    /// where <c>defaultPickFrom</c> typically names a dedicated status such as "Worker queue" that
    /// the operator must place in <c>localMarkdown.statuses</c> (second, after triage, is the
    /// recommended position).
    /// </summary>
    private static void ValidateWorkflowStatuses(TrackerConfig config)
    {
        foreach (var (value, name) in new[]
                 {
                     (config.DefaultPickFrom, "defaultPickFrom"),
                     (config.DefaultPickTo, "defaultPickTo"),
                     (config.DefaultFinishTo, "defaultFinishTo")
                 })
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                !config.LocalMarkdown!.Statuses.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                throw new TrackerException(
                    "CONFIG_INVALID",
                    $"Workflow status '{value}' ({name}) is not present in localMarkdown.statuses.",
                    3);
            }
        }
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
        ValidateTrustedCommentAuthors(config.GitHub?.TrustedCommentAuthors);
        ValidateLoginList(config.GitHub?.ContextApprovers, "github.contextApprovers");
        ValidateContinuationTrigger(config.Worker?.Continuation?.Trigger);
        ValidateControlReactions(config.Worker?.Continuation);
        ValidateCompletionPolicy(config.Worker?.Completion?.Policy);
    }

    /// <summary>
    /// An unrecognised trigger must fail rather than fall back. The permissive mode is the default,
    /// so a typo in <c>command-only</c> would otherwise silently widen what continues a session —
    /// the opposite of what someone writing that setting intended.
    /// </summary>
    /// <summary>
    /// An unrecognised policy must fail rather than fall back. The permissive value is the default,
    /// so a typo would silently let agents finish work an operator meant to review — a difference
    /// nothing downstream would report.
    /// </summary>
    private static void ValidateCompletionPolicy(string? policy)
    {
        if (policy is null) return;

        var known = new[]
        {
            WorkerCompletionConfig.CompletionPolicies.Agent,
            WorkerCompletionConfig.CompletionPolicies.UserConfirmed
        };
        if (known.Contains(policy, StringComparer.OrdinalIgnoreCase)) return;

        throw new TrackerException(
            "CONFIG_INVALID",
            $"worker.completion.policy is '{policy}', which is not a supported policy. Use " +
            $"'{WorkerCompletionConfig.CompletionPolicies.Agent}' or " +
            $"'{WorkerCompletionConfig.CompletionPolicies.UserConfirmed}'.",
            3);
    }

    private static void ValidateContinuationTrigger(string? trigger)
    {
        if (trigger is null) return;

        var known = new[]
        {
            WorkerContinuationConfig.TriggerModes.AnyTrustedComment,
            WorkerContinuationConfig.TriggerModes.CommandOnly
        };
        if (known.Contains(trigger, StringComparer.OrdinalIgnoreCase)) return;

        throw new TrackerException(
            "CONFIG_INVALID",
            $"worker.continuation.trigger is '{trigger}', which is not a supported mode. Use " +
            $"'{WorkerContinuationConfig.TriggerModes.AnyTrustedComment}' or " +
            $"'{WorkerContinuationConfig.TriggerModes.CommandOnly}'.",
            3);
    }

    private static void ValidateControlReactions(WorkerContinuationConfig? continuation)
    {
        if (continuation is null) return;
        var resume = ReactionKinds.Parse(
            continuation.ResumeReaction, "worker.continuation.resumeReaction");
        var completion = ReactionKinds.Parse(
            continuation.CompletionReaction, "worker.continuation.completionReaction");
        if (!string.Equals(resume, completion, StringComparison.Ordinal)) return;

        throw new TrackerException(
            "CONFIG_INVALID",
            "worker.continuation.resumeReaction and completionReaction must be different; " +
            "one reaction cannot express two conflicting controls.",
            3);
    }

    private static void ValidateGitHubNames(TrackerConfig config)
    {
        var fieldNames = new[]
        {
            config.StatusField,
            config.PriorityField,
            config.ExecutionPolicyField,
            config.AgentPolicyField,
            config.WorkerProfileField,
            config.ContextApprovalField,
            config.DispatchStateField,
            config.DispatchNotBeforeField,
            config.DispatchAgentField,
            config.DispatchDetailField,
            config.ClaimAgentField,
            config.ClaimantTypeField,
            config.ClaimantField,
            config.ClaimSessionIdField,
            config.ClaimWorkspacePathField,
            config.CreationAttemptIdField
        };
        var values = fieldNames.Append(config.GitHubHost).ToArray();
        if (values.Any(string.IsNullOrWhiteSpace))
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                "statusField, priorityField, executionPolicyField, agentPolicyField, contextApprovalField, " +
                "dispatchStateField, dispatchNotBeforeField, dispatchAgentField, dispatchDetailField, " +
                "claimAgentField, claimantTypeField, claimantField, claimSessionIdField, " +
                "claimWorkspacePathField, creationAttemptIdField, and gitHubHost cannot be empty.",
                3);
        }

        var invalid = fieldNames.FirstOrDefault(value => value.Contains(':'));
        if (invalid is not null)
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                $"GitHub Project field name '{invalid}' contains ':', which GitHub does not allow.",
                3);
        }

        var duplicate = fieldNames
            .GroupBy(value => value.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                $"GitHub Project field names must be distinct; '{duplicate.Key}' is configured more than once.",
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
        ValidateChoice(config.Worker?.RequirementsAssessment?.Mode,
            "worker.requirementsAssessment.mode must be inline or off.",
            WorkerRequirementsAssessmentConfig.Modes.Inline,
            WorkerRequirementsAssessmentConfig.Modes.Off);
        ValidateChoice(config.Worker?.DefaultAgent,
            "worker.defaultAgent must be claude, codex, or copilot.",
            "claude", "codex", "copilot");
        ValidateChoice(config.Worker?.Completion?.Commit,
            "worker.completion.commit must be inspect or agent.",
            "inspect", "agent");
        ValidateChoice(config.Worker?.Completion?.Integration,
            "worker.completion.integration must be none, merge-local, or push-pr.",
            "none", "merge-local", "push-pr");
        ValidateChoice(config.Worker?.HandoverComment,
            "worker.handoverComment must be full, minimal, or off.",
            "full", "minimal", "off");
        ValidateChoice(config.Worker?.AgentPermissions,
            "worker.agentPermissions must be workspace or full.",
            "workspace", "full");
        ValidateChoice(config.Worker?.DesktopSessions?.Claude,
            "worker.desktopSessions.claude must be off or experimental.",
            "off", "experimental");
        ValidateAgentOverrides(config.Worker?.Agents);
        ValidateUsageFailure(config.Worker?.UsageFailure);
        ValidateContextLimits(config.Worker?.Context);
        ValidateSessionReportMode(config.Worker?.SessionReportMode);

        ValidateTemplate(config.Worker?.WorktreeRoot, "worker.worktreeRoot",
            ["repo", "repoParent", "home", "repoPathHash"]);
        ValidateTemplate(config.Worker?.BranchFormat, "worker.branchFormat",
            ["id", "number", "title", "unique", "agent", "date"]);
        ValidateTemplate(config.Worker?.WorktreeNameFormat, "worker.worktreeNameFormat",
            ["id", "number", "title", "unique", "agent", "date"]);
    }

    private static void ValidateAgentOverrides(
        IReadOnlyDictionary<string, WorkerAgentConfig>? agents)
    {
        if (agents is null)
            return;
        foreach (var (agent, settings) in agents)
        {
            if (agent.ToLowerInvariant() is not ("claude" or "codex" or "copilot"))
                throw new TrackerException(
                    "CONFIG_INVALID",
                    $"worker.agents contains unsupported agent '{agent}'.",
                    3);
            ValidateChoice(settings.Permissions,
                $"worker.agents.{agent.ToLowerInvariant()}.permissions must be workspace or full.",
                "workspace", "full");
        }
    }

    /// <summary>
    /// Rejects limits that cannot admit anything. A zero or negative bound is almost certainly a
    /// mistake, and left alone it would refuse every launch with a message about a limit the
    /// operator believed they were raising.
    /// </summary>
    /// <summary>
    /// A misspelled mode must not silently mean "off". Publishing is the behaviour an operator is
    /// asking for, and quietly not doing it looks identical to it working.
    /// </summary>
    /// <summary>
    /// Trusted comment authors, if any are named.
    ///
    /// A blank entry would match nothing and read as a configured trust that silently does not
    /// apply, which is worse than an error. A duplicate is accepted: it changes no behaviour and
    /// rejecting it would fail a file somebody merged carelessly rather than wrongly.
    /// </summary>
    private static void ValidateTrustedCommentAuthors(IReadOnlyList<string>? authors) =>
        ValidateLoginList(authors, "github.trustedCommentAuthors");

    private static void ValidateLoginList(IReadOnlyList<string>? logins, string key)
    {
        if (logins is null) return;
        foreach (var login in logins)
        {
            if (string.IsNullOrWhiteSpace(login))
                throw new TrackerException(
                    "CONFIG_INVALID",
                    $"{key} must not contain an empty entry.",
                    2);
            if (login.Trim() != login)
                throw new TrackerException(
                    "CONFIG_INVALID",
                    $"{key} entry '{login}' has leading or trailing " +
                    "whitespace; a GitHub login never does.",
                    2);
        }
    }

    private static void ValidateSessionReportMode(string? mode)
    {
        if (mode is null) return;
        if (mode.ToLowerInvariant() is not ("off" or "completed" or "all"))
            throw new TrackerException(
                "CONFIG_INVALID",
                "worker.sessionReportMode must be off, completed, or all.",
                2);
    }

    private static void ValidateContextLimits(WorkerContextConfig? limits)
    {
        if (limits is null) return;
        if (limits.MaxDiscussionComments <= 0)
            throw new TrackerException(
                "CONFIG_INVALID",
                "worker.context.maxDiscussionComments must be positive.",
                2);
        if (limits.MaxEntryCharacters <= 0)
            throw new TrackerException(
                "CONFIG_INVALID",
                "worker.context.maxEntryCharacters must be positive.",
                2);
        if (limits.MaxTotalCharacters <= 0)
            throw new TrackerException(
                "CONFIG_INVALID",
                "worker.context.maxTotalCharacters must be positive.",
                2);
    }

    private static void ValidateUsageFailure(WorkerUsageFailureConfig? policy)
    {
        if (policy is null)
            return;
        ValidateChoice(
            policy.Action,
            "worker.usageFailure.action must be retry, handoff, or needs-attention.",
            "retry", "handoff", "needs-attention");
        if (!double.IsFinite(policy.InitialRetryMinutes) || policy.InitialRetryMinutes <= 0)
            throw new TrackerException(
                "CONFIG_INVALID",
                "worker.usageFailure.initialRetryMinutes must be positive.",
                3);
        if (!double.IsFinite(policy.BackoffMultiplier) || policy.BackoffMultiplier < 1)
            throw new TrackerException(
                "CONFIG_INVALID",
                "worker.usageFailure.backoffMultiplier must be at least 1.",
                3);
        if (!double.IsFinite(policy.MaxRetryHours) || policy.MaxRetryHours <= 0)
            throw new TrackerException(
                "CONFIG_INVALID",
                "worker.usageFailure.maxRetryHours must be positive.",
                3);
        if (policy.MaxAttempts <= 0)
            throw new TrackerException(
                "CONFIG_INVALID",
                "worker.usageFailure.maxAttempts must be positive.",
                3);
        if (!double.IsFinite(policy.ResetGraceMinutes) || policy.ResetGraceMinutes < 0)
            throw new TrackerException(
                "CONFIG_INVALID",
                "worker.usageFailure.resetGraceMinutes cannot be negative.",
                3);

        foreach (var (source, targets) in policy.Fallbacks)
        {
            if (source.ToLowerInvariant() is not ("claude" or "codex" or "copilot"))
                throw new TrackerException(
                    "CONFIG_INVALID",
                    $"worker.usageFailure.fallbacks contains unsupported source agent '{source}'.",
                    3);
            if (targets.Any(target =>
                    target.ToLowerInvariant() is not ("claude" or "codex" or "copilot")) ||
                targets.Any(target => string.Equals(target, source, StringComparison.OrdinalIgnoreCase)) ||
                targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
                throw new TrackerException(
                    "CONFIG_INVALID",
                    $"worker.usageFailure.fallbacks.{source} must contain distinct supported " +
                    "agents other than the source.",
                    3);
        }
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

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Configuration;

public enum ConfigurationScope
{
    Repository,
    User,
    Invocation
}

public enum ConfigurationEditMode
{
    Ordinary,
    QuiescenceRequired,
    MigrationOnly,
    ReadOnly
}

public enum ConfigurationEffectiveBoundary
{
    NextCommand,
    NewWorker,
    NewWebProcess,
    FreshAgentLaunch,
    RetainedSessionUnchanged,
    InitializationOrMigration
}

public sealed record ConfigurationSettingDescriptor(
    string Id,
    ConfigurationScope Scope,
    string ValueKind,
    object? StoredValue,
    object? EffectiveValue,
    string DefaultSource,
    ConfigurationEditMode EditMode,
    ConfigurationEffectiveBoundary EffectiveBoundary,
    bool RequiresQuiescence,
    string? Sensitivity,
    string Help);

public sealed record RepositoryConfigurationSnapshot(
    string SourcePath,
    TrackerConfig StoredConfiguration,
    string Revision,
    int SchemaVersion,
    bool SchemaVersionWasExplicit,
    bool ContainsComments,
    bool ContainsTrailingCommas,
    IReadOnlyList<string> UnknownProperties,
    IReadOnlyList<ConfigurationSettingDescriptor> Settings,
    // Properties an earlier Wrighty version wrote itself; see ConfigurationJsonInspector.Legacy.
    // Ignored by every reader and removed by the next configuration write — reported so the
    // operator learns they are not an error rather than discovering the file changed shape.
    IReadOnlyList<string>? LegacyProperties = null)
{
    public bool RequiresCanonicalizationApproval => ContainsComments || ContainsTrailingCommas;
}

/// <summary>
/// What the raw JSON carries beyond the schema: genuinely unrecognized properties, which fail
/// closed, and known legacy ones an earlier Wrighty version wrote, which migrate instead.
/// </summary>
public sealed record ConfigurationPropertyInspection(
    IReadOnlyList<string> Unknown,
    IReadOnlyList<string> Legacy);

public sealed record ConfigurationChange(
    string Id,
    object? Before,
    object? After);

public sealed record RepositoryConfigurationMutationResult(
    RepositoryConfigurationSnapshot Before,
    RepositoryConfigurationSnapshot After,
    IReadOnlyList<ConfigurationChange> Changes,
    bool Saved,
    bool RestartRequired);

public abstract record RepositoryConfigurationMutation
{
    internal abstract TrackerConfig Apply(TrackerConfig config);
}

public sealed record WorkflowDefaultsMutation(
    string? PickFrom,
    string? PickTo,
    string? FinishTo) : RepositoryConfigurationMutation
{
    internal override TrackerConfig Apply(TrackerConfig config) => config with
    {
        DefaultPickFrom = PickFrom ?? config.DefaultPickFrom,
        DefaultPickTo = PickTo ?? config.DefaultPickTo,
        DefaultFinishTo = FinishTo ?? config.DefaultFinishTo
    };
}

public sealed record ArchivePolicyMutation(
    IReadOnlyList<string> OnStatuses) : RepositoryConfigurationMutation
{
    internal override TrackerConfig Apply(TrackerConfig config) => config with
    {
        Archive = config.Archive with { OnStatuses = OnStatuses }
    };
}

public sealed record WorkerDefaultsMutation(
    bool SetDefaultAgent,
    string? DefaultAgent,
    string? WorkspaceMode) : RepositoryConfigurationMutation
{
    internal override TrackerConfig Apply(TrackerConfig config)
    {
        var worker = config.EffectiveWorker;
        return config with
        {
            Worker = worker with
            {
                DefaultAgent = SetDefaultAgent
                    ? NormalizeAgent(DefaultAgent)
                    : worker.DefaultAgent,
                WorkspaceMode = WorkspaceMode ?? worker.WorkspaceMode
            }
        };
    }

    private static string? NormalizeAgent(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}

public sealed record CompletionPolicyMutation(
    string? Commit,
    string? Integration) : RepositoryConfigurationMutation
{
    internal override TrackerConfig Apply(TrackerConfig config)
    {
        var worker = config.EffectiveWorker;
        var completion = worker.Completion ?? new WorkerCompletionConfig();
        return config with
        {
            Worker = worker with
            {
                Completion = completion with
                {
                    Commit = Commit ?? completion.Commit,
                    Integration = Integration ?? completion.Integration
                }
            }
        };
    }
}

public sealed record WebPolicyMutation(
    bool ProtectNonHumanClaims) : RepositoryConfigurationMutation
{
    internal override TrackerConfig Apply(TrackerConfig config) => config with
    {
        Web = config.EffectiveWeb with { ProtectNonHumanClaims = ProtectNonHumanClaims }
    };
}

public interface IRepositoryConfigurationService
{
    string ResolvePath(string startDirectory, string? explicitPath);

    Task<RepositoryConfigurationSnapshot> ReadAsync(
        string startDirectory,
        string? explicitPath,
        CancellationToken cancellationToken);

    Task<RepositoryConfigurationSnapshot> ReadPathAsync(
        string path,
        CancellationToken cancellationToken);

    Task<RepositoryConfigurationMutationResult> MutateAsync(
        string path,
        string expectedRevision,
        RepositoryConfigurationMutation mutation,
        bool approveCanonicalization,
        bool dryRun,
        CancellationToken cancellationToken);
}

public sealed class RepositoryConfigurationService(
    ITrackerConfigStore store) : IRepositoryConfigurationService
{
    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ResolvePath(string startDirectory, string? explicitPath) =>
        CanonicalPath(store.ResolvePath(startDirectory, explicitPath));

    public Task<RepositoryConfigurationSnapshot> ReadAsync(
        string startDirectory,
        string? explicitPath,
        CancellationToken cancellationToken) =>
        ReadPathAsync(ResolvePath(startDirectory, explicitPath), cancellationToken);

    public async Task<RepositoryConfigurationSnapshot> ReadPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var canonicalPath = CanonicalPath(path);
        if (!File.Exists(canonicalPath))
        {
            throw new TrackerException(
                "CONFIG_NOT_FOUND",
                $"Could not find configuration at {canonicalPath}.",
                3,
                new Dictionary<string, object?> { ["configPath"] = canonicalPath });
        }

        var bytes = await File.ReadAllBytesAsync(canonicalPath, cancellationToken);
        return await SnapshotAsync(canonicalPath, bytes, cancellationToken);
    }

    public async Task<RepositoryConfigurationMutationResult> MutateAsync(
        string path,
        string expectedRevision,
        RepositoryConfigurationMutation mutation,
        bool approveCanonicalization,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var canonicalPath = CanonicalPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(canonicalPath)!);
        var lockPath = $"{canonicalPath}.edit.lock";
        FileStream? editLock = null;
        try
        {
            try
            {
                editLock = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException exception)
            {
                throw new TrackerException(
                    "CONFIG_BUSY",
                    "Another Wrighty configuration edit is in progress.",
                    3,
                    new Dictionary<string, object?> { ["configPath"] = canonicalPath },
                    exception);
            }

            var before = await ReadPathAsync(canonicalPath, cancellationToken);
            if (!string.Equals(before.Revision, expectedRevision, StringComparison.Ordinal))
            {
                throw new TrackerException(
                    "CONFIG_CONFLICT",
                    "The configuration changed after it was read. Review the latest values and retry.",
                    3,
                    new Dictionary<string, object?>
                    {
                        ["configPath"] = canonicalPath,
                        ["expectedRevision"] = expectedRevision,
                        ["actualRevision"] = before.Revision
                    });
            }
            if (!dryRun && before.RequiresCanonicalizationApproval && !approveCanonicalization)
            {
                throw new TrackerException(
                    "CONFIG_CANONICALIZATION_CONFIRMATION_REQUIRED",
                    "Saving will normalize comments or trailing commas. Review the dry-run and retry with --yes.",
                    2,
                    new Dictionary<string, object?>
                    {
                        ["configPath"] = canonicalPath,
                        ["containsComments"] = before.ContainsComments,
                        ["containsTrailingCommas"] = before.ContainsTrailingCommas
                    });
            }

            var updated = mutation.Apply(before.StoredConfiguration) with
            {
                SchemaVersion = TrackerConfigLoader.CurrentSchemaVersion,
                SourcePath = canonicalPath
            };
            TrackerConfigLoader.Validate(updated);
            var previewBytes = Serialize(updated);
            var after = await SnapshotFromConfigurationAsync(
                canonicalPath,
                previewBytes,
                updated,
                cancellationToken);
            var changes = Changes(before.Settings, after.Settings);
            if (!dryRun && changes.Count > 0)
            {
                var finalRevision = await RevisionAsync(canonicalPath, cancellationToken);
                if (!string.Equals(finalRevision, expectedRevision, StringComparison.Ordinal))
                {
                    throw new TrackerException(
                        "CONFIG_CONFLICT",
                        "The configuration changed while the update was being prepared. Review the latest values and retry.",
                        3,
                        new Dictionary<string, object?>
                        {
                            ["configPath"] = canonicalPath,
                            ["expectedRevision"] = expectedRevision,
                            ["actualRevision"] = finalRevision
                        });
                }
                await store.SaveAsync(canonicalPath, updated, cancellationToken);
                after = await ReadPathAsync(canonicalPath, cancellationToken);
            }

            return new RepositoryConfigurationMutationResult(
                before,
                after,
                changes,
                Saved: !dryRun && changes.Count > 0,
                RestartRequired: changes.Count > 0);
        }
        finally
        {
            if (editLock is not null)
                await editLock.DisposeAsync();
            try
            {
                if (File.Exists(lockPath))
                    File.Delete(lockPath);
            }
            catch (IOException)
            {
                // A stale empty lock file is harmless because ownership is the open file handle.
            }
        }
    }

    public static string Revision(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static async Task<string> RevisionAsync(
        string path,
        CancellationToken cancellationToken) =>
        Revision(await File.ReadAllBytesAsync(path, cancellationToken));

    private Task<RepositoryConfigurationSnapshot> SnapshotAsync(
        string canonicalPath,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = Parse(bytes, canonicalPath);
        // Known legacy properties are deliberately not in this failure: an earlier Wrighty version
        // wrote them, so rejecting the file would refuse this tool's own previous output and read
        // to the operator as their mistake. They surface on the snapshot as migratable instead.
        var inspection = ConfigurationJsonInspector.Inspect(document.RootElement);
        if (inspection.Unknown.Count > 0)
        {
            throw new TrackerException(
                "CONFIG_UNKNOWN_PROPERTIES",
                $"Configuration contains unsupported properties: " +
                $"{string.Join(", ", inspection.Unknown)}.",
                3,
                new Dictionary<string, object?>
                {
                    ["configPath"] = canonicalPath,
                    ["unknownProperties"] = inspection.Unknown
                });
        }

        TrackerConfig config;
        try
        {
            config = TrackerConfigLoader.DeserializeExact(bytes, canonicalPath);
        }
        catch (JsonException exception)
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                $"Could not read configuration from {canonicalPath}: {exception.Message}",
                3,
                new Dictionary<string, object?> { ["configPath"] = canonicalPath },
                exception);
        }
        return Task.FromResult(
            Snapshot(document.RootElement, canonicalPath, bytes, config, inspection));
    }

    private static Task<RepositoryConfigurationSnapshot> SnapshotFromConfigurationAsync(
        string canonicalPath,
        byte[] bytes,
        TrackerConfig config,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = Parse(bytes, canonicalPath);
        // Bytes produced by the canonical writer: typed serialization cannot carry unknown or
        // legacy properties, so the inspection is vacuously clean by construction.
        return Task.FromResult(Snapshot(
            document.RootElement,
            canonicalPath,
            bytes,
            config with { SourceRevision = Revision(bytes) },
            ConfigurationJsonInspector.Inspect(document.RootElement)));
    }

    private static RepositoryConfigurationSnapshot Snapshot(
        JsonElement root,
        string canonicalPath,
        byte[] bytes,
        TrackerConfig config,
        ConfigurationPropertyInspection inspection)
    {
        var text = Encoding.UTF8.GetString(bytes);
        var explicitSchema = TryProperty(root, "schemaVersion", out var schema);
        return new RepositoryConfigurationSnapshot(
            canonicalPath,
            config with { SourcePath = canonicalPath },
            Revision(bytes),
            explicitSchema ? schema.GetInt32() : TrackerConfigLoader.CurrentSchemaVersion,
            explicitSchema,
            ConfigurationJsonInspector.ContainsComments(text),
            ConfigurationJsonInspector.ContainsTrailingCommas(text),
            inspection.Unknown,
            ConfigurationCatalogue.Build(root, config),
            inspection.Legacy);
    }

    private static JsonDocument Parse(byte[] bytes, string path)
    {
        try
        {
            return JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
        }
        catch (JsonException exception)
        {
            throw new TrackerException(
                "CONFIG_INVALID",
                $"Could not read configuration from {path}: {exception.Message}",
                3,
                new Dictionary<string, object?> { ["configPath"] = path },
                exception);
        }
    }

    private static string CanonicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            return fullPath;
        return new FileInfo(fullPath).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? fullPath;
    }

    private static byte[] Serialize(TrackerConfig config)
    {
        var json = JsonSerializer.Serialize(config, CanonicalJson);
        return Encoding.UTF8.GetBytes($"{json}\n");
    }

    private static IReadOnlyList<ConfigurationChange> Changes(
        IReadOnlyList<ConfigurationSettingDescriptor> before,
        IReadOnlyList<ConfigurationSettingDescriptor> after)
    {
        var afterById = after.ToDictionary(value => value.Id, StringComparer.Ordinal);
        return before
            .Where(value => afterById.TryGetValue(value.Id, out var updated) &&
                !JsonEquivalent(value.EffectiveValue, updated.EffectiveValue))
            .Select(value => new ConfigurationChange(
                value.Id,
                value.EffectiveValue,
                afterById[value.Id].EffectiveValue))
            .ToArray();
    }

    private static bool JsonEquivalent(object? left, object? right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    internal static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}

internal static class ConfigurationCatalogue
{
    public static IReadOnlyList<ConfigurationSettingDescriptor> Build(
        JsonElement root,
        TrackerConfig config)
    {
        var values = new List<ConfigurationSettingDescriptor>
        {
            Setting(root, "schemaVersion",
                config.SchemaVersion ?? TrackerConfigLoader.CurrentSchemaVersion, "integer",
                ConfigurationEditMode.MigrationOnly,
                ConfigurationEffectiveBoundary.InitializationOrMigration,
                "Repository configuration schema version."),
            Setting(root, "backend", config.Backend, "string", ConfigurationEditMode.MigrationOnly,
                ConfigurationEffectiveBoundary.InitializationOrMigration, "Tracker backend identity."),
            Setting(root, "defaultPickFrom", config.DefaultPickFrom, "string",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Status from which ordinary work is selected."),
            Setting(root, "defaultPickTo", config.DefaultPickTo, "string",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Status applied after a successful claim."),
            Setting(root, "defaultFinishTo", config.DefaultFinishTo, "string",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Status applied by normal completion."),
            Setting(root, "leaseMinutes", config.LeaseMinutes, "integer",
                new ConfigurationSettingMetadata(
                    ConfigurationEditMode.QuiescenceRequired,
                    ConfigurationEffectiveBoundary.NewWorker,
                    "Claim lease duration.",
                    RequiresQuiescence: true)),
            Setting(root, "archive.onStatuses", config.Archive.OnStatuses, "string[]",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Statuses that archive an item."),
            Setting(root, "worker.defaultAgent", config.EffectiveWorker.DefaultAgent, "string?",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Default worker vendor when item and invocation do not select one."),
            Setting(root, "worker.workspaceMode", config.EffectiveWorker.WorkspaceMode ?? "current",
                "string", ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Default worker workspace mode."),
            Setting(root, "worker.completion.commit",
                config.EffectiveWorker.Completion?.Commit ?? "inspect", "string",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Whether an agent may commit before completion."),
            Setting(root, "worker.completion.integration",
                config.EffectiveWorker.Completion?.Integration ?? "none", "string",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Completion guidance for local integration."),
            Setting(root, "worker.sessionReportMode",
                config.EffectiveWorker.SessionReportMode ?? "off", "string",
                new ConfigurationSettingMetadata(
                    ConfigurationEditMode.Ordinary,
                    ConfigurationEffectiveBoundary.NewWorker,
                    "Controls publication of historical run reports.",
                    Sensitivity: "Publishing makes run reports visible to collaborators.")),
            Setting(root, "worker.agentPermissions",
                config.EffectiveWorker.AgentPermissions ?? "workspace", "string",
                new ConfigurationSettingMetadata(
                    ConfigurationEditMode.Ordinary,
                    ConfigurationEffectiveBoundary.NewWorker,
                    "Default permission profile requested from worker agents.",
                    Sensitivity: "The full profile grants unrestricted vendor execution.")),
            Setting(root, "worker.desktopSessions.claude",
                config.EffectiveWorker.DesktopSessions?.Claude ?? "off", "string",
                new ConfigurationSettingMetadata(
                    ConfigurationEditMode.ReadOnly,
                    ConfigurationEffectiveBoundary.NewWebProcess,
                    "Controls the experimental Claude Desktop session integration.",
                    Sensitivity:
                        "Experimental integrations may depend on undocumented vendor behavior.")),
            Setting(root, "worker.worktreeRoot", config.EffectiveWorker.WorktreeRoot, "string?",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Template for the worker worktree root."),
            Setting(root, "worker.branchFormat", config.EffectiveWorker.BranchFormat, "string?",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Template for worker branch names."),
            Setting(root, "worker.worktreeNameFormat",
                config.EffectiveWorker.WorktreeNameFormat, "string?",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Template for worker worktree directory names."),
            Setting(root, "worker.handoverComment",
                config.EffectiveWorker.HandoverComment ?? "full", "string",
                new ConfigurationSettingMetadata(
                    ConfigurationEditMode.Ordinary,
                    ConfigurationEffectiveBoundary.NewWorker,
                    "Controls GitHub handover comment detail.",
                    Sensitivity:
                        "Full handovers can include machine-local context when path sharing is enabled.")),
            Setting(root, "worker.shareLocalPaths", config.EffectiveWorker.ShareLocalPaths, "boolean",
                new ConfigurationSettingMetadata(
                    ConfigurationEditMode.Ordinary,
                    ConfigurationEffectiveBoundary.NewWorker,
                    "Allows local workspace paths to be published.",
                    Sensitivity: "Local paths can contain user and machine information.")),
            Setting(root, "worker.context.maxDiscussionComments",
                config.EffectiveWorker.EffectiveContext.MaxDiscussionComments, "integer",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Maximum approved discussion entries considered for a fresh launch."),
            Setting(root, "worker.context.maxEntryCharacters",
                config.EffectiveWorker.EffectiveContext.MaxEntryCharacters, "integer",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Maximum size of one approved context entry."),
            Setting(root, "worker.context.maxTotalCharacters",
                config.EffectiveWorker.EffectiveContext.MaxTotalCharacters, "integer",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Maximum total approved context size."),
            Setting(root, "worker.usageFailure.action",
                config.EffectiveWorker.EffectiveUsageFailure.Action, "string",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Action after an authoritative provider-usage failure."),
            Setting(root, "worker.usageFailure.initialRetryMinutes",
                config.EffectiveWorker.EffectiveUsageFailure.InitialRetryMinutes, "number",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Initial deferred retry delay."),
            Setting(root, "worker.usageFailure.backoffMultiplier",
                config.EffectiveWorker.EffectiveUsageFailure.BackoffMultiplier, "number",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Deferred retry backoff multiplier."),
            Setting(root, "worker.usageFailure.maxRetryHours",
                config.EffectiveWorker.EffectiveUsageFailure.MaxRetryHours, "number",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Maximum deferred retry delay."),
            Setting(root, "worker.usageFailure.maxAttempts",
                config.EffectiveWorker.EffectiveUsageFailure.MaxAttempts, "integer",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Maximum deferred retry attempts."),
            Setting(root, "worker.usageFailure.resetGraceMinutes",
                config.EffectiveWorker.EffectiveUsageFailure.ResetGraceMinutes, "number",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Grace interval applied to a provider reset time."),
            Setting(root, "worker.usageFailure.allowCrossAgentHandoff",
                config.EffectiveWorker.EffectiveUsageFailure.AllowCrossAgentHandoff, "boolean",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Whether usage failure may hand work to another configured agent."),
            Setting(root, "worker.usageFailure.fallbacks",
                config.EffectiveWorker.EffectiveUsageFailure.Fallbacks, "object",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWorker,
                "Agent fallback order after usage failure."),
            Setting(root, "web.protectNonHumanClaims",
                config.EffectiveWeb.ProtectNonHumanClaims, "boolean",
                ConfigurationEditMode.Ordinary, ConfigurationEffectiveBoundary.NewWebProcess,
                "Protects non-human claims from ordinary web edits.")
        };

        if (string.Equals(config.Backend, "github", StringComparison.OrdinalIgnoreCase))
        {
            values.AddRange(
            [
                Setting(root, "github.repository", config.Repository, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "Configured GitHub repository."),
                Setting(root, "github.projectOwner", config.EffectiveProjectOwner, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "Configured GitHub Project owner."),
                Setting(root, "github.projectNumber", config.ProjectNumber, "integer",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "Configured GitHub Project number."),
                Setting(root, "github.gitHubHost", config.GitHubHost, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "Configured GitHub host."),
                Setting(root, "github.linkRepository", config.LinkRepository, "boolean",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "Whether initialization links the repository to the Project."),
                Setting(root, "github.statusField", config.StatusField, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "GitHub Project status field name."),
                Setting(root, "github.priorityField", config.PriorityField, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "GitHub Project priority field name."),
                Setting(root, "github.executionPolicyField", config.ExecutionPolicyField, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "GitHub Project execution-policy field name."),
                Setting(root, "github.agentPolicyField", config.AgentPolicyField, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "GitHub Project agent-policy field name."),
                Setting(root, "github.contextApprovalField", config.ContextApprovalField, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "GitHub Project context-approval field name."),
                Setting(root, "github.dispatchStateField", config.DispatchStateField, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "GitHub Project dispatch-state field name."),
                Setting(root, "github.dispatchNotBeforeField", config.DispatchNotBeforeField, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "GitHub Project dispatch-time field name."),
                Setting(root, "github.dispatchAgentField", config.DispatchAgentField, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "GitHub Project dispatch-agent field name."),
                Setting(root, "github.dispatchDetailField", config.DispatchDetailField, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "GitHub Project dispatch-detail field name."),
                Setting(root, "github.claimAgentField", config.ClaimAgentField, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "GitHub Project claim-agent field name."),
                Setting(root, "github.claimantTypeField", config.ClaimantTypeField, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "GitHub Project claimant-type field name."),
                Setting(root, "github.claimantField", config.ClaimantField, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "GitHub Project claimant field name."),
                Setting(root, "github.claimSessionIdField", config.ClaimSessionIdField, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "GitHub Project claim-session field name."),
                Setting(root, "github.claimWorkspacePathField",
                    config.ClaimWorkspacePathField, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "GitHub Project claim-workspace field name."),
                Setting(root, "github.creationAttemptIdField",
                    config.CreationAttemptIdField, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "GitHub Project creation-attempt field name."),
                Setting(root, "github.claimHistoryLimit", config.ClaimHistoryLimit, "integer",
                    new ConfigurationSettingMetadata(
                        ConfigurationEditMode.QuiescenceRequired,
                        ConfigurationEffectiveBoundary.NewWorker,
                        "Maximum retained claim history entries.",
                        RequiresQuiescence: true)),
                Setting(root, "github.trustedCommentAuthors", config.TrustedCommentAuthors,
                    "string[]",
                    new ConfigurationSettingMetadata(
                        ConfigurationEditMode.Ordinary,
                        ConfigurationEffectiveBoundary.NewWorker,
                        "Authors whose comments are trusted without a separate approval step.",
                        Sensitivity:
                            "Expanding this list changes which collaborator content may reach an agent."))
            ]);
        }
        else
        {
            values.AddRange(
            [
                Setting(root, "localMarkdown.path", config.LocalMarkdown?.Path, "string",
                    ConfigurationEditMode.MigrationOnly,
                    ConfigurationEffectiveBoundary.InitializationOrMigration,
                    "Local Markdown store path."),
                Setting(root, "localMarkdown.statuses", config.LocalMarkdown?.Statuses ?? [],
                    "string[]",
                    new ConfigurationSettingMetadata(
                        ConfigurationEditMode.QuiescenceRequired,
                        ConfigurationEffectiveBoundary.NewWorker,
                        "Allowed Local Markdown statuses.",
                        RequiresQuiescence: true)),
                Setting(root, "localMarkdown.priorities", config.LocalMarkdown?.Priorities ?? [],
                    "string[]",
                    new ConfigurationSettingMetadata(
                        ConfigurationEditMode.QuiescenceRequired,
                        ConfigurationEffectiveBoundary.NewWorker,
                        "Allowed Local Markdown priorities.",
                        RequiresQuiescence: true))
            ]);
        }

        if (config.EffectiveWorker.Agents is { } agents)
        {
            foreach (var agent in agents.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                values.Add(Setting(
                    root,
                    $"worker.agents.{agent.Key}.permissions",
                    agent.Value.Permissions,
                    "string?",
                    new ConfigurationSettingMetadata(
                        ConfigurationEditMode.Ordinary,
                        ConfigurationEffectiveBoundary.NewWorker,
                        $"Permission profile override for {agent.Key}.",
                        Sensitivity: "The full profile grants unrestricted vendor execution.")));
            }
        }

        return values;
    }

    private static ConfigurationSettingDescriptor Setting(
        JsonElement root,
        string id,
        object? effective,
        string kind,
        ConfigurationEditMode editMode,
        ConfigurationEffectiveBoundary boundary,
        string help) =>
        Setting(
            root,
            id,
            effective,
            kind,
            new ConfigurationSettingMetadata(editMode, boundary, help));

    private static ConfigurationSettingDescriptor Setting(
        JsonElement root,
        string id,
        object? effective,
        string kind,
        ConfigurationSettingMetadata metadata)
    {
        var stored = Stored(root, id);
        return new ConfigurationSettingDescriptor(
            id,
            ConfigurationScope.Repository,
            kind,
            stored,
            effective,
            stored is null ? "wrighty-default" : "repository",
            metadata.EditMode,
            metadata.Boundary,
            metadata.RequiresQuiescence,
            metadata.Sensitivity,
            metadata.Help);
    }

    private sealed record ConfigurationSettingMetadata(
        ConfigurationEditMode EditMode,
        ConfigurationEffectiveBoundary Boundary,
        string Help,
        bool RequiresQuiescence = false,
        string? Sensitivity = null);

    private static object? Stored(JsonElement root, string id)
    {
        var current = root;
        foreach (var segment in id.Split('.'))
        {
            if (current.ValueKind != JsonValueKind.Object ||
                !RepositoryConfigurationService.TryProperty(current, segment, out current))
                return null;
        }
        return Value(current);
    }

    private static object? Value(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.Array => value.EnumerateArray().Select(Value).ToArray(),
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(
            property => property.Name,
            property => Value(property.Value),
            StringComparer.Ordinal),
        _ => value.GetRawText()
    };
}

internal static class ConfigurationJsonInspector
{
    private const string WorkerSection = "worker";

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Allowed =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = Set("schemaVersion", "backend", "github", "localMarkdown", "archive", "web",
                WorkerSection, "defaultPickFrom", "defaultPickTo", "defaultFinishTo", "leaseMinutes"),
            ["github"] = Set("repository", "projectOwner", "projectNumber", "linkRepository",
                "statusField", "priorityField", "executionPolicyField", "agentPolicyField",
                "contextApprovalField", "trustedCommentAuthors", "dispatchStateField",
                "dispatchNotBeforeField", "dispatchAgentField", "dispatchDetailField",
                "claimAgentField", "claimantTypeField", "claimantField", "claimSessionIdField",
                "claimWorkspacePathField", "creationAttemptIdField", "claimHistoryLimit", "gitHubHost"),
            ["localMarkdown"] = Set("path", "statuses", "priorities"),
            ["archive"] = Set("onStatuses"),
            ["web"] = Set("protectNonHumanClaims"),
            [WorkerSection] = Set("defaultAgent", "workspaceMode", "completion", "usageFailure",
                "sessionReportMode", "context", "agentPermissions", "agents", "worktreeRoot",
                "branchFormat", "worktreeNameFormat", "handoverComment", "shareLocalPaths",
                "desktopSessions"),
            ["worker.desktopSessions"] = Set("claude"),
            ["worker.completion"] = Set("commit", "integration"),
            ["worker.context"] = Set("maxDiscussionComments", "maxEntryCharacters", "maxTotalCharacters"),
            ["worker.usageFailure"] = Set("action", "initialRetryMinutes", "backoffMultiplier",
                "maxRetryHours", "maxAttempts", "resetGraceMinutes", "allowCrossAgentHandoff", "fallbacks"),
            ["worker.agents.*"] = Set("permissions")
        };

    // Properties that earlier Wrighty versions wrote into the file themselves: the computed
    // worker "effective*" getters lacked [JsonIgnore] through v0.9.1-alpha, so every config write
    // persisted their derived values as if they were settings. They are not user error and must
    // not fail validation — they are reported as migratable and dropped by the next canonical
    // write, which serializes from the typed configuration where they no longer exist. Their
    // nested content is not inspected: it is a serialized snapshot of defaults, not settings.
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Legacy =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            [WorkerSection] = Set(
                "effectiveUsageFailure",
                "effectiveSessionReportMode",
                "effectiveContext",
                "effectiveHandoverComment")
        };

    public static ConfigurationPropertyInspection Inspect(JsonElement root)
    {
        var unknown = new List<string>();
        var legacy = new List<string>();
        Visit(root, string.Empty, unknown, legacy);
        return new ConfigurationPropertyInspection(unknown, legacy);
    }

    public static bool ContainsComments(string text)
    {
        var inString = false;
        var escaped = false;
        for (var index = 0; index < text.Length - 1; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (character == '\\') escaped = true;
                else if (character == '"') inString = false;
                continue;
            }
            if (character == '"')
            {
                inString = true;
                continue;
            }
            if (character == '/' && text[index + 1] is '/' or '*')
                return true;
        }
        return false;
    }

    public static bool ContainsTrailingCommas(string text)
    {
        var inString = false;
        var escaped = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (UpdateStringState(character, ref inString, ref escaped))
                continue;
            if (character != ',')
                continue;
            var next = NextNonWhitespace(text, index + 1);
            if (next < text.Length && text[next] is '}' or ']')
                return true;
        }
        return false;
    }

    private static bool UpdateStringState(
        char character,
        ref bool inString,
        ref bool escaped)
    {
        if (!inString)
        {
            if (character != '"')
                return false;
            inString = true;
            return true;
        }

        if (escaped)
            escaped = false;
        else if (character == '\\')
            escaped = true;
        else if (character == '"')
            inString = false;
        return true;
    }

    private static int NextNonWhitespace(string text, int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static void Visit(
        JsonElement value,
        string path,
        ICollection<string> unknown,
        ICollection<string> legacy)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return;

        var lookupPath = path.StartsWith("worker.agents.", StringComparison.OrdinalIgnoreCase)
            ? "worker.agents.*"
            : path;
        if (!Allowed.TryGetValue(lookupPath, out var allowed))
            return;
        var legacyHere = Legacy.GetValueOrDefault(lookupPath);
        foreach (var property in value.EnumerateObject())
            Classify(property, path, allowed, legacyHere, unknown, legacy);
    }

    private static void Classify(
        JsonProperty property,
        string path,
        IReadOnlySet<string> allowed,
        IReadOnlySet<string>? legacyHere,
        ICollection<string> unknown,
        ICollection<string> legacy)
    {
        var propertyPath = string.IsNullOrEmpty(path)
            ? property.Name
            : $"{path}.{property.Name}";
        if (legacyHere is not null && legacyHere.Contains(property.Name))
        {
            legacy.Add(propertyPath);
            return;
        }
        if (!allowed.Contains(property.Name))
        {
            unknown.Add(propertyPath);
            return;
        }
        if (IsAgentMap(path, property))
        {
            foreach (var agent in property.Value.EnumerateObject())
                Visit(agent.Value, $"worker.agents.{agent.Name}", unknown, legacy);
            return;
        }
        Visit(property.Value, propertyPath, unknown, legacy);
    }

    private static bool IsAgentMap(string path, JsonProperty property) =>
        string.Equals(path, WorkerSection, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(property.Name, "agents", StringComparison.OrdinalIgnoreCase) &&
        property.Value.ValueKind == JsonValueKind.Object;

    private static IReadOnlySet<string> Set(params string[] values) =>
        new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
}

using Highbyte.Wrighty.Configuration;

namespace Highbyte.Wrighty.Workers;

/// <summary>Effective startup selection, without claim credentials or parsed display commands.</summary>
public sealed record WorkerScheduling(
    string Mode,
    string? TargetItemId,
    WorkerItemIntent Intent,
    string FromStatus,
    string ToStatus,
    string? AgentOverride,
    string? DefaultAgent,
    WorkspaceMode WorkspaceMode,
    string RepositoryPath,
    IReadOnlyDictionary<string, string> Filters,
    int? ItemLimit,
    TimeSpan? IdleTimeout,
    TimeSpan ItemTimeout,
    string? Profile,
    bool DryRun)
{
    public static WorkerScheduling From(TrackerConfig config, WorkerOptions options,
        WorkerRunIdentity identity, WorkerRunSelection selection) => new(
        SelectionMode(options, selection),
        selection.ItemId?.Value, selection.Intent,
        options.FromStatus ?? config.DefaultPickFrom, options.ToStatus ?? config.DefaultPickTo,
        options.Agent, config.EffectiveWorker.DefaultAgent, options.WorkspaceMode,
        Path.GetFullPath(identity.RepositoryPath), new Dictionary<string, string>(options.Filters),
        selection.ItemId is not null || options.Once ? 1 : options.MaxItems,
        options.IdleTimeout, options.ItemTimeout, options.Profile, options.DryRun);

    public bool IsUsable() =>
        !string.IsNullOrWhiteSpace(FromStatus) && !string.IsNullOrWhiteSpace(ToStatus) &&
        !string.IsNullOrWhiteSpace(RepositoryPath) && Path.IsPathFullyQualified(RepositoryPath) &&
        Filters is not null && Enum.IsDefined(WorkspaceMode) && Enum.IsDefined(Intent) &&
        ItemTimeout > TimeSpan.Zero && (IdleTimeout is null || IdleTimeout > TimeSpan.Zero) &&
        (ItemLimit is null || ItemLimit > 0) && Mode switch
        {
            "continuous" => TargetItemId is null && ItemLimit is null,
            "bounded" => TargetItemId is null && ItemLimit is not null,
            "targeted" => TargetItemId is not null && ItemLimit == 1,
            _ => false
        };

    private static string SelectionMode(WorkerOptions options, WorkerRunSelection selection)
    {
        if (selection.ItemId is not null)
            return "targeted";
        return options.Once || options.MaxItems.HasValue ? "bounded" : "continuous";
    }

    public WorkerOptions Options() => new(AgentOverride, ItemLimit == 1, ItemLimit, WorkspaceMode,
        Filters, IdleTimeout, ItemTimeout, FencedAction.Kill, null, "agent", DryRun, true,
        FromStatus, ToStatus, Profile: Profile);
}

public sealed record WorkerRunProgress(int Processed, DateTimeOffset IdleSince);

public sealed record WorkerRegistrySnapshot(
    DateTimeOffset ObservedAt,
    string ConfigurationPathHash,
    string Coverage,
    IReadOnlyList<WorkerInstanceStatus> Workers,
    string? Detail = null)
{
    public static WorkerRegistrySnapshot Unavailable(string configurationPath) => new(
        DateTimeOffset.UtcNow, JsonWorkerInstanceRegistry.ConfigurationPathHash(configurationPath),
        "unavailable", [], "This registry does not support read-only inspection.");
}

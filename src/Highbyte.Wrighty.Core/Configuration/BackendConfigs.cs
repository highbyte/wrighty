namespace Highbyte.Wrighty.Configuration;

public sealed record ArchiveConfig
{
    public IReadOnlyList<string> OnStatuses { get; init; } = [];
}

public sealed record WebConfig
{
    public bool ProtectNonHumanClaims { get; init; } = true;
}

public sealed record WorkerConfig
{
    public string? DefaultAgent { get; init; }

    public string? WorkspaceMode { get; init; }

    public WorkerCompletionConfig? Completion { get; init; }

    public WorkerUsageFailureConfig? UsageFailure { get; init; }

    public WorkerUsageFailureConfig EffectiveUsageFailure => UsageFailure ?? new();

    /// <summary>Template for the directory that receives worker worktrees. Placeholders:
    /// {repo}, {repoParent}, {home}, {repoPathHash}. Default: {repoParent}/{repo}.worktrees.</summary>
    public string? WorktreeRoot { get; init; }

    /// <summary>Template for the worker branch name. Placeholders: {id}, {number}, {title},
    /// {unique}, {agent}, {date}. Default: wrighty-worker/{id}-{title}.</summary>
    public string? BranchFormat { get; init; }

    /// <summary>Template for the worktree directory name. Same placeholders as
    /// branchFormat. Default: {id}-{title}.</summary>
    public string? WorktreeNameFormat { get; init; }

    /// <summary>Controls the single overwrite-style handover comment the worker posts on a GitHub
    /// issue when a run ends in needs-attention or finishes with a retained worktree.
    /// "full" (default): includes the workspace path and host; "minimal": omits local machine
    /// details; "off": posts nothing. Ignored by the Local Markdown backend (the web dashboard is
    /// the equivalent surface there).</summary>
    public string? HandoverComment { get; init; }

    public HandoverCommentMode EffectiveHandoverComment => HandoverComment?.ToLowerInvariant() switch
    {
        "off" => HandoverCommentMode.Off,
        "minimal" => HandoverCommentMode.Minimal,
        _ => HandoverCommentMode.Full
    };

    /// <summary>Whether absolute local workspace paths may be published to GitHub (the claim-marker
    /// JSON, the Project workspace-path field, and the handover comment). Default false so those
    /// paths — which embed the OS username — are never disclosed unless explicitly opted in; the path
    /// is still kept in the machine-local session cache, so resume on the recording host is
    /// unaffected, and the handover comment uses path-free <c>wrighty</c> commands. Set to true only
    /// when every collaborator with repository access is trusted to see local machine paths.</summary>
    public bool ShareLocalPaths { get; init; } = false;
}

public sealed record WorkerUsageFailureConfig
{
    /// <summary>"retry" (default), "handoff", or "needs-attention". Handoff is reserved until the
    /// opt-in cross-agent continuation increment is enabled.</summary>
    public string Action { get; init; } = "retry";

    public double InitialRetryMinutes { get; init; } = 30;

    public double BackoffMultiplier { get; init; } = 2;

    public double MaxRetryHours { get; init; } = 6;

    public int MaxAttempts { get; init; } = 5;

    public double ResetGraceMinutes { get; init; } = 2;

    public bool AllowCrossAgentHandoff { get; init; }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Fallbacks { get; init; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude"] = ["codex", "copilot"],
            ["codex"] = ["claude", "copilot"],
            ["copilot"] = ["codex", "claude"]
        };
}

public enum HandoverCommentMode
{
    Full,
    Minimal,
    Off
}

/// <summary>
/// Operator policy for what happens when a worker finishes an item. Wrighty never executes
/// merge, push, or PR creation; <see cref="Integration"/> only selects which guidance the
/// finished output and the agent skill render.
/// </summary>
public sealed record WorkerCompletionConfig
{
    /// <summary>"inspect" (default): the agent must leave changes uncommitted for operator
    /// review. "agent": the agent commits its work before finishing.</summary>
    public string? Commit { get; init; }

    /// <summary>"none" (default), "merge-local", or "push-pr".</summary>
    public string? Integration { get; init; }
}

public sealed record GitHubBackendConfig
{
    public required string Repository { get; init; }

    public string? ProjectOwner { get; init; }

    public required int ProjectNumber { get; init; }

    public bool LinkRepository { get; init; } = true;

    public string StatusField { get; init; } = "Status";

    public string PriorityField { get; init; } = "Priority";

    public string AgentTypeField { get; init; } = "Current agent type";

    public string ClaimantKindField { get; init; } = "Current claimant kind";

    public string ClaimantIdField { get; init; } = "Current claimant";

    public string SessionIdField { get; init; } = "Current session ID";

    public string WorkspacePathField { get; init; } = "Current workspace path";

    public string CreationAttemptIdField { get; init; } = "Creation attempt ID";

    public int ClaimHistoryLimit { get; init; } = 10;

    public string GitHubHost { get; init; } = "github.com";
}

public sealed record LocalMarkdownBackendConfig
{
    public string Path { get; init; } = ".wrighty";

    public IReadOnlyList<string> Statuses { get; init; } = ["Todo", "In Progress", "Done"];

    public IReadOnlyList<string> Priorities { get; init; } = ["P0", "P1", "P2", "P3"];
}

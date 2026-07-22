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

    /// <summary>Template for the directory that receives worker worktrees. Placeholders:
    /// {repo}, {repoParent}, {home}, {repoPathHash}. Default: {repoParent}/{repo}.worktrees.</summary>
    public string? WorktreeRoot { get; init; }

    /// <summary>Template for the worker branch name. Placeholders: {id}, {number}, {title},
    /// {unique}, {agent}, {date}. Default: wrighty-worker/{id}-{title}.</summary>
    public string? BranchFormat { get; init; }

    /// <summary>Template for the worktree directory name. Same placeholders as
    /// branchFormat. Default: {id}-{title}.</summary>
    public string? WorktreeNameFormat { get; init; }
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

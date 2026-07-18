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

using System.Text.Json.Serialization;

namespace Highbyte.Wrighty.Configuration;

public sealed record TrackerConfig
{
    private readonly string repository = string.Empty;
    private readonly string? projectOwner;
    private readonly int projectNumber;
    private readonly bool linkRepository = true;
    private readonly string statusField = "Status";
    private readonly string priorityField = "Priority";
    private readonly string agentTypeField = "Current agent type";
    private readonly string sessionIdField = "Current session ID";
    private readonly string creationAttemptIdField = "Creation attempt ID";
    private readonly int claimHistoryLimit = 10;
    private readonly string gitHubHost = "github.com";

    public string Backend { get; init; } = "github";

    [JsonPropertyName("github")]
    public GitHubBackendConfig? GitHub { get; init; }

    public LocalMarkdownBackendConfig? LocalMarkdown { get; init; }

    public ArchiveConfig Archive { get; init; } = new();

    public string DefaultPickFrom { get; init; } = "Todo";

    public string DefaultPickTo { get; init; } = "In Progress";

    public string DefaultFinishTo { get; init; } = "Done";

    public int LeaseMinutes { get; init; } = 60;

    [JsonIgnore]
    public string? SourcePath { get; init; }

    // Compatibility construction properties keep existing internal code and tests concise while
    // persisted configuration uses the typed GitHub section above.
    [JsonIgnore]
    public string Repository { get => GitHub?.Repository ?? repository; init => repository = value; }

    [JsonIgnore]
    public string? ProjectOwner { get => GitHub?.ProjectOwner ?? projectOwner; init => projectOwner = value; }

    [JsonIgnore]
    public int ProjectNumber { get => GitHub?.ProjectNumber ?? projectNumber; init => projectNumber = value; }

    [JsonIgnore]
    public bool LinkRepository { get => GitHub?.LinkRepository ?? linkRepository; init => linkRepository = value; }

    [JsonIgnore]
    public string StatusField { get => GitHub?.StatusField ?? statusField; init => statusField = value; }

    [JsonIgnore]
    public string PriorityField { get => GitHub?.PriorityField ?? priorityField; init => priorityField = value; }

    [JsonIgnore]
    public string AgentTypeField { get => GitHub?.AgentTypeField ?? agentTypeField; init => agentTypeField = value; }

    [JsonIgnore]
    public string SessionIdField { get => GitHub?.SessionIdField ?? sessionIdField; init => sessionIdField = value; }

    [JsonIgnore]
    public string CreationAttemptIdField
    {
        get => GitHub?.CreationAttemptIdField ?? creationAttemptIdField;
        init => creationAttemptIdField = value;
    }

    [JsonIgnore]
    public int ClaimHistoryLimit { get => GitHub?.ClaimHistoryLimit ?? claimHistoryLimit; init => claimHistoryLimit = value; }

    [JsonIgnore]
    public string GitHubHost { get => GitHub?.GitHubHost ?? gitHubHost; init => gitHubHost = value; }

    [JsonIgnore]
    public GitHubBackendConfig EffectiveGitHub => GitHub ?? new GitHubBackendConfig
    {
        Repository = Repository,
        ProjectOwner = ProjectOwner,
        ProjectNumber = ProjectNumber,
        LinkRepository = LinkRepository,
        StatusField = StatusField,
        PriorityField = PriorityField,
        AgentTypeField = AgentTypeField,
        SessionIdField = SessionIdField,
        CreationAttemptIdField = CreationAttemptIdField,
        ClaimHistoryLimit = ClaimHistoryLimit,
        GitHubHost = GitHubHost
    };

    [JsonIgnore]
    public string EffectiveRepository => EffectiveGitHub.Repository;

    [JsonIgnore]
    public int EffectiveProjectNumber => EffectiveGitHub.ProjectNumber;

    [JsonIgnore]
    public string RepositoryOwner => EffectiveRepository.Split('/', 2)[0];

    [JsonIgnore]
    public string RepositoryName => EffectiveRepository.Split('/', 2)[1];

    [JsonIgnore]
    public string EffectiveProjectOwner => EffectiveGitHub.ProjectOwner ?? RepositoryOwner;

    public bool ShouldArchiveStatus(string? status) => status is not null &&
        Archive.OnStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);
}

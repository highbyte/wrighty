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
    private readonly string executionPolicyField = "Wrighty policy - execution";
    private readonly string agentPolicyField = "Wrighty policy - agent";
    private readonly string contextApprovalField = "Wrighty policy - context approval";
    private readonly IReadOnlyList<string>? trustedCommentAuthors;
    private readonly string dispatchStateField = "Wrighty dispatch - state";
    private readonly string dispatchNotBeforeField = "Wrighty dispatch - not before";
    private readonly string dispatchAgentField = "Wrighty dispatch - agent";
    private readonly string dispatchDetailField = "Wrighty dispatch - detail";
    private readonly string claimAgentField = "Wrighty claim - agent";
    private readonly string claimantTypeField = "Wrighty claim - claimant type";
    private readonly string claimantField = "Wrighty claim - claimant";
    private readonly string claimSessionIdField = "Wrighty claim - session ID";
    private readonly string claimWorkspacePathField = "Wrighty claim - workspace path";
    private readonly string creationAttemptIdField = "Wrighty creation - attempt ID";
    private readonly int claimHistoryLimit = 10;
    private readonly string gitHubHost = "github.com";

    public string Backend { get; init; } = "github";

    [JsonPropertyName("github")]
    public GitHubBackendConfig? GitHub { get; init; }

    public LocalMarkdownBackendConfig? LocalMarkdown { get; init; }

    public ArchiveConfig Archive { get; init; } = new();

    public WebConfig? Web { get; init; }

    public WorkerConfig? Worker { get; init; }

    public string DefaultPickFrom { get; init; } = "Todo";

    public string DefaultPickTo { get; init; } = "In Progress";

    public string DefaultFinishTo { get; init; } = "Done";

    public int LeaseMinutes { get; init; } = 60;

    [JsonIgnore]
    public string? SourcePath { get; init; }

    // Non-serialized construction conveniences keep backend-neutral callers concise. Persisted
    // configuration has one canonical shape: the typed GitHub section above.
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
    public string ExecutionPolicyField
    {
        get => GitHub?.ExecutionPolicyField ?? executionPolicyField;
        init => executionPolicyField = value;
    }

    [JsonIgnore]
    public string AgentPolicyField
    {
        get => GitHub?.AgentPolicyField ?? agentPolicyField;
        init => agentPolicyField = value;
    }

    /// <summary>
    /// The single-select field whose value approves the current title/body and sets the batch
    /// comment cutoff. Separate from the execution policy on purpose: one authorises scheduling,
    /// the other approves content, and a maintainer needs to change them independently.
    /// </summary>
    [JsonIgnore]
    public string ContextApprovalField
    {
        get => GitHub?.ContextApprovalField ?? contextApprovalField;
        init => contextApprovalField = value;
    }

    /// <summary>
    /// GitHub logins whose comments count as approved without a separate approval step. Empty
    /// unless the repository names one; see <see cref="GitHubBackendConfig.TrustedCommentAuthors"/>
    /// for what naming an author also accepts.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> TrustedCommentAuthors
    {
        get => GitHub?.TrustedCommentAuthors ?? trustedCommentAuthors ?? [];
        init => trustedCommentAuthors = value;
    }

    [JsonIgnore]
    public string DispatchStateField
    {
        get => GitHub?.DispatchStateField ?? dispatchStateField;
        init => dispatchStateField = value;
    }

    [JsonIgnore]
    public string DispatchNotBeforeField
    {
        get => GitHub?.DispatchNotBeforeField ?? dispatchNotBeforeField;
        init => dispatchNotBeforeField = value;
    }

    [JsonIgnore]
    public string DispatchAgentField
    {
        get => GitHub?.DispatchAgentField ?? dispatchAgentField;
        init => dispatchAgentField = value;
    }

    [JsonIgnore]
    public string DispatchDetailField
    {
        get => GitHub?.DispatchDetailField ?? dispatchDetailField;
        init => dispatchDetailField = value;
    }

    [JsonIgnore]
    public string ClaimAgentField { get => GitHub?.ClaimAgentField ?? claimAgentField; init => claimAgentField = value; }

    [JsonIgnore]
    public string ClaimantTypeField { get => GitHub?.ClaimantTypeField ?? claimantTypeField; init => claimantTypeField = value; }

    [JsonIgnore]
    public string ClaimantField { get => GitHub?.ClaimantField ?? claimantField; init => claimantField = value; }

    [JsonIgnore]
    public string ClaimSessionIdField { get => GitHub?.ClaimSessionIdField ?? claimSessionIdField; init => claimSessionIdField = value; }

    [JsonIgnore]
    public string ClaimWorkspacePathField { get => GitHub?.ClaimWorkspacePathField ?? claimWorkspacePathField; init => claimWorkspacePathField = value; }

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
        ExecutionPolicyField = ExecutionPolicyField,
        ContextApprovalField = ContextApprovalField,
        TrustedCommentAuthors = trustedCommentAuthors,
        AgentPolicyField = AgentPolicyField,
        DispatchStateField = DispatchStateField,
        DispatchNotBeforeField = DispatchNotBeforeField,
        DispatchAgentField = DispatchAgentField,
        DispatchDetailField = DispatchDetailField,
        ClaimAgentField = ClaimAgentField,
        ClaimantTypeField = ClaimantTypeField,
        ClaimantField = ClaimantField,
        ClaimSessionIdField = ClaimSessionIdField,
        ClaimWorkspacePathField = ClaimWorkspacePathField,
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

    [JsonIgnore]
    public WebConfig EffectiveWeb => Web ?? new WebConfig();

    [JsonIgnore]
    public WorkerConfig EffectiveWorker => Worker ?? new WorkerConfig();

    public bool ShouldArchiveStatus(string? status) => status is not null &&
        Archive.OnStatuses.Contains(status, StringComparer.OrdinalIgnoreCase);
}

using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Models;
using Microsoft.AspNetCore.Html;

namespace Highbyte.Wrighty.Web;

public sealed record BoardPageModel(
    IReadOnlyList<string> Statuses,
    IReadOnlyList<string> Priorities,
    IReadOnlyList<BoardColumnModel> Columns,
    IReadOnlyList<BoardCardModel> Archived,
    string Scope,
    string Revision,
    string? ErrorCode = null,
    string? ErrorMessage = null);

public sealed record BoardColumnModel(string Name, IReadOnlyList<BoardCardModel> Cards);

public sealed record BoardCardModel(
    string Id,
    string DisplayId,
    string Title,
    string? Status,
    string? Priority,
    bool Archived,
    ClaimOwnershipState ClaimState,
    string ClaimLabel,
    string? ClaimantKindLabel,
    string? AgentTypeLabel,
    bool AutomationEligible,
    string? PreferredAgent,
    string? WorkerState,
    string Activity);

public sealed record ItemPageModel(
    string Id,
    string DisplayId,
    string Title,
    string Body,
    string? Status,
    string? Priority,
    bool Archived,
    string Revision,
    ClaimOwnershipState ClaimState,
    string ClaimLabel,
    string? ClaimantKindLabel,
    string? AgentTypeLabel,
    bool WebMutationProtected,
    string? WebMutationProtectionMessage,
    bool TakeoverAvailable,
    string? ClaimantId,
    string? ClaimGeneration,
    bool HasResumeAddress,
    bool CanQueueForWorker,
    string? ResumeCommand,
    string? WorkerResumeCommand,
    string? ResumePrompt,
    string? ResumeAgentLabel,
    bool AutomationEligible,
    string? PreferredAgent,
    string? WorkerState,
    string Activity,
    IReadOnlyList<string> Statuses,
    IReadOnlyList<string> Priorities,
    IHtmlContent RenderedBody,
    string? Notice = null,
    string? ErrorCode = null,
    string? ErrorMessage = null,
    bool Editing = false,
    IReadOnlyDictionary<string, string>? Fields = null,
    string? RawFrontmatter = null)
{
    public IReadOnlyDictionary<string, string> EffectiveFields =>
        Fields ?? EmptyFields;

    private static readonly IReadOnlyDictionary<string, string> EmptyFields =
        new Dictionary<string, string>();
}

public sealed record ConflictPageModel(
    ItemPageModel Current,
    string SubmittedTitle,
    string SubmittedBody,
    string SubmittedStatus,
    string? SubmittedPriority,
    bool SubmittedAutomationEligible,
    string? SubmittedPreferredAgent);

public sealed record WebErrorModel(string Code, string Message);

public sealed record CreateItemPageModel(
    string Title,
    string Body,
    string Status,
    string? Priority,
    bool AutomationEligible,
    string? PreferredAgent,
    string CreationAttemptId,
    IReadOnlyList<string> Statuses,
    IReadOnlyList<string> Priorities,
    string? ErrorCode = null,
    string? ErrorMessage = null);

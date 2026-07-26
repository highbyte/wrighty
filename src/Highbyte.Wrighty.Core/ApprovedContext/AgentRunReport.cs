using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// What Wrighty observed a run to have done. Deliberately its own type rather than
/// <see cref="RunOutcome"/> or <see cref="WorkerItemDisposition"/>: the former has no
/// needs-attention value, and the latter admits transient dispositions such as fenced or skipped
/// that never produce a report.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<RunReportDisposition>))]
public enum RunReportDisposition
{
    /// <summary>
    /// Wrighty observed the item reach its completion state. A vendor process that merely exited
    /// successfully is NOT finished — that is needs-attention.
    /// </summary>
    [JsonStringEnumMemberName("finished")]
    Finished,

    [JsonStringEnumMemberName("needs-attention")]
    NeedsAttention,

    [JsonStringEnumMemberName("failed")]
    Failed,

    [JsonStringEnumMemberName("rejected")]
    Rejected
}

/// <summary>
/// A bounded, historical record of one worker run (plan 030 decision 18).
///
/// The split between observed and reported fields is the point of this model. Wrighty owns the
/// disposition, the process outcome and the timing; the agent supplies only narrative, and every
/// narrative field is labelled as agent-reported when rendered. An agent that believes it finished
/// cannot make a run finished.
/// </summary>
public sealed record AgentRunReport(
    string RunId,
    string ReportId,
    string AgentType,
    RunReportDisposition ObservedDisposition,
    AgentOutcome AgentProcessOutcome,
    DateTimeOffset EndedAt,
    int FormatVersion = AgentRunReport.CurrentFormatVersion,
    string? Summary = null,
    IReadOnlyList<string>? Changes = null,
    IReadOnlyList<string>? Verification = null,
    IReadOnlyList<string>? Decisions = null,
    IReadOnlyList<string>? RequestedInput = null,
    IReadOnlyList<string>? RemainingWork = null,
    string? AgentReportedBody = null)
{
    public const int CurrentFormatVersion = 1;

    /// <summary>The marker that identifies a published report, matching plan 030's report contract.</summary>
    public const string MarkerPrefix = "<!-- wrighty-session-report:v1";

    /// <summary>True when the agent supplied nothing usable, so only observed facts can be rendered.</summary>
    public bool IsObservedOnly =>
        string.IsNullOrWhiteSpace(Summary) &&
        string.IsNullOrWhiteSpace(AgentReportedBody) &&
        (Changes?.Count ?? 0) == 0 &&
        (Verification?.Count ?? 0) == 0 &&
        (Decisions?.Count ?? 0) == 0 &&
        (RequestedInput?.Count ?? 0) == 0 &&
        (RemainingWork?.Count ?? 0) == 0;

    /// <summary>
    /// A stable report identity derived from the item and the worker run, so republishing after a
    /// failed request updates that run's comment instead of creating a second one. Derived rather
    /// than random precisely so a retry in a fresh process reaches the same value.
    /// </summary>
    public static string DeriveReportId(WorkItemId itemId, string runId)
    {
        // Separated rather than concatenated: "local:1" + "23" and "local:12" + "3" must not
        // collide, or a retry would update another run's report comment.
        var seed = $"{itemId.Value}\u001f{runId}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        return "report-" + Convert.ToHexStringLower(hash)[..16];
    }
}

/// <summary>What a published report may contain, so an upgrade cannot silently start commenting.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SessionReportMode>))]
public enum SessionReportMode
{
    /// <summary>Publish nothing externally. The default for omitted configuration.</summary>
    [JsonStringEnumMemberName("off")]
    Off,

    /// <summary>Publish only runs where Wrighty observed the item completed.</summary>
    [JsonStringEnumMemberName("completed")]
    Completed,

    /// <summary>Publish completed, needs-attention, failed and rejected runs.</summary>
    [JsonStringEnumMemberName("all")]
    All
}

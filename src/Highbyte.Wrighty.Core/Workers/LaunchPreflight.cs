using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Workers;

/// <summary>
/// Why a vendor process is about to start. Distinct kinds exist because a resume re-enters an
/// existing session and workspace, so it cannot be gated by the same fresh-work selection rules.
/// </summary>
public enum LaunchKind
{
    /// <summary>A newly claimed item starting a new vendor session.</summary>
    Fresh,

    /// <summary>An operator- or queue-driven resume of a recorded vendor session.</summary>
    Resume,

    /// <summary>A fresh session replacing a recorded session that can no longer be resumed.</summary>
    Recovery,

    /// <summary>An automatic retry after a recoverable provider/usage failure.</summary>
    Retry
}

/// <summary>
/// Where in the launch sequence an evaluation happens. The stages are ordered and each one runs
/// strictly later than the previous, so a check registered at a later stage always observes state
/// the earlier stage already admitted.
/// </summary>
public enum LaunchStage
{
    /// <summary>Before a claim is acquired. Refusing here avoids claiming doomed work.</summary>
    PreClaim,

    /// <summary>After the claim, before workspace creation and session metadata.</summary>
    PostClaim,

    /// <summary>Immediately before the vendor process starts.</summary>
    PreSpawn
}

/// <summary>
/// Everything a built-in launch check may read. Deliberately a value object: a check observes the
/// launch, it does not mutate it.
/// </summary>
/// <param name="Session">
/// The session this launch re-enters, when it re-enters one. Null for a fresh launch, which has no
/// prior session by definition. A resume, recovery or retry carries it because those launches skip
/// the post-claim stage entirely — they re-enter an already-claimed item — so a pre-spawn check
/// with something to compare against has nowhere else to find its baseline.
/// </param>
public sealed record LaunchPreflightRequest(
    TrackerConfig Config,
    WorkerOptions Options,
    WorkItemDetail Detail,
    string Agent,
    LaunchKind Kind,
    LaunchStage Stage,
    Claims.AgentSessionRecord? Session = null);

/// <summary>
/// One check's verdict. A refusal always carries a stable code and an operator-facing message; it
/// never carries work-item content, because these flow into worker events and logs.
/// </summary>
public sealed record LaunchPreflightDecision(
    bool Admitted,
    string? Code = null,
    string? Message = null,
    IReadOnlyList<string>? Evidence = null)
{
    public static LaunchPreflightDecision Admit(IReadOnlyList<string>? evidence = null) =>
        new(true, Evidence: evidence);

    public static LaunchPreflightDecision Refuse(
        string code,
        string message,
        IReadOnlyList<string>? evidence = null) =>
        new(false, code, message, evidence);
}

/// <summary>
/// The outcome of a whole stage: the refusing check's verdict, or an admission carrying the
/// accumulated evidence of every check that ran.
/// </summary>
public sealed record LaunchPreflightResult(
    LaunchStage Stage,
    bool Admitted,
    string? RefusedBy = null,
    string? Code = null,
    string? Message = null,
    IReadOnlyList<string>? Evidence = null);

/// <summary>
/// A built-in launch admission check.
/// </summary>
/// <remarks>
/// This is deliberately an internal seam, not an extension point for user-supplied executables.
/// Plans 029, 025, and 030 each need to revalidate one aspect of a launch at the same boundary;
/// implementing that boundary once means a later plan adds a check rather than a second launch
/// path. Plan 031 layers external gates around this seam and does not replace it.
/// </remarks>
public interface ILaunchPreflightCheck
{
    /// <summary>Stable identifier used in refusal events and diagnostics.</summary>
    string Name { get; }

    /// <summary>Whether this check participates in the given stage for the given launch kind.</summary>
    bool AppliesTo(LaunchStage stage, LaunchKind kind);

    ValueTask<LaunchPreflightDecision> EvaluateAsync(
        LaunchPreflightRequest request,
        CancellationToken cancellationToken);
}

/// <summary>
/// A launch check that resolves state the launch must record with the session once it is admitted.
///
/// Separate from <see cref="ILaunchPreflightCheck"/> because the two answer to different callers: a
/// check reports a verdict to the preflight, while what it resolved along the way belongs to
/// whoever persists the session. Expressing that as its own interface keeps the worker from having
/// to name a specific check to collect it.
/// </summary>
public interface ILaunchSessionContextSource
{
    /// <summary>
    /// The approved-context metadata an admitted launch resolved for the item, or null when this
    /// launch resolved none. Taking it clears it, so a later launch of the same item cannot record
    /// a context this one resolved.
    /// </summary>
    ApprovedContext.SessionContextMetadata? TakeSessionContext(WorkItemId id);
}

/// <summary>
/// The single internal launch boundary. Every path that starts a vendor process runs its stages in
/// order, so authoritative Project policy (plan 029), the effective agent permission profile
/// (plan 025), and — once plan 030 lands it — the approved execution-context revision are
/// revalidated in one place instead of once per launch path.
/// </summary>
public sealed class WorkerLaunchPreflight(IEnumerable<ILaunchPreflightCheck> checks)
{
    private readonly IReadOnlyList<ILaunchPreflightCheck> checks = checks.ToArray();

    /// <summary>Which checks would run, so a stage's coverage is observable in tests and docs.</summary>
    public IReadOnlyList<string> CheckNamesFor(LaunchStage stage, LaunchKind kind) => checks
        .Where(check => check.AppliesTo(stage, kind))
        .Select(check => check.Name)
        .ToArray();

    /// <summary>
    /// Runs every applicable check for the stage and stops at the first refusal. Checks are ordered
    /// cheapest-first by registration, so an authoritative policy refusal never pays for a
    /// downstream lookup.
    /// </summary>
    public async Task<LaunchPreflightResult> EvaluateAsync(
        LaunchPreflightRequest request,
        CancellationToken cancellationToken)
    {
        List<string>? evidence = null;
        foreach (var check in checks)
        {
            if (!check.AppliesTo(request.Stage, request.Kind)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            var decision = await check.EvaluateAsync(request, cancellationToken);
            if (decision.Evidence is { Count: > 0 } values)
                (evidence ??= []).AddRange(values);
            if (decision.Admitted) continue;
            return new LaunchPreflightResult(
                request.Stage,
                false,
                check.Name,
                decision.Code,
                decision.Message,
                evidence);
        }

        return new LaunchPreflightResult(request.Stage, true, Evidence: evidence);
    }
}

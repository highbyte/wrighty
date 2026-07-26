namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// The approved-context metadata a session carries between runs (plan 030's resume behaviour).
///
/// It stores the manifest, the approval instants, and the continuation state — hashes and
/// identifiers only. Full comment bodies are deliberately absent: plan 030 forbids retaining them
/// in durable machine-local state, and nothing here needs them. A later launch re-reads the content
/// and verifies it against the recorded digest rather than trusting a stored copy.
///
/// Every field is optional and the whole record is nullable on the session, so a session written by
/// an older binary stays readable. Such a session simply has no manifest, which classifies as
/// <see cref="ContextChangeKind.ManifestUnavailable"/> and blocks unattended resume across a
/// changed revision — the safe reading of "we cannot establish what that agent was given".
/// </summary>
public sealed record SessionContextMetadata(
    ContextManifest? Manifest = null,
    DateTimeOffset? BaseApprovedAt = null,
    DateTimeOffset? BatchCommentCutoff = null,
    ContextApprovalSource ApprovalSource = ContextApprovalSource.None,
    IReadOnlyList<DiscussionDecision>? Decisions = null,
    IReadOnlyList<string>? ConsumedContinuationKeys = null,
    int AutomaticContinuations = 0,
    DateTimeOffset? LastAutomaticQueueAt = null,
    IReadOnlyList<string>? ReportRunIds = null,
    DateTimeOffset? CapturedAt = null)
{
    /// <summary>The digest last supplied to this session, when one was recorded.</summary>
    public string? SuppliedDigest => Manifest?.Digest;

    /// <summary>
    /// Whether a continuation trigger has already spent a turn. Keyed on the event's revision, so
    /// an edited comment is a new candidate while a re-observed one is not.
    /// </summary>
    public bool HasConsumed(TrustedContinuationEvent candidate) =>
        ConsumedContinuationKeys?.Contains(candidate.ConsumptionKey, StringComparer.Ordinal) == true;

    public SessionContextMetadata WithConsumed(TrustedContinuationEvent candidate, DateTimeOffset queuedAt)
    {
        if (HasConsumed(candidate)) return this;
        return this with
        {
            ConsumedContinuationKeys =
                [.. ConsumedContinuationKeys ?? [], candidate.ConsumptionKey],
            AutomaticContinuations = AutomaticContinuations + 1,
            LastAutomaticQueueAt = queuedAt
        };
    }

    /// <summary>Records what one launch supplied, replacing any previous manifest for the session.</summary>
    public static SessionContextMetadata For(ExecutionContextSnapshot snapshot) =>
        new(ContextManifest.From(snapshot),
            snapshot.Approval.BaseApprovedAt,
            snapshot.Approval.BatchCommentCutoff,
            snapshot.Approval.Source,
            snapshot.Decisions,
            CapturedAt: snapshot.Revision.CapturedAt);

    /// <summary>Carries continuation and report state forward onto a newly supplied context.</summary>
    public SessionContextMetadata Supersede(ExecutionContextSnapshot snapshot) =>
        For(snapshot) with
        {
            ConsumedContinuationKeys = ConsumedContinuationKeys,
            AutomaticContinuations = AutomaticContinuations,
            LastAutomaticQueueAt = LastAutomaticQueueAt,
            ReportRunIds = ReportRunIds
        };
}

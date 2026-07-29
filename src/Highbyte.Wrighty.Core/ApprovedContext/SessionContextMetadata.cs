namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// The approved-context metadata a session carries between runs (plan 030's resume behaviour).
///
/// It stores the manifest and the approval instants — hashes and identifiers only. Full comment
/// bodies are deliberately absent: plan 030 forbids retaining them in durable machine-local state,
/// and nothing here needs them. A later launch re-reads the content and verifies it against the
/// recorded digest rather than trusting a stored copy.
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
    IReadOnlyList<string>? ReportRunIds = null,
    DateTimeOffset? CapturedAt = null)
{
    /// <summary>The digest last supplied to this session, when one was recorded.</summary>
    public string? SuppliedDigest => Manifest?.Digest;

    /// <summary>Records what one launch supplied, replacing any previous manifest for the session.</summary>
    public static SessionContextMetadata For(ExecutionContextSnapshot snapshot) =>
        new(ContextManifest.From(snapshot),
            snapshot.Approval.BaseApprovedAt,
            snapshot.Approval.BatchCommentCutoff,
            snapshot.Approval.Source,
            snapshot.Decisions,
            CapturedAt: snapshot.Revision.CapturedAt);

    /// <summary>Carries report state forward onto a newly supplied context.</summary>
    public SessionContextMetadata Supersede(ExecutionContextSnapshot snapshot) =>
        For(snapshot) with { ReportRunIds = ReportRunIds };
}

namespace Highbyte.Wrighty.ApprovedContext;

/// <summary>
/// Whether a request for one specific context revision may be answered, and with what.
///
/// This exists for an agent that has lost the context it was launched with. Phase 0 measured that
/// loss happening under sustained window pressure — and happening honestly, the agent reporting
/// nothing available rather than inventing an answer — so recovery is worth offering.
///
/// What makes it safe is what it refuses. The agent asks for the revision it already had, by digest,
/// and gets either exactly that or nothing. It cannot ask for a newer approval, an edited
/// description, or comments nobody has decided on, so this is not the discovery the design
/// prohibits: it is a cache miss on content Wrighty already approved and pinned for this run.
/// </summary>
public static class PinnedContextRetrieval
{
    public sealed record Result(
        ExecutionContextSnapshot? Snapshot,
        string? RefusalCode = null,
        string? RefusalMessage = null)
    {
        public bool Served => Snapshot is not null;
    }

    /// <summary>
    /// Answers a request for <paramref name="pinnedDigest"/> from a freshly read context.
    ///
    /// The read is deliberately fresh rather than from a stored copy: retaining approved bodies in
    /// durable local state is what this design forbids, and re-reading then verifying the digest
    /// gives the same guarantee without keeping the content anywhere.
    /// </summary>
    public static Result Serve(ExecutionContextResult current, string pinnedDigest)
    {
        if (current.Snapshot is not { } snapshot)
            return new Result(
                null,
                current.Code ?? ExecutionContextResult.Codes.ApprovalUnavailable,
                current.Message ?? "There is no approved context to serve.");

        if (!string.Equals(snapshot.Revision.Digest, pinnedDigest, StringComparison.Ordinal))
            // Not an error to retry past. The approved context moved while this run was in flight,
            // so what the agent holds is superseded and continuing would act on requirements nobody
            // approved for this session.
            return new Result(
                null,
                ExecutionContextResult.Codes.RevisionChanged,
                $"The approved context is now {snapshot.Revision.ShortDigest}, not the revision " +
                "this run was launched with. It is not served: the content has changed since the " +
                "run started, and continuing against it would act on requirements nobody approved " +
                "for this session. Stop and report that the context moved.");

        return new Result(snapshot);
    }
}

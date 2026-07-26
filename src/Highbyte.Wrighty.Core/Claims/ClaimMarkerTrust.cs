namespace Highbyte.Wrighty.Claims;

/// <summary>
/// Which issue comments may carry a claim marker.
///
/// The claim protocol keeps its state in issue comments because there is no lock server to keep it
/// in. That makes the state readable by design — and writable by anyone the repository lets comment,
/// which on a public repository is any GitHub account at all. A marker is not signed, and the claim
/// tokens that fence one are published in the same comments, so nothing in the marker itself
/// distinguishes a claim Wrighty wrote from one somebody typed.
///
/// The association GitHub reports for the comment's author is what distinguishes them. It travels in
/// the same payload as the comment body, so filtering on it costs no extra request in a loop the
/// worker runs constantly.
/// </summary>
public static class ClaimMarkerTrust
{
    /// <summary>
    /// Associations whose comments may carry a claim marker: the repository owner, an organisation
    /// member, or an invited collaborator.
    ///
    /// Deliberately not <c>CONTRIBUTOR</c>. That means an accepted pull request and nothing more —
    /// on a popular repository it is a large, open set, and it is exactly the population a drive-by
    /// forged marker would come from.
    /// </summary>
    private static readonly HashSet<string> Trusted =
        new(StringComparer.OrdinalIgnoreCase) { "OWNER", "MEMBER", "COLLABORATOR" };

    /// <summary>
    /// Whether a comment with this author association may carry a claim marker.
    ///
    /// This is an association, not a permission: <c>COLLABORATOR</c> covers read-only collaborators
    /// too, so it does not distinguish someone who can only read the repository from someone who can
    /// push to it. Closing that gap needs a per-author permission lookup, which is a request per
    /// distinct author on a path the worker reads constantly. The open population — anyone who can
    /// comment — is what this shuts out; a read-only collaborator forging a claim is a narrower
    /// problem and a named account.
    /// </summary>
    public static bool MayCarryMarker(string? authorAssociation) =>
        authorAssociation is not null && Trusted.Contains(authorAssociation);
}

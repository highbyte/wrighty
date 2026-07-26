using Highbyte.Wrighty.ApprovedContext;

namespace Highbyte.Wrighty.GitHub;

/// <summary>One reaction on a comment, as GitHub reports it.</summary>
public sealed record GitHubReaction(
    string Id,
    string Actor,
    string Content,
    DateTimeOffset CreatedAt);

/// <summary>
/// One issue comment with everything needed to decide whether an approval covers it. Kept separate
/// from <see cref="DiscussionEntry"/> because this is the raw tracker shape: it still carries
/// reactions and minimized reasons, which the backend-neutral model has no place for.
/// </summary>
public sealed record GitHubComment(
    string StableId,
    string Author,
    string? AuthorAssociation,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastEditedAt,
    string Url,
    string Body,
    bool Minimized,
    string? MinimizedReason,
    IReadOnlyList<GitHubReaction> Reactions)
{
    /// <summary>
    /// The instant a decision must be strictly later than to cover this comment. An edit moves it;
    /// hiding does not, because hiding advances no timestamp at all.
    /// </summary>
    public DateTimeOffset RevisionAt => LastEditedAt ?? CreatedAt;

    /// <summary>Projects the raw comment onto the backend-neutral model.</summary>
    public DiscussionEntry ToEntry() =>
        new(StableId, Author, CreatedAt, Body, AuthorAssociation, LastEditedAt, Url, Minimized);
}

/// <summary>
/// A complete issue conversation: the base content, the evidence that binds it to an approval, and
/// every comment.
///
/// "Complete" is load-bearing. A partially read conversation cannot be distinguished from one where
/// somebody deleted a comment, so the reader either returns all of it or fails.
/// </summary>
public sealed record GitHubConversation(
    string Title,
    string Body,
    string Url,
    DateTimeOffset CreatedAt,
    DateTimeOffset? BodyLastEditedAt,
    int BodyEditCount,
    DateTimeOffset? TitleLastRenamedAt,
    int TitleRenameCount,
    IReadOnlyList<GitHubComment> Comments)
{
    /// <summary>
    /// The evidence binding this issue's current title and body to an approval cutoff.
    ///
    /// Title and body come from different places, which is the whole point. A body edit advances
    /// <c>lastEditedAt</c> and the user-content edit history; a title edit advances neither and is
    /// visible only as a rename event. Reading the issue's edit metadata alone would miss every
    /// title change — measured, not assumed.
    /// </summary>
    public BaseContentRevision ToBaseRevision() =>
        new(ContextRevisionSerializer.HashContent(Title),
            ContextRevisionSerializer.HashContent(Body),
            BodyLastEditedAt,
            BodyEditCount,
            TitleLastRenamedAt,
            TitleRenameCount);
}

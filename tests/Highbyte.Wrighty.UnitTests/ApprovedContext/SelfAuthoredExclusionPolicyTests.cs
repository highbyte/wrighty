using Highbyte.Wrighty.ApprovedContext;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

/// <summary>
/// Which comments Wrighty recognises as its own.
///
/// The question is authorship, not authority, and the two are separated deliberately: whose
/// judgement counts still needs the roles and teams that reaction authorization has not
/// established, while whose comment this is needs only the account the token belongs to.
/// </summary>
public sealed class SelfAuthoredExclusionPolicyTests
{
    [Fact]
    public void Our_own_comments_are_recognised_and_nobody_else_is()
    {
        var policy = new SelfAuthoredExclusionPolicy("wrighty-bot");

        Assert.True(policy.CanExcludeContent("wrighty-bot"));
        // GitHub logins are case-insensitive, and the API is not consistent about the case it
        // returns between endpoints.
        Assert.True(policy.CanExcludeContent("Wrighty-Bot"));
        Assert.False(policy.CanExcludeContent("some-maintainer"));
        Assert.False(policy.CanExcludeContent(null));
        Assert.False(policy.CanExcludeContent(""));
    }

    [Fact]
    public void A_marker_appended_to_someone_elses_comment_is_not_recognised()
    {
        // The reason this is an identity and not an authority level. GitHub lets a user with write
        // access edit another user's comment without changing its author, so "the author may
        // exclude content" would honour a marker appended to a maintainer's requirement and drop
        // that requirement from what the agent is given — while it stayed visible to every human
        // reading the issue. Hiding requirements is the failure this boundary exists to prevent.
        var policy = new SelfAuthoredExclusionPolicy("wrighty-bot");

        Assert.False(policy.CanExcludeContent("the-maintainer"));
    }

    [Fact]
    public void An_unresolved_identity_excludes_nothing()
    {
        // Failing closed costs an unnecessary re-approval. Failing open hides content from review.
        var policy = new SelfAuthoredExclusionPolicy(null);

        Assert.False(policy.CanExcludeContent("wrighty-bot"));
        Assert.False(policy.CanExcludeContent("anyone"));
    }

    [Fact]
    public void Recognising_our_own_comments_authorises_nobody_to_decide_anything()
    {
        // The whole point of splitting the two questions. Reaction authorization stays blocked on
        // observations that need a second GitHub identity and an organization, and this must not
        // become a back door into it.
        var policy = new SelfAuthoredExclusionPolicy("wrighty-bot");

        Assert.False(policy.IsApprover("wrighty-bot"));
        Assert.False(policy.IsApprover("the-maintainer"));
    }
}

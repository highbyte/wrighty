using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// What a paused item's handover offers, per backend.
///
/// The GitHub shape had no test at all, which is how it came to tell a GitHub reader that the web
/// dashboard is Local Markdown only — true, useless to them, and the only thing it said about where
/// they actually were. These assert the guidance leads with what the reader can do without leaving
/// the surface they are on.
/// </summary>
public sealed class OperatorSurfaceTests
{
    private static TrackerConfig GitHubConfig(string? approvalField = null) => new()
    {
        Backend = "github",
        GitHub = approvalField is null
            ? new GitHubBackendConfig { Repository = "owner/repo", ProjectNumber = 1 }
            : new GitHubBackendConfig
            {
                Repository = "owner/repo",
                ProjectNumber = 1,
                ContextApprovalField = approvalField
            }
    };

    [Fact]
    public void A_github_item_gets_the_issue_surface_and_a_local_one_gets_the_dashboard()
    {
        var gitHub = OperatorSurface.For(GitHubConfig(), "https://github.com/owner/repo/issues/1");
        Assert.Equal(OperatorSurfaceKind.GitHubIssue, gitHub.Kind);
        Assert.True(gitHub.HasDiscussion);

        var local = OperatorSurface.For(
            new TrackerConfig { Backend = "local-markdown" }, itemUrl: null);
        Assert.Equal(OperatorSurfaceKind.Dashboard, local.Kind);
        // The difference that changes the advice: nothing to append a clarification to.
        Assert.False(local.HasDiscussion);
    }

    [Fact]
    public void The_approval_field_is_the_configured_one_not_a_hardcoded_name()
    {
        // A repository may rename it. Guidance naming a field the reader cannot find is worse than
        // guidance naming none.
        var surface = OperatorSurface.For(
            GitHubConfig("Ready for the robots"), "https://github.com/owner/repo/issues/1");

        Assert.Equal("Ready for the robots", surface.ContextApprovalField);
        Assert.Equal("Approved", surface.ApprovedOption);
    }

    [Fact]
    public void The_default_field_names_match_what_init_provisions()
    {
        var surface = OperatorSurface.For(GitHubConfig(), "https://github.com/owner/repo/issues/1");

        Assert.Equal("Wrighty policy - context approval", surface.ContextApprovalField);
        Assert.Equal("Wrighty dispatch - state", surface.DispatchStateField);
    }

    [Fact]
    public void Naming_a_trusted_author_changes_what_the_recovery_guidance_should_say()
    {
        // With no trusted author, a reply needs a decision and the guidance must say so. With one,
        // the reply is enough on its own — and telling that reader to toggle an approval field
        // anyway is how an operator concludes Wrighty is broken when nothing needed to happen.
        var withoutTrust = OperatorSurface.For(
            GitHubConfig(), "https://github.com/owner/repo/issues/1");
        Assert.False(withoutTrust.ContinuesOnTrustedReply);

        var withTrust = OperatorSurface.For(
            new TrackerConfig
            {
                Backend = "github",
                GitHub = new GitHubBackendConfig
                {
                    Repository = "owner/repo",
                    ProjectNumber = 1,
                    TrustedCommentAuthors = ["highbyte"]
                }
            },
            "https://github.com/owner/repo/issues/1");
        Assert.True(withTrust.ContinuesOnTrustedReply);
    }

    [Fact]
    public void A_local_item_never_claims_a_trusted_reply_will_continue_it()
    {
        // Trusted-comment continuation is a GitHub-discussion mechanism; a dashboard surface has no
        // comments to reply to, so promising one would be advice the reader cannot act on.
        var local = OperatorSurface.For(
            new TrackerConfig { Backend = "local-markdown" }, itemUrl: null);

        Assert.False(local.ContinuesOnTrustedReply);
    }
}

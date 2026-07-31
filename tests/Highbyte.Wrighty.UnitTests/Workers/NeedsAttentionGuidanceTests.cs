using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// What a paused item tells the person who has to unblock it.
///
/// This text is the whole recovery path for someone working in GitHub's own UI, where a
/// dispatch-state label is otherwise the only signal. An operator who is told to toggle an approval
/// field that needed no toggling concludes the tracker is broken — which is exactly what happened
/// before a trusted reply could continue an item on its own.
/// </summary>
public sealed class NeedsAttentionGuidanceTests
{
    private static readonly WorkItemId Item = new("github:owner/repo#42");
    private const string Url = "https://github.com/owner/repo/issues/42";

    private static TrackerConfig Config(params string[] trustedAuthors) => new()
    {
        Backend = "github",
        GitHub = new GitHubBackendConfig
        {
            Repository = "owner/repo",
            ProjectNumber = 1,
            TrustedCommentAuthors = trustedAuthors.Length == 0 ? null : trustedAuthors
        }
    };

    private static IReadOnlyList<WorkerOperatorAction> Actions(TrackerConfig config) =>
        WorkerService.NeedsAttentionActions(
            Item, "codex", OperatorSurface.For(config, Url));

    private static string AllText(IReadOnlyList<WorkerOperatorAction> actions) =>
        string.Join("\n", actions.Select(a => $"{a.Scenario}\n{a.Description}"));

    [Fact]
    public void With_a_trusted_author_a_reply_is_presented_as_sufficient_on_its_own()
    {
        var text = AllText(Actions(Config("highbyte")));

        Assert.Contains("nothing else needed", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("continues this same session", text, StringComparison.OrdinalIgnoreCase);
        // The reader is still told what to do if they are not the trusted author, because the
        // comment is rendered onto an issue anyone may be reading.
        Assert.Contains("not a trusted author", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Without_a_trusted_author_the_approval_toggle_is_still_required()
    {
        var text = AllText(Actions(Config()));

        Assert.DoesNotContain("nothing else needed", text, StringComparison.OrdinalIgnoreCase);
        // Both moves, and the reason: approval is an instant, so re-selecting the value it already
        // holds renews nothing and the reply stays undecided.
        Assert.Contains("any other value and back", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Both_shapes_warn_against_editing_the_description()
    {
        // Appending is additive and any worker may carry it; rewriting the description replaces what
        // the paused session was given, and only a run the operator names may proceed across it.
        foreach (var config in new[] { Config("highbyte"), Config() })
            Assert.Contains(
                "Do not edit", AllText(Actions(config)), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_manual_resume_command_names_the_item()
    {
        var actions = Actions(Config("highbyte"));

        Assert.Contains(
            actions.SelectMany(a => a.Commands ?? []),
            command => command.Contains(Item.Value, StringComparison.Ordinal));
    }

    [Fact]
    public void An_exhausted_budget_withdraws_the_reply_alone_promise_and_names_the_limit()
    {
        // Budget exhaustion is terminal until an operator acts — by design. Guidance that keeps
        // promising hands-off continuation makes that design read as a defect: the operator
        // replies, nothing happens, and nothing says why.
        var spent = new Highbyte.Wrighty.ApprovedContext.TrustedContinuationBudget(
            MaxAutomaticContinuations: 10, Used: 10);

        var actions = WorkerService.NeedsAttentionActions(
            Item, "codex", OperatorSurface.For(Config("highbyte"), Url), budget: spent);
        var text = AllText(actions);

        Assert.DoesNotContain("nothing else needed", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("used all 10 automatic continuations", text, StringComparison.OrdinalIgnoreCase);
        // It still routes the reader forward: the reply plus a manual start, never a dead end.
        Assert.Contains(
            actions.SelectMany(a => a.Commands ?? []),
            command => command.StartsWith("wrighty worker --item", StringComparison.Ordinal));
        // And the reader who is not a trusted author still learns the approval toggle.
        Assert.Contains("any other value and back", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_budget_with_turns_left_keeps_the_reply_alone_promise()
    {
        var partlySpent = new Highbyte.Wrighty.ApprovedContext.TrustedContinuationBudget(
            MaxAutomaticContinuations: 10, Used: 9);

        var text = AllText(WorkerService.NeedsAttentionActions(
            Item, "codex", OperatorSurface.For(Config("highbyte"), Url), budget: partlySpent));

        Assert.Contains("nothing else needed", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Without_trusted_authors_an_exhausted_budget_changes_nothing()
    {
        // The exhaustion text explains why an advertised automatic continuation stopped happening.
        // A configuration that never advertised one has nothing to explain.
        var spent = new Highbyte.Wrighty.ApprovedContext.TrustedContinuationBudget(
            MaxAutomaticContinuations: 10, Used: 10);

        var text = AllText(WorkerService.NeedsAttentionActions(
            Item, "codex", OperatorSurface.For(Config(), Url), budget: spent));

        Assert.DoesNotContain("automatic continuations", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("any other value and back", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_dashboard_surface_never_promises_a_trusted_reply_will_continue_it()
    {
        // Trusted-comment continuation is a GitHub-discussion mechanism. A surface with no comments
        // to reply to must not offer it as the recovery path.
        var actions = WorkerService.NeedsAttentionActions(
            Item, "codex",
            OperatorSurface.For(new TrackerConfig { Backend = "local-markdown" }, itemUrl: null));

        Assert.DoesNotContain(
            "continues this same session", AllText(actions), StringComparison.OrdinalIgnoreCase);
    }
}

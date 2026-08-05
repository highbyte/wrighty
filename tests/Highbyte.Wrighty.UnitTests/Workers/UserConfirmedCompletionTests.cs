using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Workers;

/// <summary>
/// Holding completion for a person: what the agent is told, and what happens when the policy is
/// misspelled.
///
/// The instruction is the whole mechanism — there is no separate gate stopping an agent from calling
/// finish — so a run that receives the wrong text simply finishes work its operator meant to review,
/// and nothing downstream reports it. That is why these assert the text rather than an effect.
/// </summary>
public sealed class UserConfirmedCompletionTests : IDisposable
{
    private static readonly WorkItemId Item = new("github:owner/repo#42");

    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"wrighty-completion-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private async Task<TrackerConfig> LoadAsync(string completionJson)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, TrackerConfigLoader.FileName),
            $$"""
            {
              "backend": "github",
              "github": { "repository": "owner/repo", "projectNumber": 1 },
              "worker": { "completion": {{completionJson}} }
            }
            """);
        return await new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None);
    }

    // --- what the agent is told -----------------------------------------------------------------

    [Fact]
    public void The_default_policy_still_lets_the_agent_finish()
    {
        var instructions = WorkerPrompt.OperatingInstructions(Item);

        Assert.Contains($"Call `wrighty finish {Item.Value}`", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("on your own judgement", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Under_user_confirmation_the_agent_is_told_not_to_finish_on_its_own()
    {
        var instructions = WorkerPrompt.OperatingInstructions(Item, requiresUserConfirmation: true);

        Assert.Contains("Do not call", instructions, StringComparison.Ordinal);
        Assert.Contains("on your own judgement", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void Reporting_without_finishing_is_named_as_success_not_failure()
    {
        // An agent that reads "stop without finishing" as a failure state writes an apologetic
        // report, or worse, keeps working to avoid the outcome it thinks is wrong.
        var instructions = WorkerPrompt.OperatingInstructions(Item, requiresUserConfirmation: true);

        Assert.Contains("expected successful ending", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void An_accepted_item_may_be_finished_but_only_on_a_clear_acceptance()
    {
        // The acceptance is ordinary discussion, so the agent has to judge it. Both halves matter:
        // without the first it can never finish, and without the second a follow-up question reads
        // as approval.
        var instructions = WorkerPrompt.OperatingInstructions(Item, requiresUserConfirmation: true);

        Assert.Contains("has already accepted the work, finish it", instructions, StringComparison.Ordinal);
        Assert.Contains("is not acceptance", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rules_that_do_not_depend_on_the_policy_are_identical_either_way()
    {
        // Blocked-item handling, claim fencing and lease rules are stated once precisely so the two
        // variants cannot drift on what they mean.
        const string shared = "If a Wrighty mutation fails with CLAIM_STALE";

        Assert.Contains(shared, WorkerPrompt.OperatingInstructions(Item), StringComparison.Ordinal);
        Assert.Contains(
            shared,
            WorkerPrompt.OperatingInstructions(Item, requiresUserConfirmation: true),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_bootstrap_prompt_carries_the_policy_too()
    {
        // A policy that reaches only the approved-context path is one an operator cannot rely on.
        Assert.Contains(
            "on your own judgement",
            WorkerPrompt.For(Item, requiresUserConfirmation: true),
            StringComparison.Ordinal);
        Assert.Contains(
            "on your own judgement",
            WorkerPrompt.ForClaude(Item, requiresUserConfirmation: true),
            StringComparison.Ordinal);
    }

    // --- what the item says while it waits -------------------------------------------------------

    private static string Handover(bool requiresUserConfirmation) =>
        HandoverRenderer.Render(new HandoverContent(
            Item,
            HandoverPhase.NeedsAttention,
            RunOutcome.Succeeded,
            "Added the retry handling and its tests.",
            "host-1",
            "/tmp/workspace",
            null,
            [],
            HandoverCommentMode.Full,
            RequiresUserConfirmation: requiresUserConfirmation));

    [Fact]
    public void Ordinarily_a_needs_attention_item_reads_as_paused()
    {
        Assert.Contains("paused without finishing", Handover(false), StringComparison.Ordinal);
    }

    [Fact]
    public void Under_user_confirmation_it_does_not_read_as_a_failure_to_finish()
    {
        // The same ending now covers "I am stuck" and "I am done, please accept". Calling it paused
        // would tell a reader the agent could not finish, when the policy is why it did not.
        var handover = Handover(true);

        Assert.DoesNotContain("paused without finishing", handover, StringComparison.Ordinal);
        Assert.Contains("This repository expects", handover, StringComparison.Ordinal);
    }

    [Fact]
    public void It_says_the_two_endings_look_alike_and_where_to_tell_them_apart()
    {
        // Wrighty observes only that the run did not finish; which of the two happened is in the
        // agent's report, so the text must send the reader there rather than guess.
        var handover = Handover(true);

        Assert.Contains("Read its report", handover, StringComparison.Ordinal);
        Assert.Contains("Reply to accept", handover, StringComparison.Ordinal);
    }

    // --- configuration --------------------------------------------------------------------------

    [Fact]
    public async Task The_policy_binds_from_configuration()
    {
        var config = await LoadAsync("""{ "policy": "user-confirmed" }""");

        Assert.True(config.EffectiveWorker.Completion!.RequiresUserConfirmation);
    }

    [Fact]
    public void An_absent_policy_lets_the_agent_finish()
    {
        // The default has to be the existing behaviour: an upgrade must not start withholding
        // completion from someone who never asked for review.
        Assert.False(new WorkerCompletionConfig().RequiresUserConfirmation);
    }

    [Fact]
    public async Task An_unrecognised_policy_fails_instead_of_falling_back()
    {
        var exception = await Assert.ThrowsAsync<TrackerException>(
            () => LoadAsync("""{ "policy": "user_confirmed" }"""));

        Assert.Equal("CONFIG_INVALID", exception.Code);
        Assert.Contains("user-confirmed", exception.Message, StringComparison.Ordinal);
    }
}

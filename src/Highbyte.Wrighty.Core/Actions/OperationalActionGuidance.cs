using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Actions;

/// <summary>Shared, inert guidance used by CLI discovery and worker handover snapshots.</summary>
public static class OperationalActionGuidance
{
    public static IReadOnlyList<WorkerOperatorAction> NeedsAttentionActions(
        WorkItemId id,
        string agentName,
        OperatorSurface surface,
        DateTimeOffset? activeUntil = null,
        ApprovedContext.TrustedContinuationBudget? budget = null)
    {
        var agentLabel = agentName.Length == 0
            ? "agent"
            : $"{char.ToUpperInvariant(agentName[0])}{agentName[1..]}";
        var actions = new List<WorkerOperatorAction>();
        // The promise "a reply alone continues this" is only true while the session has automatic
        // continuations left. Once the budget is spent, an item stays here until an operator acts —
        // by design — and guidance that keeps promising hands-off continuation makes that design
        // read as a defect: the operator replies, nothing happens, nothing says why. So exhaustion
        // switches the text, and names the limit as the reason.
        var continuesAutomatically =
            surface.ContinuesOnTrustedReply && budget is not { IsExhausted: true };

        if (surface is { Kind: OperatorSurfaceKind.GitHubIssue, ItemUrl: { } url })
        {
            // Two steps because both are required and the second is the one everybody forgets:
            // approval is an instant, so re-selecting the value the field already holds moves
            // nothing and the new comment stays undecided. The walkthrough exists partly to make
            // that failure visible, which is a sign it needs saying here.
            // The URL stays even though this is rendered onto the issue itself: the same action
            // list is printed in the worker's terminal, where it is the only pointer to the item.
            // Hence "on the issue" rather than "here", which is only true in one of the two places.
            // Naming a trusted author changes the answer to "what do I do now?" enough that the two
            // cases are written out separately rather than hedged into one. Where a reply is enough
            // on its own, saying so matters: an operator who has been told to toggle a field will
            // keep toggling it, and conclude Wrighty is broken when nothing needed to happen.
            if (surface.ContinuesOnTrustedReply && !continuesAutomatically)
                actions.Add(new WorkerOperatorAction(
                    "Answer on the issue, then queue it yourself — automatic continuation is spent",
                    [url],
                    $"This session has used all {budget!.MaxAutomaticContinuations} automatic " +
                    "continuations, so a reply alone no longer starts it. That is the configured " +
                    "limit holding, not a fault; the item stays here until you act. Reply in a " +
                    "new comment with the clarification — a trusted author's reply still needs no " +
                    "approval change; anyone else's needs " +
                    $"\"{surface.ContextApprovalField}\" set to any other value and back to " +
                    $"\"{surface.ApprovedOption}\", both moves — then start the session with the " +
                    "command below, or set " +
                    $"\"{surface.DispatchStateField}\" to \"{DispatchStates.Queued}\" for the " +
                    "next continuous worker on the recording host.\n\n" +
                    "Do not edit the description: that replaces what this paused session was " +
                    "already given, and only a run you name yourself can proceed across such a " +
                    "change.", Name: "answer-on-issue", Url: url));
            else
                actions.Add(continuesAutomatically
                ? new WorkerOperatorAction(
                    "Answer on the issue — nothing else needed",
                    [url],
                    "Reply in a new comment on this issue with the clarification. If you are one " +
                    "of the configured trusted authors, a continuous worker picks the item up and " +
                    "continues this same session with what you wrote — no approval change and no " +
                    "command. Give it a moment: replies are left to settle briefly so an edit " +
                    "straight after posting is the version the agent reads.\n\n" +
                    "Do not edit the description: that replaces what this paused session was " +
                    "already given, and only a run you name yourself can proceed across such a " +
                    "change.\n\n" +
                    "If you are not a trusted author, your reply still needs a decision — set " +
                    $"\"{surface.ContextApprovalField}\" to any other value and back to " +
                    $"\"{surface.ApprovedOption}\", both moves, since approval is an instant and " +
                    "re-selecting the value it already holds moves nothing.", Name: "answer-on-issue", Url: url)
                : new WorkerOperatorAction(
                    "Answer on the issue — no CLI needed",
                    [url],
                    "1. Reply in a new comment on this issue with the clarification. Do not edit " +
                    "the description: that replaces what this paused session was already given, " +
                    "and only a run you name yourself can proceed across such a change.\n" +
                    $"2. Set \"{surface.ContextApprovalField}\" to any other value and back to " +
                    $"\"{surface.ApprovedOption}\" — both moves. Approval is an instant, so " +
                    "re-selecting the value it already holds moves nothing and your reply stays " +
                    "undecided.\n\n" +
                    "Your reply then reaches the agent as an addition to what it already holds, " +
                    "which any worker may carry to it.", Name: "answer-on-issue", Url: url));
            actions.Add(new WorkerOperatorAction(
                continuesAutomatically
                    ? $"Or start {agentLabel} yourself"
                    : $"Then start {agentLabel} again",
                [$"wrighty worker --item {id.Value} --yes"],
                $"Runs it now, reusing the recorded session. To keep it hands-off instead, set " +
                $"\"{surface.DispatchStateField}\" to \"{DispatchStates.Queued}\" and leave it: a " +
                "continuous worker takes the item once this claim lapses, and only on the host " +
                "that recorded the session.", Name: "continue-worker"));
        }
        else
        {
            actions.Add(new WorkerOperatorAction(
                "Edit the requirements in the web UI",
                ["wrighty web"],
                $"Open {id.Value}, then take over (or claim after expiry) and edit it. Choose Save " +
                $"and resume automatically to let a continuous worker continue it, Save and show " +
                $"manual {agentLabel} resume command under More actions to continue it yourself, " +
                "Finish when complete, or Archive to close it without more agent work.", Name: "open-item"));
            // Not --requeue. Rewriting the description supersedes the approved context the paused
            // session already holds, and a continuous worker refuses to resume a session across a
            // change nobody named the item to approve — so pairing the two queues a run that is
            // certain to be refused. Naming the item is what carries that judgement, so the
            // clarification and the run are two commands here rather than one. This backend has no
            // discussion to append to, so rewriting is the only way to clarify it.
            actions.Add(new WorkerOperatorAction(
                "Clarify the requirements, then continue the session yourself",
                [
                    $"wrighty edit {id.Value} --takeover --yes --body-file requirements.md",
                    $"wrighty worker --item {id.Value} --yes"
                ],
                "The first saves the clarification and ends human ownership. The second resumes " +
                "the recorded session: because you named the item, Wrighty proceeds despite the " +
                "changed description and reports that it did.", Name: "clarify-and-continue"));
        }

        var ownershipDescription = activeUntil is null
            ? "There is no active claimant to displace, so Wrighty acquires a human editing claim."
            : $"The current claim is active until {activeUntil:O}. edit --takeover works before or " +
              "after that time: while active, Wrighty asks you to confirm displacing the current " +
              "claimant; after expiry, it acquires a human editing claim without prompting. The " +
              "recorded local agent session is preserved in either case.";
        var editWarning = surface.HasDiscussion
            ? " Editing the description this way replaces what the session already holds, so only " +
              "a run you name for this item will proceed across it — prefer a comment above."
            : string.Empty;
        actions.Add(new WorkerOperatorAction(
            "Take the item over for editing",
            [
                $"wrighty edit {id.Value} --takeover",
                $"wrighty edit {id.Value} --takeover --yes --title \"Clear title\" " +
                "--body-file requirements.md"
            ],
            $"{ownershipDescription} The first command opens the title and body in VISUAL or " +
            "EDITOR. The second is the non-interactive form. Both retain the claim handle inside " +
            $"Wrighty.{editWarning}", Name: "clarify"));
        return actions;
    }

    public static WorkerOperatorAction ContinueWorker(WorkItemId id) => new(
        "Continue with a worker", [$"wrighty worker --item {id.Value} --yes"],
        "Process this item headlessly using the recorded continuation or directed handoff. " +
        "The worker revalidates claims, context, agent availability, and permissions before launch.",
        Name: "continue-worker");

    public static WorkerOperatorAction RetryNow(WorkItemId id) => new(
        "Retry now", [$"wrighty worker --item {id.Value} --yes"],
        "Explicitly override the timer and continue the recorded work now. This may consume provider usage.",
        Name: "retry-now");

    public static WorkerOperatorAction HandoffNow(WorkItemId id) => new(
        "Run the handoff now", [$"wrighty worker --item {id.Value} --yes"],
        "Start the directed agent as a new session in the retained workspace on the recording installation.",
        Name: "continue-worker");

    public static WorkerOperatorAction InspectRecovery(WorkItemId id) => new(
        "Inspect local recovery state", [$"wrighty get {id.Value}", "wrighty status"],
        "Read the current item, last run, dispatch decision, and local worker state.",
        Name: "inspect-recovery");

}

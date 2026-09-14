using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Actions;

public sealed record OperationalActionContext(
    TrackerConfig Config,
    WorkItemOperationalState State,
    DateTimeOffset ObservedAt,
    bool WorkspaceExists,
    ActionAvailability? WorkerAdmission = null,
    ActionAvailability? InteractiveAdmission = null,
    ApprovedContext.TrustedContinuationBudget? ContinuationBudget = null);

/// <summary>
/// Pure action policy over an observed snapshot. Launch checks are supplied by the owning runtime;
/// missing evidence fails closed for that action, without suppressing unrelated review guidance.
/// </summary>
public static class OperationalActionResolver
{
    private const string ConfirmationRequired = "required";

    public static OperationalActionDiscovery Resolve(OperationalActionContext context)
    {
        var (config, state) = (context.Config, context.State);
        var id = state.Item.Id;
        var surface = OperatorSurface.For(config, state.Item.Url);
        var guidance = OperationalActionGuidance.NeedsAttentionActions(
            id, state.Session?.Agent ?? "", surface,
            state.Claim.State == ClaimOwnershipState.Unclaimed ? null : state.Claim.ExpiresAt,
            context.ContinuationBudget);
        var review = surface.ItemUrl is { } url
            ? new WorkerOperatorAction("Review item", [], "Review the item in its source tracker.",
                Name: "open-item", Url: url)
            : new WorkerOperatorAction("Open item in the web console", ["wrighty web"],
                "Open the local web console and select this item.", Name: "open-item");
        List<OperationalAction> actions =
        [
            OperationalAction.FromGuidance(review, ActionAvailability.Available,
                surface.HasDiscussion ? "url" : "local-process", startsProcess: !surface.HasDiscussion)
        ];
        AddClarificationActions(context, surface, guidance, actions);
        AddBoardActions(context, actions);
        var workerAvailability = WorkerAvailability(context);
        if (state.OperationalStatus != OperationalStatuses.RetryScheduled)
            actions.Add(OperationalAction.FromGuidance(
                state.OperationalStatus == OperationalStatuses.HandoffQueued
                    ? OperationalActionGuidance.HandoffNow(id) : OperationalActionGuidance.ContinueWorker(id),
                workerAvailability, confirmation: ConfirmationRequired, startsProcess: true));
        actions.Add(OperationalAction.FromGuidance(
            new WorkerOperatorAction("Open recorded session", [$"wrighty resume-command {id.Value} --exec"],
                "Open the recorded vendor session interactively on this installation.", Name: "resume-session"),
            FirstBlocked(SessionAvailability(context), context.InteractiveAdmission ?? ActionAvailability.Unverified),
            "local-process", ConfirmationRequired, requiresTty: true, startsProcess: true));
        actions.Add(OperationalAction.FromGuidance(
            OperationalActionGuidance.RetryNow(id),
            state.OperationalStatus == OperationalStatuses.RetryScheduled
                ? workerAvailability : Block("RETRY_NOT_SCHEDULED", "No retry is scheduled for this item."),
            confirmation: ConfirmationRequired, startsProcess: true));
        actions.Add(OperationalAction.FromGuidance(OperationalActionGuidance.InspectRecovery(id),
            ActionAvailability.Available));
        // Only a clarification pause determines a next action without choosing an operator policy.
        // Scheduled retries/handoffs remain deferred, never recommendations to override a timer.
        var recommended = state.OperationalStatus == OperationalStatuses.NeedsAttention
            ? actions.FirstOrDefault(value => value.Availability == "available" &&
                value.Name == (surface.HasDiscussion ? "answer-on-issue" : "clarify"))?.Name
            : null;
        return new(id.Value, context.ObservedAt, recommended,
            actions.Select(value => value with { Recommended = value.Name == recommended })
                .OrderByDescending(value => value.Recommended).ToArray());
    }

    private static void AddClarificationActions(
        OperationalActionContext context, OperatorSurface surface,
        IReadOnlyList<WorkerOperatorAction> guidance, List<OperationalAction> actions)
    {
        var state = context.State;
        var editable = EditAvailability(state);
        var clarify = OperationalAction.FromGuidance(
            guidance.Single(value => value.Name == "clarify"), editable,
            confirmation: state.Claim.State == ClaimOwnershipState.Unclaimed ? "none" : ConfirmationRequired,
            requiresTty: true);
        actions.Add(clarify);
        if (state.OperationalStatus == OperationalStatuses.NeedsAttention)
        {
            if (surface.HasDiscussion)
            {
                var answer = guidance.Single(value => value.Name == "answer-on-issue");
                actions.Add(OperationalAction.FromGuidance(answer with { Commands = [] },
                    state.Item.Archived ? Block("ITEM_ARCHIVED", "The item is archived.") : ActionAvailability.Available,
                    "url", ConfirmationRequired));
            }
            else
            {
                actions.Add(OperationalAction.FromGuidance(
                    guidance.Single(value => value.Name == "clarify-and-continue"),
                    FirstBlocked(editable, WorkerAvailability(context)),
                    "manual-steps", ConfirmationRequired, startsProcess: true));
            }
        }
    }

    private static void AddBoardActions(OperationalActionContext context, List<OperationalAction> actions)
    {
        var (config, state) = (context.Config, context.State);
        var common = BoardAvailability(config, state);
        var untouched = FirstBlocked(common, UntouchedAvailability(state));
        var queue = FirstBlocked(untouched,
            Matches(state.Item.Status, config.DefaultPickFrom) ||
            Matches(state.Item.Status, config.DefaultPickTo) || Matches(state.Item.Status, config.DefaultFinishTo)
                ? Block("WORKFLOW_STATE_INVALID", "Only an untouched backlog item can be queued.")
                : ActionAvailability.Available);
        var backlog = WorkflowStatusPolicy.InferBacklogStatus(config, config.LocalMarkdown?.Statuses ?? []);
        var sendBack = FirstBlocked(untouched, SendBackAvailability(config, state, backlog));
        actions.Add(BoardAction("queue", "Queue", "Move to the configured worker queue. " +
            QueueConsequence(config, true), queue));
        actions.Add(BoardAction("send-back", "Send back", $"Move back to {backlog ?? "the configured backlog"}. " +
            QueueConsequence(config, false), sendBack));
        var resume = FirstBlocked(common, FirstBlocked(SessionAvailability(context), ResumeQueueAvailability(config, state)));
        actions.Add(BoardAction("resume", "Resume", "Queue the recorded session for a continuous worker. " +
            "This does not start a worker or change the requirements.", resume));
    }

    private static ActionAvailability BoardAvailability(TrackerConfig config, WorkItemOperationalState state)
    {
        if (!string.Equals(config.Backend, "local-markdown", StringComparison.OrdinalIgnoreCase))
            return Block("NOT_SUPPORTED", "This board operation is available for Local Markdown.");
        return state.Item.Archived ? Block("ITEM_ARCHIVED", "The item is archived.") : ActionAvailability.Available;
    }

    private static ActionAvailability UntouchedAvailability(WorkItemOperationalState state)
    {
        if (state.Claim.State != ClaimOwnershipState.Unclaimed)
            return Block("CLAIM_HELD", "Release the existing claim before moving this item.");
        return state.Item.DispatchState is not null
            ? Block("WORKER_RECOVERY_PENDING", "The item has dispatch/recovery state.")
            : ActionAvailability.Available;
    }

    private static ActionAvailability SendBackAvailability(
        TrackerConfig config, WorkItemOperationalState state, string? backlog)
    {
        if (!Matches(state.Item.Status, config.DefaultPickFrom))
            return Block("WORKFLOW_STATE_INVALID", "The item is not in the worker queue.");
        return backlog is null ? Block("STATUS_UNAVAILABLE", "No backlog status is configured.") : ActionAvailability.Available;
    }

    private static ActionAvailability ResumeQueueAvailability(TrackerConfig config, WorkItemOperationalState state)
    {
        if (state.OperationalStatus != OperationalStatuses.NeedsAttention)
            return Block("WORKER_ITEM_NOT_PAUSED", "The item is not waiting for attention.");
        return !state.Item.AutomaticExecutionAllowed || !Matches(state.Item.Status, config.DefaultPickTo)
            ? Block("WORKER_ITEM_INELIGIBLE", "Resuming through the queue requires automatic execution and the active-work status.")
            : ActionAvailability.Available;
    }

    private static string QueueConsequence(TrackerConfig config, bool queue)
    {
        if (!config.EffectiveWorker.UseWorkerQueue)
            return "Worker-queue authorization is disabled; execution policy remains independent.";
        return queue ? "This authorizes automatic processing; it does not start a worker."
            : "This revokes worker-queue authorization.";
    }

    private static OperationalAction BoardAction(string name, string title, string description,
        ActionAvailability availability) => OperationalAction.FromGuidance(
            new WorkerOperatorAction(title, [], description + " Use the corresponding Board action in wrighty web.",
                Name: name), availability, confirmation: ConfirmationRequired);

    private static ActionAvailability EditAvailability(WorkItemOperationalState state)
    {
        if (state.Item.Archived)
            return Block("ITEM_ARCHIVED", "The item is archived.");
        return state.Claim.State != ClaimOwnershipState.Unclaimed && !state.Claim.TakeoverAvailable
            ? Block("CLAIM_HELD", "The current claim cannot be taken over here.")
            : ActionAvailability.Available;
    }

    public static ActionAvailability SessionAvailability(OperationalActionContext context)
    {
        var state = context.State;
        if (state.Session is not { IsComplete: true } session)
            return Block("RESUME_ADDRESS_UNAVAILABLE", "There is no complete recorded session.");
        if (!session.FromCurrentInstallation)
            return Block("RESUME_ADDRESS_NOT_LOCAL", "The session belongs to another installation.");
        if (state.Claim.State == ClaimOwnershipState.HeldByOther)
            return Block("CLAIM_NOT_OWNER", "Another installation owns the claim.");
        if (state.OperationalStatus is OperationalStatuses.AgentActive or OperationalStatuses.WorkerPreparing
            or OperationalStatuses.AutomationActive or OperationalStatuses.HumanEditing)
            return Block("CLAIM_HELD", "An active claimant is using this item.");
        return context.WorkspaceExists ? ActionAvailability.Available
            : Block("RESUME_WORKTREE_ABSENT", "The recorded workspace is unavailable here.");
    }

    private static ActionAvailability WorkerAvailability(OperationalActionContext context) =>
        context.State.Item.Archived || Matches(context.State.Item.Status, context.Config.DefaultFinishTo)
            ? Block("WORKER_ITEM_TERMINAL", "Completed or archived work cannot be queued for implementation.")
            : FirstBlocked(SessionAvailability(context), context.WorkerAdmission ?? ActionAvailability.Unverified);

    private static ActionAvailability FirstBlocked(ActionAvailability first, ActionAvailability second) =>
        first.Code is null ? second : first;
    private static ActionAvailability Block(string code, string reason) => new(code, reason);
    private static bool Matches(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

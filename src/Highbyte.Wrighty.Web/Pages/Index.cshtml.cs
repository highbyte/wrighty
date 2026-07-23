using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Web.Markdown;
using Highbyte.Wrighty.Workers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Highbyte.Wrighty.Web.Pages;

public sealed class IndexModel(
    TrackerService tracker,
    WebApplicationState state,
    MarkdownRenderer markdown,
    IWorkspaceInventory workspaceInventory) : PageModel
{
    private const int MaximumBodyLength = 1_000_000;
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public async Task<IActionResult> OnGetBoardAsync(string? scope, CancellationToken cancellationToken)
    {
        var archiveScope = ParseScope(scope);
        try
        {
            var snapshot = await tracker.GetDashboardAsync(state.Config, archiveScope, cancellationToken);
            var responseRevision = ResponseRevision(snapshot.Revision, archiveScope);
            var etag = $"\"{responseRevision}\"";
            if (Request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
            {
                // htmx treats 204 as an explicit no-swap response. Some browser/htmx
                // combinations process an empty 304 as replaceable content.
                return StatusCode(StatusCodes.Status204NoContent);
            }

            Response.Headers.ETag = etag;
            return Partial("Shared/_Board", Board(snapshot, archiveScope, responseRevision));
        }
        catch (TrackerException exception)
        {
            Response.StatusCode = Status(exception);
            WebDiagnostics.RetainFailure(HttpContext, exception.Code, exception);
            return Partial("Shared/_Board", new BoardPageModel([], [], [], [], scope ?? "active", "error", exception.Code, SafeMessage(exception)));
        }
    }

    public async Task<IActionResult> OnGetItemAsync(string id, CancellationToken cancellationToken)
    {
        try { return Partial("Shared/_ItemDetail", await Item(id, cancellationToken: cancellationToken)); }
        catch (TrackerException exception) { return KnownError(exception); }
    }

    public IActionResult OnGetCreate()
    {
        var local = state.Config.LocalMarkdown
            ?? throw new TrackerException(
                "WEB_BACKEND_UNSUPPORTED",
                "Web creation is supported only by the Local Markdown backend.",
                2);
        return Partial("Shared/_CreateForm", new CreateItemPageModel(
            string.Empty,
            string.Empty,
            state.Config.DefaultPickFrom,
            null,
            false,
            null,
            CreationAttempt.NormalizeOrCreate(null),
            local.Statuses,
            local.Priorities));
    }

    public async Task<IActionResult> OnPostCreateAsync(
        string title,
        string body,
        string status,
        string? priority,
        bool automationEligible,
        string? preferredAgent,
        string creationAttemptId,
        CancellationToken cancellationToken)
    {
        var local = state.Config.LocalMarkdown
            ?? throw new TrackerException(
                "WEB_BACKEND_UNSUPPORTED",
                "Web creation is supported only by the Local Markdown backend.",
                2);
        var draft = new CreateItemPageModel(
            title,
            body,
            status,
            string.IsNullOrWhiteSpace(priority) ? null : priority,
            automationEligible,
            string.IsNullOrWhiteSpace(preferredAgent) ? null : preferredAgent,
            creationAttemptId,
            local.Statuses,
            local.Priorities);

        if (body.Length > MaximumBodyLength)
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return Partial("Shared/_CreateForm", draft with
            {
                ErrorCode = "ARGUMENT_INVALID",
                ErrorMessage = "Markdown body must not exceed 1,000,000 characters."
            });
        }

        try
        {
            var result = await tracker.CreateAsync(
                state.Config,
                new CreateWorkItemRequest(
                    title,
                    body,
                    status,
                    draft.Priority,
                    AutomationEligible: automationEligible,
                    PreferredAgent: draft.PreferredAgent),
                creationAttemptId,
                cancellationToken);
            Response.Headers["HX-Trigger"] = "wrighty:refresh";
            return Partial(
                "Shared/_ItemDetail",
                await Item(
                    result.Id.Value,
                    result.Disposition == CreateDisposition.Resumed
                        ? "Creation resumed without allocating a duplicate item."
                        : "Item created. Worker processing was not started.",
                    cancellationToken: cancellationToken));
        }
        catch (TrackerException exception)
        {
            Response.StatusCode = Status(exception);
            WebDiagnostics.RetainFailure(HttpContext, exception.Code, exception);
            return Partial("Shared/_CreateForm", draft with
            {
                ErrorCode = exception.Code,
                ErrorMessage = SafeMessage(exception)
            });
        }
    }

    public async Task<IActionResult> OnGetEditAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureWebMutationAllowed(id, cancellationToken);
            return Partial("Shared/_EditForm", (await Item(id, editing: true, cancellationToken: cancellationToken)) with { Editing = true });
        }
        catch (TrackerException exception) { return await ItemError(id, exception, cancellationToken); }
    }

    public async Task<IActionResult> OnPostClaimAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var session = await tracker.GetAgentSessionAsync(
                state.Config, resolved, cancellationToken);
            var notice = "Claimed by this Wrighty installation.";
            ClaimResult result;
            if (session is { IsComplete: true, FromCurrentInstallation: true })
            {
                // Recover the durable address under an agent claim first, then rotate it to the
                // human web claimant. A direct human acquisition cannot carry agent metadata.
                var recoveryContext = new AgentExecutionContext(
                    session.AgentType,
                    session.SessionId,
                    AgentContextSource.ExplicitOption,
                    ClaimantKind: ClaimantKind.Agent,
                    ClaimantId: $"agent:web-recover:{Guid.NewGuid():N}");
                var recovered = await tracker.ClaimAsync(
                    state.Config, resolved, recoveryContext, cancellationToken);
                recovered = await tracker.RenewClaimAsync(
                    state.Config,
                    resolved,
                    new ClaimHandle(recoveryContext, recovered.ClaimToken),
                    session.WorkspacePath,
                    session.SessionId,
                    cancellationToken);
                result = await tracker.TakeoverAsync(
                    state.Config,
                    resolved,
                    state.ClaimantContext,
                    recovered.ClaimToken,
                    cancellationToken);
                notice = "Claimed for editing. The recorded agent session was preserved.";
            }
            else
            {
                result = await tracker.ClaimAsync(
                    state.Config, resolved, state.ClaimantContext, cancellationToken);
            }
            state.Retain(resolved.Value, result);
            return Partial("Shared/_EditForm", await Item(
                id, notice, editing: true, cancellationToken: cancellationToken));
        }
        catch (TrackerException exception)
        {
            try
            {
                Response.StatusCode = Status(exception);
                return Partial("Shared/_ItemDetail", await Item(id, error: exception, cancellationToken: cancellationToken));
            }
            catch (TrackerException) { return KnownError(exception); }
        }
    }

    public async Task<IActionResult> OnPostSaveAsync(
        string id,
        string expectedRevision,
        string expectedClaimGeneration,
        string title,
        string body,
        string status,
        string? priority,
        bool automationEligible,
        string? preferredAgent,
        string action,
        CancellationToken cancellationToken)
    {
        ClaimHandle handle;
        try
        {
            handle = await PrepareSaveAsync(
                id, expectedClaimGeneration, cancellationToken);
        }
        catch (TrackerException exception) { return await ItemError(id, exception, cancellationToken); }

        if (string.Equals(action, "release", StringComparison.Ordinal))
            return await ReleaseDraftAsync(id, handle, cancellationToken);

        if (body.Length > MaximumBodyLength)
        {
            var tooLarge = new TrackerException("ARGUMENT_INVALID", "Markdown body must not exceed 1,000,000 characters.", 2);
            Response.StatusCode = 400;
            return Partial("Shared/_EditForm", await Draft(
                id, title, body, status, priority, automationEligible, preferredAgent,
                tooLarge, cancellationToken));
        }

        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var before = await tracker.GetAsync(state.Config, resolved, cancellationToken);
            var retryScheduled = string.Equals(
                before.WorkerState,
                WorkerDispatchStates.RetryScheduled,
                StringComparison.OrdinalIgnoreCase);
            if (retryScheduled &&
                !string.Equals(
                    before.PreferredAgent,
                    string.IsNullOrWhiteSpace(preferredAgent) ? null : preferredAgent,
                    StringComparison.OrdinalIgnoreCase))
            {
                var session = await tracker.GetAgentSessionAsync(
                    state.Config, resolved, cancellationToken);
                throw new TrackerException(
                    "AGENT_HANDOFF_REQUIRED",
                    $"The scheduled retry belongs to {session?.AgentType ?? "the recorded agent"}. " +
                    "Changing the preferred agent requires an explicit cross-agent handoff, " +
                    "which is not available yet.",
                    2);
            }
            var handbackClaim = await LoadHandbackClaimAsync(
                resolved, action, cancellationToken);
            var cancelScheduledRetry =
                retryScheduled &&
                (!automationEligible ||
                 !string.Equals(
                     status,
                     state.Config.DefaultPickTo,
                     StringComparison.OrdinalIgnoreCase));
            var patch = new WorkItemPatch(
                OptionalValue<string>.From(title),
                OptionalValue<string>.From(body),
                OptionalValue<string>.From(status),
                OptionalValue<string?>.From(string.IsNullOrWhiteSpace(priority) ? null : priority),
                AutomationEligible: OptionalValue<bool>.From(automationEligible),
                PreferredAgent: OptionalValue<string?>.From(
                    string.IsNullOrWhiteSpace(preferredAgent) ? null : preferredAgent),
                WorkerState: string.Equals(action, "save-handback", StringComparison.Ordinal) ||
                             cancelScheduledRetry
                    ? OptionalValue<string?>.From(null)
                    : OptionalValue<string?>.Unspecified);
            var updated = await tracker.UpdateAsync(
                state.Config, resolved, patch, expectedRevision, handle, cancellationToken);
            if (retryScheduled &&
                !string.Equals(
                    updated.Item.WorkerState,
                    WorkerDispatchStates.RetryScheduled,
                    StringComparison.OrdinalIgnoreCase))
            {
                await tracker.ClearDeferredDispatchAsync(
                    state.Config, resolved, cancellationToken);
            }
            var notice = await CompleteSaveActionAsync(
                resolved, action, handle, handbackClaim, cancellationToken);
            return Partial("Shared/_ItemDetail", await Item(id, notice, cancellationToken: cancellationToken));
        }
        catch (TrackerException exception) when (exception.Code == "UPDATE_CONFLICT")
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            var current = await Item(id, cancellationToken: cancellationToken);
            return Partial("Shared/_Conflict", new ConflictPageModel(
                current, title, body, status, priority, automationEligible, preferredAgent));
        }
        catch (TrackerException exception)
        {
            Response.StatusCode = Status(exception);
            try
            {
                return Partial("Shared/_EditForm", await Draft(
                    id, title, body, status, priority, automationEligible, preferredAgent,
                    exception, cancellationToken));
            }
            catch (TrackerException) { return KnownError(exception); }
        }
    }

    private async Task<ClaimHandle> PrepareSaveAsync(
        string id,
        string expectedClaimGeneration,
        CancellationToken cancellationToken)
    {
        await EnsureWebMutationAllowed(id, cancellationToken);
        var handle = RequiredWebHandle(id);
        var resolved = tracker.ResolveId(state.Config, id);
        if (!string.Equals(
                state.Generation(resolved.Value),
                expectedClaimGeneration,
                StringComparison.Ordinal))
            throw new TrackerException(
                "WEB_CLAIM_GENERATION_STALE",
                "This editor was opened under an older claim generation.",
                6);
        return handle;
    }

    private async Task<IActionResult> ReleaseDraftAsync(
        string id,
        ClaimHandle handle,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var preservedRetry = await ReleaseFromWebAsync(
                resolved, handle, cancellationToken);
            state.Forget(resolved.Value);
            return Partial(
                "Shared/_ItemDetail",
                await Item(
                    id,
                    preservedRetry
                        ? "Draft discarded, claim released, and scheduled retry preserved."
                        : "Draft discarded and claim released.",
                    cancellationToken: cancellationToken));
        }
        catch (TrackerException exception)
        {
            return KnownError(exception);
        }
    }

    private async Task<WorkItemClaimSummary?> LoadHandbackClaimAsync(
        WorkItemId id,
        string action,
        CancellationToken cancellationToken)
    {
        if (action is not ("save-handback" or "save-queue"))
            return null;
        var claim = (await tracker.GetEditableAsync(
            state.Config, id, cancellationToken)).Claim;
        if (!HasResumeAddress(claim))
            throw new TrackerException(
                "RESUME_ADDRESS_UNAVAILABLE",
                "This claim does not have a complete agent session address to hand back.",
                5);
        return claim;
    }

    private async Task<string> CompleteSaveActionAsync(
        WorkItemId id,
        string action,
        ClaimHandle handle,
        WorkItemClaimSummary? handbackClaim,
        CancellationToken cancellationToken)
    {
        if (action == "save-release")
        {
            var preservedRetry = await ReleaseFromWebAsync(
                id, handle, cancellationToken);
            state.Forget(id.Value);
            return preservedRetry
                ? "Saved and released. The scheduled retry was preserved."
                : "Saved and released.";
        }
        if (action == "finish")
        {
            await tracker.FinishAsync(state.Config, id, null, handle, cancellationToken);
            await tracker.ClearDeferredDispatchAsync(
                state.Config, id, cancellationToken);
            state.Forget(id.Value);
            return "Saved and finished.";
        }
        if (action == "save-queue")
        {
            await tracker.RequeueAsync(state.Config, id, handle, cancellationToken);
            await tracker.ClearDeferredDispatchAsync(
                state.Config, id, cancellationToken);
            state.Forget(id.Value);
            return "Saved and queued. A continuous worker can now resume the recorded session.";
        }
        if (handbackClaim is null)
            return "Saved. The claim remains active.";
        await tracker.ClearDeferredDispatchAsync(
            state.Config, id, cancellationToken);
        return await HandBackAsync(id, handle, handbackClaim, cancellationToken);
    }

    private async Task<bool> ReleaseFromWebAsync(
        WorkItemId id,
        ClaimHandle handle,
        CancellationToken cancellationToken)
    {
        var item = await tracker.GetAsync(state.Config, id, cancellationToken);
        var preserveRetry = string.Equals(
            item.WorkerState,
            WorkerDispatchStates.RetryScheduled,
            StringComparison.OrdinalIgnoreCase);
        if (preserveRetry)
        {
            await tracker.ReleasePreservingWorkerStateAsync(
                state.Config, id, handle, cancellationToken);
            return true;
        }

        await tracker.ReleaseAsync(
            state.Config, id, handle, false, cancellationToken);
        await tracker.ClearDeferredDispatchAsync(
            state.Config, id, cancellationToken);
        return false;
    }

    private async Task<string> HandBackAsync(
        WorkItemId id,
        ClaimHandle handle,
        WorkItemClaimSummary claim,
        CancellationToken cancellationToken)
    {
        var claimantContext = new AgentExecutionContext(
            claim.AgentType,
            claim.SessionId,
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: $"agent:web-handback:{Guid.NewGuid():N}");
        var result = await tracker.TakeoverAsync(
            state.Config, id, claimantContext, handle.ClaimToken, cancellationToken);
        state.Retain(id.Value, result, claimantContext);
        return $"Saved and handed back to {RecordedAgentTypeLabel(claim) ?? "the agent"}.";
    }

    public async Task<IActionResult> OnPostReleaseAsync(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            await EnsureWebMutationAllowed(id, cancellationToken);
            var resolved = tracker.ResolveId(state.Config, id);
            var preservedRetry = await ReleaseFromWebAsync(
                resolved, RequiredWebHandle(id), cancellationToken);
            state.Forget(resolved.Value);
            return Partial(
                "Shared/_ItemDetail",
                await Item(
                    id,
                    preservedRetry
                        ? "Released. The scheduled retry was preserved."
                        : "Released.",
                    cancellationToken: cancellationToken));
        }
        catch (TrackerException exception)
        {
            return await ItemError(id, exception, cancellationToken);
        }
    }

    public Task<IActionResult> OnPostArchiveAsync(string id, CancellationToken cancellationToken) =>
        Mutate(id, async resolved => { await tracker.ArchiveAsync(state.Config, resolved, RequiredWebHandle(id), cancellationToken); state.Forget(resolved.Value); }, "Archived.", cancellationToken, protectNonHumanClaim: true);

    // Archives an unclaimed item in one step: archiving requires an owned claim, so acquire a human
    // web claim, then archive with it. The archive's session preservation is address-only, so this
    // human claim (which carries no workspace address) never clobbers the recorded agent session.
    public Task<IActionResult> OnPostClaimAndArchiveAsync(string id, CancellationToken cancellationToken) =>
        Mutate(id, async resolved =>
        {
            var claim = await tracker.ClaimAsync(state.Config, resolved, state.ClaimantContext, cancellationToken);
            state.Retain(resolved.Value, claim);
            var handle = new ClaimHandle(state.ClaimantContext, claim.ClaimToken);
            try
            {
                await tracker.ArchiveAsync(state.Config, resolved, handle, cancellationToken);
            }
            catch (TrackerException)
            {
                // Never strand the just-acquired claim if archiving fails; release it best-effort.
                try { await tracker.ReleaseAsync(state.Config, resolved, handle, false, cancellationToken); }
                catch (TrackerException) { /* best effort — surface the original archive error */ }
                state.Forget(resolved.Value);
                throw;
            }
            state.Forget(resolved.Value);
        }, "Archived.", cancellationToken);

    public Task<IActionResult> OnPostUnarchiveAsync(string id, CancellationToken cancellationToken) =>
        Mutate(id, async resolved => await tracker.UnarchiveAsync(state.Config, resolved, cancellationToken), "Restored to the active dashboard.", cancellationToken);

    public async Task<IActionResult> OnPostTakeoverAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var result = await tracker.TakeoverAsync(state.Config, resolved, state.ClaimantContext, null, cancellationToken);
            state.Retain(resolved.Value, result);
            return Partial("Shared/_EditForm", await Item(id,
                "Takeover complete. The previous claimant is fenced from later Wrighty mutations. " +
                "Save keeps human ownership. Use Save and hand back to rotate the claim to the " +
                "recorded agent before resuming it. " +
                "An operation already holding the store lock may have finished first.",
                editing: true, cancellationToken: cancellationToken));
        }
        catch (TrackerException exception) { return await ItemError(id, exception, cancellationToken); }
    }

    public async Task<IActionResult> OnPostOverrideReleaseAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            await tracker.ReleaseAsync(state.Config, resolved,
                new ClaimHandle(state.ClaimantContext, null), true, cancellationToken);
            state.Forget(resolved.Value);
            return Partial("Shared/_ItemDetail", await Item(id, "Existing claim released.", cancellationToken: cancellationToken));
        }
        catch (TrackerException exception) { return await ItemError(id, exception, cancellationToken); }
    }

    public async Task<IActionResult> OnPostQueueForWorkerAsync(
        string id,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            await tracker.QueuePausedAsync(state.Config, resolved, cancellationToken);
            state.Forget(resolved.Value);
            Response.Headers["HX-Trigger"] = "wrighty:refresh";
            return Partial(
                "Shared/_ItemDetail",
                await Item(
                    id,
                    "Queued. A continuous worker can now resume the recorded session.",
                    cancellationToken: cancellationToken));
        }
        catch (TrackerException exception)
        {
            return await ItemError(id, exception, cancellationToken);
        }
    }

    private async Task<IActionResult> Mutate(
        string id,
        Func<WorkItemId, Task> operation,
        string notice,
        CancellationToken cancellationToken,
        bool protectNonHumanClaim = false)
    {
        try
        {
            if (protectNonHumanClaim)
            {
                await EnsureWebMutationAllowed(id, cancellationToken);
            }
            await operation(tracker.ResolveId(state.Config, id));
            return Partial("Shared/_ItemDetail", await Item(id, notice, cancellationToken: cancellationToken));
        }
        catch (TrackerException exception)
        {
            try
            {
                Response.StatusCode = Status(exception);
                return Partial("Shared/_ItemDetail", await Item(id, error: exception, cancellationToken: cancellationToken));
            }
            catch (TrackerException) { return KnownError(exception); }
        }
    }

    private async Task EnsureWebMutationAllowed(string id, CancellationToken cancellationToken)
    {
        var editable = await tracker.GetEditableAsync(
            state.Config,
            tracker.ResolveId(state.Config, id),
            cancellationToken);
        if (IsExactWebClaim(editable.Claim) && state.TryHandle(editable.Item.Id.Value, out _))
        {
            return;
        }

        var claimant = AgentTypeLabel(editable.Claim) ?? ClaimantKindLabel(editable.Claim) ?? "another claimant";
        throw new TrackerException(
            "CLAIM_STALE",
            $"This item is claimed by {claimant}. Take over explicitly before editing.",
            7);
    }

    private async Task<IActionResult> ItemError(
        string id,
        TrackerException exception,
        CancellationToken cancellationToken)
    {
        WebDiagnostics.RetainFailure(HttpContext, exception.Code, exception);
        try
        {
            Response.StatusCode = Status(exception);
            return Partial("Shared/_ItemDetail", await Item(id, error: exception, cancellationToken: cancellationToken));
        }
        catch (TrackerException)
        {
            return KnownError(exception);
        }
    }

    private IActionResult KnownError(TrackerException exception)
    {
        WebDiagnostics.RetainFailure(HttpContext, exception.Code, exception);
        Response.StatusCode = Status(exception);
        return Partial("Shared/_Error", new WebErrorModel(exception.Code, SafeMessage(exception)));
    }

    private string SafeMessage(TrackerException exception)
    {
        var message = exception.Message;
        if (state.Config.SourcePath is { } sourcePath)
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            if (!string.IsNullOrEmpty(root))
            {
                message = message.Replace(root, "<tracker>", StringComparison.Ordinal);
            }
        }

        return message;
    }

    private async Task<ItemPageModel> Draft(
        string id,
        string title,
        string body,
        string status,
        string? priority,
        bool automationEligible,
        string? preferredAgent,
        TrackerException error,
        CancellationToken cancellationToken)
    {
        var current = await Item(id, editing: true, cancellationToken: cancellationToken);
        return current with
        {
            Title = title,
            Body = body,
            Status = status,
            Priority = priority,
            AutomationEligible = automationEligible,
            PreferredAgent = string.IsNullOrWhiteSpace(preferredAgent) ? null : preferredAgent,
            ErrorCode = error.Code,
            ErrorMessage = SafeMessage(error),
            Editing = true
        };
    }

    private async Task<ItemPageModel> Item(
        string id,
        string? notice = null,
        TrackerException? error = null,
        bool editing = false,
        CancellationToken cancellationToken = default)
    {
        var resolvedId = tracker.ResolveId(state.Config, id);
        var editable = await tracker.GetEditableAsync(state.Config, resolvedId, cancellationToken);
        var item = editable.Item;
        // The durable session record (survives claim release, and carries the captured run outcome)
        // is the authority for the "Last run" block and the completed-vs-paused activity label.
        var operational = await tracker.GetOperationalAsync(state.Config, resolvedId, cancellationToken);
        var durableSession = operational.Session;
        var workspaceView = await WorkspaceViewAsync(durableSession, cancellationToken);
        var claimantKindLabel = ClaimantKindLabel(editable.Claim);
        var agentTypeLabel = AgentTypeLabel(editable.Claim);
        var webMutationProtected = IsWebMutationProtected(editable.Claim);
        var session = durableSession ?? (HasResumeAddress(editable.Claim)
            ? new AgentSessionRecord(
                editable.Claim.AgentType,
                editable.Claim.SessionId,
                editable.Claim.WorkspacePath,
                editable.Claim.ExpiresAt ?? DateTimeOffset.MinValue,
                editable.Claim.State != ClaimOwnershipState.HeldByOther)
            : null);
        var activity = WorkItemActivities.Resolve(
            item,
            editable.Claim,
            session,
            state.Config.DefaultPickFrom,
            state.Config.DefaultFinishTo);
        var lastRun = LastRunView.From(session);
        var canQueueForWorker =
            !item.Archived &&
            activity == WorkItemActivities.NeedsAttention &&
            editable.Claim.State != ClaimOwnershipState.HeldByOther &&
            item.AutomationEligible &&
            string.Equals(item.Status, state.Config.DefaultPickTo,
                StringComparison.OrdinalIgnoreCase) &&
            session is { IsComplete: true, FromCurrentInstallation: true };
        return new ItemPageModel(
            item.Id.Value,
            tracker.FormatShort(state.Config, item.Id),
            item.Title,
            item.Body,
            item.Status,
            item.Priority,
            item.Archived,
            editable.Revision,
            editable.Claim.State,
            ClaimLabel(editable.Claim),
            claimantKindLabel,
            agentTypeLabel,
            webMutationProtected,
            webMutationProtected
                ? activity == WorkItemActivities.NeedsAttention
                    ? $"{agentTypeLabel ?? claimantKindLabel ?? "The agent"} has paused and its headless process has exited. The retained claim is ownership and fencing metadata for the recorded session."
                    : $"This item is claimed by {agentTypeLabel ?? claimantKindLabel ?? "another claimant"}. Takeover does not stop that process; it fences later cooperating Wrighty mutations. An operation already executing may finish first."
                : null,
            editable.Claim.TakeoverAvailable && editable.Claim.State == ClaimOwnershipState.OwnedByCurrent,
            editable.Claim.ClaimantId,
            state.Generation(item.Id.Value),
            HasResumeAddress(editable.Claim),
            canQueueForWorker,
            BuildResumeCommand(item.Id, editable.Claim),
            BuildWorkerResumeCommand(item.Id, editable.Claim),
            BuildResumePrompt(item.Id, editable.Claim),
            HasResumeAddress(editable.Claim) ? RecordedAgentTypeLabel(editable.Claim) : null,
            item.AutomationEligible,
            item.PreferredAgent,
            item.WorkerState,
            activity,
            state.Config.LocalMarkdown?.Statuses ?? [],
            state.Config.LocalMarkdown?.Priorities ?? [],
            markdown.Render(item.Body),
            notice,
            error?.Code,
            error is null ? null : SafeMessage(error),
            editing,
            item.EffectiveFields.ToDictionary(
                pair => pair.Key,
                pair => FormatFieldValue(pair.Value),
                StringComparer.Ordinal),
            item.RawFrontmatter,
            workspaceView,
            lastRun,
            session?.Dispatch);
    }

    // Reads the durable recorded session (which survives claim release, unlike editable.Claim) and,
    // when a worktree is recorded, safely calculates its git state for display. The probe applies
    // its own timeout and never throws for git failures, so a missing or unreadable worktree
    // degrades to an "unavailable" message instead of breaking the item view.
    private async Task<WorkspaceView?> WorkspaceViewAsync(
        AgentSessionRecord? session,
        CancellationToken cancellationToken)
    {
        if (session?.WorkspacePath is not { } workspacePath ||
            string.IsNullOrWhiteSpace(workspacePath))
        {
            return null;
        }

        var repositoryRoot = state.Config.SourcePath is { } sourcePath
            ? Path.GetDirectoryName(Path.GetFullPath(sourcePath)) ?? Directory.GetCurrentDirectory()
            : Directory.GetCurrentDirectory();
        var status = await workspaceInventory.GetStatusAsync(
            repositoryRoot, workspacePath, session.Branch, cancellationToken);
        // Completion commands are only meaningful when the git state could actually be read and a
        // branch is recorded; otherwise the workspace-status line already explains why it is
        // unavailable. The integrate step reads the current worker.completion.integration setting
        // (a repo preference, deliberately not snapshotted onto the item), matching the CLI/skill.
        var completionActions = status is { IsAvailable: true, Status: { } gitStatus }
            && session.Branch is { } branch
            ? WorkerCompletionGuidance.ForCompletedWorktree(
                workspacePath,
                branch,
                state.Config.Worker?.Completion?.Integration,
                gitStatus.Dirty,
                gitStatus.MergedIntoHead)
            : [];
        return new WorkspaceView(
            workspacePath,
            session.Branch,
            status.IsAvailable,
            status.Status?.Dirty ?? false,
            status.Status?.MergedIntoHead ?? false,
            status.Unavailable,
            status.WorktreeAbsent,
            completionActions);
    }

    private string? BuildResumeCommand(WorkItemId id, WorkItemClaimSummary claim)
    {
        if (!HasResumeAddress(claim) ||
            ClaimantKinds.FromStorageValue(claim.ClaimantKind, claim.AgentType) != ClaimantKind.Agent ||
            !state.TryHandle(id.Value, out var handle) ||
            !string.Equals(handle.ClaimantId, claim.ClaimantId, StringComparison.Ordinal) ||
            handle.ClaimToken is null)
        {
            return null;
        }

        IAgentAdapter adapter = claim.AgentType switch
        {
            "claude" => new ClaudeAgentAdapter(),
            "codex" => new CodexAgentAdapter(),
            "copilot" => new CopilotAgentAdapter(),
            _ => throw new TrackerException(
                "AGENT_UNSUPPORTED",
                $"Unsupported recorded agent '{claim.AgentType}'.",
                3)
        };
        var environment = TrackerEnvironment();
        environment["WRIGHTY_CLAIMANT_ID"] = handle.ClaimantId;
        environment["WRIGHTY_CLAIM_TOKEN"] = handle.ClaimToken;
        return adapter.BuildInteractiveCommand(
            new SessionHandle(claim.SessionId!),
            new Workspace(claim.WorkspacePath!),
            environment);
    }

    private string? BuildWorkerResumeCommand(WorkItemId id, WorkItemClaimSummary claim)
    {
        if (!HasResumeAddress(claim) ||
            !state.TryHandle(id.Value, out var handle) ||
            !string.Equals(handle.ClaimantId, claim.ClaimantId, StringComparison.Ordinal) ||
            handle.ClaimToken is null)
        {
            return null;
        }

        var configPrefix = string.IsNullOrWhiteSpace(state.Config.SourcePath)
            ? string.Empty
            : $"{TrackerConfigLoader.ConfigPathEnvironmentVariable}=" +
              $"{ShellQuote(Path.GetFullPath(state.Config.SourcePath))} ";
        return $"cd {ShellQuote(claim.WorkspacePath!)} && " +
               configPrefix +
               $"WRIGHTY_CLAIMANT_ID={ShellQuote(handle.ClaimantId)} " +
               $"WRIGHTY_CLAIM_TOKEN={ShellQuote(handle.ClaimToken)} " +
               $"wrighty worker --item {ShellQuote(id.Value)} --resume --yes";
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private Dictionary<string, string> TrackerEnvironment()
    {
        var environment = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(state.Config.SourcePath))
            environment[TrackerConfigLoader.ConfigPathEnvironmentVariable] =
                Path.GetFullPath(state.Config.SourcePath);
        return environment;
    }

    private static string? BuildResumePrompt(WorkItemId id, WorkItemClaimSummary claim) =>
        HasResumeAddress(claim) &&
        ClaimantKinds.FromStorageValue(claim.ClaimantKind, claim.AgentType) == ClaimantKind.Agent &&
        claim.AgentType is not null
            ? WorkerPrompt.ForResume(id, claim.AgentType)
            : null;

    private static bool HasResumeAddress(WorkItemClaimSummary claim) =>
        claim.AgentType is "claude" or "codex" or "copilot" &&
        !string.IsNullOrWhiteSpace(claim.SessionId) &&
        !string.IsNullOrWhiteSpace(claim.WorkspacePath);

    private static string FormatFieldValue(JsonElement value) =>
        value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? JsonSerializer.Serialize(value, IndentedJson)
            : value.ToString();

    private BoardPageModel Board(DashboardSnapshot snapshot, ArchiveScope scope, string responseRevision)
    {
        var cards = snapshot.Items.Select(value => new BoardCardModel(
                value.Item.Id.Value,
                tracker.FormatShort(state.Config, value.Item.Id),
                value.Item.Title,
                value.Item.Status,
                value.Item.Priority,
                value.Item.Archived,
                value.Claim.State,
                ClaimLabel(value.Claim),
                ClaimantKindLabel(value.Claim),
                AgentTypeLabel(value.Claim),
                value.Item.AutomationEligible,
                value.Item.PreferredAgent,
                value.Item.WorkerState,
                WorkItemActivities.Resolve(
                    value.Item,
                    value.Claim,
                    state.Config.DefaultPickFrom),
                value.HasRecordedWorktree))
            .ToArray();
        var active = cards.Where(card => !card.Archived).ToArray();
        var columns = snapshot.Statuses
            .Select(status => new BoardColumnModel(
                status,
                active
                    .Where(card => string.Equals(card.Status, status, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(card => ActivityRank(card.Activity))
                    .ToArray()))
            .ToList();
        var unassigned = active.Where(card => card.Status is null || !snapshot.Statuses.Contains(card.Status, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (unassigned.Length > 0) columns.Add(new BoardColumnModel("No configured status", unassigned));
        return new BoardPageModel(snapshot.Statuses, snapshot.Priorities, columns, cards.Where(card => card.Archived).ToArray(), scope.ToString().ToLowerInvariant(), responseRevision);
    }

    private static int ActivityRank(string activity) => activity switch
    {
        WorkItemActivities.NeedsAttention => 0,
        WorkItemActivities.AgentActive => 1,
        WorkItemActivities.RetryScheduled => 2,
        WorkItemActivities.HandoffQueued => 3,
        WorkItemActivities.Queued => 4,
        _ => 5
    };

    private static string ResponseRevision(string snapshotRevision, ArchiveScope scope)
    {
        var value = $"{snapshotRevision}\n{scope}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string ClaimLabel(WorkItemClaimSummary claim) => claim.State switch
    {
        ClaimOwnershipState.Unclaimed => "Unclaimed",
        ClaimOwnershipState.OwnedByCurrent => "Claimed by this Wrighty installation",
        _ => "Claimed by another Wrighty installation"
    };

    private static string? AgentTypeLabel(WorkItemClaimSummary claim)
    {
        if (claim.State == ClaimOwnershipState.Unclaimed ||
            ClaimantKinds.FromStorageValue(claim.ClaimantKind, claim.AgentType) != ClaimantKind.Agent)
        {
            return null;
        }

        return claim.AgentType?.Trim().ToLowerInvariant() switch
        {
            "codex" => "Codex",
            "claude" => "Claude",
            "copilot" => "Copilot",
            _ => "Other"
        };
    }

    private static string? RecordedAgentTypeLabel(WorkItemClaimSummary claim) =>
        claim.AgentType?.Trim().ToLowerInvariant() switch
        {
            "codex" => "Codex",
            "claude" => "Claude",
            "copilot" => "Copilot",
            _ => null
        };

    private static string? ClaimantKindLabel(WorkItemClaimSummary claim)
    {
        if (claim.State == ClaimOwnershipState.Unclaimed) return null;
        return ClaimantKinds.FromStorageValue(claim.ClaimantKind, claim.AgentType) switch
        {
            ClaimantKind.Agent => "Agent",
            ClaimantKind.Human => "Human",
            ClaimantKind.Automation => "Automation",
            _ => "Unknown"
        };
    }

    private bool IsWebMutationProtected(WorkItemClaimSummary claim) =>
        claim.State != ClaimOwnershipState.Unclaimed && !IsExactWebClaim(claim);

    private bool IsExactWebClaim(WorkItemClaimSummary claim) =>
        claim.State == ClaimOwnershipState.OwnedByCurrent &&
        string.Equals(claim.ClaimantId, state.ClaimantId, StringComparison.Ordinal);

    private ClaimHandle RequiredWebHandle(string id)
    {
        var resolved = tracker.ResolveId(state.Config, id);
        if (state.TryHandle(resolved.Value, out var handle)) return handle;
        throw new TrackerException("CLAIM_TOKEN_REQUIRED",
            "This web session does not hold the claim token. Use explicit takeover to recover the claim.", 6);
    }

    private static ArchiveScope ParseScope(string? scope) => scope?.ToLowerInvariant() switch
    {
        "archived" => ArchiveScope.Archived,
        "all" => ArchiveScope.All,
        _ => ArchiveScope.Active
    };

    private static int Status(TrackerException exception) => exception.Code switch
    {
        "WORK_ITEM_NOT_FOUND" => 404,
        "CLAIM_REQUIRED" or "CLAIM_HELD" or "CLAIM_HELD_BY_LOCAL_CLAIMANT" or "CLAIM_STALE" or "CLAIM_TOKEN_REQUIRED" or
            "UPDATE_CONFLICT" or "WEB_CLAIM_GENERATION_STALE" or
            "WORKER_ITEM_NOT_PAUSED" => 409,
        "LOCAL_STORE_INVALID" or "CONFIG_INVALID" => 422,
        _ when exception.ExitCode == 2 => 400,
        _ => 500
    };
}

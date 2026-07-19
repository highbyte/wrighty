using System.Text.Json;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.Workers;

public sealed class WorkerService(
    TrackerService tracker,
    IAgentProcessRunner processes,
    IWorkspaceManager workspaces,
    IEnumerable<IAgentAdapter> adapters,
    Func<TimeSpan, CancellationToken, Task>? delay = null,
    Func<DateTimeOffset>? clock = null,
    IExecutableResolver? executables = null,
    IWorkspaceExecutionLock? workspaceExecutionLock = null,
    IWorkerSkillAvailability? skillAvailability = null)
{
    private readonly IReadOnlyDictionary<string, IAgentAdapter> adaptersByName = adapters
        .ToDictionary(adapter => adapter.AgentType, StringComparer.OrdinalIgnoreCase);
    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly IWorkspaceExecutionLock workspaceLocks =
        workspaceExecutionLock ?? NoOpWorkspaceExecutionLock.Instance;
    private readonly IWorkerSkillAvailability skills =
        skillAvailability ?? NoOpWorkerSkillAvailability.Instance;

    public async Task CheckAsync(string? selectedAgent, string repositoryPath,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        if (executables is null)
            throw new TrackerException("WORKER_UNAVAILABLE", "Executable checking is not configured.", 7);
        var selected = NormalizeAgent(selectedAgent);
        IReadOnlyList<IAgentAdapter> values;
        if (selected is null)
            values = adaptersByName.Values.OrderBy(value => value.AgentType).ToArray();
        else if (adaptersByName.TryGetValue(selected, out var selectedAdapter))
            values = [selectedAdapter];
        else
            throw new TrackerException("AGENT_UNSUPPORTED",
                $"Unsupported worker agent '{selected}'.", 2);
        foreach (var adapter in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = executables.Resolve(adapter.AgentType);
            var probeId = new WorkItemId("worker:check");
            var probeGeneration = $"probe:{Guid.NewGuid():N}";
            var handle = adapter.AgentType == "claude"
                ? SessionHandles.ForClaude(probeId, probeGeneration)
                : SessionHandles.ForNamedVendor(probeId, probeGeneration);
            var result = await processes.RunAsync(
                adapter.BuildCheck(handle, new Workspace(Path.GetFullPath(repositoryPath))),
                adapter,
                TimeSpan.FromMinutes(2),
                new Dictionary<string, string>(),
                sessionStarted: null,
                killOnCancellation: true,
                cancellationToken: cancellationToken);
            var handleMatches = adapter.AgentType != "claude" ||
                                string.Equals(result.SessionId, handle.Value, StringComparison.OrdinalIgnoreCase);
            if (result.Outcome != AgentOutcome.Succeeded || result.SessionId is null || !handleMatches)
                throw new TrackerException("AGENT_CHECK_FAILED",
                    $"{adapter.AgentType} probe failed or did not emit the expected session handle.", 7,
                    new Dictionary<string, object?>
                    {
                        ["agent"] = adapter.AgentType,
                        ["executable"] = path,
                        ["outcome"] = result.Outcome.ToString(),
                        ["sessionId"] = result.SessionId
                    });
            await emit(new WorkerEvent("check", Agent: adapter.AgentType,
                Message: $"{path}; session={result.SessionId}"));
        }
    }

    public async Task<WorkerRunSummary> RunAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        Validate(options);
        if (options.DryRun)
            return await DryRunAsync(config, options, repositoryPath, emit, cancellationToken);

        var completed = 0;
        var needsAttention = 0;
        var failed = 0;
        var idleStarted = now();
        var backoff = TimeSpan.FromSeconds(2);
        while (!cancellationToken.IsCancellationRequested &&
               (!options.MaxItems.HasValue || completed < options.MaxItems.Value))
        {
            string? selectedAgent = null;
            WorkItemDetail? selectedDetail = null;
            var diagnostics = new WorkerCandidateDiagnostics(
                options.FromStatus ?? config.DefaultPickFrom);
            var claimantId = options.ClaimantId ?? $"agent:worker:{Guid.NewGuid():N}";
            var kind = ParseClaimantKind(options.ClaimantKind);
            var commandAgent = NormalizeAgent(options.Agent);
            var context = new AgentExecutionContext(
                commandAgent,
                null,
                AgentContextSource.ExplicitOption,
                ClaimantKind: kind,
                ClaimantId: claimantId);
            try
            {
                await using var workspaceLease = options.WorkspaceMode == WorkspaceMode.Current
                    ? await workspaceLocks.AcquireAsync(repositoryPath, cancellationToken)
                    : null;
                var picked = await tracker.PickWithClaimAsync(
                    config,
                    options.FromStatus,
                    options.ToStatus,
                    context,
                    cancellationToken,
                    detail =>
                    {
                        var evaluation = EvaluateCandidate(
                            detail,
                            options,
                            config.EffectiveWorker.DefaultAgent,
                            diagnostics);
                        if (!evaluation.Eligible) return false;
                        if (options.WorkspaceMode == WorkspaceMode.Worktree)
                            skills.EnsureWorktreeReady(evaluation.Agent!, repositoryPath);
                        selectedAgent = evaluation.Agent;
                        selectedDetail = detail;
                        return true;
                    });
                if (selectedAgent is null || selectedDetail is null)
                    throw new TrackerException("AGENT_REQUIRED",
                        "An eligible item did not resolve to a supported agent.", 2);

                idleStarted = now();
                backoff = TimeSpan.FromSeconds(2);
                var disposition = await ProcessAsync(config, options, repositoryPath, picked.Claim, selectedDetail,
                    selectedAgent, claimantId, kind, emit, cancellationToken);
                completed++;
                if (disposition == WorkerItemDisposition.NeedsAttention) needsAttention++;
                if (disposition is WorkerItemDisposition.Failed or WorkerItemDisposition.TimedOut
                    or WorkerItemDisposition.Rejected) failed++;
                if (options.Once) break;
            }
            catch (TrackerException exception) when (exception.Code == "NO_ITEM_AVAILABLE")
            {
                var candidates = diagnostics.Summary;
                if (options.Once)
                {
                    await emit(new WorkerEvent(
                        "no-item",
                        Message: diagnostics.Describe(options.Filters.Count > 0),
                        Candidates: candidates));
                    break;
                }
                if (options.IdleTimeout is { } idle &&
                    now() - idleStarted >= idle)
                    break;
                await emit(new WorkerEvent(
                    "idle",
                    Message: $"Waiting for claimable items in " +
                             $"'{options.FromStatus ?? config.DefaultPickFrom}'; " +
                             $"retrying in {(int)backoff.TotalSeconds}s.",
                    Candidates: candidates));
                await wait(backoff, cancellationToken);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
            catch (TrackerException exception) when (
                exception.Code == "WORKSPACE_BUSY" && !options.Once)
            {
                if (options.IdleTimeout is { } idle &&
                    now() - idleStarted >= idle)
                    break;
                await emit(new WorkerEvent(
                    "workspace-busy",
                    WorkspacePath: Path.GetFullPath(repositoryPath),
                    Message: $"Another Wrighty worker is using the current workspace; " +
                             $"retrying in {(int)backoff.TotalSeconds}s."));
                await wait(backoff, cancellationToken);
                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
        return new WorkerRunSummary(completed, needsAttention, failed);
    }

    public async Task<bool> PreflightAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        Validate(options);
        var status = options.FromStatus ?? config.DefaultPickFrom;
        var diagnostics = new WorkerCandidateDiagnostics(status);
        var items = await tracker.ListAsync(
            config,
            new ListWorkItemsRequest(status, null),
            cancellationToken);
        WorkItemDetail? first = null;
        string? firstAgent = null;
        var readyAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var summary in items)
        {
            var detail = await tracker.GetAsync(config, summary.Id, cancellationToken);
            var evaluation = EvaluateCandidate(
                detail,
                options,
                config.EffectiveWorker.DefaultAgent,
                diagnostics);
            if (!evaluation.Eligible) continue;

            var ownership = await tracker.GetClaimOwnershipAsync(
                config,
                detail.Id,
                cancellationToken);
            if (ownership.State == ClaimOwnershipState.Unclaimed)
            {
                if (options.WorkspaceMode == WorkspaceMode.Worktree &&
                    readyAgents.Add(evaluation.Agent!))
                    skills.EnsureWorktreeReady(evaluation.Agent!, repositoryPath);
                diagnostics.Claimable++;
                if (first is null)
                {
                    first = detail;
                    firstAgent = evaluation.Agent;
                }
            }
            else
            {
                diagnostics.Claimed++;
            }
        }

        if (first is null)
        {
            await emit(new WorkerEvent(
                options.Once ? "no-item" : "waiting",
                Message: diagnostics.DescribePreflight(options.Filters.Count > 0),
                Candidates: diagnostics.Summary));
            return false;
        }

        await emit(new WorkerEvent(
            "ready",
            first.Id.Value,
            firstAgent,
            Message: diagnostics.DescribeReady(options.Filters.Count > 0),
            Candidates: diagnostics.Summary));
        return true;
    }

    public async Task PreflightItemAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        WorkItemId id,
        WorkerItemIntent intent,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var state = await ResolveItemActionAsync(
            config, options, repositoryPath, id, intent, cancellationToken);
        if (state.Action == ResolvedItemAction.Fresh)
        {
            await PreflightFreshAsync(
                config, options, repositoryPath, id, emit, cancellationToken);
            return;
        }

        var active = state.Ownership.State == ClaimOwnershipState.OwnedByCurrent;
        await emit(new WorkerEvent(
            "ready",
            id.Value,
            state.AgentName,
            state.Session!.WorkspacePath,
            Message: active
                ? "An active resumable session was found on this Wrighty installation. " +
                  "The worker will take over the claim, fence the previous claimant, and resume it."
                : $"The prior claim expired at {state.Session.ClaimExpiresAt:O}. " +
                  "The worker will acquire a new claim generation and resume the recorded session.",
            SessionId: state.Session.SessionId));
    }

    public async Task<WorkerRunSummary> RunItemAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        WorkItemId id,
        WorkerItemIntent intent,
        string? currentClaimToken,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var state = await ResolveItemActionAsync(
            config, options, repositoryPath, id, intent, cancellationToken);
        return state.Action switch
        {
            ResolvedItemAction.Fresh => await FreshAsync(
                config, options, repositoryPath, id, emit, cancellationToken),
            ResolvedItemAction.ResumeActive => await ResumeAsync(
                config, options, repositoryPath, id, currentClaimToken,
                emit, cancellationToken),
            ResolvedItemAction.ResumeExpired => await RecoverExpiredSessionAsync(
                config, options, repositoryPath, state.Detail, state.Session!,
                state.AgentName!, emit, cancellationToken),
            _ => throw new InvalidOperationException("Unsupported exact-item worker action.")
        };
    }

    public async Task PreflightResumeAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        WorkItemId id,
        string? currentClaimToken,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        Validate(options);
        if (ParseClaimantKind(options.ClaimantKind) != ClaimantKind.Agent)
            throw new TrackerException("ARGUMENT_INVALID",
                "Resuming a recorded vendor session requires --claimant-kind agent.", 2);

        await tracker.GetAsync(config, id, cancellationToken);
        var ownership = await tracker.GetClaimOwnershipAsync(config, id, cancellationToken);
        if (ownership.State != ClaimOwnershipState.OwnedByCurrent)
            throw new TrackerException(
                ownership.State == ClaimOwnershipState.HeldByOther ? "CLAIM_NOT_OWNER" : "CLAIM_NOT_FOUND",
                ownership.State == ClaimOwnershipState.HeldByOther
                    ? $"Work item '{id}' is claimed by another Wrighty installation."
                    : $"Work item '{id}' does not have an active resumable claim.",
                6);
        if (currentClaimToken is null)
            throw new TrackerException(
                "CLAIM_TOKEN_REQUIRED",
                $"Resuming '{id}' requires the current claim token.",
                6);
        if (ownership.AgentType is null || ownership.SessionId is null || ownership.WorkspacePath is null)
            throw new TrackerException("RESUME_ADDRESS_UNAVAILABLE",
                $"Claim '{id}' does not have a complete agent session address.", 5);

        var agentName = NormalizeAgent(ownership.AgentType)!;
        var requestedAgent = NormalizeAgent(options.Agent);
        if (requestedAgent is not null && !string.Equals(requestedAgent, agentName,
                StringComparison.OrdinalIgnoreCase))
            throw new TrackerException("AGENT_MISMATCH",
                $"Recorded session '{id}' belongs to {agentName}, not {requestedAgent}.", 2);
        if (!adaptersByName.ContainsKey(agentName))
            throw new TrackerException("AGENT_UNSUPPORTED",
                $"Unsupported recorded agent '{agentName}'.", 3);
        if (!Directory.Exists(ownership.WorkspacePath))
            throw new TrackerException("RESUME_ADDRESS_UNAVAILABLE",
                $"Recorded workspace does not exist: {ownership.WorkspacePath}", 5);
        if (!SamePath(ownership.WorkspacePath, repositoryPath))
            skills.EnsureWorktreeReady(
                ownership.AgentType,
                repositoryPath,
                ownership.WorkspacePath);

        var workspace = Path.GetFullPath(ownership.WorkspacePath);
        var repository = Path.GetFullPath(repositoryPath);
        await emit(new WorkerEvent(
            "ready",
            id.Value,
            agentName,
            workspace,
            Message: string.Equals(workspace, repository, StringComparison.Ordinal)
                ? "The recorded session is currently resumable in the current workspace."
                : "The recorded session is currently resumable in its retained worktree.",
            SessionId: ownership.SessionId));
    }

    public async Task PreflightFreshAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        WorkItemId id,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        Validate(options);
        var detail = await tracker.GetAsync(config, id, cancellationToken);
        EnsureFreshStatus(config, options, detail);
        var diagnostics = new WorkerCandidateDiagnostics(detail.Status ?? "(none)");
        var evaluation = EvaluateCandidate(
            detail,
            options,
            config.EffectiveWorker.DefaultAgent,
            diagnostics);
        if (!evaluation.Eligible)
            throw new TrackerException(
                "WORKER_ITEM_INELIGIBLE",
                $"Work item '{id}' is not eligible for a fresh worker run. " +
                "It must have wrighty-auto=true, match every --filter, and resolve a supported agent.",
                5);

        var ownership = await tracker.GetClaimOwnershipAsync(config, id, cancellationToken);
        if (ownership.State != ClaimOwnershipState.Unclaimed)
            throw new TrackerException(
                "CLAIM_HELD",
                $"Work item '{id}' still has an active claim until {ownership.ExpiresAt:O}; " +
                "use takeover or wait for expiry before starting fresh.",
                6);
        if (options.WorkspaceMode == WorkspaceMode.Worktree)
            skills.EnsureWorktreeReady(evaluation.Agent!, repositoryPath);

        await emit(new WorkerEvent(
            "ready",
            id.Value,
            evaluation.Agent,
            Message: $"The requested item is unclaimed and eligible for a fresh agent session " +
                     $"from status '{detail.Status}'."));
    }

    public async Task<WorkerRunSummary> FreshAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        WorkItemId id,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        Validate(options);
        var detail = await tracker.GetAsync(config, id, cancellationToken);
        EnsureFreshStatus(config, options, detail);
        var diagnostics = new WorkerCandidateDiagnostics(detail.Status ?? "(none)");
        var evaluation = EvaluateCandidate(
            detail,
            options,
            config.EffectiveWorker.DefaultAgent,
            diagnostics);
        if (!evaluation.Eligible)
            throw new TrackerException(
                "WORKER_ITEM_INELIGIBLE",
                $"Work item '{id}' is not eligible for a fresh worker run. " +
                "It must have wrighty-auto=true, match every --filter, and resolve a supported agent.",
                5);

        var agentName = evaluation.Agent!;
        if (options.DryRun)
        {
            var adapter = adaptersByName[agentName];
            var previewGeneration = $"dry-run:{Guid.NewGuid():N}";
            var session = adapter.AgentType == "claude"
                ? SessionHandles.ForClaude(detail.Id, previewGeneration)
                : SessionHandles.ForNamedVendor(detail.Id, previewGeneration);
            var workspace = new Workspace(Path.GetFullPath(repositoryPath),
                options.WorkspaceMode == WorkspaceMode.Worktree);
            var invocation = adapter.BuildStart(detail, session, workspace);
            await emit(new WorkerEvent("dry-run", detail.Id.Value, agentName, workspace.Path,
                Arguments: [invocation.Executable, .. invocation.Arguments],
                Message: "WRIGHTY_CLAIM_TOKEN=<redacted>"));
            return new WorkerRunSummary(1);
        }

        await using var workspaceLease = options.WorkspaceMode == WorkspaceMode.Current
            ? await workspaceLocks.AcquireAsync(repositoryPath, cancellationToken)
            : null;
        var claimantId = options.ClaimantId ?? $"agent:worker:{Guid.NewGuid():N}";
        var kind = ParseClaimantKind(options.ClaimantKind);
        var context = new AgentExecutionContext(
            agentName,
            null,
            AgentContextSource.ExplicitOption,
            ClaimantKind: kind,
            ClaimantId: claimantId);
        var claim = await tracker.ClaimAsync(config, id, context, cancellationToken);

        var targetStatus = options.ToStatus ?? config.DefaultPickTo;
        if (!string.IsNullOrWhiteSpace(targetStatus) &&
            !string.Equals(detail.Status, targetStatus, StringComparison.OrdinalIgnoreCase))
        {
            var updated = await tracker.UpdateAsync(
                config,
                id,
                WorkItemPatch.StatusOnly(targetStatus),
                expectedRevision: null,
                new ClaimHandle(context with { ClaimantId = claim.ClaimantId }, claim.ClaimToken),
                cancellationToken);
            detail = updated.Item;
        }

        var disposition = await ProcessAsync(config, options, repositoryPath, claim, detail,
            agentName, claimantId, kind, emit, cancellationToken);
        return Summary(disposition);
    }

    public async Task<WorkerRunSummary> ResumeAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        WorkItemId id,
        string? currentClaimToken,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        Validate(options);
        if (ParseClaimantKind(options.ClaimantKind) != ClaimantKind.Agent)
            throw new TrackerException("ARGUMENT_INVALID",
                "Resuming a recorded vendor session requires --claimant-kind agent.", 2);

        var detail = await tracker.GetAsync(config, id, cancellationToken);
        var ownership = await tracker.GetClaimOwnershipAsync(config, id, cancellationToken);
        if (ownership.State != ClaimOwnershipState.OwnedByCurrent)
            throw new TrackerException(
                ownership.State == ClaimOwnershipState.HeldByOther ? "CLAIM_NOT_OWNER" : "CLAIM_NOT_FOUND",
                ownership.State == ClaimOwnershipState.HeldByOther
                    ? $"Work item '{id}' is claimed by another Wrighty installation."
                    : $"Work item '{id}' does not have an active resumable claim.",
                6);
        if (ownership.AgentType is null || ownership.SessionId is null || ownership.WorkspacePath is null)
            throw new TrackerException("RESUME_ADDRESS_UNAVAILABLE",
                $"Claim '{id}' does not have a complete agent session address.", 5);

        var agentName = NormalizeAgent(ownership.AgentType)!;
        var requestedAgent = NormalizeAgent(options.Agent);
        if (requestedAgent is not null && !string.Equals(requestedAgent, agentName,
                StringComparison.OrdinalIgnoreCase))
            throw new TrackerException("AGENT_MISMATCH",
                $"Recorded session '{id}' belongs to {agentName}, not {requestedAgent}.", 2);
        if (!adaptersByName.TryGetValue(agentName, out var adapter))
            throw new TrackerException("AGENT_UNSUPPORTED",
                $"Unsupported recorded agent '{agentName}'.", 3);
        if (!Directory.Exists(ownership.WorkspacePath))
            throw new TrackerException("RESUME_ADDRESS_UNAVAILABLE",
                $"Recorded workspace does not exist: {ownership.WorkspacePath}", 5);
        if (!SamePath(ownership.WorkspacePath, repositoryPath))
            skills.EnsureWorktreeReady(
                ownership.AgentType,
                repositoryPath,
                ownership.WorkspacePath);

        var workspacePath = Path.GetFullPath(ownership.WorkspacePath);
        var repository = Path.GetFullPath(repositoryPath);
        var workspace = new Workspace(workspacePath,
            !string.Equals(workspacePath, repository, StringComparison.Ordinal));
        var handle = new SessionHandle(ownership.SessionId);
        var invocation = adapter.BuildResume(handle, workspace,
            WorkerPrompt.ForResume(id, agentName));
        if (options.DryRun)
        {
            await emit(new WorkerEvent("dry-run", id.Value, agentName, workspace.Path,
                Arguments: [invocation.Executable, .. invocation.Arguments],
                SessionId: ownership.SessionId));
            return new WorkerRunSummary(1);
        }

        await using var workspaceLease = options.WorkspaceMode == WorkspaceMode.Shared
            ? null
            : await workspaceLocks.AcquireAsync(workspace.Path, cancellationToken);
        var claimantId = options.ClaimantId ?? $"agent:worker:{Guid.NewGuid():N}";
        var takeoverContext = new AgentExecutionContext(
            agentName,
            ownership.SessionId,
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: claimantId,
            ClaimToken: currentClaimToken);
        var claim = await tracker.TakeoverAsync(config, id, takeoverContext,
            currentClaimToken, cancellationToken);
        var claimContext = takeoverContext with { ClaimToken = claim.ClaimToken };
        var grant = new ClaimHandle(claimContext, claim.ClaimToken);
        await emit(new WorkerEvent("resumed", id.Value, agentName, workspace.Path,
            Arguments: [invocation.Executable, .. invocation.Arguments],
            SessionId: ownership.SessionId));
        var disposition = await RunClaimedAsync(config, options, detail, agentName, claimantId,
            claimContext, grant, workspace, invocation, emit, cancellationToken);
        return Summary(disposition);
    }

    private async Task<WorkerRunSummary> RecoverExpiredSessionAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        WorkItemDetail detail,
        AgentSessionRecord session,
        string agentName,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var adapter = adaptersByName[agentName];
        var workspacePath = Path.GetFullPath(session.WorkspacePath!);
        var repository = Path.GetFullPath(repositoryPath);
        var workspace = new Workspace(workspacePath, !SamePath(workspacePath, repository));
        var handle = new SessionHandle(session.SessionId!);
        var invocation = adapter.BuildResume(
            handle, workspace, WorkerPrompt.ForResume(detail.Id, agentName));
        if (options.DryRun)
        {
            await emit(new WorkerEvent(
                "dry-run",
                detail.Id.Value,
                agentName,
                workspace.Path,
                Arguments: [invocation.Executable, .. invocation.Arguments],
                Message: "Will acquire a new claim generation and resume the expired session.",
                SessionId: session.SessionId));
            return new WorkerRunSummary(1);
        }

        await using var workspaceLease = options.WorkspaceMode == WorkspaceMode.Shared
            ? null
            : await workspaceLocks.AcquireAsync(workspace.Path, cancellationToken);
        var ownership = await tracker.GetClaimOwnershipAsync(
            config, detail.Id, cancellationToken);
        if (ownership.State != ClaimOwnershipState.Unclaimed)
            throw new TrackerException(
                "CLAIM_HELD",
                $"Work item '{detail.Id}' was claimed before its expired session could be recovered.",
                6);

        var claimantId = options.ClaimantId ?? $"agent:worker:{Guid.NewGuid():N}";
        var context = new AgentExecutionContext(
            agentName,
            session.SessionId,
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: claimantId);
        var claim = await tracker.ClaimAsync(
            config, detail.Id, context, cancellationToken);
        var claimContext = context with { ClaimToken = claim.ClaimToken };
        var grant = new ClaimHandle(claimContext, claim.ClaimToken);

        var targetStatus = options.ToStatus ?? config.DefaultPickTo;
        if (!string.IsNullOrWhiteSpace(targetStatus) &&
            string.Equals(detail.Status, options.FromStatus ?? config.DefaultPickFrom,
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(detail.Status, targetStatus, StringComparison.OrdinalIgnoreCase))
        {
            var updated = await tracker.UpdateAsync(
                config,
                detail.Id,
                WorkItemPatch.StatusOnly(targetStatus),
                expectedRevision: null,
                grant,
                cancellationToken);
            detail = updated.Item;
        }

        await tracker.RenewClaimAsync(
            config,
            detail.Id,
            grant,
            workspace.Path,
            session.SessionId,
            cancellationToken);
        await emit(new WorkerEvent(
            "resumed",
            detail.Id.Value,
            agentName,
            workspace.Path,
            Arguments: [invocation.Executable, .. invocation.Arguments],
            Message: "Recovered the recorded session under a new claim generation.",
            SessionId: session.SessionId));
        var disposition = await RunClaimedAsync(
            config, options, detail, agentName, claimantId,
            claimContext, grant, workspace, invocation, emit, cancellationToken);
        return Summary(disposition);
    }

    private async Task<ResolvedItemState> ResolveItemActionAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        WorkItemId id,
        WorkerItemIntent intent,
        CancellationToken cancellationToken)
    {
        Validate(options);
        var detail = await tracker.GetAsync(config, id, cancellationToken);
        var ownership = await tracker.GetClaimOwnershipAsync(
            config, id, cancellationToken);
        var session = await tracker.GetAgentSessionAsync(
            config, id, cancellationToken);

        if (ownership.State == ClaimOwnershipState.HeldByOther)
            throw new TrackerException(
                "CLAIM_NOT_OWNER",
                $"Work item '{id}' has an active claim from another Wrighty installation " +
                $"until {ownership.ExpiresAt:O}; it cannot be started or resumed here.",
                6);

        if (intent == WorkerItemIntent.Fresh)
        {
            if (ownership.State != ClaimOwnershipState.Unclaimed)
                throw new TrackerException(
                    "CLAIM_HELD",
                    $"Work item '{id}' has an active claim until {ownership.ExpiresAt:O}; " +
                    "--fresh requires an unclaimed item.",
                    6);
            return new ResolvedItemState(
                ResolvedItemAction.Fresh, detail, ownership, session, null);
        }

        if (session is { HasAddress: true })
        {
            if (!session.FromCurrentInstallation)
                throw new TrackerException(
                    "RESUME_ADDRESS_NOT_LOCAL",
                    $"Work item '{id}' has an expired agent session from another Wrighty " +
                    "installation. Its workspace and vendor session are not safely resumable here. " +
                    "Use --fresh explicitly to start a local session.",
                    5);
            if (!session.IsComplete)
                throw new TrackerException(
                    "RESUME_ADDRESS_UNAVAILABLE",
                    $"Work item '{id}' has recorded agent-session metadata, but its agent, " +
                    "session ID, or workspace path is missing. Use --fresh explicitly to " +
                    "discard that incomplete address once the item is unclaimed.",
                    5);
            var agentName = ValidateRecordedSession(
                options, repositoryPath, id, session);
            return new ResolvedItemState(
                ownership.State == ClaimOwnershipState.OwnedByCurrent
                    ? ResolvedItemAction.ResumeActive
                    : ResolvedItemAction.ResumeExpired,
                detail,
                ownership,
                session,
                agentName);
        }

        if (intent == WorkerItemIntent.Resume)
            throw new TrackerException(
                "RESUME_ADDRESS_UNAVAILABLE",
                $"Work item '{id}' has no recorded agent session to resume. " +
                "Remove --resume or use --fresh to start a new session.",
                5);
        if (ownership.State == ClaimOwnershipState.OwnedByCurrent)
            throw new TrackerException(
                "RESUME_ADDRESS_UNAVAILABLE",
                $"Work item '{id}' has an active claim without a complete agent session address.",
                5);
        return new ResolvedItemState(
            ResolvedItemAction.Fresh, detail, ownership, session, null);
    }

    private string ValidateRecordedSession(
        WorkerOptions options,
        string repositoryPath,
        WorkItemId id,
        AgentSessionRecord session)
    {
        if (ParseClaimantKind(options.ClaimantKind) != ClaimantKind.Agent)
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "Resuming a recorded vendor session requires --claimant-kind agent.",
                2);
        var agentName = NormalizeAgent(session.AgentType)!;
        var requestedAgent = NormalizeAgent(options.Agent);
        if (requestedAgent is not null &&
            !string.Equals(requestedAgent, agentName, StringComparison.OrdinalIgnoreCase))
            throw new TrackerException(
                "AGENT_MISMATCH",
                $"Recorded session '{id}' belongs to {agentName}, not {requestedAgent}.",
                2);
        if (!adaptersByName.ContainsKey(agentName))
            throw new TrackerException(
                "AGENT_UNSUPPORTED",
                $"Unsupported recorded agent '{agentName}'.",
                3);
        if (!Directory.Exists(session.WorkspacePath))
            throw new TrackerException(
                "RESUME_ADDRESS_UNAVAILABLE",
                $"Recorded workspace does not exist: {session.WorkspacePath}. " +
                "Use --fresh explicitly to start without the recorded session.",
                5);
        if (!SamePath(session.WorkspacePath!, repositoryPath))
            skills.EnsureWorktreeReady(
                session.AgentType!,
                repositoryPath,
                session.WorkspacePath);
        return agentName;
    }

    private async Task<WorkerItemDisposition> ProcessAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        ClaimResult claim,
        WorkItemDetail detail,
        string agentName,
        string claimantId,
        ClaimantKind kind,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var adapter = adaptersByName[agentName];
        var claimGeneration = claim.ClaimToken
            ?? throw new TrackerException(
                "CLAIM_TOKEN_REQUIRED",
                $"Worker claim for '{detail.Id}' did not return a fencing token.",
                6);
        var handle = adapter.AgentType == "claude"
            ? SessionHandles.ForClaude(detail.Id, claimGeneration)
            : SessionHandles.ForNamedVendor(detail.Id, claimGeneration);
        var claimContext = new AgentExecutionContext(agentName,
            adapter.SupportsPreassignedHandle ? handle.Value : null,
            AgentContextSource.ExplicitOption, ClaimantKind: kind,
            ClaimantId: claimantId, ClaimToken: claim.ClaimToken);
        var grant = new ClaimHandle(claimContext, claim.ClaimToken);
        var workspace = await workspaces.PrepareAsync(options.WorkspaceMode, repositoryPath,
            detail.Id, claimantId, claim.WorkspacePath, cancellationToken);

        // This metadata transition is fenced and happens before spawn, closing the workspace/session
        // orphan window for preassigned-handle vendors.
        try
        {
            await tracker.RenewClaimAsync(config, detail.Id, grant, workspace.Path,
                claimContext.SessionId, cancellationToken);
        }
        catch (TrackerException exception) when (
            exception.Code is "CLAIM_STALE" or "CLAIM_EXPIRED" or "CLAIM_NOT_OWNER")
        {
            await emit(new WorkerEvent("fenced", detail.Id.Value, agentName, workspace.Path,
                Message: exception.Code));
            return WorkerItemDisposition.Fenced;
        }
        var invocation = adapter.BuildStart(detail, handle, workspace);
        await emit(new WorkerEvent("started", detail.Id.Value, agentName, workspace.Path,
            Arguments: [invocation.Executable, .. invocation.Arguments]));
        return await RunClaimedAsync(config, options, detail, agentName, claimantId,
            claimContext, grant, workspace, invocation, emit, cancellationToken);
    }

    private async Task<WorkerItemDisposition> RunClaimedAsync(
        TrackerConfig config,
        WorkerOptions options,
        WorkItemDetail detail,
        string agentName,
        string claimantId,
        AgentExecutionContext claimContext,
        ClaimHandle grant,
        Workspace workspace,
        AgentInvocation invocation,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var adapter = adaptersByName[agentName];
        var environment = new Dictionary<string, string>
        {
            ["WRIGHTY_CLAIMANT_ID"] = claimantId,
            ["WRIGHTY_CLAIM_TOKEN"] = grant.ClaimToken!
        };
        if (!string.IsNullOrWhiteSpace(config.SourcePath))
            environment[TrackerConfigLoader.ConfigPathEnvironmentVariable] =
                Path.GetFullPath(config.SourcePath);
        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deadline = now() + options.ItemTimeout;
        var fenced = false;
        var budgetExhausted = false;
        var leaseTask = KeepAliveAsync(config, detail.Id, grant, workspace.Path, deadline,
            options, emit, runCts, leaseCts.Token, () => fenced = true,
            () => budgetExhausted = true);
        var result = await processes.RunAsync(invocation, adapter, options.ItemTimeout, environment,
            async (sessionId, token) =>
            {
                try
                {
                    await tracker.RenewClaimAsync(config, detail.Id, grant, workspace.Path, sessionId, token);
                    await emit(new WorkerEvent("session", detail.Id.Value, agentName, workspace.Path,
                        Message: sessionId));
                }
                catch (TrackerException exception) when (
                    exception.Code is "CLAIM_STALE" or "CLAIM_EXPIRED" or "CLAIM_NOT_OWNER")
                {
                    fenced = true;
                    await emit(new WorkerEvent("fenced", detail.Id.Value, agentName, workspace.Path,
                        Message: exception.Code));
                    runCts.Cancel();
                }
            },
            options.OnFenced == FencedAction.Kill,
            runCts.Token);
        if (budgetExhausted)
            result = result with
            {
                Outcome = AgentOutcome.TimedOut,
                FinalMessage = $"Agent exceeded the {options.ItemTimeout} renewal budget."
            };
        leaseCts.Cancel();
        try { await leaseTask; } catch (OperationCanceledException) when (leaseCts.IsCancellationRequested) { }

        var sessionId = result.SessionId ?? claimContext.SessionId;
        if (fenced)
        {
            await emit(new WorkerEvent("fenced", detail.Id.Value, agentName,
                workspace.Path, result.Outcome, result.FinalMessage, SessionId: sessionId));
            return WorkerItemDisposition.Fenced;
        }

        if (result.Outcome == AgentOutcome.Succeeded)
        {
            try
            {
                // A fenced renewal is the atomic residual-claim test. Success means the vendor
                // process exited without calling finish/release, so keep the resumable claim but
                // stop renewing it and report operator attention rather than item completion.
                var retained = await tracker.RenewClaimAsync(config, detail.Id, grant,
                    workspace.Path, sessionId, cancellationToken);
                await emit(new WorkerEvent(
                    "needs-attention",
                    detail.Id.Value,
                    agentName,
                    workspace.Path,
                    result.Outcome,
                    result.FinalMessage,
                    SessionId: sessionId,
                    ClaimExpiresAt: retained.ExpiresAt,
                    OperatorActions: NeedsAttentionActions(
                        detail.Id, agentName, retained.ExpiresAt)));
                return WorkerItemDisposition.NeedsAttention;
            }
            catch (TrackerException exception) when (
                exception.Code is "CLAIM_STALE" or "CLAIM_NOT_OWNER")
            {
                await emit(new WorkerEvent("fenced", detail.Id.Value, agentName,
                    workspace.Path, result.Outcome, exception.Code, SessionId: sessionId));
                return WorkerItemDisposition.Fenced;
            }
            catch (TrackerException exception) when (
                exception.Code is "CLAIM_NOT_FOUND" or "CLAIM_EXPIRED")
            {
                var current = await tracker.GetAsync(config, detail.Id, cancellationToken);
                if (current.Archived || string.Equals(current.Status, config.DefaultFinishTo,
                        StringComparison.OrdinalIgnoreCase))
                {
                    var workspaceRemoved = !options.KeepWorkspace &&
                                           await workspaces.CleanupAsync(workspace, cancellationToken);
                    var reviewCommand = !workspaceRemoved &&
                                        Directory.Exists(workspace.Path) &&
                                        !string.IsNullOrWhiteSpace(sessionId)
                        ? adapter.BuildInteractiveCommand(
                            new SessionHandle(sessionId),
                            workspace)
                        : null;
                    await emit(new WorkerEvent("finished", detail.Id.Value, agentName,
                        workspace.Path, result.Outcome, result.FinalMessage,
                        SessionId: sessionId,
                        ReviewCommand: reviewCommand));
                    if (workspaceRemoved)
                        await emit(new WorkerEvent("workspace-removed", detail.Id.Value, agentName,
                            workspace.Path));
                    return WorkerItemDisposition.Finished;
                }

                await emit(new WorkerEvent(
                    "needs-attention",
                    detail.Id.Value,
                    agentName,
                    workspace.Path,
                    result.Outcome,
                    result.FinalMessage,
                    SessionId: sessionId,
                    OperatorActions: NeedsAttentionActions(detail.Id, agentName)));
                return WorkerItemDisposition.NeedsAttention;
            }
        }

        try
        {
            await tracker.ReleaseAsync(config, detail.Id, grant, false, cancellationToken);
        }
        catch (TrackerException exception) when (
            exception.Code is "CLAIM_NOT_FOUND" or "CLAIM_EXPIRED")
        {
            // The generation already ended; never reacquire it during failure cleanup.
        }
        catch (TrackerException exception) when (
            exception.Code is "CLAIM_STALE" or "CLAIM_NOT_OWNER")
        {
            await emit(new WorkerEvent("fenced", detail.Id.Value, agentName,
                workspace.Path, result.Outcome, exception.Code, SessionId: sessionId));
            return WorkerItemDisposition.Fenced;
        }

        var type = result.Outcome switch
        {
            AgentOutcome.TimedOut => "timed-out",
            AgentOutcome.Rejected => "rejected",
            _ => "failed"
        };
        await emit(new WorkerEvent(type, detail.Id.Value, agentName, workspace.Path,
            result.Outcome, result.FinalMessage, SessionId: sessionId));
        return result.Outcome switch
        {
            AgentOutcome.TimedOut => WorkerItemDisposition.TimedOut,
            AgentOutcome.Rejected => WorkerItemDisposition.Rejected,
            _ => WorkerItemDisposition.Failed
        };
    }

    private static IReadOnlyList<WorkerOperatorAction> NeedsAttentionActions(
        WorkItemId id,
        string agentName,
        DateTimeOffset? activeUntil = null)
    {
        var agentLabel = agentName.Length == 0
            ? "agent"
            : $"{char.ToUpperInvariant(agentName[0])}{agentName[1..]}";
        var actions = new List<WorkerOperatorAction>
        {
            new(
                "Edit the requirements in the web UI",
                ["wrighty web"],
                $"Open {id.Value}, then take over (or claim after expiry) and edit it. Save and hand " +
                $"back to {agentLabel} to continue, choose Finish in the editor, or save and use " +
                "Archive from the item view."),
            new(
                $"Continue with {agentLabel} after saving the clarification",
                [$"wrighty worker --item {id.Value} --yes"],
                "This same command works while the claim is active or after it expires. " +
                "Wrighty reuses the recorded agent session when it is safely recoverable.")
        };
        var ownershipDescription = activeUntil is null
            ? "There is no active claimant to displace, so Wrighty acquires a human editing claim."
            : $"The current claim is active until {activeUntil:O}. edit --takeover works before or " +
              "after that time: while active, Wrighty asks you to confirm displacing the current " +
              "claimant; after expiry, it acquires a human editing claim without prompting. The " +
              "recorded local agent session is preserved in either case.";
        actions.Add(new WorkerOperatorAction(
            "Use the CLI instead",
            [
                $"wrighty edit {id.Value} --takeover",
                $"wrighty edit {id.Value} --takeover --yes --title \"Clear title\" " +
                "--body-file requirements.md"
            ],
            $"{ownershipDescription} The first command opens the title and body in VISUAL or " +
            "EDITOR. The second is the non-interactive form. Both retain the claim handle inside " +
            "Wrighty."));
        return actions;
    }

    private static WorkerRunSummary Summary(WorkerItemDisposition disposition) =>
        new(1,
            disposition == WorkerItemDisposition.NeedsAttention ? 1 : 0,
            disposition is WorkerItemDisposition.Failed or WorkerItemDisposition.TimedOut
                or WorkerItemDisposition.Rejected ? 1 : 0);

    private async Task KeepAliveAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle grant,
        string workspacePath,
        DateTimeOffset deadline,
        WorkerOptions options,
        Func<WorkerEvent, Task> emit,
        CancellationTokenSource runCts,
        CancellationToken cancellationToken,
        Action markFenced,
        Action markBudgetExhausted)
    {
        var interval = TimeSpan.FromMinutes(config.LeaseMinutes / 2d);
        while (now() < deadline)
        {
            var remaining = deadline - now();
            await wait(remaining < interval ? remaining : interval, cancellationToken);
            if (now() >= deadline) break;
            try
            {
                var renewed = await tracker.RenewClaimAsync(config, id, grant, workspacePath,
                    grant.Claimant.SessionId, cancellationToken);
                await emit(new WorkerEvent("renewed", id.Value, grant.Claimant.AgentType,
                    workspacePath, Message: renewed.ExpiresAt.ToString("O")));
            }
            catch (TrackerException exception) when (
                exception.Code is "CLAIM_STALE" or "CLAIM_EXPIRED" or "CLAIM_NOT_OWNER")
            {
                markFenced();
                await emit(new WorkerEvent("fenced", id.Value, grant.Claimant.AgentType,
                    workspacePath, Message: exception.Code));
                runCts.Cancel();
                return;
            }
        }
        // The fixed spawn-time deadline is the hard renewal budget. Cancelling the run here ensures
        // max claim hold remains item-timeout + LeaseMinutes even for a healthy but hung process.
        if (!cancellationToken.IsCancellationRequested)
        {
            markBudgetExhausted();
            runCts.Cancel();
        }
    }

    private async Task<WorkerRunSummary> DryRunAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var items = await tracker.ListAsync(config,
            new ListWorkItemsRequest(options.FromStatus ?? config.DefaultPickFrom, null),
            cancellationToken);
        var diagnostics = new WorkerCandidateDiagnostics(
            options.FromStatus ?? config.DefaultPickFrom);
        var count = 0;
        var readyAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var summary in items)
        {
            var detail = await tracker.GetAsync(config, summary.Id, cancellationToken);
            var evaluation = EvaluateCandidate(
                detail,
                options,
                config.EffectiveWorker.DefaultAgent,
                diagnostics);
            if (!evaluation.Eligible) continue;
            var agent = evaluation.Agent!;
            var adapter = adaptersByName[agent];
            var ownership = await tracker.GetClaimOwnershipAsync(config, detail.Id, cancellationToken);
            if (ownership.State != ClaimOwnershipState.Unclaimed)
            {
                var claimant = string.IsNullOrWhiteSpace(ownership.ClaimantId)
                    ? ownership.ClaimantKind
                    : $"{ownership.ClaimantKind} {ownership.ClaimantId}";
                await emit(new WorkerEvent(
                    "skipped-claimed",
                    detail.Id.Value,
                    agent,
                    Message: $"Active claim held by {claimant}.",
                    ClaimExpiresAt: ownership.ExpiresAt));
                continue;
            }
            if (options.WorkspaceMode == WorkspaceMode.Worktree &&
                readyAgents.Add(agent))
                skills.EnsureWorktreeReady(agent, repositoryPath);
            var previewGeneration = $"dry-run:{Guid.NewGuid():N}";
            var session = adapter.AgentType == "claude"
                ? SessionHandles.ForClaude(detail.Id, previewGeneration)
                : SessionHandles.ForNamedVendor(detail.Id, previewGeneration);
            var workspace = new Workspace(Path.GetFullPath(repositoryPath),
                options.WorkspaceMode == WorkspaceMode.Worktree);
            var invocation = adapter.BuildStart(detail, session, workspace);
            await emit(new WorkerEvent("dry-run", detail.Id.Value, agent, workspace.Path,
                Arguments: [invocation.Executable, .. invocation.Arguments],
                Message: "WRIGHTY_CLAIM_TOKEN=<redacted>"));
            count++;
            if (options.Once || (options.MaxItems.HasValue && count >= options.MaxItems.Value)) break;
        }
        if (count == 0)
        {
            await emit(new WorkerEvent(
                "no-item",
                Message: diagnostics.Describe(options.Filters.Count > 0),
                Candidates: diagnostics.Summary));
        }
        return new WorkerRunSummary(count);
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private CandidateEvaluation EvaluateCandidate(
        WorkItemDetail detail,
        WorkerOptions options,
        string? configuredAgent,
        WorkerCandidateDiagnostics diagnostics)
    {
        diagnostics.StatusItems++;
        if (string.IsNullOrWhiteSpace(detail.PreferredAgent))
            diagnostics.MissingItemAgent++;
        if (!detail.AutomationEligible)
        {
            diagnostics.MissingAuto++;
            return CandidateEvaluation.Ineligible;
        }
        if (!MatchesFilters(detail, options.Filters))
        {
            diagnostics.FilteredOut++;
            return CandidateEvaluation.Ineligible;
        }

        var agent = ResolveAgent(options.Agent, detail.PreferredAgent, configuredAgent);
        if (agent is null || !adaptersByName.ContainsKey(agent))
        {
            diagnostics.UnresolvedAgent++;
            return CandidateEvaluation.Ineligible;
        }

        diagnostics.Eligible++;
        return new CandidateEvaluation(true, agent);
    }

    private static bool MatchesFilters(
        WorkItemDetail detail,
        IReadOnlyDictionary<string, string> filters)
    {
        foreach (var filter in filters)
        {
            var actual = filter.Key.ToLowerInvariant() switch
            {
                "status" => detail.Status,
                "priority" => detail.Priority,
                "agent" => detail.PreferredAgent,
                "label" => detail.Labels?.FirstOrDefault(label =>
                    string.Equals(label, filter.Value, StringComparison.OrdinalIgnoreCase)),
                _ => detail.EffectiveFields.TryGetValue(filter.Key, out var value)
                    ? Scalar(value)
                    : null
            };
            if (!string.Equals(actual, filter.Value, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    private static string? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        _ => null
    };

    private static string? ResolveAgent(string? option, string? item, string? configured) =>
        NormalizeAgent(option) ?? NormalizeAgent(item) ?? NormalizeAgent(configured);

    private static string? NormalizeAgent(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static ClaimantKind ParseClaimantKind(string value) => value.ToLowerInvariant() switch
    {
        "agent" => ClaimantKind.Agent,
        "automation" => ClaimantKind.Automation,
        _ => throw new TrackerException("ARGUMENT_INVALID",
            "--claimant-kind for worker must be agent or automation.", 2)
    };

    private static void EnsureFreshStatus(
        TrackerConfig config,
        WorkerOptions options,
        WorkItemDetail detail)
    {
        if (detail.Archived)
            throw new TrackerException("WORK_ITEM_ARCHIVED",
                $"Work item '{detail.Id}' is archived and cannot be started fresh.", 5);
        var from = options.FromStatus ?? config.DefaultPickFrom;
        var to = options.ToStatus ?? config.DefaultPickTo;
        if (!string.Equals(detail.Status, from, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(detail.Status, to, StringComparison.OrdinalIgnoreCase))
            throw new TrackerException(
                "WORKER_ITEM_STATUS_INVALID",
                $"Work item '{detail.Id}' is in status '{detail.Status}'. A fresh worker run " +
                $"requires source status '{from}' or active status '{to}'.",
                5);
    }

    private static void Validate(WorkerOptions options)
    {
        if (options.MaxItems is <= 0)
            throw new TrackerException("ARGUMENT_INVALID", "--max-items must be positive.", 2);
        if (options.ItemTimeout <= TimeSpan.Zero)
            throw new TrackerException("ARGUMENT_INVALID", "--item-timeout must be positive.", 2);
        if (options.IdleTimeout is { } idleTimeout && idleTimeout <= TimeSpan.Zero)
            throw new TrackerException("ARGUMENT_INVALID", "--idle-timeout must be positive.", 2);
        if (string.Equals(options.ClaimantKind, "automation", StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(options.ClaimantId))
            throw new TrackerException("ARGUMENT_INVALID",
                "Automation requires an explicit --claimant-id.", 2);
    }

    private sealed record CandidateEvaluation(bool Eligible, string? Agent = null)
    {
        public static CandidateEvaluation Ineligible { get; } = new(false);
    }

    private enum ResolvedItemAction { Fresh, ResumeActive, ResumeExpired }

    private sealed record ResolvedItemState(
        ResolvedItemAction Action,
        WorkItemDetail Detail,
        ClaimOwnershipResult Ownership,
        AgentSessionRecord? Session,
        string? AgentName);

    private sealed class WorkerCandidateDiagnostics(string status)
    {
        public int StatusItems { get; set; }
        public int MissingAuto { get; set; }
        public int MissingItemAgent { get; set; }
        public int FilteredOut { get; set; }
        public int UnresolvedAgent { get; set; }
        public int Eligible { get; set; }
        public int Claimed { get; set; }
        public int Claimable { get; set; }

        public WorkerCandidateSummary Summary => new(
            status,
            StatusItems,
            MissingAuto,
            MissingItemAgent,
            FilteredOut,
            UnresolvedAgent,
            Eligible,
            Claimed,
            Claimable);

        public string Describe(bool hasFilters)
        {
            var filters = hasFilters
                ? $"{FilteredOut} excluded by --filter; "
                : string.Empty;
            return $"No worker item could be claimed from status '{status}'. " +
                   $"Considered {StatusItems} active item(s): " +
                   $"{MissingAuto} missing wrighty-auto=true; " +
                   $"{MissingItemAgent} missing a wrighty-agent item preference " +
                   $"(allowed when --agent or worker.defaultAgent supplies one); " +
                   filters +
                   $"{UnresolvedAgent} opted-in item(s) without a supported resolved agent; " +
                   $"{Eligible} otherwise eligible item(s) were unavailable because they were " +
                   $"already claimed or lost claim contention. Candidates must be active in " +
                   $"'{status}', have wrighty-auto=true, match every --filter, resolve an agent " +
                   $"via --agent > wrighty-agent > worker.defaultAgent, and be unclaimed.";
        }

        public string DescribePreflight(bool hasFilters)
        {
            var filters = hasFilters
                ? $"{FilteredOut} excluded by --filter; "
                : string.Empty;
            return $"No worker item is currently claimable from status '{status}'. " +
                   $"Considered {StatusItems} active item(s): " +
                   $"{MissingAuto} missing wrighty-auto=true; " +
                   $"{MissingItemAgent} missing a wrighty-agent item preference " +
                   $"(allowed when --agent or worker.defaultAgent supplies one); " +
                   filters +
                   $"{UnresolvedAgent} opted-in item(s) without a supported resolved agent; " +
                   $"{Claimed} otherwise eligible item(s) currently claimed; " +
                   $"{Claimable} currently claimable. Candidates must be active in '{status}', " +
                   $"have wrighty-auto=true, match every --filter, resolve an agent via " +
                   $"--agent > wrighty-agent > worker.defaultAgent, and be unclaimed.";
        }

        public string DescribeReady(bool hasFilters)
        {
            var filters = hasFilters
                ? $"; {FilteredOut} excluded by --filter"
                : string.Empty;
            return $"{Claimable} currently claimable worker item(s) in status '{status}'; " +
                   $"{StatusItems} active item(s) considered " +
                   $"({MissingAuto} missing wrighty-auto=true; " +
                   $"{MissingItemAgent} missing a wrighty-agent item preference " +
                   $"(allowed when --agent or worker.defaultAgent supplies one); " +
                   $"{UnresolvedAgent} without a supported resolved agent; " +
                   $"{Claimed} currently claimed{filters}). " +
                   $"Candidates must be active in '{status}', have wrighty-auto=true, match every " +
                   $"--filter, resolve an agent via --agent > wrighty-agent > " +
                   "worker.defaultAgent, and be unclaimed. This is a read-only snapshot; the " +
                   "atomic pick occurs after confirmation.";
        }
    }
}

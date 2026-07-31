using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.ApprovedContext;
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
    IWorkerSkillAvailability? skillAvailability = null,
    Settings.IHostLabelProvider? hostLabelProvider = null,
    IProviderCapacityStore? providerCapacityStore = null,
    IEnumerable<IAgentCapacityProbe>? capacityProbes = null,
    IEnumerable<ILaunchPreflightCheck>? launchPreflightChecks = null,
    IAgentRuntimeCatalog? runtimeCatalog = null,
    TrustedContinuationScan? continuations = null)
    : IProviderCapacityProbeService
{
    // The lifecycle event name, distinct from the WorkerDispatchStates value of the same text: one
    // is the emitted event, the other the published item state.
    private const string NeedsAttentionEvent = "needs-attention";

    /// <summary>Emitted whenever a claim turned out to be stale, expired, or owned elsewhere.</summary>
    private const string FencedEvent = "fenced";

    // The claim-fencing error codes are matched in many exception filters; naming them keeps those
    // filters identical rather than relying on eight literals staying in step.
    private const string ClaimStale = "CLAIM_STALE";
    private const string ClaimExpired = "CLAIM_EXPIRED";
    private const string ClaimNotOwner = "CLAIM_NOT_OWNER";
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ProviderProbeLeaseDuration = TimeSpan.FromMinutes(2);
    private readonly Settings.IHostLabelProvider hostLabel =
        hostLabelProvider ?? new Settings.AnonymousHostLabelProvider();

    private readonly IReadOnlyDictionary<string, IAgentAdapter> adaptersByName = adapters
        .ToDictionary(adapter => adapter.Agent, StringComparer.OrdinalIgnoreCase);
    private readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly IWorkspaceExecutionLock workspaceLocks =
        workspaceExecutionLock ?? NoOpWorkspaceExecutionLock.Instance;
    private readonly IWorkerSkillAvailability skills =
        skillAvailability ?? NoOpWorkerSkillAvailability.Instance;
    private readonly IProviderCapacityStore providerCapacity =
        providerCapacityStore ?? NoOpProviderCapacityStore.Instance;
    private readonly bool providerCapacityEnabled = providerCapacityStore is not null;
    private readonly IReadOnlyDictionary<string, IAgentCapacityProbe> capacityProbesByAgent =
        (capacityProbes ?? [])
        .ToDictionary(probe => probe.Agent, StringComparer.OrdinalIgnoreCase);
    private readonly IAgentRuntimeCatalog runtimes =
        runtimeCatalog ?? new AssumeInstalledAgentRuntimeCatalog(adapters);

    private WorkerLaunchPreflight? preflight;

    /// <summary>
    /// The one internal launch boundary every vendor spawn passes through. Built-in checks come
    /// first and cannot be displaced; additional checks are appended, which is how plan 030's
    /// approved-context revalidation joins without adding a second launch path.
    /// </summary>
    private WorkerLaunchPreflight LaunchPreflight => preflight ??= new WorkerLaunchPreflight(
    [
        new WorkerPolicyLaunchCheck(adaptersByName.ContainsKey),
        new AgentPermissionLaunchCheck(adaptersByName.ContainsKey, DescribePermissions),
        .. launchPreflightChecks ?? []
    ]);

    /// <summary>Which checks gate a stage, so coverage is observable rather than implied.</summary>
    public IReadOnlyList<string> LaunchPreflightChecks(LaunchStage stage, LaunchKind kind) =>
        LaunchPreflight.CheckNamesFor(stage, kind);

    public IReadOnlyList<string> SupportedAgents =>
        adaptersByName.Keys.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// The effective spawned-agent permission posture per agent, so an operator sees what a live
    /// run actually grants before confirming it. Agents the configuration cannot resolve are
    /// omitted rather than guessed.
    /// </summary>
    public IReadOnlyList<AgentPermissions> DescribeAgentPermissions(
        TrackerConfig config,
        string? selectedAgent = null)
    {
        var selected = NormalizeAgent(selectedAgent);
        if (selected is null)
            return SupportedAgents.Select(agent => DescribePermissions(config, agent)).ToArray();
        return adaptersByName.ContainsKey(selected)
            ? [DescribePermissions(config, selected)]
            : [];
    }

    private static AgentPermissionProfile PermissionsFor(TrackerConfig config, string agentName) =>
        config.EffectiveWorker.RequestedAgentPermissions(agentName);

    private AgentPermissions DescribePermissions(TrackerConfig config, string agentName) =>
        adaptersByName[agentName].DescribePermissions(PermissionsFor(config, agentName));

    public async Task CheckAsync(string? selectedAgent, string repositoryPath,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        if (executables is null)
            throw new TrackerException("WORKER_UNAVAILABLE", "Executable checking is not configured.", 7);
        var selected = NormalizeAgent(selectedAgent);
        IReadOnlyList<IAgentAdapter> values;
        if (selected is null)
            values = adaptersByName.Values.OrderBy(value => value.Agent).ToArray();
        else if (adaptersByName.TryGetValue(selected, out var selectedAdapter))
            values = [selectedAdapter];
        else
            throw new TrackerException("AGENT_UNSUPPORTED",
                $"Unsupported worker agent '{selected}'.", 2);
        foreach (var adapter in values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = runtimes.Snapshot();
            if (!snapshot.IsInstalled(adapter.Agent))
                throw AgentNotInstalled(adapter.Agent, null, "check", snapshot);
            var path = executables.Resolve(adapter.ExecutableName);
            var probeId = new WorkItemId("worker:check");
            var probeGeneration = $"probe:{Guid.NewGuid():N}";
            var handle = adapter.Agent == "claude"
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
            var handleMatches = adapter.Agent != "claude" ||
                                string.Equals(result.SessionId, handle.Value, StringComparison.OrdinalIgnoreCase);
            if (result.Outcome != AgentOutcome.Succeeded || result.SessionId is null || !handleMatches)
                throw new TrackerException("AGENT_CHECK_FAILED",
                    $"{adapter.Agent} probe failed or did not emit the expected session handle.", 7,
                    new Dictionary<string, object?>
                    {
                        ["agent"] = adapter.Agent,
                        ["executable"] = path,
                        ["outcome"] = result.Outcome.ToString(),
                        ["sessionId"] = result.SessionId
                    });
            await emit(new WorkerEvent("check", Agent: adapter.Agent,
                Message: $"{path}; session={result.SessionId}"));
        }
    }

    public async Task<ProviderCapacity> ProbeProviderAsync(
        TrackerConfig config,
        string agentType,
        string repositoryPath,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        if (!providerCapacityEnabled)
            throw new TrackerException(
                "PROVIDER_PROBE_UNAVAILABLE",
                "Provider capacity probing requires the machine-local availability store.",
                7);
        var agentName = NormalizeAgent(agentType);
        if (agentName is null)
            throw new TrackerException(
                "AGENT_REQUIRED",
                "A provider agent is required.",
                2);
        if (!adaptersByName.TryGetValue(agentName, out var adapter))
            throw new TrackerException(
                "AGENT_UNSUPPORTED",
                $"Unsupported worker agent '{agentName}'.",
                2);
        EnsureAgentInstalled(agentName, null, "option");

        var observedAt = now();
        var priorAvailability = await providerCapacity.GetAsync(
            agentName,
            cancellationToken);

        var lease = await providerCapacity.TryAcquireProbeAsync(
            agentName,
            observedAt,
            ProviderProbeLeaseDuration,
            cancellationToken,
            allowBeforeUnavailableUntil: true,
            allowWhenAvailable: true);
        if (lease is null)
        {
            var concurrentAvailability =
                await providerCapacity.GetAsync(agentName, cancellationToken)
                ?? throw new TrackerException(
                    "PROVIDER_PROBE_BUSY",
                    $"Another {agentName} provider capacity probe is already in progress.",
                    2);
            await emit(ProviderUnavailableEvent(
                concurrentAvailability,
                null,
                repositoryPath));
            return concurrentAvailability;
        }

        var availability = await providerCapacity.GetAsync(agentName, cancellationToken)
                           ?? throw new TrackerException(
                               "PROVIDER_PROBE_STATE_MISSING",
                               $"The {agentName} provider probe lease could not be read.",
                               9);
        await emit(new WorkerEvent(
            "provider-probe-started",
            Agent: agentName,
            WorkspacePath: Path.GetFullPath(repositoryPath),
            Message: "Started an explicit provider capacity probe without claiming a work item.",
            ProviderCapacity: availability));

        try
        {
            var probeId = new WorkItemId($"provider:{agentName}");
            var generation = $"probe:{Guid.NewGuid():N}";
            var handle = agentName == "claude"
                ? SessionHandles.ForClaude(probeId, generation)
                : SessionHandles.ForNamedVendor(probeId, generation);
            var result = await processes.RunAsync(
                adapter.BuildCheck(
                    handle,
                    new Workspace(Path.GetFullPath(repositoryPath))),
                adapter,
                TimeSpan.FromMinutes(2),
                new Dictionary<string, string>(),
                sessionStarted: null,
                killOnCancellation: true,
                cancellationToken: cancellationToken);

            if (IsUsageCapacityFailure(result.Failure))
            {
                var failure = result.Failure!;
                var attempt = Math.Max(1, availability.ConsecutiveFailures + 1);
                var unavailableUntil = RetrySchedule.ChooseNotBefore(
                    now(),
                    probeId,
                    failure,
                    config.EffectiveWorker.EffectiveUsageFailure,
                    attempt);
                var reopened = await providerCapacity.RecordUnavailableAsync(
                    agentName,
                    failure.SanitizedMessage ?? FailureKindLabel(failure.Kind),
                    unavailableUntil,
                    failure.Confidence,
                    now(),
                    cancellationToken);
                await emit(ProviderUnavailableEvent(reopened, null, repositoryPath) with
                {
                    Outcome = result.Outcome,
                    Failure = failure,
                    Message = failure.SanitizedMessage ??
                              "The provider capacity probe remains usage-limited."
                });
                return reopened;
            }

            await providerCapacity.RecordAvailableAsync(agentName, now(), cancellationToken);
            var available = await providerCapacity.GetAsync(agentName, cancellationToken)
                            ?? new ProviderCapacity(
                                agentName,
                                ProviderCapacityState.Available,
                                "The provider capacity probe completed.",
                                null,
                                AgentFailureConfidence.Authoritative,
                                0,
                                now());
            await emit(new WorkerEvent(
                "provider-available",
                Agent: agentName,
                WorkspacePath: Path.GetFullPath(repositoryPath),
                Outcome: result.Outcome,
                Message: result.Outcome == AgentOutcome.Succeeded
                    ? "The explicit provider capacity probe succeeded; automatic work is enabled."
                    : priorAvailability is null or
                    { State: ProviderCapacityState.Available }
                        ? "The provider returned a non-capacity failure; the capacity circuit remains closed."
                        : "The provider returned a non-capacity failure; the capacity circuit was cleared.",
                Failure: result.Failure,
                ProviderCapacity: available));
            return available;
        }
        catch
        {
            await providerCapacity.ReleaseProbeAsync(
                lease,
                now(),
                CancellationToken.None);
            throw;
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
        if (!options.DryRun)
            EnsureWorkerHostAvailable(options);
        if (options.DryRun)
            return await DryRunAsync(config, options, repositoryPath, emit, cancellationToken);

        var state = new WorkerLoopState(now());
        while (!cancellationToken.IsCancellationRequested &&
               (!options.MaxItems.HasValue || state.Processed < options.MaxItems.Value))
            if (await RunIterationAsync(
                    config, options, repositoryPath, state, emit, cancellationToken))
                break;
        return state.RunSummary;
    }

    private async Task<bool> RunIterationAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        WorkerLoopState state,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var diagnostics = new WorkerCandidateDiagnostics(
            options.FromStatus ?? config.DefaultPickFrom);
        // Before looking for queued work, let a trusted reply create some. Anything this queues is
        // picked up by the very next call, so a continuation costs no extra poll.
        await EvaluateContinuationsAsync(config, options, emit, cancellationToken);
        var queued = await TryRunQueuedAsync(
            config, options, repositoryPath, diagnostics, emit, cancellationToken);
        if (queued is not null)
        {
            state.Record(queued, now());
            return options.Once;
        }

        try
        {
            var disposition = await RunFreshCandidateAsync(
                config, options, repositoryPath, diagnostics, emit, cancellationToken);
            state.Record(disposition, now());
            return options.Once;
        }
        catch (TrackerException exception) when (exception.Code == "NO_ITEM_AVAILABLE")
        {
            return await HandleNoItemAsync(
                config, options, state, diagnostics, emit, cancellationToken);
        }
        catch (TrackerException exception) when (
            exception.Code == "WORKSPACE_BUSY" && !options.Once)
        {
            return await HandleWorkspaceBusyAsync(
                options, repositoryPath, state, emit, cancellationToken);
        }
    }

    private async Task<WorkerItemDisposition> RunFreshCandidateAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        WorkerCandidateDiagnostics diagnostics,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        string? selectedAgent = null;
        WorkItemDetail? selectedDetail = null;
        var claimantId = options.ClaimantId ?? $"agent:worker:{Guid.NewGuid():N}";
        var kind = ParseClaimantKind(options.ClaimantKind);
        var context = new AgentExecutionContext(
            NormalizeAgent(options.Agent),
            null,
            AgentContextSource.ExplicitOption,
            ClaimantKind: kind,
            ClaimantId: claimantId);
        var providerStates = await ProviderStatesAsync(cancellationToken);
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
                    detail, options, config.EffectiveWorker.DefaultAgent, diagnostics);
                if (!evaluation.Eligible)
                    return false;
                if (providerStates.TryGetValue(evaluation.Agent!, out var provider) &&
                    provider.State != ProviderCapacityState.Available)
                {
                    diagnostics.RecordProviderUnavailable(provider);
                    return false;
                }
                if (options.WorkspaceMode == WorkspaceMode.Worktree)
                    skills.EnsureWorktreeReady(evaluation.Agent!, repositoryPath);
                selectedAgent = evaluation.Agent;
                selectedDetail = detail;
                return true;
            });
        if (selectedAgent is null || selectedDetail is null)
            throw new TrackerException("AGENT_REQUIRED",
                "An eligible item did not resolve to a supported agent.", 2);
        return await ProcessAsync(
            config, options, repositoryPath, picked.Claim, selectedDetail,
            selectedAgent, claimantId, kind, emit, cancellationToken);
    }

    private async Task<bool> HandleNoItemAsync(
        TrackerConfig config,
        WorkerOptions options,
        WorkerLoopState state,
        WorkerCandidateDiagnostics diagnostics,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var candidates = diagnostics.CreateSnapshot();
        foreach (var provider in diagnostics.UnavailableProviders.Values)
            await emit(ProviderUnavailableEvent(provider, null, null));
        if (options.Once)
        {
            await emit(new WorkerEvent(
                "no-item",
                Message: diagnostics.Describe(options.Filters.Count > 0),
                Candidates: candidates));
            return true;
        }
        if (IdleTimedOut(options, state))
            return true;
        var unresolvedAgentChanged =
            candidates.UnresolvedAgent > 0 &&
            candidates.UnresolvedAgent != state.PreviousUnresolvedAgentCount;
        var unavailableSignature = diagnostics.UnavailableAgentSignature;
        var unavailableAgentChanged =
            candidates.UnavailableAgent > 0 &&
            !string.Equals(
                unavailableSignature,
                state.PreviousUnavailableAgentSignature,
                StringComparison.Ordinal);
        var eventType = unavailableAgentChanged ? "agent-unavailable" : "idle";
        string idleMessage;
        if (unavailableAgentChanged)
            idleMessage = diagnostics.DescribeUnavailableAgents();
        else if (unresolvedAgentChanged)
            idleMessage = DescribeUnresolvedAgentIdle(candidates.UnresolvedAgent);
        else
            idleMessage = $"Waiting for queued resumable sessions or claimable items in " +
                          $"'{options.FromStatus ?? config.DefaultPickFrom}'; " +
                          $"retrying in {(int)state.Backoff.TotalSeconds}s.";
        await emit(new WorkerEvent(eventType, Message: idleMessage, Candidates: candidates));
        state.PreviousUnresolvedAgentCount = candidates.UnresolvedAgent;
        state.PreviousUnavailableAgentSignature = unavailableSignature;
        await state.WaitAndBackOffAsync(wait, cancellationToken);
        return false;
    }

    private async Task<bool> HandleWorkspaceBusyAsync(
        WorkerOptions options,
        string repositoryPath,
        WorkerLoopState state,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        if (IdleTimedOut(options, state))
            return true;
        await emit(new WorkerEvent(
            "workspace-busy",
            WorkspacePath: Path.GetFullPath(repositoryPath),
            Message: $"Another Wrighty worker is using the current workspace; " +
                     $"retrying in {(int)state.Backoff.TotalSeconds}s."));
        await state.WaitAndBackOffAsync(wait, cancellationToken);
        return false;
    }

    private bool IdleTimedOut(WorkerOptions options, WorkerLoopState state) =>
        options.IdleTimeout is { } idle && now() - state.IdleStarted >= idle;

    private static string DescribeUnresolvedAgentIdle(int count)
    {
        var item = count == 1 ? "item needs" : "items need";
        return $"{count} automation-enabled {item} an agent; set agent policy, --agent, " +
               "or worker.defaultAgent.";
    }

    public async Task<bool> PreflightAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        Validate(options);
        EnsureWorkerHostAvailable(options);
        var status = options.FromStatus ?? config.DefaultPickFrom;
        var diagnostics = new WorkerCandidateDiagnostics(status);
        var queued = await FirstQueuedCandidateAsync(
            config, options, repositoryPath, diagnostics, cancellationToken);
        if (queued is not null)
        {
            await emit(new WorkerEvent(
                "ready",
                queued.Detail.Id.Value,
                queued.AgentName,
                queued.Session.WorkspacePath,
                Message: "A clarified In Progress item is queued. The worker will acquire a new " +
                         "claim generation and resume its recorded agent session.",
                SessionId: queued.Session.SessionId));
            return true;
        }

        var first = await FindPreflightCandidateAsync(
            config, options, repositoryPath, status, diagnostics, cancellationToken);
        if (first is null)
        {
            foreach (var provider in diagnostics.UnavailableProviders.Values)
                await emit(ProviderUnavailableEvent(provider, null, null));
            await emit(new WorkerEvent(
                options.Once ? "no-item" : "waiting",
                Message: diagnostics.DescribePreflight(options.Filters.Count > 0),
                Candidates: diagnostics.CreateSnapshot()));
            return false;
        }

        await emit(new WorkerEvent(
            "ready",
            first.Detail.Id.Value,
            first.Agent,
            Message: diagnostics.DescribeReady(options.Filters.Count > 0),
            Candidates: diagnostics.CreateSnapshot()));
        return true;
    }

    private async Task<PreflightCandidate?> FindPreflightCandidateAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        string status,
        WorkerCandidateDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        var items = await tracker.ListAsync(
            config, new ListWorkItemsRequest(status, null), cancellationToken);
        PreflightCandidate? first = null;
        var readyAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var summary in items)
        {
            var detail = await tracker.GetAsync(config, summary.Id, cancellationToken);
            var evaluation = EvaluateCandidate(
                detail, options, config.EffectiveWorker.DefaultAgent, diagnostics);
            if (!evaluation.Eligible)
                continue;
            if (await IsProviderBlockedForFreshAsync(
                    evaluation.Agent!, diagnostics, cancellationToken))
                continue;
            var ownership = await tracker.GetClaimOwnershipAsync(
                config, detail.Id, cancellationToken);
            if (ownership.State != ClaimOwnershipState.Unclaimed)
            {
                diagnostics.Claimed++;
                continue;
            }
            EnsurePreflightWorkspaceReady(
                options, repositoryPath, evaluation.Agent!, readyAgents);
            diagnostics.Claimable++;
            first ??= new PreflightCandidate(detail, evaluation.Agent!);
        }
        return first;
    }

    private void EnsurePreflightWorkspaceReady(
        WorkerOptions options,
        string repositoryPath,
        string agent,
        ISet<string> readyAgents)
    {
        if (options.WorkspaceMode == WorkspaceMode.Worktree && readyAgents.Add(agent))
            skills.EnsureWorktreeReady(agent, repositoryPath);
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

    /// <summary>
    /// Processes one named item. Everything this reaches is <see cref="LaunchPreflightRequest.OperatorRequested"/>:
    /// somebody typed this item's id, which is a different thing from a scan having selected it.
    /// </summary>
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
                state.AgentName!, emit, cancellationToken, operatorRequested: true),
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
                ownership.State == ClaimOwnershipState.HeldByOther ? ClaimNotOwner : "CLAIM_NOT_FOUND",
                ownership.State == ClaimOwnershipState.HeldByOther
                    ? $"Work item '{id}' is claimed by another Wrighty installation."
                    : $"Work item '{id}' does not have an active resumable claim.",
                6);
        if (currentClaimToken is null)
            throw new TrackerException(
                "CLAIM_TOKEN_REQUIRED",
                $"Resuming '{id}' requires the current claim token.",
                6);
        if (ownership.Agent is null || ownership.SessionId is null || ownership.WorkspacePath is null)
            throw new TrackerException("RESUME_ADDRESS_UNAVAILABLE",
                $"Claim '{id}' does not have a complete agent session address.", 5);

        var agentName = NormalizeAgent(ownership.Agent)!;
        var requestedAgent = NormalizeAgent(options.Agent);
        if (requestedAgent is not null && !string.Equals(requestedAgent, agentName,
                StringComparison.OrdinalIgnoreCase))
            throw new TrackerException("AGENT_MISMATCH",
                $"Recorded session '{id}' belongs to {agentName}, not {requestedAgent}.", 2);
        if (!adaptersByName.ContainsKey(agentName))
            throw new TrackerException("AGENT_UNSUPPORTED",
                $"Unsupported recorded agent '{agentName}'.", 3);
        EnsureAgentInstalled(agentName, id, "session");
        if (!Directory.Exists(ownership.WorkspacePath))
            throw new TrackerException("RESUME_ADDRESS_UNAVAILABLE",
                $"Recorded workspace does not exist: {ownership.WorkspacePath}", 5);
        if (!SamePath(ownership.WorkspacePath, repositoryPath))
            skills.EnsureWorktreeReady(
                ownership.Agent,
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
        {
            ThrowIfAgentUnavailable(evaluation, id);
            throw new TrackerException(
                "WORKER_ITEM_INELIGIBLE",
                $"Work item '{id}' is not eligible for a fresh worker run. " +
                "Automatic execution must be allowed, projected context must be approved when " +
                "present, every --filter must match, and a supported agent must resolve.",
                5);
        }

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
        {
            ThrowIfAgentUnavailable(evaluation, id);
            throw new TrackerException(
                "WORKER_ITEM_INELIGIBLE",
                $"Work item '{id}' is not eligible for a fresh worker run. " +
                "Automatic execution must be allowed, projected context must be approved when " +
                "present, every --filter must match, and a supported agent must resolve.",
                5);
        }

        var agentName = evaluation.Agent!;
        if (options.DryRun)
        {
            var adapter = adaptersByName[agentName];
            var previewGeneration = $"dry-run:{Guid.NewGuid():N}";
            var session = adapter.Agent == "claude"
                ? SessionHandles.ForClaude(detail.Id, previewGeneration)
                : SessionHandles.ForNamedVendor(detail.Id, previewGeneration);
            var workspace = new Workspace(Path.GetFullPath(repositoryPath),
                options.WorkspaceMode == WorkspaceMode.Worktree);
            var invocation = adapter.BuildStart(detail, session, workspace,
            PermissionsFor(config, agentName),
            WorkerPrompt.CommitInstruction(workspace, config.Worker?.Completion?.Commit));
            await emit(new WorkerEvent("dry-run", detail.Id.Value, agentName, workspace.Path,
                Arguments: [invocation.Executable, .. invocation.Arguments],
                Message: "WRIGHTY_CLAIM_TOKEN=<redacted>",
                Permissions: DescribePermissions(config, agentName)));
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
        var (agentName, adapter, sessionId, recordedWorkspace) =
            ResolveResumeTarget(options, id, ownership, repositoryPath);

        var workspacePath = Path.GetFullPath(recordedWorkspace);
        var repository = Path.GetFullPath(repositoryPath);
        var workspace = new Workspace(workspacePath,
            !string.Equals(workspacePath, repository, StringComparison.Ordinal));
        var handle = new SessionHandle(sessionId);
        // A dry run reports without claiming, so no context has been resolved and none can be: this
        // preview shows the shape of the launch, not the prompt a real one would carry.
        var invocation = adapter.BuildResume(handle, workspace,
            WorkerPrompt.Append(
                WorkerPrompt.ForResume(id, agentName),
                WorkerPrompt.CommitInstruction(workspace, config.Worker?.Completion?.Commit)),
            PermissionsFor(config, agentName));
        if (options.DryRun)
        {
            await emit(new WorkerEvent("dry-run", id.Value, agentName, workspace.Path,
                Arguments: [invocation.Executable, .. invocation.Arguments],
                SessionId: ownership.SessionId,
                Permissions: DescribePermissions(config, agentName)));
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
        detail = await ClearDispatchStateAsync(
            config, detail, grant, cancellationToken);

        // A resume never runs the post-claim stage, so a check comparing against what this session
        // was already given has no in-flight baseline. The recorded session is that baseline, and it
        // is read here rather than earlier so it reflects the state after the takeover.
        var recordedSession = await tracker.GetAgentSessionAsync(config, id, cancellationToken);
        // Operator-requested unconditionally: this method resumes one named item, and the only way
        // to reach it is someone asking for that item. The continuous scan resumes through
        // RecoverExpiredSessionAsync instead, which decides for itself.
        var resumeRequest = new LaunchPreflightRequest(
            config, options, detail, agentName, LaunchKind.Resume, LaunchStage.PreSpawn,
            recordedSession, OperatorRequested: true);
        var resumePreSpawn = await LaunchPreflight.EvaluateAsync(resumeRequest, cancellationToken);
        if (!resumePreSpawn.Admitted)
            return Summary(await ReleaseAfterPreflightRefusalAsync(
                new RefusedLaunch(resumeRequest, grant, resumePreSpawn, workspace),
                emit, cancellationToken, restoreSourceStatus: false, cleanupWorkspace: false));

        await ReportNotableAdmissionAsync(resumePreSpawn, detail, agentName, emit);
        var resumeContext = await TakeAndRecordContextAsync(
            config, detail, agentName, emit, cancellationToken);
        invocation = BuildResumeInvocation(
            adapter, config, detail, agentName, handle, workspace, resumeContext);
        await emit(new WorkerEvent("resumed", id.Value, agentName, workspace.Path,
            Arguments: [invocation.Executable, .. invocation.Arguments],
            SessionId: ownership.SessionId));
        var disposition = await RunClaimedAsync(
            new ClaimedRun(config, options, detail, agentName, claimantId,
                claimContext, grant, workspace, invocation,
                RestoreSourceStatus: false, CleanupWorkspace: false,
                InvocationKind: AgentInvocationKind.Resume,
                ExpectedSessionId: sessionId),
            claim.ExpiresAt, emit, cancellationToken);
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
        CancellationToken cancellationToken,
        bool operatorRequested = false)
    {
        var adapter = adaptersByName[agentName];
        var workspacePath = Path.GetFullPath(session.WorkspacePath!);
        var repository = Path.GetFullPath(repositoryPath);
        var workspace = new Workspace(workspacePath, !SamePath(workspacePath, repository));
        var handle = new SessionHandle(session.SessionId!);
        var invocation = adapter.BuildResume(
            handle, workspace, WorkerPrompt.Append(
                WorkerPrompt.ForResume(detail.Id, agentName),
                WorkerPrompt.CommitInstruction(workspace, config.Worker?.Completion?.Commit)),
            PermissionsFor(config, agentName));
        if (options.DryRun)
        {
            await emit(new WorkerEvent(
                "dry-run",
                detail.Id.Value,
                agentName,
                workspace.Path,
                Arguments: [invocation.Executable, .. invocation.Arguments],
                Message: "Will acquire a new claim generation and resume the expired session.",
                SessionId: session.SessionId,
                Permissions: DescribePermissions(config, agentName)));
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

        var renewed = await tracker.RenewClaimAsync(
            config,
            detail.Id,
            grant,
            workspace.Path,
            session.SessionId,
            workspace.Branch ?? session.Branch,
            cancellationToken);

        var recoveryKind = session.Dispatch is null ? LaunchKind.Recovery : LaunchKind.Retry;
        var recoveryRequest = new LaunchPreflightRequest(
            config, options, detail, agentName, recoveryKind, LaunchStage.PreSpawn, session,
            operatorRequested);
        var preSpawn = await LaunchPreflight.EvaluateAsync(recoveryRequest, cancellationToken);
        if (!preSpawn.Admitted)
            return Summary(await ReleaseAfterPreflightRefusalAsync(
                new RefusedLaunch(recoveryRequest, grant, preSpawn, workspace),
                emit, cancellationToken, restoreSourceStatus: false, cleanupWorkspace: false));

        await ReportNotableAdmissionAsync(preSpawn, detail, agentName, emit);
        var recoveryContext = await TakeAndRecordContextAsync(
            config, detail, agentName, emit, cancellationToken);
        invocation = BuildResumeInvocation(
            adapter, config, detail, agentName, handle, workspace, recoveryContext);
        await emit(new WorkerEvent(
            session.Dispatch is null ? "resumed" : "retry-started",
            detail.Id.Value,
            agentName,
            workspace.Path,
            Arguments: [invocation.Executable, .. invocation.Arguments],
            Message: session.Dispatch is null
                ? "Recovered the recorded session under a new claim generation."
                : $"Started scheduled retry {session.Dispatch.Attempt} of " +
                  $"{session.Dispatch.MaxAttempts} under a new claim generation.",
            SessionId: session.SessionId,
            Dispatch: session.Dispatch));
        var disposition = await RunClaimedAsync(
            new ClaimedRun(config, options, detail, agentName, claimantId,
                claimContext, grant, workspace, invocation,
                RestoreSourceStatus: false, CleanupWorkspace: false,
                InvocationKind: AgentInvocationKind.Resume,
                ExpectedSessionId: session.SessionId),
            renewed.ExpiresAt, emit, cancellationToken,
            session.Dispatch?.Attempt ?? 0, session.Dispatch);
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
                ClaimNotOwner,
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

    /// <summary>
    /// Resolves the vendor and adapter a recorded session must resume as, refusing anything that is
    /// not a complete, owned, same-installation address. Kept out of <see cref="ResumeAsync"/> so
    /// that method stays within its complexity budget as launch stages are added to it.
    /// </summary>
    private (string AgentName, IAgentAdapter Adapter, string SessionId, string WorkspacePath)
        ResolveResumeTarget(
        WorkerOptions options,
        WorkItemId id,
        ClaimOwnershipResult ownership,
        string repositoryPath)
    {
        if (ownership.State != ClaimOwnershipState.OwnedByCurrent)
            throw new TrackerException(
                ownership.State == ClaimOwnershipState.HeldByOther ? ClaimNotOwner : "CLAIM_NOT_FOUND",
                ownership.State == ClaimOwnershipState.HeldByOther
                    ? $"Work item '{id}' is claimed by another Wrighty installation."
                    : $"Work item '{id}' does not have an active resumable claim.",
                6);
        if (ownership.Agent is null || ownership.SessionId is null || ownership.WorkspacePath is null)
            throw new TrackerException("RESUME_ADDRESS_UNAVAILABLE",
                $"Claim '{id}' does not have a complete agent session address.", 5);

        var agentName = NormalizeAgent(ownership.Agent)!;
        var requestedAgent = NormalizeAgent(options.Agent);
        if (requestedAgent is not null && !string.Equals(requestedAgent, agentName,
                StringComparison.OrdinalIgnoreCase))
            throw new TrackerException("AGENT_MISMATCH",
                $"Recorded session '{id}' belongs to {agentName}, not {requestedAgent}.", 2);
        if (!adaptersByName.TryGetValue(agentName, out var adapter))
            throw new TrackerException("AGENT_UNSUPPORTED",
                $"Unsupported recorded agent '{agentName}'.", 3);
        EnsureAgentInstalled(agentName, id, "session");
        if (!Directory.Exists(ownership.WorkspacePath))
            throw new TrackerException("RESUME_ADDRESS_UNAVAILABLE",
                $"Recorded workspace does not exist: {ownership.WorkspacePath}", 5);
        if (!SamePath(ownership.WorkspacePath, repositoryPath))
            skills.EnsureWorktreeReady(
                ownership.Agent,
                repositoryPath,
                ownership.WorkspacePath);
        return (agentName, adapter, ownership.SessionId, ownership.WorkspacePath);
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
        var agentName = NormalizeAgent(session.Agent)!;
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
        EnsureAgentInstalled(agentName, id, "session");
        if (!Directory.Exists(session.WorkspacePath))
            throw new TrackerException(
                "RESUME_ADDRESS_UNAVAILABLE",
                $"Recorded workspace does not exist: {session.WorkspacePath}. " +
                "Use --fresh explicitly to start without the recorded session.",
                5);
        if (!SamePath(session.WorkspacePath!, repositoryPath))
            skills.EnsureWorktreeReady(
                session.Agent!,
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
        var handle = adapter.Agent == "claude"
            ? SessionHandles.ForClaude(detail.Id, claimGeneration)
            : SessionHandles.ForNamedVendor(detail.Id, claimGeneration);
        var claimContext = new AgentExecutionContext(agentName,
            adapter.SupportsPreassignedHandle ? handle.Value : null,
            AgentContextSource.ExplicitOption, ClaimantKind: kind,
            ClaimantId: claimantId, ClaimToken: claim.ClaimToken);
        var grant = new ClaimHandle(claimContext, claim.ClaimToken);
        var revalidated = await tracker.GetAsync(config, detail.Id, cancellationToken);
        var postClaimRequest = new LaunchPreflightRequest(
            config, options, revalidated, agentName, LaunchKind.Fresh, LaunchStage.PostClaim);
        var postClaim = await LaunchPreflight.EvaluateAsync(postClaimRequest, cancellationToken);
        if (!postClaim.Admitted)
            return await ReleaseAfterPreflightRefusalAsync(
                new RefusedLaunch(postClaimRequest, grant, postClaim, Workspace: null),
                emit, cancellationToken);

        detail = revalidated;
        var workspace = await workspaces.PrepareAsync(
            new WorkspaceRequest(
                options.WorkspaceMode, repositoryPath, detail.Id, claimantId,
                claim.WorkspacePath, detail.Title, agentName, config.Worker),
            cancellationToken);

        // This metadata transition is fenced and happens before spawn, closing the workspace/session
        // orphan window for preassigned-handle vendors.
        ClaimResult prepared;
        try
        {
            prepared = await tracker.RenewClaimAsync(config, detail.Id, grant, workspace.Path,
                claimContext.SessionId, workspace.Branch, cancellationToken);
            detail = await ClearDispatchStateAsync(
                config, detail, grant, cancellationToken);
        }
        catch (TrackerException exception) when (
            exception.Code is ClaimStale or ClaimExpired or ClaimNotOwner)
        {
            await emit(new WorkerEvent(FencedEvent, detail.Id.Value, agentName, workspace.Path,
                Message: exception.Code));
            return WorkerItemDisposition.Fenced;
        }
        // The last gate before a vendor process exists. Workspace and session preparation are
        // themselves observable to collaborators, so anything that changed during them must be
        // caught here rather than reaching the agent.
        var preSpawnRequest = new LaunchPreflightRequest(
            config, options, detail, agentName, LaunchKind.Fresh, LaunchStage.PreSpawn);
        var preSpawn = await LaunchPreflight.EvaluateAsync(preSpawnRequest, cancellationToken);
        if (!preSpawn.Admitted)
            return await ReleaseAfterPreflightRefusalAsync(
                new RefusedLaunch(preSpawnRequest, grant, preSpawn, workspace),
                emit, cancellationToken);

        var resolvedContext = await TakeAndRecordContextAsync(
            config, detail, agentName, emit, cancellationToken);

        // With an approved context, the agent is given the content itself rather than told to go and
        // read the item. Reading the item would return whatever is on the tracker now — unapproved
        // comments, post-approval edits — which is what the gate above just refused to allow, so the
        // bootstrap prompt would walk around a check that had already been paid for.
        //
        // Without one, the backend has no approval surface and the bootstrap prompt is still how an
        // agent learns what to do.
        var commitInstruction =
            WorkerPrompt.CommitInstruction(workspace, config.Worker?.Completion?.Commit);
        var invocation = resolvedContext is { } approved
            ? adapter.BuildStartWithPrompt(handle, workspace, PermissionsFor(config, agentName),
                ApprovedContext.ExecutionPromptRenderer.ForFreshLaunch(
                    approved.Snapshot,
                    WorkerPrompt.OperatingInstructions(detail.Id),
                    commitInstruction))
            : adapter.BuildStart(detail, handle, workspace,
                PermissionsFor(config, agentName), commitInstruction);
        await emit(new WorkerEvent("started", detail.Id.Value, agentName, workspace.Path,
            Arguments: [invocation.Executable, .. invocation.Arguments],
            Permissions: DescribePermissions(config, agentName)));
        return await RunClaimedAsync(
            new ClaimedRun(config, options, detail, agentName, claimantId,
                claimContext, grant, workspace, invocation,
                RestoreSourceStatus: true, CleanupWorkspace: true,
                InvocationKind: AgentInvocationKind.Start,
                ExpectedSessionId: null),
            prepared.ExpiresAt, emit, cancellationToken);
    }

    /// <summary>
    /// Undoes a launch the preflight refused: restore the source status, drop any workspace this
    /// aborted launch created, and release the claim so another worker can retry once the refusal
    /// is resolved. A dirty worktree is deliberately retained by the workspace manager — and a
    /// retained workspace this launch did not create is never a cleanup target at all.
    /// </summary>
    /// <summary>
    /// A launch the preflight refused, with everything needed to unwind it. The request is reused
    /// rather than re-passing its config, options, item and agent, so the release path cannot
    /// disagree with the evaluation about which launch it is unwinding.
    /// </summary>
    private sealed record RefusedLaunch(
        LaunchPreflightRequest Request,
        ClaimHandle Grant,
        LaunchPreflightResult Result,
        Workspace? Workspace);

    private async Task<WorkerItemDisposition> ReleaseAfterPreflightRefusalAsync(
        RefusedLaunch refused,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken,
        bool restoreSourceStatus = true,
        bool cleanupWorkspace = true)
    {
        var (request, grant, refusal, workspace) = refused;
        var (config, options, detail, agentName, _, _, _, _) = request;
        try
        {
            var sourceStatus = options.FromStatus ?? config.DefaultPickFrom;
            var targetStatus = options.ToStatus ?? config.DefaultPickTo;
            try
            {
                // Only a fresh launch moved the item into the active status, so only a fresh
                // launch may move it back. A refused resume leaves an already-active item alone.
                if (restoreSourceStatus &&
                    !string.IsNullOrWhiteSpace(sourceStatus) &&
                    !string.Equals(sourceStatus, targetStatus, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(detail.Status, targetStatus, StringComparison.OrdinalIgnoreCase))
                {
                    await tracker.UpdateAsync(
                        config,
                        detail.Id,
                        WorkItemPatch.StatusOnly(sourceStatus),
                        expectedRevision: null,
                        grant,
                        cancellationToken);
                }
            }
            catch (TrackerException exception) when (
                exception.Code is not (ClaimStale or ClaimExpired or ClaimNotOwner))
            {
                await emit(new WorkerEvent(
                    "policy-status-restore-failed",
                    detail.Id.Value,
                    agentName,
                    Message: exception.Code));
            }

            if (cleanupWorkspace && workspace is not null)
                await workspaces.CleanupAsync(workspace, cancellationToken);

            // How the release treats the dispatch state depends on what the launch was. A refused
            // fresh launch has nothing behind it: the item goes back to the claimable pool and the
            // status restore above is its whole unwind. A refused re-entry of a RECORDED session
            // was put in motion by a person — an operator queued it from needs-attention, or a
            // retry was scheduled against it — and the plain release cleared that dispatch state,
            // which read as the item having nothing left to do. It dropped out of needs-attention,
            // where the queue action lives, so the refusal erased the operator's own way of acting
            // on it. Seen live when a queued session's recorded context could not be established.
            //
            // Needs-attention rather than whatever dispatch state it arrived with: the refusal is
            // unresolved and needs a person, and leaving the item queued would re-refuse on every
            // poll while telling nobody anything new.
            if (request.Kind is LaunchKind.Fresh)
            {
                await tracker.ReleaseAsync(config, detail.Id, grant, false, cancellationToken);
            }
            else
            {
                await MarkNeedsAttentionAsync(config, detail.Id, grant, cancellationToken);
                await tracker.ReleasePreservingDispatchStateAsync(
                    config, detail.Id, grant, cancellationToken);
            }
        }
        catch (TrackerException exception) when (
            exception.Code is ClaimStale or ClaimExpired or ClaimNotOwner)
        {
            await emit(new WorkerEvent(
                "fenced",
                detail.Id.Value,
                agentName,
                workspace?.Path,
                Message: exception.Code));
            return WorkerItemDisposition.Fenced;
        }

        var released = workspace is null
            ? "the claim was released before workspace creation or agent launch."
            : "the claim was released before the agent was launched.";
        var reason = refusal.Message ?? "A launch check refused this run.";
        await emit(new WorkerEvent(
            "skipped-policy",
            detail.Id.Value,
            agentName,
            workspace?.Path,
            Message: $"{reason} Refused by the {StageName(refusal.Stage)} check " +
                     $"'{refusal.RefusedBy}' ({refusal.Code}); {released}"));
        return WorkerItemDisposition.Skipped;
    }

    private static string StageName(LaunchStage stage) => stage switch
    {
        LaunchStage.PreClaim => "pre-claim",
        LaunchStage.PostClaim => "post-claim",
        _ => "pre-spawn"
    };

    /// <summary>
    /// Everything a claimed run needs that is fixed at launch. Bundled so the run entry point keeps
    /// a readable signature as launch concerns accumulate, and so callers cannot transpose two
    /// same-typed arguments.
    /// </summary>
    private sealed record ClaimedRun(
        TrackerConfig Config,
        WorkerOptions Options,
        WorkItemDetail Detail,
        string AgentName,
        string ClaimantId,
        AgentExecutionContext ClaimContext,
        ClaimHandle Grant,
        Workspace Workspace,
        AgentInvocation Invocation,
        bool RestoreSourceStatus,
        bool CleanupWorkspace,
        AgentInvocationKind InvocationKind,
        string? ExpectedSessionId);

    private enum AgentInvocationKind
    {
        Start,
        Resume
    }

    private async Task<WorkerItemDisposition> RunClaimedAsync(
        ClaimedRun run,
        DateTimeOffset initialClaimExpiresAt,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken,
        int recoveryAttempt = 0,
        DispatchInfo? recoveryDispatch = null)
    {
        var (config, options, detail, agentName, claimantId,
            claimContext, grant, workspace, invocation, _, _, invocationKind,
            expectedSessionId) = run;
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
        var startedAt = now();
        var deadline = startedAt + options.ItemTimeout;
        var fenceState = new RunFenceState();
        string? unexpectedSessionId = null;
        var leaseTask = KeepAliveAsync(config, detail.Id, grant, workspace.Path,
            startedAt, deadline, initialClaimExpiresAt,
            options, emit, runCts, leaseCts.Token, () => fenceState.Fenced = true,
            () => runCts.Cancel());
        AgentRunResult result;
        try
        {
            result = await processes.RunAsync(
                invocation,
                adapter,
                options.ItemTimeout,
                environment,
                (sessionId, token) =>
                {
                    if (invocationKind == AgentInvocationKind.Resume &&
                        expectedSessionId is not null &&
                        !SessionIdsEqual(expectedSessionId, sessionId))
                    {
                        unexpectedSessionId = sessionId;
                        return Task.CompletedTask;
                    }
                    return RecordSessionAsync(
                        config, detail, agentName, workspace, grant, sessionId, token,
                        fenceState, runCts, emit);
                },
                options.OnFenced == FencedAction.Kill,
                runCts.Token);
        }
        catch (TrackerException exception) when (exception.Code == "AGENT_START_FAILED")
        {
            await StopLeaseAsync(leaseCts, leaseTask);
            return await HandleAgentStartFailureAsync(run, exception, emit);
        }
        await StopLeaseAsync(leaseCts, leaseTask);

        result = EnforceExpectedSessionIdentity(
            result,
            invocationKind,
            expectedSessionId,
            unexpectedSessionId,
            agentName);

        // A resume is always fenced to its recorded identity. Even a rejected vendor result must
        // keep reporting and renewing that original address rather than replacing it with a value
        // returned by the process.
        var sessionId = expectedSessionId ?? result.SessionId ?? claimContext.SessionId;
        if (recoveryDispatch is not null && cancellationToken.IsCancellationRequested)
        {
            await RestoreInterruptedRetryAsync(
                config, detail.Id, grant, agentName, workspace, sessionId,
                recoveryDispatch, emit);
            return WorkerItemDisposition.Rejected;
        }

        if (fenceState.Fenced)
        {
            await emit(new WorkerEvent(FencedEvent, detail.Id.Value, agentName,
                workspace.Path, result.Outcome, EventMessage(result), SessionId: sessionId,
                Failure: result.Failure));
            return WorkerItemDisposition.Fenced;
        }

        if (providerCapacityEnabled && !IsUsageCapacityFailure(result.Failure))
            await providerCapacity.RecordAvailableAsync(agentName, now(), cancellationToken);

        if (recoveryDispatch is not null && !IsUsageCapacityFailure(result.Failure))
            detail = await ClearDispatchStateAsync(
                config, detail, grant, cancellationToken);

        return result.Outcome == AgentOutcome.Succeeded
            ? await HandleSuccessfulRunAsync(
                config, options, detail, agentName, adapter, grant, workspace,
                result, sessionId, emit, cancellationToken)
            : await HandleFailedRunAsync(
                config, options,
                new EndedRun(detail, agentName, grant, workspace, result, sessionId),
                adapter, emit, cancellationToken, recoveryAttempt);
    }

    private async Task<WorkerItemDisposition> HandleAgentStartFailureAsync(
        ClaimedRun run,
        TrackerException failure,
        Func<WorkerEvent, Task> emit)
    {
        var (config, options, detail, agentName, _, _, grant, workspace, _,
            restoreSourceStatus, cleanupWorkspace, _, _) = run;
        var cleanupIncomplete = false;
        try
        {
            var sourceStatus = options.FromStatus ?? config.DefaultPickFrom;
            var targetStatus = options.ToStatus ?? config.DefaultPickTo;
            if (restoreSourceStatus &&
                !string.IsNullOrWhiteSpace(sourceStatus) &&
                !string.Equals(sourceStatus, targetStatus, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(detail.Status, targetStatus, StringComparison.OrdinalIgnoreCase))
            {
                using var restoreTimeout =
                    new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await tracker.UpdateAsync(
                    config,
                    detail.Id,
                    WorkItemPatch.StatusOnly(sourceStatus),
                    expectedRevision: null,
                    grant,
                    restoreTimeout.Token);
            }
        }
        catch (TrackerException exception) when (
            exception.Code is ClaimStale or ClaimExpired or ClaimNotOwner)
        {
            await emit(new WorkerEvent(
                FencedEvent,
                detail.Id.Value,
                agentName,
                workspace.Path,
                AgentOutcome.Failed,
                exception.Code));
            return WorkerItemDisposition.Fenced;
        }
        catch (Exception)
        {
            cleanupIncomplete = true;
        }

        if (cleanupWorkspace)
        {
            try
            {
                using var workspaceTimeout =
                    new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await workspaces.CleanupAsync(workspace, workspaceTimeout.Token);
            }
            catch (Exception)
            {
                cleanupIncomplete = true;
            }
        }

        try
        {
            using var releaseTimeout =
                new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ReleaseAfterFailureAsync(
                config,
                detail.Id,
                grant,
                releaseTimeout.Token);
        }
        catch (TrackerException exception) when (
            exception.Code is ClaimStale or ClaimExpired or ClaimNotOwner)
        {
            await emit(new WorkerEvent(
                FencedEvent,
                detail.Id.Value,
                agentName,
                workspace.Path,
                AgentOutcome.Failed,
                exception.Code));
            return WorkerItemDisposition.Fenced;
        }

        await emit(new WorkerEvent(
            "failed",
            detail.Id.Value,
            agentName,
            workspace.Path,
            AgentOutcome.Failed,
            $"AGENT_START_FAILED: {failure.Message} The exact claim generation was released." +
            (cleanupIncomplete
                ? " Some status or workspace cleanup was incomplete; inspect the item and workspace."
                : string.Empty)));
        return WorkerItemDisposition.Failed;
    }

    private static async Task StopLeaseAsync(
        CancellationTokenSource leaseCancellation,
        Task leaseTask)
    {
        await leaseCancellation.CancelAsync();
        try
        {
            await leaseTask;
        }
        catch (OperationCanceledException) when (leaseCancellation.IsCancellationRequested)
        {
            // Cancellation is the expected completion path for the lease-renewal loop.
        }
    }

    private async Task RestoreInterruptedRetryAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle grant,
        string agentName,
        Workspace workspace,
        string? sessionId,
        DispatchInfo dispatch,
        Func<WorkerEvent, Task> emit)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await SetDispatchStateAsync(
                config, id, grant, DispatchStates.RetryScheduled, cleanup.Token);
            await ReleaseAfterFailureAsync(config, id, grant, cleanup.Token);
            await emit(new WorkerEvent(
                "retry-interrupted",
                id.Value,
                agentName,
                workspace.Path,
                AgentOutcome.Rejected,
                "The scheduled retry was interrupted; it remains due for another worker.",
                SessionId: sessionId,
                Dispatch: dispatch));
        }
        catch (Exception exception) when (
            exception is TrackerException or OperationCanceledException)
        {
            // The machine-local dispatch remains durable. Once this claim expires, queued
            // discovery can reconstruct the missing portable marker and retry safely.
        }
    }

    private async Task RecordSessionAsync(
        TrackerConfig config,
        WorkItemDetail detail,
        string agentName,
        Workspace workspace,
        ClaimHandle grant,
        string sessionId,
        CancellationToken cancellationToken,
        RunFenceState fenceState,
        CancellationTokenSource runCts,
        Func<WorkerEvent, Task> emit)
    {
        try
        {
            await tracker.RenewClaimAsync(
                config, detail.Id, grant, workspace.Path, sessionId, workspace.Branch,
                cancellationToken);
            await emit(new WorkerEvent(
                "session", detail.Id.Value, agentName, workspace.Path, Message: sessionId));
        }
        catch (TrackerException exception) when (
            exception.Code is ClaimStale or ClaimExpired or ClaimNotOwner)
        {
            fenceState.Fenced = true;
            await emit(new WorkerEvent(
                "fenced", detail.Id.Value, agentName, workspace.Path, Message: exception.Code));
            runCts.Cancel();
        }
    }

    private async Task<WorkerItemDisposition> HandleSuccessfulRunAsync(
        TrackerConfig config,
        WorkerOptions options,
        WorkItemDetail detail,
        string agentName,
        IAgentAdapter adapter,
        ClaimHandle grant,
        Workspace workspace,
        AgentRunResult result,
        string? sessionId,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        try
        {
            // A fenced renewal is the atomic residual-claim test. Success means the vendor
            // process exited without calling finish/release, so retain the resumable claim.
            var retained = await tracker.RenewClaimAsync(
                config, detail.Id, grant, workspace.Path, sessionId, cancellationToken);
            await MarkNeedsAttentionAsync(config, detail.Id, grant, cancellationToken);
            await RecordRunOutcomeAsync(config, detail.Id, result, cancellationToken,
                ApprovedContext.RunReportDisposition.NeedsAttention, agentName, workspace.Branch);
            var attentionActions = NeedsAttentionActions(
                detail.Id, agentName, OperatorSurface.For(config, detail.Url),
                retained.ExpiresAt);
            await PostHandoverAsync(
                config, detail.Id, HandoverPhase.NeedsAttention, result, workspace,
                attentionActions, cancellationToken);
            await emit(new WorkerEvent(
                NeedsAttentionEvent, detail.Id.Value, agentName, workspace.Path,
                result.Outcome, EventMessage(result), SessionId: sessionId,
                ClaimExpiresAt: retained.ExpiresAt,
                OperatorActions: attentionActions,
                Failure: result.Failure));
            return WorkerItemDisposition.NeedsAttention;
        }
        catch (TrackerException exception) when (
            exception.Code is ClaimStale or ClaimNotOwner)
        {
            await emit(new WorkerEvent(
                "fenced", detail.Id.Value, agentName, workspace.Path,
                result.Outcome, exception.Code, SessionId: sessionId));
            return WorkerItemDisposition.Fenced;
        }
        catch (TrackerException exception) when (
            exception.Code is "CLAIM_NOT_FOUND" or ClaimExpired)
        {
            return await HandleEndedSuccessfulClaimAsync(
                config, options, detail, agentName, adapter, workspace,
                result, sessionId, emit, cancellationToken);
        }
    }

    private const int FinalMessageMaxLength = 2000;

    private static RunOutcome ToRunOutcome(AgentOutcome outcome) => outcome switch
    {
        AgentOutcome.Rejected => RunOutcome.Rejected,
        AgentOutcome.Succeeded => RunOutcome.Succeeded,
        _ => RunOutcome.Failed
    };

    /// <summary>
    /// What an event carries as the agent's closing words: the final message without its report
    /// block.
    ///
    /// The block's content reaches an operator as structured fields on every surface that renders a
    /// run, so repeating it here says the same thing twice. It also renders badly: an event message
    /// is truncated for a terminal, and truncating JSON mid-object leaves a fenced block that never
    /// closes.
    /// </summary>
    private static string? EventMessage(AgentRunResult result) =>
        ApprovedContext.AgentReportParser.WithoutReportBlock(result.FinalMessage);

    /// <summary>
    /// The agent's closing words as the durable record keeps them: report block removed first, then
    /// bounded.
    ///
    /// The order is the whole point. Bounding first can cut inside the report block, and a block
    /// that loses its closing fence is no longer removable — every later reader calls
    /// <see cref="ApprovedContext.AgentReportParser.WithoutReportBlock"/>, matches nothing, and
    /// hands an operator half a JSON object as the agent's closing words. Stripping first means a
    /// partial block cannot be stored in the first place.
    ///
    /// The report itself is parsed from the complete response before this runs, so nothing
    /// structured depends on what survives here.
    /// </summary>
    private static string? StoredFinalMessage(string? message)
    {
        var stripped = ApprovedContext.AgentReportParser.WithoutReportBlock(message);
        if (string.IsNullOrWhiteSpace(stripped))
            return null;
        if (stripped.Length <= FinalMessageMaxLength)
            return stripped;

        // Never split a surrogate pair: half of one is not a character, and it travels into JSON,
        // Markdown and a terminal as a replacement glyph or an encoding error.
        var cut = FinalMessageMaxLength;
        if (char.IsHighSurrogate(stripped[cut - 1]))
            cut--;

        // Marked, because a message that simply stops is indistinguishable from an agent that
        // stopped — the same reason BoundedFallback marks its own cut.
        return stripped[..cut] + "\n… (truncated)";
    }

    /// <summary>
    /// Persists the just-ended run's outcome to the durable session record so the "what happened"
    /// signal survives the worker terminal (surfaced in wrighty get/status, the web item panel, and
    /// the GitHub handover comment). Best-effort: the durable capture must never fail the run.
    /// </summary>
    /// <summary>
    /// Reports a launch that was admitted despite something a check would otherwise refuse.
    ///
    /// Without this, an operator-requested resume across a changed item is indistinguishable in the
    /// log from one where nothing had changed — and the whole basis for allowing it is that somebody
    /// decided to, which is worth a line saying so.
    /// </summary>
    private static async Task ReportNotableAdmissionAsync(
        LaunchPreflightResult result,
        WorkItemDetail detail,
        string agentName,
        Func<WorkerEvent, Task> emit)
    {
        if (!result.Admitted || result.Code is null) return;
        await emit(new WorkerEvent(
            "policy-override",
            detail.Id.Value,
            agentName,
            Message: $"{result.Message ?? "A launch check admitted this run with a notice."} " +
                     $"({result.Code})"));
    }

    /// <summary>
    /// Records what an admitted launch resolved as the approved context, so a later launch can
    /// establish what this session was already given rather than guessing.
    ///
    /// Called after the pre-spawn stage admits and before the process starts, which is the only
    /// point at which the context is both final and not yet delivered. A check that resolved
    /// nothing contributes nothing — the ordinary case for a backend with no approval surface.
    ///
    /// A write failure is logged and does not stop the launch. Degrading the other way would let a
    /// machine-local cache problem block work that is properly approved, and the failure is already
    /// safe: a session with no recorded context refuses to resume rather than assuming.
    /// </summary>
    /// <summary>
    /// The invocation for re-entering a recorded session.
    ///
    /// With a resolved context the prompt carries rendered content on standard input: the delta for
    /// an ordinary resume, or the complete current snapshot when an operator overrode a blocking
    /// change. Without one the generic clarified-item prompt is used, which carries no item content
    /// and so is safe on the command line; that is the case for a backend with no approval surface,
    /// and for a session whose context this launch could not resolve.
    /// </summary>
    private AgentInvocation BuildResumeInvocation(
        IAgentAdapter adapter,
        TrackerConfig config,
        WorkItemDetail detail,
        string agentName,
        SessionHandle handle,
        Workspace workspace,
        ApprovedContext.ResolvedLaunchContext? resolved)
    {
        var commitInstruction =
            WorkerPrompt.CommitInstruction(workspace, config.Worker?.Completion?.Commit);
        var permissions = PermissionsFor(config, agentName);

        if (resolved is { Comparison: { } comparison, Previous.Manifest: { } supplied })
        {
            // Which variant the change deserves follows from the classification, so the renderer
            // decides it. A blocking comparison reaching here means an operator asked for this run,
            // overriding a change the unattended rule refused; that resume carries the complete
            // current snapshot, because a non-additive change has no delta and what the session
            // already holds is what has to stop being authoritative.
            var prompt = ApprovedContext.ExecutionPromptRenderer.ForClassifiedResume(
                resolved.Snapshot, comparison, supplied,
                WorkerPrompt.OperatingInstructions(detail.Id), commitInstruction);

            return adapter.BuildResumeWithPrompt(handle, workspace, permissions, prompt);
        }

        // No rendered context to send: a backend with no execution-context provider, or a launch
        // that could not resolve one. This is the only remaining caller of the prompt that tells an
        // agent to re-read the item for itself, and it is correct precisely because there is
        // nothing approved to hand over instead.
        return adapter.BuildResume(handle, workspace,
            WorkerPrompt.Append(WorkerPrompt.ForResume(detail.Id, agentName), commitInstruction),
            permissions);
    }

    private async Task<ApprovedContext.ResolvedLaunchContext?> TakeAndRecordContextAsync(
        TrackerConfig config,
        WorkItemDetail detail,
        string agentName,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        ApprovedContext.ResolvedLaunchContext? taken = null;
        foreach (var source in (launchPreflightChecks ?? []).OfType<ILaunchSessionContextSource>())
        {
            if (source.TakeResolvedContext(detail.Id) is not { } resolved) continue;
            taken = resolved;
            var context = resolved.SessionContext;
            try
            {
                await tracker.RecordSessionContextAsync(
                    config, detail.Id, context, cancellationToken);
            }
            catch (TrackerException exception)
            {
                await emit(new WorkerEvent(
                    "context-record-failed",
                    detail.Id.Value,
                    agentName,
                    Message: $"The approved context could not be recorded with the session " +
                             $"({exception.Code}). This run is unaffected, but resuming it later " +
                             "will need a fresh session."));
            }
        }

        return taken;
    }

    private async Task RecordRunOutcomeAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentRunResult result,
        CancellationToken cancellationToken,
        ApprovedContext.RunReportDisposition? disposition = null,
        string? agentName = null,
        string? branch = null)
    {
        try
        {
            await tracker.RecordRunOutcomeAsync(
                config, id, ToRunOutcome(result.Outcome),
                StoredFinalMessage(result.FinalMessage), now(), result.Failure,
                cancellationToken);
        }
        catch (TrackerException)
        {
            // Swallow: a session-record write failure must not turn a completed run into a failure.
        }

        if (disposition is { } observed)
            await PublishRunReportAsync(
                config, id, result, observed, agentName, branch, cancellationToken);
    }

    /// <summary>
    /// Publishes the durable record of a finished run, when configured to.
    ///
    /// The disposition comes from the caller because only the caller knows it: a vendor process
    /// that exited cleanly without Wrighty observing the completion state is needs-attention, not
    /// finished, and nothing in the run result distinguishes those.
    ///
    /// Off by default, and best-effort when on. Publishing writes to a surface other people read,
    /// so a failure to write must not turn a completed run into a failed one — the run happened
    /// either way, and the local record already has it.
    /// </summary>
    private async Task PublishRunReportAsync(
        TrackerConfig config,
        WorkItemId id,
        AgentRunResult result,
        ApprovedContext.RunReportDisposition disposition,
        string? agentName,
        string? branch,
        CancellationToken cancellationToken)
    {
        // The vendor session identifies the run: it is stable across a republish and changes when a
        // retry starts a new session, so a retry records its own report instead of overwriting the
        // attempt before it.
        var runId = result.SessionId ?? $"{id.Value}@{now():O}";
        var report = ApprovedContext.RunReportRenderer.Build(
            new ApprovedContext.RunIdentity(id, runId, agentName ?? "unknown"),
            disposition, result.Outcome, now(), result.Report, result.ReportFallback);

        // Stored regardless of the mode. Publishing decides whether other people see the report;
        // storing decides whether it survives at all, and an agent's account of what it decided
        // should not be lost because nobody wanted it commented on the issue.
        try
        {
            await tracker.RecordRunReportAsync(config, id, report, cancellationToken);
        }
        catch (TrackerException)
        {
            // Best-effort by design; see above.
        }

        var mode = config.EffectiveWorker.EffectiveSessionReportMode;
        if (mode == ApprovedContext.SessionReportMode.Off) return;
        if (mode == ApprovedContext.SessionReportMode.Completed &&
            disposition != ApprovedContext.RunReportDisposition.Finished)
            return;

        try
        {
            await tracker.PublishRunReportAsync(config, id, report, branch, cancellationToken);
        }
        catch (TrackerException)
        {
            // Best-effort by design; see above.
        }
    }

    /// <summary>
    /// Posts (or overwrites) the single GitHub handover comment for a terminal run. Best-effort and
    /// backend-neutral: skipped when handoverComment=off, a no-op on the Local Markdown backend, and
    /// a failure never fails the run.
    /// </summary>
    private async Task PostHandoverAsync(
        TrackerConfig config,
        WorkItemId id,
        HandoverPhase phase,
        AgentRunResult result,
        Workspace workspace,
        IReadOnlyList<WorkerOperatorAction>? actions,
        CancellationToken cancellationToken,
        DispatchInfo? dispatch = null,
        ProviderCapacity? provider = null,
        WorkItemPolicyPresentation? workerPolicy = null)
    {
        var mode = config.EffectiveWorker.EffectiveHandoverComment;
        if (mode == HandoverCommentMode.Off)
            return;
        try
        {
            // With shareLocalPaths=false the absolute workspace path is not published in the "Where"
            // line (the caller has already swapped in path-free completion commands).
            var shareLocalPaths = config.EffectiveWorker.ShareLocalPaths;
            var content = new HandoverContent(
                id,
                phase,
                ToRunOutcome(result.Outcome),
                phase == HandoverPhase.RetryScheduled
                    ? result.Failure?.SanitizedMessage
                    : StoredFinalMessage(result.FinalMessage),
                await hostLabel.GetHostLabelAsync(cancellationToken),
                shareLocalPaths ? workspace.Path : null,
                workspace.Branch,
                actions ?? [],
                mode,
                dispatch,
                provider,
                workerPolicy);
            await tracker.PostHandoverAsync(config, content, cancellationToken);
        }
        catch (TrackerException)
        {
            // Best-effort: a handover-comment write failure must not fail the run.
        }
    }

    private async Task ResolveHandoverAsync(
        TrackerConfig config,
        WorkItemId id,
        string reason,
        CancellationToken cancellationToken)
    {
        if (config.EffectiveWorker.EffectiveHandoverComment == HandoverCommentMode.Off)
            return;
        try
        {
            await tracker.ResolveHandoverAsync(config, id, reason, cancellationToken);
        }
        catch (TrackerException)
        {
            // Best-effort: trimming a stale handover comment must not fail the run.
        }
    }

    private async Task MarkNeedsAttentionAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle grant,
        CancellationToken cancellationToken) =>
        await SetDispatchStateAsync(
            config,
            id,
            grant,
            DispatchStates.NeedsAttention,
            cancellationToken);

    private async Task SetDispatchStateAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle grant,
        string dispatchState,
        CancellationToken cancellationToken)
    {
        await tracker.UpdateAsync(
            config,
            id,
            new WorkItemPatch(
                OptionalValue<string>.Unspecified,
                OptionalValue<string>.Unspecified,
                OptionalValue<string>.Unspecified,
                OptionalValue<string?>.Unspecified,
                DispatchState: OptionalValue<string?>.From(dispatchState)),
            expectedRevision: null,
            grant,
            cancellationToken);
    }

    private async Task<WorkerItemDisposition> HandleEndedSuccessfulClaimAsync(
        TrackerConfig config,
        WorkerOptions options,
        WorkItemDetail detail,
        string agentName,
        IAgentAdapter adapter,
        Workspace workspace,
        AgentRunResult result,
        string? sessionId,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var current = await tracker.GetAsync(config, detail.Id, cancellationToken);
        if (!current.Archived && !string.Equals(
                current.Status, config.DefaultFinishTo, StringComparison.OrdinalIgnoreCase))
        {
            await RecordRunOutcomeAsync(config, detail.Id, result, cancellationToken,
                ApprovedContext.RunReportDisposition.NeedsAttention, agentName, workspace.Branch);
            var attentionActions = NeedsAttentionActions(detail.Id, agentName, OperatorSurface.For(config, detail.Url));
            await PostHandoverAsync(
                config, detail.Id, HandoverPhase.NeedsAttention, result, workspace,
                attentionActions, cancellationToken);
            await emit(new WorkerEvent(
                NeedsAttentionEvent, detail.Id.Value, agentName, workspace.Path,
                result.Outcome, EventMessage(result), SessionId: sessionId,
                OperatorActions: attentionActions,
                Failure: result.Failure));
            return WorkerItemDisposition.NeedsAttention;
        }
        // Under the inspect commit policy the worktree is the operator's review queue: skip the
        // cleanup attempt instead of relying on git's dirty-tree refusal.
        var inspect = workspace.IsWorktree && !AgentCommitPolicy(config);
        var cleanupAttempted = workspace.IsWorktree && !options.KeepWorkspace && !inspect;
        var workspaceRemoved = cleanupAttempted &&
                               await workspaces.CleanupAsync(workspace, cancellationToken);
        // Cleanup was expected but git refused (uncommitted or untracked files remain, often tool
        // artifacts). The worktree is safely retained; tell the operator why rather than removing
        // it silently.
        var cleanupRefused = cleanupAttempted && !workspaceRemoved;
        var reviewCommand = ReviewCommand(adapter, workspace, sessionId, workspaceRemoved);
        await RecordRunOutcomeAsync(config, detail.Id, result, cancellationToken,
            ApprovedContext.RunReportDisposition.Finished, agentName, workspace.Branch);
        var completionActions = CompletionActions(
            config, detail.Id, workspace, workspaceRemoved, sessionId, cleanupRefused);
        // The terminal (recording host) keeps the pathful git commands; the GitHub handover uses
        // the path-free variant when shareLocalPaths=false so no absolute path is published.
        var handoverActions = config.EffectiveWorker.ShareLocalPaths
            ? completionActions
            : RedactedCompletionActions(detail.Id, workspace, sessionId);
        // The handover comment is the review queue's discovery surface. It is only meaningful while
        // a worktree is retained for inspection; once cleanup removed it, the item is done and the
        // instructions would be stale, so trim any prior handover to its resolved form instead.
        if (!workspaceRemoved)
            await PostHandoverAsync(
                config, detail.Id, HandoverPhase.Completed, result, workspace,
                handoverActions, cancellationToken);
        else
            await ResolveHandoverAsync(
                config, detail.Id, "The item finished and its workspace was cleaned up.",
                cancellationToken);
        await emit(new WorkerEvent(
            "finished", detail.Id.Value, agentName, workspace.Path,
            result.Outcome, EventMessage(result), SessionId: sessionId,
            ReviewCommand: reviewCommand,
            OperatorActions: completionActions,
            Branch: workspace.Branch,
            Failure: result.Failure));
        if (workspaceRemoved)
            await emit(new WorkerEvent(
                "workspace-removed", detail.Id.Value, agentName, workspace.Path));
        return WorkerItemDisposition.Finished;
    }

    private static bool AgentCommitPolicy(TrackerConfig config) =>
        string.Equals(config.Worker?.Completion?.Commit, "agent",
            StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<WorkerOperatorAction>? CompletionActions(
        TrackerConfig config,
        WorkItemId id,
        Workspace workspace,
        bool workspaceRemoved,
        string? sessionId,
        bool cleanupRefused = false)
    {
        if (!workspace.IsWorktree || workspace.Branch is null)
            return null;
        var inspect = !AgentCommitPolicy(config);
        var path = InteractiveAgentCommand.Quote(workspace.Path);
        var branch = InteractiveAgentCommand.Quote(workspace.Branch);
        var actions = new List<WorkerOperatorAction>();
        if (cleanupRefused)
            actions.Add(new WorkerOperatorAction(
                "Worktree retained: git would not remove it",
                [$"cd {path} && git status"],
                "The work was committed, but git refused to remove the worktree because it still " +
                "contains uncommitted or untracked files (often tool artifacts such as " +
                ".memsearch/ or .claude/). Review them, then run " +
                $"'wrighty workspaces cleanup {id.Value}' — or add such paths to .gitignore so " +
                "future runs remove the worktree automatically."));
        if (inspect && !workspaceRemoved)
            actions.Add(new WorkerOperatorAction(
                "Review the uncommitted changes",
                [$"cd {path} && git status && git diff"],
                "The workspace is retained for review; changes are left uncommitted by policy " +
                "(worker.completion.commit=inspect)."));
        if (!workspaceRemoved && !string.IsNullOrWhiteSpace(sessionId))
            actions.Add(new WorkerOperatorAction(
                "Guided completion in the recorded session",
                [$"wrighty resume-command {id.Value}"],
                "Run this in your terminal: it prints the vendor command for the recorded session — " +
                "run that command to open the session interactively (or add --exec to open it in " +
                "one step). Then paste the follow-up prompt below into that session and approve " +
                "each step.",
                AgentPrompt: $"/wrighty Complete item {id.Value}: summarize the diff, propose a " +
                "commit message, and after my approval commit, integrate, clean up the workspace, " +
                "and archive the item."));
        var commit = inspect && !workspaceRemoved
            ? $"cd {path} && git add -A && git commit && cd -"
            : null;
        if (IntegrationAction(config, path, branch, commit, workspaceRemoved) is { } integration)
            actions.Add(integration);
        return actions.Count == 0 ? null : actions;
    }

    // Path-free completion guidance for the GitHub handover comment when shareLocalPaths=false: it
    // publishes no absolute worktree path, only wrighty commands that resolve the retained worktree
    // locally on the recording host. The raw git commands (with paths) still print to that host's
    // terminal via the pathful CompletionActions.
    private static IReadOnlyList<WorkerOperatorAction>? RedactedCompletionActions(
        WorkItemId id,
        Workspace workspace,
        string? sessionId)
    {
        if (!workspace.IsWorktree || workspace.Branch is null)
            return null;
        var actions = new List<WorkerOperatorAction>();
        if (!string.IsNullOrWhiteSpace(sessionId))
            actions.Add(new WorkerOperatorAction(
                "Guided completion in the recorded session",
                [$"wrighty resume-command {id.Value} --exec"],
                "On the recording host, run this to open the recorded session — it resolves the " +
                "retained worktree locally, so no path is published here. Then paste the follow-up " +
                "prompt below and approve each step.",
                AgentPrompt: $"/wrighty Complete item {id.Value}: summarize the diff, propose a " +
                "commit message, and after my approval commit, integrate, clean up the workspace, " +
                "and archive the item."));
        actions.Add(new WorkerOperatorAction(
            "Or inspect and clean up from the CLI",
            [$"wrighty get {id.Value}", $"wrighty workspaces cleanup {id.Value}"],
            "On the recording host, `wrighty get` shows the retained worktree's path and git state; " +
            "`wrighty workspaces cleanup` removes the worktree and deletes its merged branch."));
        return actions;
    }

    private static WorkerOperatorAction? IntegrationAction(
        TrackerConfig config,
        string path,
        string branch,
        string? commit,
        bool workspaceRemoved) =>
        WorkerCompletionGuidance.IntegrationAction(
            config.Worker?.Completion?.Integration, path, branch, commit, workspaceRemoved);

    private static string? ReviewCommand(
        IAgentAdapter adapter,
        Workspace workspace,
        string? sessionId,
        bool workspaceRemoved) =>
        !workspaceRemoved && Directory.Exists(workspace.Path) &&
        !string.IsNullOrWhiteSpace(sessionId)
            ? adapter.BuildInteractiveCommand(new SessionHandle(sessionId), workspace)
            : null;

    private async Task<WorkerItemDisposition> HandleFailedRunAsync(
        TrackerConfig config,
        WorkerOptions options,
        EndedRun run,
        IAgentAdapter adapter,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken,
        int recoveryAttempt)
    {
        var (detail, agentName, grant, workspace, result, sessionId) = run;

        // The agent can finish the item and release its claim before the vendor session itself
        // ends badly — hitting a usage limit immediately after `wrighty finish`, for example. The
        // tracked work landed, so this is not a run to recover: recovery would write dispatch
        // state through a grant that no longer exists and fail with CLAIM_REQUIRED, leaving a
        // completed item recorded as a failed run. The success path makes the same check once a
        // claim has ended.
        if (await ItemAlreadyFinishedAsync(config, detail.Id, cancellationToken))
            return await HandleEndedSuccessfulClaimAsync(
                config, options, detail, agentName, adapter, workspace,
                // What landed is the item's outcome; the vendor failure stays attached to the run
                // as how the session ended.
                result with { Outcome = AgentOutcome.Succeeded },
                sessionId, emit, cancellationToken);

        // A usage deferral is not a finished unit of work, so it publishes nothing: the scheduled
        // retry produces its own report.
        await RecordRunOutcomeAsync(config, detail.Id, result, cancellationToken,
            IsUsageCapacityFailure(result.Failure)
                ? null
                : ApprovedContext.RunReportDisposition.Failed,
            agentName, workspace.Branch);
        if (IsUsageCapacityFailure(result.Failure))
            return await HandleUsageCapacityFailureAsync(
                config,
                detail,
                agentName,
                grant,
                workspace,
                result,
                sessionId,
                emit,
                cancellationToken,
                recoveryAttempt);

        if (RequiresOperatorAttention(result.Failure))
            return await HandleUnrecoverableFailureAsync(config, run, emit, cancellationToken);

        try
        {
            await tracker.ReleaseAsync(config, detail.Id, grant, false, cancellationToken);
        }
        catch (TrackerException exception) when (
            exception.Code is "CLAIM_NOT_FOUND" or ClaimExpired)
        {
            // The generation already ended; never reacquire it during failure cleanup.
        }
        catch (TrackerException exception) when (
            exception.Code is ClaimStale or ClaimNotOwner)
        {
            await emit(new WorkerEvent(FencedEvent, detail.Id.Value, agentName,
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
            result.Outcome, EventMessage(result), SessionId: sessionId,
            Failure: result.Failure));
        return result.Outcome switch
        {
            AgentOutcome.TimedOut => WorkerItemDisposition.TimedOut,
            AgentOutcome.Rejected => WorkerItemDisposition.Rejected,
            _ => WorkerItemDisposition.Failed
        };
    }

    private static bool IsUsageCapacityFailure(AgentFailure? failure) =>
        failure is
        {
            IsRetryable: true,
            Kind: AgentFailureKind.UsageExhausted or AgentFailureKind.RateLimited
        };

    /// <summary>
    /// A permission, authentication, or billing failure is a machine or configuration condition
    /// that re-running cannot clear. These must stop rather than fall through to a bare release:
    /// a released claim returns the item to the claimable pool, so the next poll would spawn the
    /// same agent and fail identically, looping instead of telling the operator.
    /// </summary>
    private static bool RequiresOperatorAttention(AgentFailure? failure) =>
        failure is
        {
            IsRetryable: false,
            Kind: AgentFailureKind.PermissionDenied or AgentFailureKind.Authentication
                or AgentFailureKind.BillingUnavailable
        } ||
        failure?.ProviderCode is "SESSION_ID_CHANGED" or "SESSION_ID_MISSING";

    private static AgentRunResult EnforceExpectedSessionIdentity(
        AgentRunResult result,
        AgentInvocationKind invocationKind,
        string? expectedSessionId,
        string? unexpectedSessionId,
        string agentName)
    {
        if (invocationKind != AgentInvocationKind.Resume || expectedSessionId is null)
            return result;

        var reportedSessionId = unexpectedSessionId ?? result.SessionId;
        if (reportedSessionId is null)
        {
            return SessionIdentityFailure(
                result,
                "SESSION_ID_MISSING",
                $"The resumed {agentName} process did not confirm the recorded session ID.");
        }

        return SessionIdsEqual(expectedSessionId, reportedSessionId)
            ? result
            : SessionIdentityFailure(
                result,
                "SESSION_ID_CHANGED",
                $"The resumed {agentName} process reported a different session ID. " +
                "Wrighty kept the recorded session address unchanged.");
    }

    private static AgentRunResult SessionIdentityFailure(
        AgentRunResult result,
        string code,
        string message) =>
        result with
        {
            Outcome = AgentOutcome.Rejected,
            SessionId = null,
            FinalMessage = message,
            Failure = new AgentFailure(
                AgentFailureKind.AgentFailure,
                code,
                RetryAt: null,
                RetryAfter: null,
                IsRetryable: false,
                AgentFailureConfidence.Authoritative,
                message)
        };

    private static bool SessionIdsEqual(string expected, string actual) =>
        Guid.TryParse(expected, out var expectedUuid) &&
        Guid.TryParse(actual, out var actualUuid)
            ? expectedUuid == actualUuid
            : string.Equals(expected, actual, StringComparison.Ordinal);

    /// <summary>The terminal state of one agent run, grouped so a handler can take it as a unit.</summary>
    private sealed record EndedRun(
        WorkItemDetail Detail,
        string AgentName,
        ClaimHandle Grant,
        Workspace Workspace,
        AgentRunResult Result,
        string? SessionId);

    /// <summary>
    /// Whether the tracked work already landed — the agent drove the item to the configured finish
    /// state (or archived it) and released its own claim. Read-only and best-effort: a backend that
    /// cannot be read falls through to ordinary failure handling rather than turning an unreadable
    /// item into a second error.
    /// </summary>
    private async Task<bool> ItemAlreadyFinishedAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await tracker.GetAsync(config, id, cancellationToken);
            return current.Archived || string.Equals(
                current.Status, config.DefaultFinishTo, StringComparison.OrdinalIgnoreCase);
        }
        catch (TrackerException)
        {
            return false;
        }
    }

    private async Task<WorkerItemDisposition> HandleUnrecoverableFailureAsync(
        TrackerConfig config,
        EndedRun run,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var (detail, agentName, grant, workspace, result, sessionId) = run;
        var failure = result.Failure!;
        try
        {
            await MarkNeedsAttentionAsync(config, detail.Id, grant, cancellationToken);
            await ReleaseAfterFailureAsync(config, detail.Id, grant, cancellationToken);
        }
        catch (TrackerException exception) when (
            exception.Code is ClaimStale or ClaimNotOwner)
        {
            await emit(new WorkerEvent(FencedEvent, detail.Id.Value, agentName,
                workspace.Path, result.Outcome, exception.Code, SessionId: sessionId));
            return WorkerItemDisposition.Fenced;
        }

        var attentionActions = NeedsAttentionActions(detail.Id, agentName, OperatorSurface.For(config, detail.Url));
        // This is the same kind of terminal stop as the usage policy giving up, so the handover
        // carries the item's policy presentation too. No provider capacity is passed: a permission,
        // authentication, or billing failure is not a recorded capacity state for this agent.
        await PostHandoverAsync(
            config, detail.Id, HandoverPhase.NeedsAttention, result, workspace,
            attentionActions, cancellationToken,
            workerPolicy: new WorkItemPolicyPresentation(
                detail.AutomaticExecutionAllowed,
                detail.AgentPolicy));
        // The agent's own words come first when it produced any; the categorical label is the
        // fallback for a vendor that terminated without a final message. The words are quoted
        // without their report block, like every surface that quotes an agent's closing text —
        // event messages are truncated for terminals, and a fenced block cut mid-JSON never closes.
        // A response that was only a block strips to null and falls through to the label.
        var reason = failure.SanitizedMessage ?? EventMessage(result) ??
                     UnrecoverableFailureLabel(failure.Kind);
        await emit(new WorkerEvent(
            NeedsAttentionEvent, detail.Id.Value, agentName, workspace.Path,
            result.Outcome, reason, SessionId: sessionId,
            OperatorActions: attentionActions,
            Failure: failure));
        return WorkerItemDisposition.NeedsAttention;
    }

    private static string UnrecoverableFailureLabel(AgentFailureKind kind) => kind switch
    {
        AgentFailureKind.PermissionDenied =>
            "The agent was denied a permission it needed, and re-running will not clear it. " +
            "Review the effective worker permission profile for this agent.",
        AgentFailureKind.Authentication => "The agent is not authenticated on this machine.",
        _ => "The agent's provider account is unavailable for billing reasons."
    };

    private async Task<WorkerItemDisposition> HandleUsageCapacityFailureAsync(
        TrackerConfig config,
        WorkItemDetail detail,
        string agentName,
        ClaimHandle grant,
        Workspace workspace,
        AgentRunResult result,
        string? sessionId,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken,
        int recoveryAttempt)
    {
        var failure = result.Failure!;
        var policy = config.EffectiveWorker.EffectiveUsageFailure;
        var nextAttempt = recoveryAttempt + 1;
        var current = now();
        var notBefore = RetrySchedule.ChooseNotBefore(
            current, detail.Id, failure, policy, nextAttempt);
        ProviderCapacity? provider = null;
        if (providerCapacityEnabled)
        {
            provider = await providerCapacity.RecordUnavailableAsync(
                agentName,
                FailureKindLabel(failure.Kind),
                notBefore,
                failure.Confidence,
                current,
                cancellationToken);
            await emit(ProviderUnavailableEvent(provider, detail.Id, workspace.Path));
        }
        var action = policy.Action.ToLowerInvariant();
        if (action != "retry" || nextAttempt > policy.MaxAttempts)
        {
            await tracker.ClearPendingDispatchAsync(config, detail.Id, cancellationToken);
            await MarkNeedsAttentionAsync(config, detail.Id, grant, cancellationToken);
            await ReleaseAfterFailureAsync(config, detail.Id, grant, cancellationToken);
            var reason = nextAttempt > policy.MaxAttempts
                ? $"Automatic retry stopped after {policy.MaxAttempts} attempts."
                : action == "handoff"
                    ? "Cross-agent handoff is configured but is not enabled by this recovery increment."
                    : "Usage recovery policy requires operator attention.";
            var attentionActions = NeedsAttentionActions(
                detail.Id, agentName, OperatorSurface.For(config, detail.Url));
            await PostHandoverAsync(
                config,
                detail.Id,
                HandoverPhase.NeedsAttention,
                result,
                workspace,
                attentionActions,
                cancellationToken,
                provider: provider,
                workerPolicy: new WorkItemPolicyPresentation(
                    detail.AutomaticExecutionAllowed,
                    detail.AgentPolicy));
            await emit(new WorkerEvent(
                NeedsAttentionEvent,
                detail.Id.Value,
                agentName,
                workspace.Path,
                result.Outcome,
                reason,
                SessionId: sessionId,
                Failure: failure,
                OperatorActions: attentionActions));
            return WorkerItemDisposition.NeedsAttention;
        }

        var dispatch = new PendingDispatch(
            detail.Id.Value,
            DispatchStates.RetryScheduled,
            failure.SanitizedMessage ?? FailureKindLabel(failure.Kind),
            agentName,
            sessionId,
            null,
            notBefore,
            nextAttempt,
            policy.MaxAttempts,
            failure.Confidence,
            current);

        // Store the exact machine-local decision first, then publish only its categorical state.
        // If either write fails, the still-held fenced claim prevents another worker from acting on
        // an incomplete schedule.
        await tracker.RecordPendingDispatchAsync(
            config, detail.Id, dispatch, cancellationToken);
        await SetDispatchStateAsync(
            config,
            detail.Id,
            grant,
            DispatchStates.RetryScheduled,
            cancellationToken);
        await ReleaseAfterFailureAsync(config, detail.Id, grant, cancellationToken);
        var projection = dispatch.ToInfo(true);
        await tracker.PresentDispatchAsync(
            config, detail.Id, projection, cancellationToken);
        var presentationDetail = detail;
        try
        {
            // The run began only after a post-claim policy revalidation. Refresh once more for the
            // handover so an operator change made during the run is presented accurately.
            presentationDetail = await tracker.GetAsync(
                config, detail.Id, cancellationToken);
        }
        catch (TrackerException)
        {
            // Presentation is best-effort; retain the last field-authoritative snapshot.
        }
        await PostHandoverAsync(
            config,
            detail.Id,
            HandoverPhase.RetryScheduled,
            result,
            workspace,
            [
                new WorkerOperatorAction(
                    $"Probe {AgentDisplayName(agentName)} capacity",
                    [$"wrighty provider probe {agentName}"],
                    "Run this on the recording installation to perform one bounded provider " +
                    "capacity check without claiming or changing the item. It may consume " +
                    "subscription usage and asks for confirmation."),
                new WorkerOperatorAction(
                    "Retry now",
                    [$"wrighty worker --item {detail.Id.Value} --yes"],
                    "Explicitly override the timer and resume the recorded vendor session now."),
                new WorkerOperatorAction(
                    "Inspect local recovery state",
                    [$"wrighty get {detail.Id.Value}", "wrighty status"],
                    "Run these on the recording installation for the exact timer and retained " +
                    "session details.")
            ],
            cancellationToken,
            projection,
            provider,
            new WorkItemPolicyPresentation(
                presentationDetail.AutomaticExecutionAllowed,
                presentationDetail.AgentPolicy));
        await emit(new WorkerEvent(
            "retry-scheduled",
            detail.Id.Value,
            agentName,
            workspace.Path,
            result.Outcome,
            $"Retry no earlier than {notBefore:O} (attempt {nextAttempt} of " +
            $"{policy.MaxAttempts}).",
            SessionId: sessionId,
            Failure: failure,
            Dispatch: projection));
        return WorkerItemDisposition.RetryScheduled;
    }

    private async Task ReleaseAfterFailureAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle grant,
        CancellationToken cancellationToken)
    {
        try
        {
            await tracker.ReleasePreservingDispatchStateAsync(
                config, id, grant, cancellationToken);
        }
        catch (TrackerException exception) when (
            exception.Code is "CLAIM_NOT_FOUND" or ClaimExpired)
        {
            // The generation already ended after the decision was durably recorded.
        }
    }

    private static string FailureKindLabel(AgentFailureKind kind) => kind switch
    {
        AgentFailureKind.UsageExhausted => "Agent usage is exhausted.",
        AgentFailureKind.RateLimited => "Agent requests are temporarily rate limited.",
        _ => "Agent capacity is temporarily unavailable."
    };

    private static string AgentDisplayName(string agentName) =>
        agentName.Length == 0
            ? "Agent"
            : $"{char.ToUpperInvariant(agentName[0])}{agentName[1..]}";

    /// <summary>
    /// What an operator can do about a paused session, in the order they should consider it.
    ///
    /// The order is the point. Where the backend has a native surface that can carry a
    /// clarification, that comes first and needs no CLI at all — a GitHub reader arriving at this
    /// comment can answer and re-approve without leaving the issue. The CLI follows as the way to
    /// start the run immediately, and last as the way to take the item over. Nothing here mentions
    /// the other backend's surface: it was only ever true as a disclaimer, and a disclaimer is not
    /// guidance.
    /// </summary>
    private static IReadOnlyList<WorkerOperatorAction> NeedsAttentionActions(
        WorkItemId id,
        string agentName,
        OperatorSurface surface,
        DateTimeOffset? activeUntil = null)
    {
        var agentLabel = agentName.Length == 0
            ? "agent"
            : $"{char.ToUpperInvariant(agentName[0])}{agentName[1..]}";
        var actions = new List<WorkerOperatorAction>();

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
            actions.Add(surface.ContinuesOnTrustedReply
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
                    "re-selecting the value it already holds moves nothing.")
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
                    "which any worker may carry to it."));
            actions.Add(new WorkerOperatorAction(
                surface.ContinuesOnTrustedReply
                    ? $"Or start {agentLabel} yourself"
                    : $"Then start {agentLabel} again",
                [$"wrighty worker --item {id.Value} --yes"],
                $"Runs it now, reusing the recorded session. To keep it hands-off instead, set " +
                $"\"{surface.DispatchStateField}\" to \"{DispatchStates.Queued}\" and leave it: a " +
                "continuous worker takes the item once this claim lapses, and only on the host " +
                "that recorded the session."));
        }
        else
        {
            actions.Add(new WorkerOperatorAction(
                "Edit the requirements in the web UI",
                ["wrighty web"],
                $"Open {id.Value}, then take over (or claim after expiry) and edit it. Choose Save " +
                $"and resume automatically to let a continuous worker continue it, Save and show " +
                $"manual {agentLabel} resume command under More actions to continue it yourself, " +
                "Finish when complete, or Archive to close it without more agent work."));
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
                "changed description and reports that it did."));
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
            $"Wrighty.{editWarning}"));
        return actions;
    }

    private async Task<IReadOnlyDictionary<string, ProviderCapacity>> ProviderStatesAsync(
        CancellationToken cancellationToken)
    {
        if (!providerCapacityEnabled)
            return new Dictionary<string, ProviderCapacity>(
                StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, ProviderCapacity>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var agent in adaptersByName.Keys)
        {
            var availability = await providerCapacity.GetAsync(agent, cancellationToken);
            if (availability is not null)
                result[agent] = availability;
        }
        return result;
    }

    private async Task<bool> IsProviderBlockedForFreshAsync(
        string agentName,
        WorkerCandidateDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        if (!providerCapacityEnabled)
            return false;
        var availability = await providerCapacity.GetAsync(agentName, cancellationToken);
        if (availability is null ||
            availability.State == ProviderCapacityState.Available)
            return false;
        diagnostics.RecordProviderUnavailable(availability);
        return true;
    }

    private async Task<ProviderGate> TryEnterProviderAsync(
        TrackerConfig config,
        WorkItemId itemId,
        string agentName,
        Workspace workspace,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        if (!providerCapacityEnabled)
            return ProviderGate.AllowedWithoutProbe;
        var current = now();
        var availability = await providerCapacity.GetAsync(agentName, cancellationToken);
        if (availability is null ||
            availability.State == ProviderCapacityState.Available)
            return ProviderGate.AllowedWithoutProbe;
        if (ProviderProbeBlocked(availability, current))
        {
            await emit(ProviderUnavailableEvent(availability, itemId, workspace.Path));
            return ProviderGate.Blocked;
        }

        var lease = await providerCapacity.TryAcquireProbeAsync(
            agentName,
            current,
            ProviderProbeLeaseDuration,
            cancellationToken);
        if (lease is null)
        {
            availability = await providerCapacity.GetAsync(agentName, cancellationToken)
                           ?? availability;
            await emit(ProviderUnavailableEvent(availability, itemId, workspace.Path));
            return ProviderGate.Blocked;
        }

        if (!capacityProbesByAgent.TryGetValue(agentName, out var probe))
            return new ProviderGate(true, lease);

        var suspectedFailure = new AgentFailure(
            AgentFailureKind.UsageExhausted,
            null,
            availability.UnavailableUntil,
            null,
            true,
            availability.Confidence,
            availability.Reason);
        var result = await probe.ProbeAsync(
            new AgentCapacityProbeRequest(agentName, workspace, suspectedFailure),
            cancellationToken);
        if (result is null)
            return new ProviderGate(true, lease);
        if (result.Available)
        {
            await providerCapacity.RecordAvailableAsync(
                agentName, result.ObservedAt, cancellationToken);
            await emit(new WorkerEvent(
                "provider-available",
                itemId.Value,
                agentName,
                workspace.Path,
                Message: "The due provider capacity probe succeeded."));
            return ProviderGate.AllowedWithoutProbe;
        }

        var failure = result.Failure ?? suspectedFailure;
        if (!IsUsageCapacityFailure(failure))
        {
            await providerCapacity.RecordAvailableAsync(
                agentName, result.ObservedAt, cancellationToken);
            return ProviderGate.AllowedWithoutProbe;
        }
        var policy = config.EffectiveWorker.EffectiveUsageFailure;
        var attempt = Math.Max(1, availability.ConsecutiveFailures + 1);
        var unavailableUntil = RetrySchedule.ChooseNotBefore(
            result.ObservedAt, itemId, failure, policy, attempt);
        var reopened = await providerCapacity.RecordUnavailableAsync(
            agentName,
            FailureKindLabel(failure.Kind),
            unavailableUntil,
            failure.Confidence,
            result.ObservedAt,
            cancellationToken);
        await emit(ProviderUnavailableEvent(reopened, itemId, workspace.Path));
        return ProviderGate.Blocked;
    }

    private static bool ProviderProbeBlocked(
        ProviderCapacity? availability,
        DateTimeOffset current) =>
        availability is
        {
            State: ProviderCapacityState.UnavailableUntil,
            UnavailableUntil: { } unavailableUntil
        } && unavailableUntil > current ||
        availability is
        {
            State: ProviderCapacityState.ProbeInProgress,
            UnavailableUntil: { } leaseExpiresAt
        } && leaseExpiresAt > current;

    private static WorkerEvent ProviderUnavailableEvent(
        ProviderCapacity availability,
        WorkItemId? itemId,
        string? workspacePath)
    {
        var timing = availability.UnavailableUntil is { } unavailableUntil
            ? $" No automatic {availability.Agent} run will start before " +
              $"{unavailableUntil:O}."
            : string.Empty;
        var reason = availability is
        {
            State: ProviderCapacityState.ProbeInProgress,
            ConsecutiveFailures: 0
        }
            ? "A provider capacity probe is already in progress."
            : availability.Reason ?? "Provider capacity is unavailable.";
        return new WorkerEvent(
            "provider-unavailable",
            itemId?.Value,
            availability.Agent,
            workspacePath,
            Message: $"{reason}{timing}",
            ProviderCapacity: availability);
    }

    private async Task<WorkerRunSummary?> TryRunQueuedAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        WorkerCandidateDiagnostics diagnostics,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in await QueuedCandidatesAsync(
                     config, options, repositoryPath, diagnostics, cancellationToken))
        {
            var workspace = new Workspace(
                Path.GetFullPath(candidate.Session.WorkspacePath!),
                !SamePath(candidate.Session.WorkspacePath!, repositoryPath));
            var providerGate = await TryEnterProviderAsync(
                config,
                candidate.Detail.Id,
                candidate.AgentName,
                workspace,
                emit,
                cancellationToken);
            if (!providerGate.Allowed)
                continue;
            try
            {
                if (candidate.Dispatch is not null)
                    await emit(new WorkerEvent(
                        "retry-due",
                        candidate.Detail.Id.Value,
                        candidate.AgentName,
                        candidate.Session.WorkspacePath,
                        SessionId: candidate.Session.SessionId,
                        Dispatch: candidate.Dispatch));
                return await RecoverExpiredSessionAsync(
                    config,
                    options,
                    repositoryPath,
                    candidate.Detail,
                    candidate.Session,
                    candidate.AgentName,
                    emit,
                    cancellationToken,
                    // A queued session with no dispatch was put there by an operator clarifying it
                    // and handing it back, which is their decision to resume even though no one is
                    // at a terminal for it. A dispatch means Wrighty scheduled the retry itself,
                    // and nobody has judged anything.
                    operatorRequested: candidate.Dispatch is null);
            }
            catch (TrackerException exception) when (
                exception.Code is "CLAIM_HELD" or "CLAIM_HELD_BY_LOCAL_CLAIMANT")
            {
                if (providerGate.ProbeLease is not null)
                    await providerCapacity.ReleaseProbeAsync(
                        providerGate.ProbeLease, now(), cancellationToken);
                // Another worker won contention for this queued session. Continue in priority order.
            }
        }

        return null;
    }

    private async Task<QueuedCandidate?> FirstQueuedCandidateAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        WorkerCandidateDiagnostics diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in await QueuedCandidatesAsync(
                     config, options, repositoryPath, diagnostics, cancellationToken))
        {
            var availability = await providerCapacity.GetAsync(
                candidate.AgentName, cancellationToken);
            if (!ProviderProbeBlocked(availability, now()))
                return candidate;
            if (availability is not null)
                diagnostics.RecordProviderUnavailable(availability);
        }
        return null;
    }

    private async Task<IReadOnlyList<QueuedCandidate>> QueuedCandidatesAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        WorkerCandidateDiagnostics? diagnostics,
        CancellationToken cancellationToken)
    {
        var activeStatus = options.ToStatus ?? config.DefaultPickTo;
        var summaries = await tracker.ListAsync(
            config,
            new ListWorkItemsRequest(activeStatus, null),
            cancellationToken);
        var candidates = new List<QueuedCandidate>();
        foreach (var summary in summaries)
        {
            var detail = await tracker.GetAsync(config, summary.Id, cancellationToken);
            var queued = string.Equals(
                detail.DispatchState,
                DispatchStates.Queued,
                StringComparison.OrdinalIgnoreCase);
            var retryScheduled = string.Equals(
                detail.DispatchState,
                DispatchStates.RetryScheduled,
                StringComparison.OrdinalIgnoreCase);
            var mayBeInterruptedRetry = detail.DispatchState is null;
            if ((!queued && !retryScheduled && !mayBeInterruptedRetry) ||
                !detail.AutomaticExecutionAllowed ||
                !WorkerPolicyGate.MatchesFilters(detail, options.Filters))
                continue;

            var ownership = await tracker.GetClaimOwnershipAsync(
                config, detail.Id, cancellationToken);
            if (ownership.State != ClaimOwnershipState.Unclaimed)
                continue;
            var session = await tracker.GetAgentSessionAsync(
                config, detail.Id, cancellationToken);
            if (session is not { IsComplete: true, FromCurrentInstallation: true })
                continue;
            var dispatch = retryScheduled ? session.Dispatch : null;
            var dueLocalRetry = dispatch is
            {
                State: DispatchStates.RetryScheduled,
                FromCurrentInstallation: true
            } && dispatch.NotBefore <= now();
            var interruptedRetry = mayBeInterruptedRetry && dueLocalRetry;
            if (!queued && !retryScheduled && !interruptedRetry)
                continue;
            if (retryScheduled && !dueLocalRetry)
                continue;
            string agentName;
            try
            {
                agentName = ValidateRecordedSession(
                    options, repositoryPath, detail.Id, session);
            }
            catch (TrackerException exception) when (exception.Code == "AGENT_NOT_INSTALLED")
            {
                // A queue scan is host-local scheduling, not an exact operator request. Preserve the
                // recorded vendor session and continue looking for work this host can run.
                diagnostics?.RecordUnavailableAgent(
                    NormalizeAgent(session.Agent) ?? "unknown");
                continue;
            }
            candidates.Add(new QueuedCandidate(detail, session, agentName, dispatch));
        }

        return candidates;
    }

    /// <summary>
    /// Queues any waiting session a trusted author has replied to. Best-effort by design: a scan
    /// that fails must not stop the worker from doing the work it already had, so a failure is
    /// reported and the poll continues. The items it would have queued stay in needs-attention and
    /// are reconsidered next poll.
    /// </summary>
    private async Task EvaluateContinuationsAsync(
        TrackerConfig config,
        WorkerOptions options,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        if (continuations is null) return;

        IReadOnlyList<ContinuationScanResult> results;
        try
        {
            results = await continuations.RunAsync(config, options, cancellationToken);
        }
        // Deliberately broad. This scan is an enhancement to a poll that has real work to do, and
        // no failure inside it — including a backend that does not implement a method it calls —
        // justifies stopping the worker from claiming and running items. Cancellation still
        // propagates, because that is the operator asking it to stop.
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await emit(new WorkerEvent(
                "continuation-scan-failed",
                Message: $"Could not check waiting items for trusted replies: {exception.Message}. " +
                         "The worker continues with its other work."));
            return;
        }

        foreach (var result in results)
        {
            if (result.Outcome == ContinuationOutcome.Queue)
            {
                await emit(new WorkerEvent(
                    "continuation-queued",
                    result.Id.Value,
                    Message: $"{result.Actor} replied, so the recorded session is queued to " +
                             "continue with what they wrote."));
                continue;
            }

            // Anything with a reason is said. An earlier version reported only outcomes carrying a
            // trigger, which suppressed exactly the two — no candidate, already consumed — that
            // explain why a waiting item is not moving. Silence then meant both "nothing to do" and
            // "something you would want to know", and the operator cannot tell those apart.
            if (result.Reason is not null)
                await emit(new WorkerEvent(
                    "continuation-skipped",
                    result.Id.Value,
                    Message: result.Reason));
        }
    }

    private static WorkerRunSummary Summary(WorkerItemDisposition disposition) =>
        new(1,
            disposition == WorkerItemDisposition.NeedsAttention ? 1 : 0,
            disposition is WorkerItemDisposition.Failed or WorkerItemDisposition.TimedOut
                or WorkerItemDisposition.Rejected ? 1 : 0);

    private async Task<WorkItemDetail> ClearDispatchStateAsync(
        TrackerConfig config,
        WorkItemDetail detail,
        ClaimHandle grant,
        CancellationToken cancellationToken)
    {
        // The agent may have called finish/archive while it was running. Those transitions end
        // the claim and clear deferred state atomically, so do not use the stale pre-run detail to
        // issue a second claimed update after the process exits.
        var current = await tracker.GetAsync(config, detail.Id, cancellationToken);
        if (current.DispatchState is null)
            return current;
        var updated = await tracker.UpdateAsync(
            config,
            current.Id,
            new WorkItemPatch(
                OptionalValue<string>.Unspecified,
                OptionalValue<string>.Unspecified,
                OptionalValue<string>.Unspecified,
                OptionalValue<string?>.Unspecified,
                DispatchState: OptionalValue<string?>.From(null)),
            expectedRevision: null,
            grant,
            cancellationToken);
        return updated.Item;
    }

    private async Task KeepAliveAsync(
        TrackerConfig config,
        WorkItemId id,
        ClaimHandle grant,
        string workspacePath,
        DateTimeOffset startedAt,
        DateTimeOffset deadline,
        DateTimeOffset initialClaimExpiresAt,
        WorkerOptions options,
        Func<WorkerEvent, Task> emit,
        CancellationTokenSource runCts,
        CancellationToken cancellationToken,
        Action markFenced,
        Action markBudgetExhausted)
    {
        var renewalInterval = TimeSpan.FromMinutes(config.LeaseMinutes / 2d);
        var nextRenewalAt = startedAt + renewalInterval;
        var nextHeartbeatAt = startedAt + HeartbeatInterval;
        var claimExpiresAt = initialClaimExpiresAt;
        while (now() < deadline)
        {
            var current = now();
            var wakeAt = new[] { deadline, nextRenewalAt, nextHeartbeatAt }.Min();
            if (wakeAt > current)
                await wait(wakeAt - current, cancellationToken);
            current = now();
            if (current >= deadline) break;

            if (current >= nextRenewalAt)
            {
                try
                {
                    var renewed = await tracker.RenewClaimAsync(config, id, grant, workspacePath,
                        grant.Claimant.SessionId, cancellationToken);
                    claimExpiresAt = renewed.ExpiresAt;
                    await emit(new WorkerEvent("renewed", id.Value, grant.Claimant.Agent,
                        workspacePath, Message: renewed.ExpiresAt.ToString("O"),
                        ClaimExpiresAt: renewed.ExpiresAt, OccurredAt: current));
                    nextRenewalAt = current + renewalInterval;
                }
                catch (TrackerException exception) when (
                    exception.Code is ClaimStale or ClaimExpired or ClaimNotOwner)
                {
                    markFenced();
                    await emit(new WorkerEvent(FencedEvent, id.Value, grant.Claimant.Agent,
                        workspacePath, Message: exception.Code, OccurredAt: current));
                    runCts.Cancel();
                    return;
                }
            }

            if (current >= nextHeartbeatAt)
            {
                var elapsed = current - startedAt;
                var timeoutRemaining = deadline - current;
                await emit(new WorkerEvent(
                    "running",
                    id.Value,
                    grant.Claimant.Agent,
                    workspacePath,
                    Message: $"{FormatDuration(elapsed)} elapsed; claim valid until " +
                             $"{claimExpiresAt:O}; timeout in {FormatDuration(timeoutRemaining)}; " +
                             $"workspace {options.WorkspaceMode.ToString().ToLowerInvariant()}",
                    ClaimExpiresAt: claimExpiresAt,
                    OccurredAt: current,
                    Elapsed: elapsed,
                    TimeoutRemaining: timeoutRemaining,
                    TimeoutAt: deadline,
                    WorkspaceMode: options.WorkspaceMode.ToString().ToLowerInvariant()));
                nextHeartbeatAt = current + HeartbeatInterval;
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

    private static string FormatDuration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
            return "0m";
        var minutes = (int)Math.Ceiling(value.TotalMinutes);
        if (minutes < 60)
            return $"{minutes}m";
        var hours = minutes / 60;
        var remainder = minutes % 60;
        return remainder == 0 ? $"{hours}h" : $"{hours}h {remainder}m";
    }

    private async Task<WorkerRunSummary> DryRunAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var count = await DryRunQueuedAsync(
            config, options, repositoryPath, emit, cancellationToken);
        if (LimitReached(options, count))
            return new WorkerRunSummary(count);
        count = await DryRunFreshAsync(
            config, options, repositoryPath, count, emit, cancellationToken);
        return new WorkerRunSummary(count);
    }

    private async Task<int> DryRunQueuedAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var count = 0;
        var diagnostics = new WorkerCandidateDiagnostics(
            options.ToStatus ?? config.DefaultPickTo);
        foreach (var queued in await QueuedCandidatesAsync(
                     config, options, repositoryPath, diagnostics, cancellationToken))
        {
            var adapter = adaptersByName[queued.AgentName];
            var workspacePath = Path.GetFullPath(queued.Session.WorkspacePath!);
            var workspace = new Workspace(
                workspacePath,
                !SamePath(workspacePath, repositoryPath));
            var invocation = adapter.BuildResume(
                new SessionHandle(queued.Session.SessionId!),
                workspace,
                WorkerPrompt.Append(
                    WorkerPrompt.ForResume(queued.Detail.Id, queued.AgentName),
                    WorkerPrompt.CommitInstruction(
                        workspace, config.Worker?.Completion?.Commit)),
                PermissionsFor(config, queued.AgentName));
            await emit(new WorkerEvent(
                "dry-run",
                queued.Detail.Id.Value,
                queued.AgentName,
                workspace.Path,
                Arguments: [invocation.Executable, .. invocation.Arguments],
                Message: "Would acquire a new claim generation and resume the queued session. " +
                         "WRIGHTY_CLAIM_TOKEN=<redacted>",
                SessionId: queued.Session.SessionId,
                Permissions: DescribePermissions(config, queued.AgentName)));
            count++;
            if (LimitReached(options, count))
                break;
        }
        if (count == 0 && diagnostics.UnavailableAgent > 0)
            await emit(new WorkerEvent(
                "agent-unavailable",
                Message: diagnostics.DescribeUnavailableAgents(),
                Candidates: diagnostics.CreateSnapshot()));
        return count;
    }

    private async Task<int> DryRunFreshAsync(
        TrackerConfig config,
        WorkerOptions options,
        string repositoryPath,
        int count,
        Func<WorkerEvent, Task> emit,
        CancellationToken cancellationToken)
    {
        var items = await tracker.ListAsync(config,
            new ListWorkItemsRequest(options.FromStatus ?? config.DefaultPickFrom, null),
            cancellationToken);
        var diagnostics = new WorkerCandidateDiagnostics(
            options.FromStatus ?? config.DefaultPickFrom);
        var readyAgents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preview = new DryRunPreviewContext(
            config, options, repositoryPath, readyAgents, emit);
        foreach (var summary in items)
        {
            var detail = await tracker.GetAsync(config, summary.Id, cancellationToken);
            var evaluation = EvaluateCandidate(
                detail,
                options,
                config.EffectiveWorker.DefaultAgent,
                diagnostics);
            if (!evaluation.Eligible)
                continue;
            if (await PreviewFreshItemAsync(
                    preview, detail, evaluation.Agent!, cancellationToken))
                count++;
            if (LimitReached(options, count))
                break;
        }
        if (count == 0)
        {
            await emit(new WorkerEvent(
                "no-item",
                Message: diagnostics.Describe(options.Filters.Count > 0),
                Candidates: diagnostics.CreateSnapshot()));
        }
        return count;
    }

    private async Task<bool> PreviewFreshItemAsync(
        DryRunPreviewContext preview,
        WorkItemDetail detail,
        string agent,
        CancellationToken cancellationToken)
    {
        var ownership = await tracker.GetClaimOwnershipAsync(
            preview.Config, detail.Id, cancellationToken);
        if (ownership.State != ClaimOwnershipState.Unclaimed)
        {
            var claimant = string.IsNullOrWhiteSpace(ownership.ClaimantId)
                ? ownership.ClaimantKind
                : $"{ownership.ClaimantKind} {ownership.ClaimantId}";
            await preview.Emit(new WorkerEvent(
                "skipped-claimed", detail.Id.Value, agent,
                Message: $"Active claim held by {claimant}.",
                ClaimExpiresAt: ownership.ExpiresAt));
            return false;
        }
        if (preview.Options.WorkspaceMode == WorkspaceMode.Worktree &&
            preview.ReadyAgents.Add(agent))
            skills.EnsureWorktreeReady(agent, preview.RepositoryPath);
        var adapter = adaptersByName[agent];
        var previewGeneration = $"dry-run:{Guid.NewGuid():N}";
        var session = adapter.Agent == "claude"
            ? SessionHandles.ForClaude(detail.Id, previewGeneration)
            : SessionHandles.ForNamedVendor(detail.Id, previewGeneration);
        var workspace = new Workspace(
            Path.GetFullPath(preview.RepositoryPath),
            preview.Options.WorkspaceMode == WorkspaceMode.Worktree);
        var invocation = adapter.BuildStart(detail, session, workspace,
            PermissionsFor(preview.Config, agent),
            WorkerPrompt.CommitInstruction(workspace, preview.Config.Worker?.Completion?.Commit));
        await preview.Emit(new WorkerEvent(
            "dry-run", detail.Id.Value, agent, workspace.Path,
            Arguments: [invocation.Executable, .. invocation.Arguments],
            Message: "WRIGHTY_CLAIM_TOKEN=<redacted>",
            Permissions: DescribePermissions(preview.Config, agent)));
        return true;
    }

    private static bool LimitReached(WorkerOptions options, int count) =>
        count > 0 &&
        (options.Once || (options.MaxItems.HasValue && count >= options.MaxItems.Value));

    private static bool SamePath(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    /// <summary>
    /// The pre-claim candidate scan. Admission itself comes from <see cref="WorkerPolicyGate"/> —
    /// the same evaluation the post-claim launch preflight runs — so this method only translates
    /// the shared decision into the idle diagnostics an operator sees.
    /// </summary>
    private CandidateEvaluation EvaluateCandidate(
        WorkItemDetail detail,
        WorkerOptions options,
        string? configuredAgent,
        WorkerCandidateDiagnostics diagnostics)
    {
        diagnostics.StatusItems++;
        if (string.IsNullOrWhiteSpace(detail.AgentPolicy))
            diagnostics.MissingItemAgent++;
        var decision = WorkerPolicyGate.Evaluate(
            detail, options, configuredAgent, adaptersByName.ContainsKey);
        switch (decision.Reason)
        {
            case WorkerPolicyReason.PausedOrQueued:
                diagnostics.PausedOrQueued++;
                return CandidateEvaluation.Ineligible;
            case WorkerPolicyReason.ExecutionNotAutomatic:
                diagnostics.MissingAuto++;
                return CandidateEvaluation.Ineligible;
            case WorkerPolicyReason.FilteredOut:
                diagnostics.FilteredOut++;
                return CandidateEvaluation.Ineligible;
            case WorkerPolicyReason.UnresolvedAgent:
                diagnostics.UnresolvedAgent++;
                return CandidateEvaluation.Ineligible;
            default:
                if (detail.ContextApprovalFieldApproved is false)
                {
                    diagnostics.ContextNotApproved++;
                    return CandidateEvaluation.Ineligible;
                }
                if (!runtimes.Snapshot().IsInstalled(decision.Agent!))
                {
                    diagnostics.RecordUnavailableAgent(decision.Agent!);
                    return new CandidateEvaluation(
                        false,
                        decision.Agent,
                        decision.AgentSource,
                        Unavailable: true);
                }
                diagnostics.Eligible++;
                return new CandidateEvaluation(
                    true,
                    decision.Agent,
                    decision.AgentSource);
        }
    }

    private void EnsureWorkerHostAvailable(WorkerOptions options)
    {
        var snapshot = runtimes.Snapshot();
        if (NormalizeAgent(options.Agent) is { } selected &&
            adaptersByName.ContainsKey(selected) &&
            !snapshot.IsInstalled(selected))
        {
            throw AgentNotInstalled(selected, null, "option", snapshot);
        }
        if (!snapshot.AnyInstalled)
        {
            throw new TrackerException(
                "NO_AGENT_INSTALLED",
                "No supported local AI agent CLI was found on PATH. Install one of: " +
                string.Join(", ", snapshot.Agents.Select(runtime => runtime.ExecutableName)) + ".",
                7,
                new Dictionary<string, object?>
                {
                    ["supportedAgents"] = snapshot.Agents.Select(runtime => runtime.Agent).ToArray()
                });
        }
    }

    private void EnsureAgentInstalled(string agent, WorkItemId? id, string source)
    {
        var snapshot = runtimes.Snapshot();
        if (!snapshot.IsInstalled(agent))
            throw AgentNotInstalled(agent, id, source, snapshot);
    }

    private void ThrowIfAgentUnavailable(CandidateEvaluation evaluation, WorkItemId id)
    {
        if (!evaluation.Unavailable || evaluation.Agent is null)
            return;
        throw AgentNotInstalled(
            evaluation.Agent,
            id,
            evaluation.AgentSource ?? "unknown",
            runtimes.Snapshot());
    }

    private static TrackerException AgentNotInstalled(
        string agent,
        WorkItemId? id,
        string source,
        AgentRuntimeSnapshot snapshot)
    {
        var available = snapshot.InstalledAgents.Select(runtime => runtime.Agent).ToArray();
        var item = id is null ? string.Empty : $" for work item '{id}'";
        var alternatives = available.Length == 0
            ? "No supported agents are installed locally."
            : $"Installed local agents: {string.Join(", ", available)}.";
        return new TrackerException(
            "AGENT_NOT_INSTALLED",
            $"Resolved agent '{agent}' from {source}{item}, but its executable is not installed " +
            $"on this worker host. {alternatives}",
            7,
            new Dictionary<string, object?>
            {
                ["agent"] = agent,
                ["itemId"] = id?.Value,
                ["source"] = source,
                ["availableAgents"] = available
            });
    }

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

    private sealed record CandidateEvaluation(
        bool Eligible,
        string? Agent = null,
        string? AgentSource = null,
        bool Unavailable = false)
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

    private sealed record QueuedCandidate(
        WorkItemDetail Detail,
        AgentSessionRecord Session,
        string AgentName,
        DispatchInfo? Dispatch);

    private sealed record ProviderGate(
        bool Allowed,
        ProviderProbeLease? ProbeLease)
    {
        public static ProviderGate AllowedWithoutProbe { get; } = new(true, null);
        public static ProviderGate Blocked { get; } = new(false, null);
    }

    private sealed record DryRunPreviewContext(
        TrackerConfig Config,
        WorkerOptions Options,
        string RepositoryPath,
        HashSet<string> ReadyAgents,
        Func<WorkerEvent, Task> Emit);

    private sealed record PreflightCandidate(WorkItemDetail Detail, string Agent);

    private sealed class RunFenceState
    {
        public bool Fenced { get; set; }
    }

    private sealed class WorkerLoopState(DateTimeOffset startedAt)
    {
        public int Processed { get; private set; }
        public int NeedsAttention { get; private set; }
        public int Failed { get; private set; }
        public DateTimeOffset IdleStarted { get; private set; } = startedAt;
        public TimeSpan Backoff { get; private set; } = TimeSpan.FromSeconds(2);
        public int PreviousUnresolvedAgentCount { get; set; }
        public string PreviousUnavailableAgentSignature { get; set; } = string.Empty;
        public WorkerRunSummary RunSummary => new(Processed, NeedsAttention, Failed);

        public void Record(WorkerRunSummary summary, DateTimeOffset current)
        {
            Processed += summary.Processed;
            NeedsAttention += summary.NeedsAttention;
            Failed += summary.Failed;
            ResetIdle(current);
        }

        public void Record(WorkerItemDisposition disposition, DateTimeOffset current)
        {
            Processed++;
            if (disposition == WorkerItemDisposition.NeedsAttention)
                NeedsAttention++;
            if (disposition is WorkerItemDisposition.Failed or
                WorkerItemDisposition.TimedOut or WorkerItemDisposition.Rejected)
                Failed++;
            ResetIdle(current);
        }

        public async Task WaitAndBackOffAsync(
            Func<TimeSpan, CancellationToken, Task> wait,
            CancellationToken cancellationToken)
        {
            await wait(Backoff, cancellationToken);
            Backoff = TimeSpan.FromSeconds(Math.Min(Backoff.TotalSeconds * 2, 30));
        }

        private void ResetIdle(DateTimeOffset current)
        {
            IdleStarted = current;
            Backoff = TimeSpan.FromSeconds(2);
            PreviousUnresolvedAgentCount = 0;
            PreviousUnavailableAgentSignature = string.Empty;
        }
    }

    private sealed class WorkerCandidateDiagnostics(string status)
    {
        public int StatusItems { get; set; }
        public int MissingAuto { get; set; }
        public int MissingItemAgent { get; set; }
        public int ContextNotApproved { get; set; }
        public int PausedOrQueued { get; set; }
        public int FilteredOut { get; set; }
        public int UnresolvedAgent { get; set; }
        public int Eligible { get; set; }
        public int Claimed { get; set; }
        public int Claimable { get; set; }
        public int ProviderUnavailable { get; private set; }
        public int UnavailableAgent { get; private set; }
        public Dictionary<string, ProviderCapacity> UnavailableProviders { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> UnavailableAgents { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public void RecordProviderUnavailable(ProviderCapacity availability)
        {
            ProviderUnavailable++;
            UnavailableProviders[availability.Agent] = availability;
        }

        public void RecordUnavailableAgent(string agent)
        {
            UnavailableAgent++;
            UnavailableAgents[agent] = UnavailableAgents.GetValueOrDefault(agent) + 1;
        }

        public string UnavailableAgentSignature => string.Join(
            ";",
            UnavailableAgents
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}:{pair.Value}"));

        public string DescribeUnavailableAgents() =>
            $"{UnavailableAgent} otherwise eligible item(s) require unavailable local agent " +
            $"executable(s): {string.Join(", ", UnavailableAgents.OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key} ({pair.Value})"))}. Install the requested CLI or " +
            "use an explicit --agent override; no item was claimed.";

        public WorkerCandidateSummary CreateSnapshot() => new(
            status,
            StatusItems,
            MissingAuto,
            MissingItemAgent,
            FilteredOut,
            UnresolvedAgent,
            Eligible,
            Claimed,
            Claimable,
            ProviderUnavailable,
            UnavailableAgent,
            new Dictionary<string, int>(UnavailableAgents, StringComparer.OrdinalIgnoreCase),
            ContextNotApproved);

        public string Describe(bool hasFilters)
        {
            var filters = hasFilters
                ? $"{FilteredOut} excluded by --filter; "
                : string.Empty;
            return $"No worker item could be claimed from status '{status}'. " +
                   $"Considered {StatusItems} active item(s): " +
                   $"{MissingAuto} manual-only; " +
                   $"{ContextNotApproved} without approved projected context; " +
                   $"{MissingItemAgent} missing an item agent policy " +
                   $"(allowed when --agent or worker.defaultAgent supplies one); " +
                   $"{PausedOrQueued} paused or explicitly queued item(s); " +
                   filters +
                   $"{UnresolvedAgent} opted-in item(s) without a supported resolved agent; " +
                   $"{UnavailableAgent} otherwise eligible item(s) with an unavailable local agent; " +
                   $"{ProviderUnavailable} otherwise eligible item(s) blocked by provider capacity; " +
                   $"{Eligible - ProviderUnavailable} otherwise eligible item(s) were unavailable " +
                   $"because they were already claimed or lost claim contention. Candidates must be active in " +
                   $"'{status}', allow automatic execution, have approved context when the backend " +
                   $"projects it, match every --filter, resolve an agent via --agent > agent policy > " +
                   "worker.defaultAgent, and be unclaimed.";
        }

        public string DescribePreflight(bool hasFilters)
        {
            var filters = hasFilters
                ? $"{FilteredOut} excluded by --filter; "
                : string.Empty;
            return $"No worker item is currently claimable from status '{status}'. " +
                   $"Considered {StatusItems} active item(s): " +
                   $"{MissingAuto} manual-only; " +
                   $"{ContextNotApproved} without approved projected context; " +
                   $"{MissingItemAgent} missing an item agent policy " +
                   $"(allowed when --agent or worker.defaultAgent supplies one); " +
                   $"{PausedOrQueued} paused or explicitly queued item(s); " +
                   filters +
                   $"{UnresolvedAgent} opted-in item(s) without a supported resolved agent; " +
                   $"{UnavailableAgent} otherwise eligible item(s) with an unavailable local agent; " +
                   $"{ProviderUnavailable} otherwise eligible item(s) blocked by provider capacity; " +
                   $"{Claimed} otherwise eligible item(s) currently claimed; " +
                   $"{Claimable} currently claimable. Candidates must be active in '{status}', " +
                   $"allow automatic execution, have approved context when the backend projects it, " +
                   $"match every --filter, resolve an agent via --agent > agent policy > " +
                   "worker.defaultAgent, and be unclaimed.";
        }

        public string DescribeReady(bool hasFilters)
        {
            var filters = hasFilters
                ? $"; {FilteredOut} excluded by --filter"
                : string.Empty;
            return $"{Claimable} currently claimable worker item(s) in status '{status}'; " +
                   $"{StatusItems} active item(s) considered " +
                   $"({MissingAuto} manual-only; " +
                   $"{ContextNotApproved} without approved projected context; " +
                   $"{MissingItemAgent} missing an item agent policy " +
                   $"(allowed when --agent or worker.defaultAgent supplies one); " +
                   $"{PausedOrQueued} paused or explicitly queued item(s); " +
                   $"{UnresolvedAgent} without a supported resolved agent; " +
                   $"{UnavailableAgent} with an unavailable local agent; " +
                   $"{ProviderUnavailable} blocked by provider capacity; " +
                   $"{Claimed} currently claimed{filters}). " +
                   $"Candidates must be active in '{status}', allow automatic execution, match every " +
                   $"--filter, have approved context when the backend projects it, resolve an agent " +
                   $"via --agent > agent policy > worker.defaultAgent, and be unclaimed. This is a read-only snapshot; the " +
                   "atomic pick occurs after confirmation.";
        }
    }
}

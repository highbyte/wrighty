using System.Text.Json;
using System.Text.Json.Serialization;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Projects;
using Highbyte.Wrighty.Initialization;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Importing;
using Highbyte.Wrighty.Workers;
using Highbyte.Wrighty.Cli.Skills;

namespace Highbyte.Wrighty.Cli.Output;

public sealed record StatusOutputContext(
    IReadOnlyList<ProviderCapacity>? ProviderCapacities = null,
    IReadOnlyList<WorkerInstanceStatus>? WorkerInstances = null,
    string? ConfigurationRevision = null);

public sealed class OutputWriter(
    TextWriter output,
    TextWriter error,
    Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    private static readonly string[] PartialErrorDetailKeys =
    [
        "id", "displayId", "url", "failedStage", "configPath",
        "repository", "projectOwner", "projectNumber", "projectUrl",
        "appliedFields", "pendingFields", "targetStatus", "statusApplied",
        "archived", "claimReleased", "causeCode", "causeMessage", "retry"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task WriteItemsAsync(
        IEnumerable<WorkItemSummary> items,
        bool compact,
        bool json,
        Func<WorkItemId, string> formatShort)
    {
        var materialized = items.ToArray();
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = materialized.Select(item => SummaryDto(item, formatShort)).ToArray()
            });
            return;
        }

        foreach (var item in materialized)
        {
            if (compact)
            {
                await output.WriteLineAsync(
                    $"{formatShort(item.Id)} {Token(item.Status, "-")} {Token(item.Priority, "-")}{(item.Archived ? " archived" : string.Empty)} {SingleLine(item.Title)}");
            }
            else
            {
                await output.WriteLineAsync(
                    $"{formatShort(item.Id),-7} {Token(item.Status, "(no status)"),-16} {Token(item.Priority, "-"),-8} {SingleLine(item.Title)}{(item.Archived ? " [archived]" : string.Empty)}");
            }
        }
    }

    public async Task WriteOperationalItemsAsync(
        IEnumerable<WorkItemOperationalState> items,
        bool compact,
        bool json,
        Func<WorkItemId, string> formatShort)
    {
        var materialized = items.ToArray();
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = materialized
                    .Select(item => OperationalDto(item, formatShort))
                    .ToArray()
            });
            return;
        }

        if (compact)
        {
            foreach (var value in materialized)
            {
                var item = value.Item;
                await output.WriteLineAsync(
                    $"{formatShort(item.Id)} {Token(item.Status, "-")} " +
                    $"{Token(item.Priority, "-")} {AutomationToken(item)} " +
                    $"{OperationalStatusToken(value)}{LeaseToken(value)} " +
                    $"{SingleLine(item.Title)}{WorktreeMarker(value)}");
            }
            return;
        }

        await output.WriteLineAsync(
            $"{"ID",-8} {"STATUS",-16} {"PRIORITY",-9} {"AUTOMATION",-13} " +
            $"{"ACTIVITY",-24} {"LEASE",-12} TITLE");
        foreach (var value in materialized)
        {
            var item = value.Item;
            await output.WriteLineAsync(
                $"{formatShort(item.Id),-8} " +
                $"{Truncate(Token(item.Status, "(none)"), 16),-16} " +
                $"{Truncate(Token(item.Priority, "-"), 9),-9} " +
                $"{Truncate(AutomationLabel(item), 13),-13} " +
                $"{Truncate(OperationalStatusLabel(value), 24),-24} " +
                $"{Truncate(LeaseLabel(value), 12),-12} " +
                $"{SingleLine(item.Title)}{(item.Archived ? " [archived]" : string.Empty)}" +
                $"{WorktreeMarker(value)}");
        }
    }

    /// <summary>
    /// Renders `wrighty status`: the machine-side "what needs me?" discovery surface, grouped by
    /// the operator's next action. For the GitHub backend it substitutes for the Local Markdown web
    /// dashboard. Workspace git state is supplied by the caller (bounded, machine-local) so this
    /// method stays pure rendering.
    /// </summary>
    public async Task WriteStatusAsync(
        IReadOnlyList<WorkItemOperationalState> items,
        IReadOnlyDictionary<string, WorkspaceStatusResult> workspaceStatuses,
        string? integration,
        bool json,
        Func<WorkItemId, string> formatShort,
        StatusOutputContext? context = null)
    {
        var providerCapacity = (context?.ProviderCapacities ?? [])
            .Where(value => value.State != ProviderCapacityState.Available)
            .OrderBy(value => value.Agent, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var needsAttention = Group(items, OperationalStatuses.NeedsAttention);
        var completed = Group(items, OperationalStatuses.Completed);
        var paused = Group(items, OperationalStatuses.PausedSession);
        var active = items.Where(value => value.OperationalStatus
            is OperationalStatuses.AgentActive
            or OperationalStatuses.HumanEditing
            or OperationalStatuses.AutomationActive).ToArray();
        var queued = Group(items, OperationalStatuses.Queued);
        var retries = Group(items, OperationalStatuses.RetryScheduled);
        var handoffs = Group(items, OperationalStatuses.HandoffQueued);
        var localWorkers = context?.WorkerInstances ?? [];
        var configurationRevision = context?.ConfigurationRevision;
        var configurationDriftWorkers = configurationRevision is null
            ? []
            : localWorkers.Where(worker => !string.Equals(
                worker.Instance.ConfigurationRevision,
                configurationRevision,
                StringComparison.Ordinal)).ToArray();

        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = new
                {
                    needsAttention = needsAttention
                        .Select(value => StatusDto(value, workspaceStatuses, formatShort)).ToArray(),
                    completed = completed
                        .Select(value => StatusDto(value, workspaceStatuses, formatShort)).ToArray(),
                    paused = paused
                        .Select(value => StatusDto(value, workspaceStatuses, formatShort)).ToArray(),
                    active = active
                        .Select(value => StatusDto(value, workspaceStatuses, formatShort)).ToArray(),
                    queued = queued
                        .Select(value => StatusDto(value, workspaceStatuses, formatShort)).ToArray(),
                    retries = retries
                        .Select(value => StatusDto(value, workspaceStatuses, formatShort)).ToArray(),
                    handoffs = handoffs
                        .Select(value => StatusDto(value, workspaceStatuses, formatShort)).ToArray(),
                    providerCapacity,
                    localWorkers,
                    configurationRevision,
                    configurationDriftWorkerCount = configurationDriftWorkers.Length
                }
            });
            return;
        }

        if (needsAttention.Length + completed.Length + paused.Length +
            active.Length + queued.Length + retries.Length + handoffs.Length +
            providerCapacity.Length + localWorkers.Count == 0)
        {
            await output.WriteLineAsync("Nothing needs attention: no blocked, retained, active, or queued items.");
            return;
        }

        await WriteLocalWorkersAsync(localWorkers);
        if (configurationDriftWorkers.Length > 0)
        {
            await output.WriteLineAsync(
                $"Configuration restart required: {configurationDriftWorkers.Length} local " +
                "worker process(es) use a different repository revision.");
        }
        await WriteProviderCapacityAsync(providerCapacity);
        await WriteStatusGroupAsync("Needs attention", needsAttention, formatShort,
            async value =>
            {
                await WriteLastRunExcerptAsync(value);
                await output.WriteLineAsync(
                    $"      wrighty edit {value.Item.Id.Value} --takeover --yes --body-file requirements.md --requeue");
                await output.WriteLineAsync(
                    $"      wrighty worker --item {value.Item.Id.Value} --yes");
            });
        await WriteStatusGroupAsync("Completed — retained worktree", completed, formatShort,
            value => WriteWorktreeAndCompletionAsync(value, workspaceStatuses, integration));
        await WriteStatusGroupAsync("Paused — resumable session", paused, formatShort,
            async value =>
            {
                await WriteLastRunExcerptAsync(value);
                await output.WriteLineAsync(
                    $"      wrighty resume-command {value.Item.Id.Value}");
                await output.WriteLineAsync(
                    $"      wrighty worker --item {value.Item.Id.Value} --yes");
            });
        await WriteStatusGroupAsync("Active", active, formatShort,
            value =>
            {
                var until = value.Claim.ExpiresAt is { } expiry ? $" until {expiry:O}" : string.Empty;
                return output.WriteLineAsync($"      {OperationalStatusLabel(value)}{until}");
            });
        await WriteStatusGroupAsync("Queued", queued, formatShort, _ => Task.CompletedTask);
        await WriteStatusGroupAsync("Retry scheduled", retries, formatShort,
            WriteDispatchExcerptAsync);
        await WriteStatusGroupAsync("Handoff queued", handoffs, formatShort,
            WriteDispatchExcerptAsync);
    }

    private async Task WriteLocalWorkersAsync(
        IReadOnlyList<WorkerInstanceStatus> workers)
    {
        if (workers.Count == 0)
            return;
        await output.WriteLineAsync($"Local worker processes ({workers.Count})");
        foreach (var worker in workers)
        {
            var value = worker.Instance;
            await output.WriteLineAsync(
                $"  pid {value.ProcessId}  {worker.Liveness.ToString().ToLowerInvariant()}  " +
                $"{value.State.ToString().ToLowerInvariant()}" +
                (value.CurrentItemId is null ? string.Empty : $"  item {value.CurrentItemId}"));
            await output.WriteLineAsync(
                $"      config {ShortRevision(value.ConfigurationRevision)}; heartbeat {value.LastHeartbeatAt:O}");
            if (worker.Detail is not null)
                await output.WriteLineAsync($"      {worker.Detail}");
        }
        await output.WriteLineAsync();
    }

    private static string ShortRevision(string revision) =>
        revision.Length <= 12 ? revision : revision[..12];

    private async Task WriteProviderCapacityAsync(
        IReadOnlyList<ProviderCapacity> providerCapacity)
    {
        if (providerCapacity.Count == 0)
            return;
        var heading = providerCapacity.Any(value => value.ConsecutiveFailures > 0)
            ? "Provider unavailable"
            : "Provider capacity probe in progress";
        await output.WriteLineAsync($"{heading} ({providerCapacity.Count})");
        foreach (var availability in providerCapacity)
        {
            var label = AgentLabel(availability.Agent) ?? availability.Agent;
            var state = availability.State == ProviderCapacityState.ProbeInProgress
                ? "capacity probe in progress"
                : "automatic work paused";
            var until = availability.UnavailableUntil is { } timestamp
                ? $" until {timestamp:O}"
                : string.Empty;
            await output.WriteLineAsync($"  {label}  {state}{until}");
            if (!string.IsNullOrWhiteSpace(availability.Reason))
                await output.WriteLineAsync($"      {SingleLine(availability.Reason)}");
            if (availability.ConsecutiveFailures > 0)
            {
                await output.WriteLineAsync(
                    $"      {availability.Confidence.ToString().ToLowerInvariant()}; " +
                    $"{availability.ConsecutiveFailures} consecutive capacity failure(s)");
            }
            else
            {
                await output.WriteLineAsync(
                    "      explicit capacity check; no capacity failure recorded");
            }
        }
        await output.WriteLineAsync();
    }

    private static WorkItemOperationalState[] Group(
        IReadOnlyList<WorkItemOperationalState> items, string activity) =>
        items.Where(value => value.OperationalStatus == activity).ToArray();

    private async Task WriteStatusGroupAsync(
        string title,
        IReadOnlyList<WorkItemOperationalState> group,
        Func<WorkItemId, string> formatShort,
        Func<WorkItemOperationalState, Task> writeDetail)
    {
        if (group.Count == 0)
            return;
        await output.WriteLineAsync($"{title} ({group.Count})");
        foreach (var value in group)
        {
            await output.WriteLineAsync(
                $"  {formatShort(value.Item.Id)}  {SingleLine(value.Item.Title)}");
            await writeDetail(value);
        }
        await output.WriteLineAsync();
    }

    private async Task WriteLastRunExcerptAsync(WorkItemOperationalState value)
    {
        if (value.Session is { Outcome: { } outcome } session)
        {
            var excerpt = FirstLine(session.FinalMessage);
            await output.WriteLineAsync(
                $"      last run: {RunOutcomeLabel(outcome)}" +
                (excerpt is null ? string.Empty : $" — {excerpt}"));
        }
    }

    private Task WriteDispatchExcerptAsync(WorkItemOperationalState value)
    {
        if (value.Session?.Dispatch is not { } dispatch)
            return output.WriteLineAsync(
                "      details unavailable on this Wrighty installation");
        return output.WriteLineAsync(
            $"      {dispatch.Reason}; no earlier than {dispatch.NotBefore:O}; " +
            $"attempt {dispatch.Attempt} of {dispatch.MaxAttempts}");
    }

    private async Task WriteWorktreeAndCompletionAsync(
        WorkItemOperationalState value,
        IReadOnlyDictionary<string, WorkspaceStatusResult> workspaceStatuses,
        string? integration)
    {
        var branch = value.Session?.Branch;
        var status = workspaceStatuses.GetValueOrDefault(value.Item.Id.Value);
        if (status is { WorktreeAbsent: true })
        {
            await output.WriteLineAsync("      worktree: removed — no longer present on this host");
            return;
        }

        var state = status is { Status: { } git }
            ? $" ({(git.Dirty ? "dirty" : "clean")}, {(git.MergedIntoHead ? "merged" : "unmerged")})"
            : status is { Unavailable: { } reason } ? $" ({reason})" : string.Empty;
        if (!string.IsNullOrWhiteSpace(branch))
            await output.WriteLineAsync($"      branch {branch}{state}");
        if (value.Session?.WorkspacePath is { } path && !string.IsNullOrWhiteSpace(branch) &&
            status is { Status: { } gitStatus })
        {
            await WriteCompletionGuidanceAsync(
                path, branch, integration, gitStatus.Dirty, gitStatus.MergedIntoHead);
        }
    }

    private async Task WriteCompletionGuidanceAsync(
        string path, string branch, string? integration, bool dirty, bool mergedIntoHead)
    {
        foreach (var action in WorkerCompletionGuidance.ForCompletedWorktree(
            path, branch, integration, dirty, mergedIntoHead))
        {
            await output.WriteLineAsync($"      {action.Scenario}:");
            foreach (var command in action.Commands)
                await output.WriteLineAsync($"        {command}");
        }
    }

    private object StatusDto(
        WorkItemOperationalState value,
        IReadOnlyDictionary<string, WorkspaceStatusResult> workspaceStatuses,
        Func<WorkItemId, string> formatShort)
    {
        var status = workspaceStatuses.GetValueOrDefault(value.Item.Id.Value);
        return new
        {
            id = value.Item.Id.Value,
            displayId = formatShort(value.Item.Id),
            value.Item.Title,
            value.Item.Status,
            operationalStatus = value.OperationalStatus,
            branch = value.Session?.Branch,
            hasRecordedWorktree = value.Session?.HasRecordedWorktree ?? false,
            lastRun = value.Session?.Outcome is not { } outcome
                ? null
                : new
                {
                    outcome = outcome.ToString().ToLowerInvariant(),
                    endedAt = value.Session.EndedAt,
                    // Stripped like every other surface that quotes the agent's closing words. A
                    // record written before the report block was stripped on the way in still
                    // carries one, and a consumer of this JSON should not have to know that.
                    finalMessage = AgentReportParser.WithoutReportBlock(value.Session.FinalMessage),
                    failure = value.Session.Failure
                },
            dispatch = value.Session?.Dispatch,
            worktree = status is null
                ? null
                : new
                {
                    available = status.IsAvailable,
                    removed = status.WorktreeAbsent,
                    dirty = status.Status?.Dirty,
                    mergedIntoHead = status.Status?.MergedIntoHead,
                    unavailableReason = status.Unavailable
                }
        };
    }

    private static string? FirstLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;
        var line = message.Replace("\r\n", "\n").Split('\n', 2)[0].Trim();
        return line.Length <= 120 ? line : line[..120] + "…";
    }

    public async Task WriteOperationalDetailAsync(
        WorkItemOperationalState value,
        bool json,
        Func<WorkItemId, string> formatShort,
        WorkspaceStatusResult? workspaceStatus = null)
    {
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = OperationalDto(value, formatShort, includeBody: true, workspaceStatus)
            });
            return;
        }

        var item = value.Item;
        await WriteItemHeaderAsync(item, formatShort);
        await WriteWorkerDetailAsync(value);
        await WriteClaimDetailAsync(value);
        await WriteLastRunAsync(value);
        await WriteDispatchAsync(value);
        await WriteSessionDetailAsync(value, workspaceStatus);

        foreach (var field in item.EffectiveFields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            await output.WriteLineAsync($"{field.Key}: {field.Value}");

        await WriteOperationalActionsAsync(value);
        await output.WriteLineAsync();
        await output.WriteLineAsync("Body");
        await output.WriteAsync(item.Body);
        if (!item.Body.EndsWith('\n'))
            await output.WriteLineAsync();
    }

    /// <summary>
    /// Reports what an unattended agent would be given for one item, or why it would be refused.
    ///
    /// Counts, identifiers, timestamps and the revision digest only — never a comment body. This
    /// runs on a terminal and into logs, and the approved content is exactly what must not appear
    /// there. The pending comments are named by URL so a maintainer can go and decide them.
    /// </summary>
    public async Task WriteApprovedContextAsync(
        WorkItemId id,
        ExecutionContextResult result,
        ContextLimits limits,
        bool json)
    {
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = new
                {
                    id = id.Value,
                    approved = result.IsApproved,
                    code = result.Code,
                    message = result.Message,
                    pending = result.PendingUrls,
                    approval = result.Snapshot is null ? null : new
                    {
                        // Serialized as the enum value, not ToString(): the type declares stable
                        // kebab-case wire names, and ToString() would put the C# identifier in a
                        // documented JSON contract instead.
                        source = result.Snapshot.Approval.Source,
                        baseApprovedAt = result.Snapshot.Approval.BaseApprovedAt,
                        batchCommentCutoff = result.Snapshot.Approval.BatchCommentCutoff
                    },
                    revision = result.Snapshot is null ? null : new
                    {
                        formatVersion = result.Snapshot.Revision.FormatVersion,
                        digest = result.Snapshot.Revision.Digest,
                        capturedAt = result.Snapshot.Revision.CapturedAt
                    },
                    discussion = result.Snapshot is null ? null : new
                    {
                        included = result.Snapshot.IncludedCount,
                        excluded = result.Snapshot.ExcludedCount,
                        pending = result.Snapshot.PendingCount
                    },
                    limits = new
                    {
                        maxDiscussionComments = limits.MaxDiscussionEntries,
                        maxEntryCharacters = limits.MaxEntryCharacters,
                        maxTotalCharacters = limits.MaxTotalCharacters
                    }
                }
            });
            return;
        }

        await output.WriteLineAsync($"{id} approved context");
        if (result.Snapshot is not { } snapshot)
        {
            await output.WriteLineAsync($"Approved: no ({result.Code})");
            if (result.Message is { } message)
                await output.WriteLineAsync(message);
            foreach (var url in result.PendingUrls ?? [])
                await output.WriteLineAsync($"  Undecided: {url}");
            return;
        }

        await output.WriteLineAsync("Approved: yes");
        await output.WriteLineAsync($"Approval source: {snapshot.Approval.Source.WireName()}");
        if (snapshot.Approval.BaseApprovedAt is { } approvedAt)
            await output.WriteLineAsync($"Base approved at: {approvedAt:O}");
        if (snapshot.Approval.BatchCommentCutoff is { } cutoff)
            await output.WriteLineAsync($"Batch comment cutoff: {cutoff:O}");
        await output.WriteLineAsync($"Context revision: {snapshot.Revision.ShortDigest}");
        await output.WriteLineAsync(
            $"Discussion: {snapshot.IncludedCount} included, {snapshot.ExcludedCount} excluded, " +
            $"{snapshot.PendingCount} pending");
        await output.WriteLineAsync(
            $"Limits: {limits.MaxDiscussionEntries} entries, {limits.MaxEntryCharacters} per entry, " +
            $"{limits.MaxTotalCharacters} total characters");
    }

    private async Task WriteItemHeaderAsync(
        WorkItemDetail item,
        Func<WorkItemId, string> formatShort)
    {
        await output.WriteLineAsync($"{formatShort(item.Id)} {SingleLine(item.Title)}");
        await output.WriteLineAsync($"Status: {Token(item.Status, "-")}");
        await output.WriteLineAsync($"Priority: {Token(item.Priority, "-")}");
        await output.WriteLineAsync($"Archived: {(item.Archived ? "yes" : "no")}");
        if (item.Url is not null)
            await output.WriteLineAsync($"URL: {item.Url}");
    }

    private async Task WriteWorkerDetailAsync(WorkItemOperationalState value)
    {
        var item = value.Item;
        await output.WriteLineAsync();
        await output.WriteLineAsync("Execution policy");
        await output.WriteLineAsync(
            $"  Automatic execution: {(item.AutomaticExecutionAllowed ? "allowed" : "manual only")}");
        await output.WriteLineAsync(
            $"  Agent: {AgentLabel(item.AgentPolicy) ?? "repository default"}");
        await output.WriteLineAsync();
        await output.WriteLineAsync($"Operational status: {OperationalStatusLabel(value)}");
        if (IsWorkerRunClaim(value))
            await output.WriteLineAsync(
                "  Active claim from a Wrighty worker (not a process-liveness guarantee)");
    }

    private async Task WriteClaimDetailAsync(WorkItemOperationalState value)
    {
        await output.WriteLineAsync();
        await output.WriteLineAsync("Claim");
        await output.WriteLineAsync($"  State: {ClaimStateLabel(value.Claim.State)}");
        if (value.Claim.State != ClaimOwnershipState.Unclaimed)
        {
            await output.WriteLineAsync(
                $"  Claimant type: {ClaimantTypeLabel(value.Claim)}");
            if (!string.IsNullOrWhiteSpace(value.Claim.ClaimantId))
                await output.WriteLineAsync($"  Claimant ID: {value.Claim.ClaimantId}");
            if (!string.IsNullOrWhiteSpace(value.Claim.Agent))
                await output.WriteLineAsync($"  Agent: {AgentLabel(value.Claim.Agent)}");
            if (value.Claim.ExpiresAt is not null)
            {
                await output.WriteLineAsync($"  Expires: {value.Claim.ExpiresAt:O}");
                await output.WriteLineAsync($"  Lease remaining: {LeaseLabel(value)}");
            }
            await output.WriteLineAsync(
                $"  Installation: {(value.Claim.State == ClaimOwnershipState.OwnedByCurrent ? "this installation" : "another installation")}");
        }
    }

    private async Task WriteLastRunAsync(WorkItemOperationalState value)
    {
        if (value.Session is not { Outcome: { } outcome } session)
            return;
        await output.WriteLineAsync();
        await output.WriteLineAsync("Last run");
        // What Wrighty observed leads, and the vendor's process result is named as the vendor's.
        // Printing only the latter under "Outcome" reads as a verdict on the run, which it is not:
        // a vendor exits successfully whenever it stops cleanly, including when it stopped to ask a
        // question. The published comment has always drawn this line; this surface now draws it too.
        if (session.LastReport is { } lastReport)
        {
            await output.WriteLineAsync($"  Outcome: {DispositionLabel(lastReport.ObservedDisposition)}");
            await output.WriteLineAsync($"  Vendor process: {RunOutcomeLabel(outcome)}");
        }
        else
        {
            await output.WriteLineAsync($"  Outcome: {RunOutcomeLabel(outcome)}");
        }
        if (session.EndedAt is { } endedAt)
            await output.WriteLineAsync($"  Ended: {endedAt:O}");
        if (session.Failure is { } failure)
        {
            await output.WriteLineAsync(
                $"  Failure: {FailureKindLabel(failure.Kind)} " +
                $"({failure.Confidence.ToString().ToLowerInvariant()})");
            if (!string.IsNullOrWhiteSpace(failure.ProviderCode))
                await output.WriteLineAsync($"  Provider code: {failure.ProviderCode}");
            if (failure.RetryAt is { } retryAt)
                await output.WriteLineAsync($"  Provider retry at: {retryAt:O}");
            if (failure.RetryAfter is { } retryAfter)
                await output.WriteLineAsync($"  Provider retry after: {retryAfter}");
        }
        // Without the report block: it is rendered as structured fields immediately below, and
        // printing both puts the same account on screen twice.
        if (ApprovedContext.AgentReportParser.WithoutReportBlock(session.FinalMessage) is { } message)
        {
            await output.WriteLineAsync("  Final message:");
            foreach (var line in message.Replace("\r\n", "\n").Split('\n'))
                await output.WriteLineAsync($"    {line}");
        }

        await WriteAgentReportAsync(session.LastReport);
    }

    /// <summary>
    /// The agent's own report on its last run, when it produced one.
    ///
    /// Labelled at the heading rather than per line, and the checks heading names the claimant,
    /// because a run report is the agent's account and not a set of established facts. The outcome
    /// above it is Wrighty's; nothing here can contradict it.
    /// </summary>
    private async Task WriteAgentReportAsync(ApprovedContext.AgentRunReport? report)
    {
        if (report is null || report.IsObservedOnly) return;

        await output.WriteLineAsync();
        await output.WriteLineAsync("Agent report (the agent's account, not verified by Wrighty)");
        if (!string.IsNullOrWhiteSpace(report.Summary))
            await output.WriteLineAsync($"  {report.Summary}");

        await WriteReportSectionAsync("Changed", report.Changes);
        await WriteReportSectionAsync("Checks the agent says it ran", report.Verification);
        await WriteReportSectionAsync("Decisions and assumptions", report.Decisions);
        await WriteReportSectionAsync("Input requested", report.RequestedInput);
        await WriteReportSectionAsync("Remaining work", report.RemainingWork);

        if (!string.IsNullOrWhiteSpace(report.AgentReportedBody))
        {
            await output.WriteLineAsync("  Unstructured response:");
            foreach (var line in report.AgentReportedBody.Replace("\r\n", "\n").Split('\n'))
                await output.WriteLineAsync($"    {line}");
        }
    }

    private async Task WriteReportSectionAsync(string heading, IReadOnlyList<string>? items)
    {
        if (items is null || items.Count == 0) return;
        await output.WriteLineAsync($"  {heading}:");
        foreach (var item in items)
            await output.WriteLineAsync($"    - {item}");
    }

    private static string DispositionLabel(ApprovedContext.RunReportDisposition disposition) =>
        disposition switch
        {
            ApprovedContext.RunReportDisposition.Finished => "finished",
            ApprovedContext.RunReportDisposition.NeedsAttention => "needs attention",
            ApprovedContext.RunReportDisposition.Failed => "failed",
            _ => "rejected"
        };

    private static string RunOutcomeLabel(RunOutcome outcome) => outcome switch
    {
        RunOutcome.Succeeded => "succeeded",
        RunOutcome.Failed => "failed",
        RunOutcome.Rejected => "rejected",
        _ => outcome.ToString().ToLowerInvariant()
    };

    private static string FailureKindLabel(AgentFailureKind kind) =>
        kind.ToString().Select((character, index) =>
                index > 0 && char.IsUpper(character)
                    ? $" {char.ToLowerInvariant(character)}"
                    : char.ToLowerInvariant(character).ToString())
            .Aggregate(string.Concat);

    private async Task WriteDispatchAsync(WorkItemOperationalState value)
    {
        if (value.OperationalStatus is not (
                OperationalStatuses.RetryScheduled or OperationalStatuses.HandoffQueued))
            return;
        await output.WriteLineAsync();
        await output.WriteLineAsync("Pending dispatch");
        if (value.Session?.Dispatch is not { } dispatch)
        {
            await output.WriteLineAsync(
                "  Scheduled on another installation; exact details are unavailable here.");
            return;
        }

        await output.WriteLineAsync($"  State: {dispatch.State}");
        await output.WriteLineAsync($"  Reason: {dispatch.Reason}");
        if (!string.IsNullOrWhiteSpace(dispatch.Agent))
            await output.WriteLineAsync($"  Agent: {AgentLabel(dispatch.Agent)}");
        if (!string.IsNullOrWhiteSpace(dispatch.SessionAgent) &&
            !string.Equals(dispatch.SessionAgent, dispatch.Agent, StringComparison.OrdinalIgnoreCase))
            await output.WriteLineAsync($"  Session agent: {AgentLabel(dispatch.SessionAgent)}");
        await output.WriteLineAsync(
            $"  Not before (local): {dispatch.NotBefore.ToLocalTime():yyyy-MM-dd HH:mm:ss zzz}");
        await output.WriteLineAsync(
            $"  Not before (UTC): {dispatch.NotBefore.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z");
        await output.WriteLineAsync(
            $"  Attempt: {dispatch.Attempt} of {dispatch.MaxAttempts}");
        await output.WriteLineAsync(
            $"  Installation: {(dispatch.FromCurrentInstallation ? "this installation" : "another installation")}");
    }

    private async Task WriteSessionDetailAsync(
        WorkItemOperationalState value,
        WorkspaceStatusResult? workspaceStatus = null)
    {
        await output.WriteLineAsync();
        await output.WriteLineAsync("Session");
        await output.WriteLineAsync(
            $"  Resume address complete: {(value.Session is { IsComplete: true } ? "yes" : "no")}");
        if (value.Session is { } session)
            await WriteSessionBodyAsync(session, workspaceStatus);
    }

    private async Task WriteSessionBodyAsync(
        AgentSessionRecord session,
        WorkspaceStatusResult? workspaceStatus)
    {
        if (!string.IsNullOrWhiteSpace(session.Agent))
            await output.WriteLineAsync($"  Agent: {AgentLabel(session.Agent)}");
        if (!string.IsNullOrWhiteSpace(session.SessionId))
            await output.WriteLineAsync($"  Session ID: {session.SessionId}");
        if (workspaceStatus is { WorktreeAbsent: true })
        {
            // The worktree was removed (e.g. cleaned up after completion). The durable session
            // is kept for the record, but the path/branch no longer exist — collapse them into
            // one honest line instead of printing a dead path.
            await output.WriteLineAsync("  Worktree: removed — no longer present on this host");
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(session.WorkspacePath))
                await output.WriteLineAsync($"  Workspace: {session.WorkspacePath}");
            if (!string.IsNullOrWhiteSpace(session.Branch))
                await output.WriteLineAsync($"  Branch: {session.Branch}");
            await WriteWorkspaceStatusAsync(workspaceStatus);
        }
        // A removed worktree cannot be resumed into (the recorded path is gone), so it is not
        // resumable here regardless of the session address being otherwise complete.
        var resumableHere = session.IsComplete && session.FromCurrentInstallation
            && workspaceStatus is not { WorktreeAbsent: true };
        await output.WriteLineAsync(
            $"  Resumable here: {(resumableHere ? "yes" : "no")}");
    }

    private async Task WriteWorkspaceStatusAsync(WorkspaceStatusResult? workspaceStatus)
    {
        if (workspaceStatus is { Status: { } status })
        {
            await output.WriteLineAsync(
                $"  Working tree: {(status.Dirty ? "dirty" : "clean")}");
            await output.WriteLineAsync(
                $"  Branch state: {(status.MergedIntoHead ? "merged" : "unmerged")}");
        }
        else if (workspaceStatus is { Unavailable: { } unavailable })
        {
            await output.WriteLineAsync($"  Worktree status: {unavailable}");
        }
    }

    private async Task WriteOperationalActionsAsync(WorkItemOperationalState value)
    {
        var actions = OperationalActions(value);
        if (actions.Count == 0)
            return;
        await output.WriteLineAsync();
        await output.WriteLineAsync("Next actions");
        foreach (var action in actions)
            await output.WriteLineAsync($"  {action}");
    }

    public async Task WriteInitializationAsync(
        TrackerInitializationResult result,
        bool checkOnly,
        bool json,
        AgentRuntimeSnapshot? runtimes = null)
    {
        var local = string.Equals(
            result.Config.Backend,
            "local-markdown",
            StringComparison.OrdinalIgnoreCase);
        if (json)
        {
            await WriteInitializationJsonAsync(result, checkOnly, local, runtimes);
            return;
        }

        await WriteInitializationHumanAsync(result, checkOnly, local, runtimes);
    }

    public async Task WriteImportAsync(LocalMarkdownImportResult result, bool json)
    {
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = new
                {
                    result.DryRun,
                    result.Moved,
                    count = result.Items.Count,
                    items = result.Items
                }
            });
            return;
        }

        foreach (var item in result.Items)
        {
            await output.WriteLineAsync(
                $"{(result.DryRun ? "would import" : "imported")} {item.SourcePath} -> local:{item.Id} {item.DestinationPath} [{item.Status}] {item.Title}");
        }

        await output.WriteLineAsync(result.DryRun
            ? $"dry run: {result.Items.Count} file(s); no changes written"
            : $"imported {result.Items.Count} file(s){(result.Moved ? " and removed verified sources" : string.Empty)}");
    }

    public async Task WritePortableImportPlanAsync(
        PortableImportSource source,
        string status,
        bool json)
    {
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = new
                {
                    dryRun = true,
                    sourcePath = source.Path,
                    source.Title,
                    status,
                    source.Priority,
                    customFields = source.CustomFieldNames
                }
            });
            return;
        }
        await output.WriteLineAsync(
            $"would import {source.Path} -> github [status: {status}, priority: {source.Priority ?? "-"}] {source.Title}");
        await output.WriteLineAsync("dry run: source and tracker unchanged");
    }

    internal async Task WriteWholeStoreImportAsync(
        WholeStoreImportSummary summary,
        bool json)
    {
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = summary
            });
            return;
        }

        await output.WriteLineAsync(
            summary.DryRun
                ? $"dry run: planned {summary.Planned} item(s), approximately {summary.EstimatedRemoteOperations} remote operations; no backend or manifest writes"
                : $"whole-store import: {summary.Created} created, {summary.Resumed} resumed, {summary.Skipped} skipped, {summary.Failed} failed");
        if (summary.DryRun)
        {
            foreach (var item in summary.PlannedItems)
            {
                await output.WriteLineAsync($"would import {item}");
            }
        }
        await output.WriteLineAsync($"manifest: {summary.ManifestPath}");
        foreach (var warning in summary.ReferenceWarnings)
        {
            await output.WriteLineAsync($"reference warning: {warning}");
        }
        await output.WriteLineAsync(summary.BackendSwitchGuidance);
    }

    private Task WriteInitializationJsonAsync(
        TrackerInitializationResult result,
        bool checkOnly,
        bool local,
        AgentRuntimeSnapshot? runtimes) => WriteJsonAsync(new
        {
            schemaVersion = 1,
            result = new
            {
                result.Config.Backend,
                result.BackendSelection,
                repository = local ? null : result.Config.Repository,
                projectOwner = local ? null : result.Config.EffectiveProjectOwner,
                projectNumber = local ? (int?)null : result.Config.ProjectNumber,
                projectTitle = local ? null : result.ProjectTitle,
                projectUrl = local ? null : result.ProjectUrl,
                localPath = local ? result.ProjectUrl : null,
                result.ConfigPath,
                result.CreatedProject,
                result.LinkedRepository,
                initialized = !checkOnly,
                valid = true,
                changed = result.Changed,
                worker = new Dictionary<string, string?>
                {
                    ["defaultAgent"] = result.Config.EffectiveWorker.DefaultAgent
                },
                agents = runtimes?.Agents.Select(runtime => new
                {
                    agent = runtime.Agent,
                    supported = runtime.Supported,
                    installed = runtime.Installed,
                    executable = runtime.ExecutablePath,
                    readiness = runtime.Readiness.ToString().ToLowerInvariant()
                }).ToArray(),
                actions = result.Actions
            }
        });

    private async Task WriteInitializationHumanAsync(
        TrackerInitializationResult result,
        bool checkOnly,
        bool local,
        AgentRuntimeSnapshot? runtimes)
    {
        await output.WriteLineAsync($"Backend: {result.Config.Backend}");
        await output.WriteLineAsync($"Backend selection: {result.BackendSelection}");
        await WriteInitializationTargetAsync(result, local);
        await output.WriteLineAsync($"Configuration: {result.ConfigPath}");
        await WriteInitializationAgentsAsync(result, runtimes);
        await output.WriteLineAsync(InitializationResultMessage(result, checkOnly));
        foreach (var action in result.Actions)
            await output.WriteLineAsync($"- {action}");
    }

    private async Task WriteInitializationTargetAsync(
        TrackerInitializationResult result,
        bool local)
    {
        if (local)
        {
            await output.WriteLineAsync($"Store: {result.ProjectUrl}");
            return;
        }

        await output.WriteLineAsync($"Repository: {result.Config.Repository}");
        await output.WriteLineAsync(
            $"Project: {result.Config.EffectiveProjectOwner}/{result.Config.ProjectNumber} ({result.ProjectTitle})");
    }

    private async Task WriteInitializationAgentsAsync(
        TrackerInitializationResult result,
        AgentRuntimeSnapshot? runtimes)
    {
        var defaultAgent = result.Config.EffectiveWorker.DefaultAgent;
        var defaultState = DefaultAgentState(defaultAgent, runtimes);
        await output.WriteLineAsync(
            $"Worker default agent: {defaultAgent ?? "none"}{defaultState}");
        if (runtimes is null)
            return;

        await output.WriteLineAsync("Local agent CLIs:");
        foreach (var runtime in runtimes.Agents)
            await output.WriteLineAsync(AgentRuntimeLine(runtime));
    }

    private static string DefaultAgentState(
        string? defaultAgent,
        AgentRuntimeSnapshot? runtimes)
    {
        if (defaultAgent is null || runtimes is null)
            return string.Empty;
        return runtimes.IsInstalled(defaultAgent)
            ? " (installed)"
            : " (not installed locally)";
    }

    private static string AgentRuntimeLine(AgentRuntime runtime) =>
        runtime.Installed
            ? $"- {runtime.Agent}: installed at {runtime.ExecutablePath}; readiness unknown"
            : $"- {runtime.Agent}: not installed; readiness unknown";

    private static string InitializationResultMessage(
        TrackerInitializationResult result,
        bool checkOnly)
    {
        if (checkOnly)
            return "configuration and Wrighty resources are valid";
        return result.Changed
            ? "Wrighty initialized"
            : "Wrighty already initialized";
    }

    public async Task WriteClaimAsync(
        WorkItemId id,
        string displayId,
        ClaimResult claim,
        bool json)
    {
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = new
                {
                    id = id.Value,
                    displayId,
                    outcome = claim.Outcome.ToString(),
                    claim.InstallationId,
                    claim.ExpiresAt,
                    claim.EventId,
                    claim.ClaimantId,
                    claim.ClaimToken,
                    claim.Agent,
                    claim.SessionId,
                    claim.ClaimantKind,
                    claim.TakeoverAvailable
                }
            });
            return;
        }

        var verb = claim.Outcome == ClaimOutcome.AlreadyOwned ? "already own" : "claimed";
        await output.WriteLineAsync(
            $"{verb} {displayId} as claimant {claim.ClaimantId} until {claim.ExpiresAt:O}");
        if (claim.ClaimToken is not null)
        {
            await output.WriteLineAsync($"Claim token: {claim.ClaimToken}");
            await output.WriteLineAsync("Pass it with --claim-token or WRIGHTY_CLAIM_TOKEN on every mutation.");
        }
    }

    public async Task WriteWorkspacesAsync(
        IReadOnlyList<(Workers.WorkerWorkspaceInfo Workspace, string? ItemId)> entries,
        bool json)
    {
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = new
                {
                    workspaces = entries.Select(entry => new
                    {
                        path = entry.Workspace.Path,
                        branch = entry.Workspace.Branch,
                        dirty = entry.Workspace.Dirty,
                        mergedIntoHead = entry.Workspace.MergedIntoHead,
                        itemId = entry.ItemId
                    }).ToArray()
                }
            });
            return;
        }

        if (entries.Count == 0)
        {
            await output.WriteLineAsync("No retained worker worktrees.");
            return;
        }

        foreach (var (workspace, itemId) in entries)
        {
            await output.WriteLineAsync(
                $"{workspace.Path} " +
                $"[{(workspace.Dirty ? "dirty" : "clean")}, " +
                $"{(workspace.MergedIntoHead ? "merged" : "unmerged")}]" +
                $"{(workspace.Branch is null ? "" : $" branch {workspace.Branch}")}" +
                $"{(itemId is null ? "" : $" item {itemId}")}");
        }
    }

    public async Task WriteWorkspaceCleanupAsync(
        WorkItemId id,
        string displayId,
        string? workspacePath,
        string? branch,
        bool workspaceRemoved,
        bool branchDeleted,
        bool json)
    {
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = new
                {
                    id = id.Value,
                    displayId,
                    workspacePath,
                    branch,
                    workspaceRemoved,
                    branchDeleted
                }
            });
            return;
        }

        await output.WriteLineAsync(
            $"cleaned up {displayId}: workspace " +
            $"{(workspaceRemoved ? "removed" : "already absent")}, branch " +
            $"{(branchDeleted ? $"deleted ({branch})" : branch is null ? "not recorded" : "already absent")}");
    }

    public async Task WriteReleaseAsync(WorkItemId id, string displayId, bool json)
    {
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = new { id = id.Value, displayId, released = true }
            });
            return;
        }

        await output.WriteLineAsync($"released {displayId}");
    }

    public async Task WriteRequeueAsync(WorkItemId id, string displayId, bool json)
    {
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = new
                {
                    id = id.Value,
                    displayId,
                    dispatchState = DispatchStates.Queued,
                    queued = true
                }
            });
            return;
        }

        await output.WriteLineAsync(
            $"queued {displayId} to resume its recorded agent session");
        await output.WriteLineAsync(
            "A continuous worker will pick it from In Progress; " +
            $"run `wrighty worker --item {id.Value} --yes` to continue immediately.");
    }

    public Task WritePickedAsync(
        PickWorkItemResult picked,
        bool json,
        Func<WorkItemId, string> formatShort)
    {
        if (!json)
            return WritePickedHumanAsync(picked, formatShort);
        return WriteJsonAsync(new
        {
            schemaVersion = 1,
            result = new
            {
                item = SummaryDto(picked.Item, formatShort),
                claimantKind = picked.Claim.ClaimantKind,
                claimantId = picked.Claim.ClaimantId,
                agent = picked.Claim.Agent,
                sessionId = picked.Claim.SessionId,
                claimToken = picked.Claim.ClaimToken,
                expiresAt = picked.Claim.ExpiresAt,
                takeoverAvailable = picked.Claim.TakeoverAvailable
            }
        });
    }

    public Task WritePickedAsync(WorkItemSummary item, bool json,
        Func<WorkItemId, string> formatShort) =>
        WriteItemsAsync([item], compact: !json, json, formatShort);

    private async Task WritePickedHumanAsync(PickWorkItemResult picked, Func<WorkItemId, string> formatShort)
    {
        await WriteItemsAsync([picked.Item], compact: true, json: false, formatShort);
        await output.WriteLineAsync($"Claimant ID: {picked.Claim.ClaimantId}");
        await output.WriteLineAsync($"Claim token: {picked.Claim.ClaimToken}");
        await output.WriteLineAsync("Pass both values on every later mutation.");
    }

    public async Task WriteDetailAsync(
        WorkItemDetail item,
        bool json,
        Func<WorkItemId, string> formatShort)
    {
        var displayId = formatShort(item.Id);
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = new
                {
                    id = item.Id.Value,
                    displayId,
                    item.Title,
                    item.Body,
                    item.Url,
                    item.Status,
                    item.Priority,
                    item.Archived,
                    fields = item.EffectiveFields
                }
            });
            return;
        }

        await output.WriteLineAsync($"{displayId} {SingleLine(item.Title)}");
        await output.WriteLineAsync($"Status: {Token(item.Status, "-")}");
        await output.WriteLineAsync($"Priority: {Token(item.Priority, "-")}");
        await output.WriteLineAsync($"Archived: {(item.Archived ? "yes" : "no")}");
        foreach (var field in item.EffectiveFields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            await output.WriteLineAsync($"{field.Key}: {field.Value}");
        }
        if (item.Url is not null)
        {
            await output.WriteLineAsync($"URL: {item.Url}");
        }
        await output.WriteLineAsync();
        await output.WriteAsync(item.Body);
        if (!item.Body.EndsWith('\n'))
        {
            await output.WriteLineAsync();
        }
    }

    public async Task WriteCreateAsync(
        CreateWorkItemResult result,
        bool json,
        Func<WorkItemId, string> formatShort)
    {
        var displayId = formatShort(result.Id);
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = new
                {
                    id = result.Id.Value,
                    displayId,
                    result.Url,
                    result.CreationAttemptId,
                    disposition = result.Disposition.ToString().ToLowerInvariant(),
                    reconciledStages = result.EffectiveReconciledStages,
                    item = result.Item is null ? null : DetailDto(result.Item, formatShort)
                }
            });
            return;
        }

        await output.WriteLineAsync(result.Url is null
            ? $"{CreateVerb(result)} {displayId}"
            : $"{CreateVerb(result)} {displayId} {result.Url}");
        await output.WriteLineAsync($"creation attempt: {result.CreationAttemptId}");
        await output.WriteLineAsync($"disposition: {result.Disposition.ToString().ToLowerInvariant()}");
        if (result.EffectiveReconciledStages.Count > 0)
        {
            await output.WriteLineAsync($"reconciled: {string.Join(", ", result.EffectiveReconciledStages)}");
        }
    }

    public async Task WriteAdoptAsync(
        IReadOnlyList<AdoptWorkItemResult> results,
        bool json,
        Func<WorkItemId, string> formatShort)
    {
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = results.Select(result => new
                {
                    id = result.Id.Value,
                    displayId = formatShort(result.Id),
                    sourceReference = result.SourceReference,
                    result.Url,
                    disposition = AdoptDispositionText(result.Disposition),
                    appliedStages = result.AppliedStages,
                    pendingStages = result.PendingStages
                }).ToArray()
            });
            return;
        }

        foreach (var result in results)
        {
            await output.WriteLineAsync(
                $"{AdoptDispositionText(result.Disposition)} " +
                $"{formatShort(result.Id)}{(result.Url is null ? string.Empty : $" {result.Url}")}");
            if (result.AppliedStages.Count > 0)
            {
                await output.WriteLineAsync($"applied: {string.Join(", ", result.AppliedStages)}");
            }
        }
    }

    private static string AdoptDispositionText(AdoptDisposition disposition) =>
        disposition == AdoptDisposition.AlreadyAdopted
            ? "already-adopted"
            : disposition.ToString().ToLowerInvariant();

    public async Task WriteCreationAttemptAsync(string creationAttemptId, bool json)
    {
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = new { creationAttemptId }
            });
            return;
        }

        await output.WriteLineAsync(creationAttemptId);
    }

    private static string CreateVerb(CreateWorkItemResult result) =>
        result.Disposition == CreateDisposition.Resumed ? "resumed" : "created";

    public async Task WriteUpdateAsync(
        UpdateWorkItemResult result,
        bool move,
        bool json,
        Func<WorkItemId, string> formatShort)
    {
        var displayId = formatShort(result.Item.Id);
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = new
                {
                    id = result.Item.Id.Value,
                    displayId,
                    result.Changed,
                    result.ChangedFields,
                    item = DetailDto(result.Item, formatShort)
                }
            });
            return;
        }

        if (!result.Changed)
        {
            await output.WriteLineAsync(move
                ? $"{displayId} already has status {result.Item.Status}"
                : $"{displayId} already matches the requested values");
            return;
        }

        await output.WriteLineAsync(move
            ? $"moved {displayId} to {result.Item.Status}"
            : $"updated {displayId}: {string.Join(", ", result.ChangedFields)}");
    }

    public async Task WriteArchiveAsync(
        ArchiveWorkItemResult result,
        bool json,
        Func<WorkItemId, string> formatShort)
    {
        var displayId = formatShort(result.Item.Id);
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = new
                {
                    id = result.Item.Id.Value,
                    displayId,
                    result.Archived,
                    result.Changed,
                    item = DetailDto(result.Item, formatShort)
                }
            });
            return;
        }

        await output.WriteLineAsync(result.Changed
            ? $"{(result.Archived ? "archived" : "unarchived")} {displayId}"
            : $"{displayId} is already {(result.Archived ? "archived" : "active")}");
    }

    public async Task WriteFinishAsync(
        FinishWorkItemResult result,
        bool json,
        Func<WorkItemId, string> formatShort)
    {
        var displayId = formatShort(result.Item.Id);
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = new
                {
                    id = result.Item.Id.Value,
                    displayId,
                    disposition = result.Disposition == FinishDisposition.AlreadyFinished
                        ? "already-finished"
                        : "finished",
                    result.StatusChanged,
                    result.ClaimReleased,
                    item = DetailDto(result.Item, formatShort)
                }
            });
            return;
        }

        await output.WriteLineAsync(result.Disposition == FinishDisposition.AlreadyFinished
            ? $"{displayId} is already finished"
            : $"finished {displayId} with status {result.Item.Status}");
    }

    public async Task WriteSkillOperationsAsync(
        IReadOnlyList<SkillOperationResult> results,
        string operation,
        bool json)
    {
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = new
                {
                    operation,
                    installations = results.Select(item => new
                    {
                        item.Agent,
                        item.Scope,
                        item.Path,
                        previousState = item.PreviousState.ToString().ToLowerInvariant(),
                        state = item.State.ToString().ToLowerInvariant(),
                        item.Changed,
                        item.PreviousVersion,
                        item.Version,
                        item.DescriptionPreserved
                    })
                }
            });
            return;
        }

        foreach (var result in results)
        {
            await output.WriteLineAsync(
                $"{result.Agent}: {result.State.ToString().ToLowerInvariant()} {result.Path}" +
                (result.Changed ? " (changed)" : string.Empty));
        }
    }

    public async Task<int> WriteErrorAsync(TrackerException exception, bool json)
    {
        if (json)
        {
            await WriteJsonErrorAsync(exception);
        }
        else
        {
            await WriteHumanErrorAsync(exception);
        }

        return exception.ExitCode;
    }

    private async Task WriteJsonErrorAsync(TrackerException exception)
    {
        var payload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            error = new
            {
                code = exception.Code,
                message = exception.Message,
                details = exception.Details
            }
        }, JsonOptions);
        await error.WriteLineAsync(payload);
    }

    private async Task WriteHumanErrorAsync(TrackerException exception)
    {
        await error.WriteLineAsync($"{exception.Code}: {exception.Message}");
        if (!IsPartialError(exception.Code))
        {
            return;
        }

        foreach (var key in PartialErrorDetailKeys)
        {
            if (exception.Details.TryGetValue(key, out var value) && value is not null)
            {
                await error.WriteLineAsync($"{key}: {FormatDetail(value)}");
            }
        }
    }

    private static bool IsPartialError(string code) =>
        code is "PARTIAL_CREATE" or "PARTIAL_INITIALIZATION" or
        "PARTIAL_UPDATE" or "PARTIAL_FINISH";

    private static string? FormatDetail(object value) => value is IEnumerable<string> values
        ? string.Join(", ", values)
        : value.ToString();

    private async Task WriteJsonAsync(object value)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(value, JsonOptions));
    }

    private static string Token(string? value, string fallback)
    {
        return string.IsNullOrWhiteSpace(value)
            ? fallback
            : string.Concat(value.Where(character => !char.IsWhiteSpace(character))).ToLowerInvariant();
    }

    private static string SingleLine(string value)
    {
        return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static string AutomationToken(WorkItemDetail item)
    {
        if (!item.AutomaticExecutionAllowed)
        {
            return "-";
        }

        return item.AgentPolicy is null
            ? "auto"
            : $"auto:{item.AgentPolicy.ToLowerInvariant()}";
    }

    private static string AutomationLabel(WorkItemDetail item) =>
        item.AutomaticExecutionAllowed
            ? AgentLabel(item.AgentPolicy) ?? "Auto"
            : "No";

    // Cheap at-a-glance signal that a worker worktree is recorded (no git shell-out). The
    // dirty/merged detail stays on the single-item surfaces (get, item viewer, workspaces).
    private static string WorktreeMarker(WorkItemOperationalState value) =>
        value.Session is { HasRecordedWorktree: true } ? " [worktree]" : string.Empty;

    private string OperationalStatusToken(WorkItemOperationalState value) => value.OperationalStatus switch
    {
        OperationalStatuses.NeedsAttention => "!attention",
        OperationalStatuses.AgentActive when IsWorkerRunClaim(value) =>
            $"processing:{value.Claim.Agent ?? "agent"}",
        OperationalStatuses.AgentActive => $"claimed:{value.Claim.Agent ?? "agent"}",
        OperationalStatuses.Queued => $"queued:{value.Session?.Agent ?? "agent"}",
        OperationalStatuses.RetryScheduled => value.Session?.Dispatch is { } dispatch
            ? $"retry:{dispatch.NotBefore.ToLocalTime():HH:mm}"
            : "retry",
        OperationalStatuses.HandoffQueued =>
            $"handoff:{value.Session?.Dispatch?.Agent ?? "agent"}",
        OperationalStatuses.PausedSession => $"paused:{value.Session?.Agent ?? "agent"}",
        OperationalStatuses.Completed => "completed",
        OperationalStatuses.HumanEditing => "human",
        OperationalStatuses.AutomationActive => "automation",
        OperationalStatuses.Ready => "ready",
        _ => "-"
    };

    private string OperationalStatusLabel(WorkItemOperationalState value) => value.OperationalStatus switch
    {
        OperationalStatuses.NeedsAttention => "Needs attention",
        OperationalStatuses.AgentActive when IsWorkerRunClaim(value) =>
            $"{AgentLabel(value.Claim.Agent) ?? "Agent"} processing",
        OperationalStatuses.AgentActive => $"{AgentLabel(value.Claim.Agent) ?? "Agent"} claimed",
        OperationalStatuses.Queued => "Resume queued",
        OperationalStatuses.RetryScheduled => value.Session?.Dispatch is { } dispatch
            ? $"Retry {dispatch.NotBefore.ToLocalTime():HH:mm}"
            : "Retry scheduled",
        OperationalStatuses.HandoffQueued => value.Session?.Dispatch is { } dispatch
            ? $"{AgentLabel(dispatch.SessionAgent) ?? "Agent"} → " +
              $"{AgentLabel(dispatch.Agent) ?? "agent"}"
            : "Handoff queued",
        OperationalStatuses.PausedSession => "Session retained",
        OperationalStatuses.Completed => "Completed",
        OperationalStatuses.HumanEditing => "Human editing",
        OperationalStatuses.AutomationActive => "Automation active",
        OperationalStatuses.Ready => "Ready",
        _ => "-"
    };

    private string LeaseToken(WorkItemOperationalState value)
    {
        var label = LeaseDuration(value);
        return label is null ? string.Empty : $" lease:{label}";
    }

    private string LeaseLabel(WorkItemOperationalState value)
    {
        var label = LeaseDuration(value);
        return label switch
        {
            null => "-",
            "expired" => "expired",
            _ => $"{label} left"
        };
    }

    private string? LeaseDuration(WorkItemOperationalState value)
    {
        if (value.Claim.State == ClaimOwnershipState.Unclaimed ||
            value.Claim.ExpiresAt is not { } expiresAt)
            return null;
        var remaining = expiresAt - now();
        if (remaining <= TimeSpan.Zero)
            return "expired";
        var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
        if (minutes < 60)
            return $"{minutes}m";
        var hours = minutes / 60;
        var remainder = minutes % 60;
        return remainder == 0 ? $"{hours}h" : $"{hours}h{remainder}m";
    }

    private static bool IsWorkerRunClaim(WorkItemOperationalState value) =>
        value.OperationalStatus == OperationalStatuses.AgentActive &&
        value.Claim.ClaimantId?.StartsWith(
            "agent:worker:", StringComparison.OrdinalIgnoreCase) == true;

    private static string? AgentLabel(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : $"{char.ToUpperInvariant(value[0])}{value[1..]}";

    private static string ClaimStateLabel(ClaimOwnershipState state) => state switch
    {
        ClaimOwnershipState.OwnedByCurrent => "active on this installation",
        ClaimOwnershipState.HeldByOther => "active on another installation",
        _ => "unclaimed"
    };

    private static string ClaimantTypeLabel(WorkItemClaimSummary claim) =>
        ClaimantKinds.FromStorageValue(claim.ClaimantKind).ToString();

    private static string Truncate(string value, int width) =>
        value.Length <= width
            ? value
            : width <= 1
                ? value[..width]
                : $"{value[..(width - 1)]}…";

    private static IReadOnlyList<string> OperationalActions(
        WorkItemOperationalState value)
    {
        if (value.OperationalStatus is not (
                OperationalStatuses.NeedsAttention or
                OperationalStatuses.Queued or
                OperationalStatuses.RetryScheduled or
                OperationalStatuses.HandoffQueued or
                OperationalStatuses.PausedSession))
            return [];
        // The web dashboard is Local Markdown only; GitHub items carry a URL, so point there instead.
        var reviewAction = value.Item.Url is { } issueUrl
            ? $"Review on GitHub: {issueUrl}"
            : "Open web UI: wrighty web";
        return
        [
            reviewAction,
            $"Edit requirements: wrighty edit {value.Item.Id.Value} --takeover",
            $"Resume headlessly: wrighty worker --item {value.Item.Id.Value} --yes"
        ];
    }

    private object OperationalDto(
        WorkItemOperationalState value,
        Func<WorkItemId, string> formatShort,
        bool includeBody = false,
        WorkspaceStatusResult? workspaceStatus = null)
    {
        // A single nullable view collapses the repeated "unclaimed ? null : …" projections into
        // null-conditional access below (which does not add to cognitive complexity).
        var claimView = value.Claim.State == ClaimOwnershipState.Unclaimed ? null : value.Claim;
        return new
        {
            id = value.Item.Id.Value,
            displayId = formatShort(value.Item.Id),
            value.Item.Title,
            body = includeBody ? value.Item.Body : null,
            value.Item.Url,
            value.Item.Status,
            value.Item.Priority,
            value.Item.Archived,
            fields = includeBody ? value.Item.EffectiveFields : null,
            policy = new
            {
                execution = value.Item.AutomaticExecutionAllowed ? "automatic" : "manual",
                agent = value.Item.AgentPolicy
            },
            operationalStatus = value.OperationalStatus,
            pendingDispatch = PendingDispatchDto(value),
            hasRecordedWorktree = value.Session?.HasRecordedWorktree ?? false,
            claim = new
            {
                state = value.Claim.State.ToString(),
                installationId = claimView?.InstallationId,
                expiresAt = claimView?.ExpiresAt,
                agent = claimView?.Agent,
                claimantKind = claimView?.ClaimantKind,
                claimantId = claimView?.ClaimantId,
                sessionId = claimView?.SessionId,
                workspacePath = claimView?.WorkspacePath,
                workerRun = IsWorkerRunClaim(value),
                leaseRemainingSeconds = LeaseRemainingSeconds(value.Claim),
                value.Claim.TakeoverAvailable
            },
            session = value.Session is null
                ? null
                : new
                {
                    available = value.Session.IsComplete,
                    value.Session.Agent,
                    value.Session.SessionId,
                    value.Session.WorkspacePath,
                    value.Session.Branch,
                    value.Session.ClaimExpiresAt,
                    value.Session.FromCurrentInstallation,
                    resumableHere = value.Session.IsComplete &&
                                    value.Session.FromCurrentInstallation &&
                                    workspaceStatus is not { WorktreeAbsent: true },
                    lastRun = LastRunDto(value.Session),
                    workspaceStatus = workspaceStatus is null
                        ? null
                        : new
                        {
                            available = workspaceStatus.IsAvailable,
                            dirty = workspaceStatus.Status?.Dirty,
                            mergedIntoHead = workspaceStatus.Status?.MergedIntoHead,
                            unavailableReason = workspaceStatus.Unavailable
                        }
                }
        };
    }

    /// <summary>
    /// The last run's projection, or null when no run has been recorded.
    ///
    /// `outcome` is the vendor's process result and stays what it always was. `disposition` is what
    /// Wrighty observed the run achieve, and is the one a consumer deciding anything should read.
    /// `agentReport` is the agent's own account, named so it cannot be mistaken for something
    /// Wrighty established.
    /// </summary>
    private static object? LastRunDto(AgentSessionRecord session)
    {
        if (session.Outcome is not { } outcome) return null;

        var report = session.LastReport;
        return new
        {
            outcome = outcome.ToString().ToLowerInvariant(),
            disposition = report?.ObservedDisposition.ToString().ToLowerInvariant(),
            endedAt = session.EndedAt,
            finalMessage = ApprovedContext.AgentReportParser.WithoutReportBlock(session.FinalMessage),
            failure = session.Failure,
            agentReport = report is { IsObservedOnly: false }
                ? new
                {
                    report.Summary,
                    report.Changes,
                    report.Verification,
                    report.Decisions,
                    report.RequestedInput,
                    report.RemainingWork,
                    report.AgentReportedBody
                }
                : null
        };
    }

    private static object? PendingDispatchDto(WorkItemOperationalState value)
    {
        var dispatch = value.Session?.Dispatch;
        var state = dispatch?.State ?? value.Item.DispatchState;
        return state is null
            ? null
            : new
            {
                state,
                reason = dispatch?.Reason,
                sessionAgent = dispatch?.SessionAgent,
                agent = dispatch?.Agent,
                notBefore = dispatch?.NotBefore,
                attempt = dispatch?.Attempt,
                maxAttempts = dispatch?.MaxAttempts,
                updatedAt = dispatch?.UpdatedAt,
                fromCurrentInstallation = dispatch?.FromCurrentInstallation
            };
    }

    private double? LeaseRemainingSeconds(WorkItemClaimSummary claim) =>
        claim.State == ClaimOwnershipState.Unclaimed || claim.ExpiresAt is not { } expiry
            ? null
            : Math.Max(0, (expiry - now()).TotalSeconds);

    private static object SummaryDto(
        WorkItemSummary item,
        Func<WorkItemId, string> formatShort) => new
        {
            id = item.Id.Value,
            displayId = formatShort(item.Id),
            item.Title,
            item.Url,
            item.Status,
            item.Priority,
            item.Archived,
            policy = new
            {
                execution = item.AutomaticExecutionAllowed ? "automatic" : "manual",
                agent = item.AgentPolicy
            },
            item.DispatchState
        };

    private static object DetailDto(
        WorkItemDetail item,
        Func<WorkItemId, string> formatShort) => new
        {
            id = item.Id.Value,
            displayId = formatShort(item.Id),
            item.Title,
            item.Body,
            item.Url,
            item.Status,
            item.Priority,
            item.Archived,
            policy = new
            {
                execution = item.AutomaticExecutionAllowed ? "automatic" : "manual",
                agent = item.AgentPolicy
            },
            item.DispatchState,
            fields = item.EffectiveFields
        };
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Projects;
using Highbyte.Wrighty.Initialization;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Importing;
using Highbyte.Wrighty.Cli.Skills;

namespace Highbyte.Wrighty.Cli.Output;

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
        "archived", "claimReleased", "causeCode", "retry"
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
                    $"{ActivityToken(value)}{LeaseToken(value)} {SingleLine(item.Title)}");
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
                $"{Truncate(ActivityLabel(value), 24),-24} " +
                $"{Truncate(LeaseLabel(value), 12),-12} " +
                $"{SingleLine(item.Title)}{(item.Archived ? " [archived]" : string.Empty)}");
        }
    }

    public async Task WriteOperationalDetailAsync(
        WorkItemOperationalState value,
        bool json,
        Func<WorkItemId, string> formatShort)
    {
        if (json)
        {
            await WriteJsonAsync(new
            {
                schemaVersion = 1,
                result = OperationalDto(value, formatShort, includeBody: true)
            });
            return;
        }

        var item = value.Item;
        await WriteItemHeaderAsync(item, formatShort);
        await WriteWorkerDetailAsync(value);
        await WriteClaimDetailAsync(value);
        await WriteSessionDetailAsync(value);

        foreach (var field in item.EffectiveFields.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            await output.WriteLineAsync($"{field.Key}: {field.Value}");

        await WriteOperationalActionsAsync(value);
        await output.WriteLineAsync();
        await output.WriteLineAsync("Body");
        await output.WriteAsync(item.Body);
        if (!item.Body.EndsWith('\n'))
            await output.WriteLineAsync();
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
        await output.WriteLineAsync("Worker");
        await output.WriteLineAsync($"  Eligible: {(item.AutomationEligible ? "yes" : "no")}");
        await output.WriteLineAsync(
            $"  Preferred agent: {AgentLabel(item.PreferredAgent) ?? "no item preference"}");
        await output.WriteLineAsync($"  Activity: {ActivityLabel(value)}");
        if (IsWorkerRunClaim(value))
            await output.WriteLineAsync(
                "  Worker run: active claim from a Wrighty worker (not a process-liveness guarantee)");
    }

    private async Task WriteClaimDetailAsync(WorkItemOperationalState value)
    {
        await output.WriteLineAsync();
        await output.WriteLineAsync("Claim");
        await output.WriteLineAsync($"  State: {ClaimStateLabel(value.Claim.State)}");
        if (value.Claim.State != ClaimOwnershipState.Unclaimed)
        {
            await output.WriteLineAsync(
                $"  Claimant: {ClaimantLabel(value.Claim)}");
            if (!string.IsNullOrWhiteSpace(value.Claim.ClaimantId))
                await output.WriteLineAsync($"  Claimant ID: {value.Claim.ClaimantId}");
            if (value.Claim.ExpiresAt is not null)
            {
                await output.WriteLineAsync($"  Expires: {value.Claim.ExpiresAt:O}");
                await output.WriteLineAsync($"  Lease remaining: {LeaseLabel(value)}");
            }
            await output.WriteLineAsync(
                $"  Installation: {(value.Claim.State == ClaimOwnershipState.OwnedByCurrent ? "this installation" : "another installation")}");
        }
    }

    private async Task WriteSessionDetailAsync(WorkItemOperationalState value)
    {
        await output.WriteLineAsync();
        await output.WriteLineAsync("Session");
        await output.WriteLineAsync(
            $"  Resume address complete: {(value.Session is { IsComplete: true } ? "yes" : "no")}");
        if (value.Session is { } session)
        {
            if (!string.IsNullOrWhiteSpace(session.AgentType))
                await output.WriteLineAsync($"  Agent: {AgentLabel(session.AgentType)}");
            if (!string.IsNullOrWhiteSpace(session.SessionId))
                await output.WriteLineAsync($"  Session ID: {session.SessionId}");
            if (!string.IsNullOrWhiteSpace(session.WorkspacePath))
                await output.WriteLineAsync($"  Workspace: {session.WorkspacePath}");
            if (!string.IsNullOrWhiteSpace(session.Branch))
                await output.WriteLineAsync($"  Branch: {session.Branch}");
            await output.WriteLineAsync(
                $"  Resumable here: {(session.IsComplete && session.FromCurrentInstallation ? "yes" : "no")}");
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
        bool json)
    {
        var local = string.Equals(
            result.Config.Backend,
            "local-markdown",
            StringComparison.OrdinalIgnoreCase);
        if (json)
        {
            await WriteInitializationJsonAsync(result, checkOnly, local);
            return;
        }

        await WriteInitializationHumanAsync(result, checkOnly, local);
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
        bool local) => WriteJsonAsync(new
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
                actions = result.Actions
            }
        });

    private async Task WriteInitializationHumanAsync(
        TrackerInitializationResult result,
        bool checkOnly,
        bool local)
    {
        await output.WriteLineAsync($"Backend: {result.Config.Backend}");
        await output.WriteLineAsync($"Backend selection: {result.BackendSelection}");
        if (local)
        {
            await output.WriteLineAsync($"Store: {result.ProjectUrl}");
        }
        else
        {
            await output.WriteLineAsync($"Repository: {result.Config.Repository}");
            await output.WriteLineAsync(
                $"Project: {result.Config.EffectiveProjectOwner}/{result.Config.ProjectNumber} ({result.ProjectTitle})");
        }
        await output.WriteLineAsync($"Configuration: {result.ConfigPath}");
        await output.WriteLineAsync(checkOnly
            ? "configuration and Wrighty resources are valid"
            : result.Changed
                ? "Wrighty initialized"
                : "Wrighty already initialized");
        foreach (var action in result.Actions)
        {
            await output.WriteLineAsync($"- {action}");
        }
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
                    claim.WorkerIdentity,
                    claim.ExpiresAt,
                    claim.ClaimAttemptId,
                    claim.ClaimantId,
                    claim.ClaimToken,
                    claim.AgentType,
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
                    workerState = WorkerDispatchStates.Queued,
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
                agentType = picked.Claim.AgentType,
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

    private static string AutomationToken(WorkItemDetail item) =>
        item.AutomationEligible
            ? item.PreferredAgent is null
                ? "auto"
                : $"auto:{item.PreferredAgent.ToLowerInvariant()}"
            : "-";

    private static string AutomationLabel(WorkItemDetail item) =>
        item.AutomationEligible
            ? AgentLabel(item.PreferredAgent) ?? "Auto"
            : "No";

    private string ActivityToken(WorkItemOperationalState value) => value.Activity switch
    {
        WorkItemActivities.NeedsAttention => "!attention",
        WorkItemActivities.AgentActive when IsWorkerRunClaim(value) =>
            $"processing:{value.Claim.AgentType ?? "agent"}",
        WorkItemActivities.AgentActive => $"claimed:{value.Claim.AgentType ?? "agent"}",
        WorkItemActivities.Queued => $"queued:{value.Session?.AgentType ?? "agent"}",
        WorkItemActivities.PausedSession => $"paused:{value.Session?.AgentType ?? "agent"}",
        WorkItemActivities.HumanEditing => "human",
        WorkItemActivities.AutomationActive => "automation",
        WorkItemActivities.Ready => "ready",
        _ => "-"
    };

    private string ActivityLabel(WorkItemOperationalState value) => value.Activity switch
    {
        WorkItemActivities.NeedsAttention => "Needs attention",
        WorkItemActivities.AgentActive when IsWorkerRunClaim(value) =>
            $"{AgentLabel(value.Claim.AgentType) ?? "Agent"} processing",
        WorkItemActivities.AgentActive => $"{AgentLabel(value.Claim.AgentType) ?? "Agent"} claimed",
        WorkItemActivities.Queued => "Queued to resume",
        WorkItemActivities.PausedSession => "Paused session available",
        WorkItemActivities.HumanEditing => "Human editing",
        WorkItemActivities.AutomationActive => "Automation active",
        WorkItemActivities.Ready => "Ready",
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
        value.Activity == WorkItemActivities.AgentActive &&
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

    private static string ClaimantLabel(WorkItemClaimSummary claim)
    {
        var kind = ClaimantKinds.FromStorageValue(claim.ClaimantKind, claim.AgentType);
        return kind == ClaimantKind.Agent && claim.AgentType is not null
            ? $"Agent ({AgentLabel(claim.AgentType)})"
            : kind.ToString();
    }

    private static string Truncate(string value, int width) =>
        value.Length <= width
            ? value
            : width <= 1
                ? value[..width]
                : $"{value[..(width - 1)]}…";

    private static IReadOnlyList<string> OperationalActions(
        WorkItemOperationalState value)
    {
        if (value.Activity is not (
                WorkItemActivities.NeedsAttention or
                WorkItemActivities.Queued or
                WorkItemActivities.PausedSession))
            return [];
        return
        [
            "Open web UI: wrighty web",
            $"Edit requirements: wrighty edit {value.Item.Id.Value} --takeover",
            $"Resume headlessly: wrighty worker --item {value.Item.Id.Value} --yes"
        ];
    }

    private object OperationalDto(
        WorkItemOperationalState value,
        Func<WorkItemId, string> formatShort,
        bool includeBody = false) => new
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
            automation = new
            {
                eligible = value.Item.AutomationEligible,
                preferredAgent = value.Item.PreferredAgent
            },
            worker = new
            {
                state = value.Item.WorkerState,
                activity = value.Activity
            },
            claim = new
            {
                state = value.Claim.State.ToString(),
                workerIdentity = value.Claim.State == ClaimOwnershipState.Unclaimed
                    ? null
                    : value.Claim.WorkerIdentity,
                expiresAt = value.Claim.State == ClaimOwnershipState.Unclaimed
                    ? null
                    : value.Claim.ExpiresAt,
                agentType = value.Claim.State == ClaimOwnershipState.Unclaimed
                    ? null
                    : value.Claim.AgentType,
                claimantKind = value.Claim.State == ClaimOwnershipState.Unclaimed
                    ? null
                    : value.Claim.ClaimantKind,
                claimantId = value.Claim.State == ClaimOwnershipState.Unclaimed
                    ? null
                    : value.Claim.ClaimantId,
                sessionId = value.Claim.State == ClaimOwnershipState.Unclaimed
                    ? null
                    : value.Claim.SessionId,
                workspacePath = value.Claim.State == ClaimOwnershipState.Unclaimed
                    ? null
                    : value.Claim.WorkspacePath,
                workerRun = IsWorkerRunClaim(value),
                leaseRemainingSeconds = value.Claim.State == ClaimOwnershipState.Unclaimed ||
                                        value.Claim.ExpiresAt is not { } claimExpiry
                    ? (double?)null
                    : Math.Max(0, (claimExpiry - now()).TotalSeconds),
                value.Claim.TakeoverAvailable
            },
            session = value.Session is null
                ? null
                : new
                {
                    available = value.Session.IsComplete,
                    value.Session.AgentType,
                    value.Session.SessionId,
                    value.Session.WorkspacePath,
                    value.Session.Branch,
                    value.Session.ClaimExpiresAt,
                    value.Session.FromCurrentInstallation,
                    resumableHere = value.Session.IsComplete &&
                                    value.Session.FromCurrentInstallation
                }
        };

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
            item.AutomationEligible,
            item.PreferredAgent,
            item.WorkerState
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
            item.AutomationEligible,
            item.PreferredAgent,
            item.WorkerState,
            fields = item.EffectiveFields
        };
}

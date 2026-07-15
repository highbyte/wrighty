using System.Text.Json;
using System.Text.Json.Serialization;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Projects;
using Highbyte.Wrighty.Initialization;
using Highbyte.Wrighty.Cli.Skills;

namespace Highbyte.Wrighty.Cli.Output;

public sealed class OutputWriter(TextWriter output, TextWriter error)
{
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
                    claim.AgentType,
                    claim.SessionId
                }
            });
            return;
        }

        var verb = claim.Outcome == ClaimOutcome.AlreadyOwned ? "already own" : "claimed";
        await output.WriteLineAsync(
            $"{verb} {displayId} as worker {claim.WorkerIdentity} until {claim.ExpiresAt:O}");
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

    public Task WritePickedAsync(
        WorkItemSummary item,
        bool json,
        Func<WorkItemId, string> formatShort)
    {
        return WriteItemsAsync([item], compact: !json, json, formatShort);
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
                    item.Archived
                }
            });
            return;
        }

        await output.WriteLineAsync($"{displayId} {SingleLine(item.Title)}");
        await output.WriteLineAsync($"Status: {Token(item.Status, "-")}");
        await output.WriteLineAsync($"Priority: {Token(item.Priority, "-")}");
        await output.WriteLineAsync($"Archived: {(item.Archived ? "yes" : "no")}");
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
            item.Archived
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
            item.Archived
        };
}

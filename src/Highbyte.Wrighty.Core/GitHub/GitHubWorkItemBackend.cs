using System.Text.Json;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Projects;

namespace Highbyte.Wrighty.GitHub;

public sealed class GitHubWorkItemBackend(
    GhApi api,
    IProjectClient projects,
    GitHubWorkItemAddressResolver resolver,
    IWorkItemMutationGuard mutationGuard,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IWorkItemBackend
{
    private readonly Func<TimeSpan, CancellationToken, Task> retryDelay =
        delay ?? Task.Delay;

    public async Task<WorkItemDetail?> GetAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        var address = resolver.Decode(id, config);
        var projectItem = (await projects.ListAsync(
                config,
                null,
                null,
                ArchiveScope.All,
                cancellationToken))
            .SingleOrDefault(item => item.Number == address.IssueNumber);
        if (projectItem is null)
        {
            return null;
        }

        try
        {
            using var issue = await api.GetAsync(
                config.GitHubHost,
                $"repos/{address.Owner}/{address.Repository}/issues/{address.IssueNumber}",
                cancellationToken);
            var root = issue.RootElement;
            if (root.TryGetProperty("pull_request", out _))
            {
                return null;
            }

            return new WorkItemDetail(
                id,
                root.GetProperty("title").GetString() ?? projectItem.Title,
                root.GetProperty("body").GetString() ?? string.Empty,
                root.GetProperty("html_url").GetString() ?? projectItem.Url,
                projectItem.Status,
                projectItem.Priority,
                projectItem.Summary.Archived);
        }
        catch (TrackerException exception) when (
            exception.Code == "GH_API_ERROR" &&
            (exception.Message.Contains("404", StringComparison.OrdinalIgnoreCase) ||
             exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
    }

    public Task<CreateWorkItemResult> CreateAsync(
        TrackerConfig config,
        CreateWorkItemRequest request,
        CancellationToken cancellationToken) =>
        CreateAsync(
            config,
            new CreateWorkItemOperation(
                request with { Status = request.Status ?? config.DefaultPickFrom },
                config.ShouldArchiveStatus(request.Status ?? config.DefaultPickFrom),
                CreationAttempt.NormalizeOrCreate(null)),
            cancellationToken);

    public async Task<CreateWorkItemResult> CreateAsync(
        TrackerConfig config,
        CreateWorkItemOperation operation,
        CancellationToken cancellationToken)
    {
        var request = operation.Request;
        ValidateRequest(request);
        var status = request.Status ?? config.DefaultPickFrom;
        if (string.IsNullOrWhiteSpace(status))
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "A non-empty status is required when creating a work item.",
                2);
        }

        var attemptId = CreationAttempt.NormalizeOrCreate(
            string.IsNullOrWhiteSpace(operation.CreationAttemptId) ? null : operation.CreationAttemptId);
        var resolvedRequest = request with { Status = status };
        var intentHash = CreationAttempt.ComputeIntentHash(resolvedRequest, operation.ArchiveAfterCreate);
        await projects.ValidateCreateFieldsAsync(
            config,
            status,
            request.Priority,
            cancellationToken);

        var projectMatches = await projects.FindByCreationAttemptIdAsync(
            config,
            attemptId,
            cancellationToken);
        if (projectMatches.Count > 1)
        {
            throw DuplicateAttempt(attemptId, projectMatches.Select(item => item.Summary.Id.Value));
        }

        if (projectMatches.Count == 1)
        {
            var matched = projectMatches[0];
            using var issue = await api.GetAsync(
                config.GitHubHost,
                $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/{matched.Number}",
                cancellationToken);
            var matchedAllocation = ParseIssue(issue.RootElement);
            var matchedLabelName = TemporaryLabelName(attemptId);
            if (matchedAllocation.Labels.Contains(matchedLabelName, StringComparer.OrdinalIgnoreCase))
            {
                return await ResumeProjectMatchAsync(
                    config,
                    operation,
                    resolvedRequest,
                    matched,
                    matchedAllocation,
                    attemptId,
                    intentHash,
                    cancellationToken);
            }

            var resumedDetail = new WorkItemDetail(
                matched.Summary.Id,
                issue.RootElement.GetProperty("title").GetString() ?? matched.Title,
                issue.RootElement.GetProperty("body").GetString() ?? string.Empty,
                matchedAllocation.Url ?? matched.Url,
                matched.Status,
                matched.Priority,
                matched.Summary.Archived);
            return new CreateWorkItemResult(
                resumedDetail.Id,
                resumedDetail.Url,
                resumedDetail,
                attemptId,
                CreateDisposition.Resumed,
                []);
        }

        await EnsureLabelPermissionAsync(config, cancellationToken);
        var labelName = TemporaryLabelName(attemptId);
        await EnsureTemporaryLabelAsync(config, labelName, intentHash, cancellationToken);

        var labelledIssues = await FindIssuesByLabelAsync(config, labelName, cancellationToken);
        if (labelledIssues.Count > 1)
        {
            throw DuplicateAttempt(attemptId, labelledIssues.Select(issue =>
                resolver.FromIssueNumber(config, issue.Number).Value));
        }

        IssueAllocation allocation;
        var disposition = CreateDisposition.Resumed;
        if (labelledIssues.Count == 1)
        {
            allocation = labelledIssues[0];
        }
        else
        {
            try
            {
                using var issue = await api.SendJsonAsync(
                    config.GitHubHost,
                    "POST",
                    $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues",
                    new { title = request.Title, body = request.Body, labels = new[] { labelName } },
                    cancellationToken);
                allocation = ParseIssue(issue.RootElement);
                disposition = CreateDisposition.Created;
            }
            catch (Exception exception)
            {
                var recoveryToken = cancellationToken.IsCancellationRequested
                    ? CancellationToken.None
                    : cancellationToken;
                labelledIssues = await FindIssuesWithRetryAsync(config, labelName, recoveryToken);
                if (labelledIssues.Count == 0)
                {
                    throw new TrackerException(
                        "CREATION_OUTCOME_UNKNOWN",
                        "GitHub issue creation had an ambiguous outcome and no labelled issue became visible.",
                        10,
                        new Dictionary<string, object?>
                        {
                            ["creationAttemptId"] = attemptId,
                            ["failedStage"] = "issue-recovery"
                        },
                        exception);
                }

                if (labelledIssues.Count > 1)
                {
                    throw DuplicateAttempt(attemptId, labelledIssues.Select(value =>
                        resolver.FromIssueNumber(config, value.Number).Value));
                }

                allocation = labelledIssues[0];
            }
        }

        var id = resolver.FromIssueNumber(config, allocation.Number);
        var url = allocation.Url;
        var reconciled = new List<string>();

        if (!allocation.Labels.Contains(labelName, StringComparer.OrdinalIgnoreCase))
        {
            throw PartialCreate(id, url, attemptId, "temporary-label-verify",
                new TrackerException(
                    "GITHUB_PERMISSION_REQUIRED",
                    "GitHub did not attach the temporary creation label atomically.",
                    3));
        }

        var existingProjectItems = (await projects.ListAsync(
                config,
                null,
                null,
                ArchiveScope.All,
                cancellationToken))
            .Where(item => item.Number == allocation.Number)
            .ToArray();
        if (existingProjectItems.Length > 1)
        {
            throw DuplicateAttempt(
                attemptId,
                existingProjectItems.Select(item => item.Summary.Id.Value));
        }

        string projectItemId;
        if (existingProjectItems.Length == 1)
        {
            projectItemId = existingProjectItems[0].ProjectItemId;
        }
        else
        {
            try
            {
                projectItemId = await projects.AddIssueAsync(config, allocation.NodeId, cancellationToken);
                reconciled.Add("project-add");
            }
            catch (Exception exception)
            {
                throw PartialCreate(id, url, attemptId, "project-add", exception);
            }
        }

        var operationalItem = new GitHubProjectItem(
            resolver.Decode(id, config),
            new WorkItemSummary(id, request.Title, url, status, request.Priority),
            allocation.NodeId,
            projectItemId);
        try
        {
            await projects.UpdateCreationAttemptIdAsync(config, operationalItem, attemptId, cancellationToken);
            reconciled.Add("creation-id-set");
        }
        catch (Exception exception)
        {
            throw PartialCreate(id, url, attemptId, "creation-id-set", exception);
        }

        try
        {
            await projects.UpdateStatusAsync(config, operationalItem, status, cancellationToken);
            reconciled.Add("status-set");
        }
        catch (Exception exception)
        {
            throw PartialCreate(id, url, attemptId, "status-set", exception);
        }

        if (request.Priority is not null)
        {
            try
            {
                await projects.UpdatePriorityAsync(
                    config,
                    operationalItem,
                    request.Priority,
                    cancellationToken);
                reconciled.Add("priority-set");
            }
            catch (Exception exception)
            {
                throw PartialCreate(id, url, attemptId, "priority-set", exception);
            }
        }

        if (operation.ArchiveAfterCreate)
        {
            try
            {
                await projects.ArchiveAsync(config, operationalItem, cancellationToken);
                reconciled.Add("archive");
            }
            catch (Exception exception)
            {
                throw PartialCreate(id, url, attemptId, "archive", exception);
            }
        }

        WorkItemDetail detail;
        try
        {
            WorkItemDetail? found = null;
            for (var attempt = 0; attempt < 5 && found is null; attempt++)
            {
                found = await GetAsync(config, id, cancellationToken);
                if (found is null && attempt < 4)
                {
                    await retryDelay(
                        TimeSpan.FromMilliseconds(250 * (1 << attempt)),
                        cancellationToken);
                }
            }

            if (found is null)
            {
                throw new TrackerException(
                    "WORK_ITEM_NOT_FOUND",
                    "The created issue was not found in the configured Project.",
                    5);
            }

            detail = found;
        }
        catch (Exception exception)
        {
            throw PartialCreate(id, url, attemptId, "final-read", exception);
        }

        try
        {
            await CleanupTemporaryLabelAsync(
                config,
                allocation.Number,
                labelName,
                intentHash,
                cancellationToken);
        }
        catch (Exception exception)
        {
            throw PartialCreate(id, url, attemptId, "temporary-label-delete", exception);
        }

        return new CreateWorkItemResult(
            id,
            url,
            detail,
            attemptId,
            disposition,
            reconciled);
    }

    private async Task<CreateWorkItemResult> ResumeProjectMatchAsync(
        TrackerConfig config,
        CreateWorkItemOperation operation,
        CreateWorkItemRequest request,
        GitHubProjectItem item,
        IssueAllocation allocation,
        string attemptId,
        string intentHash,
        CancellationToken cancellationToken)
    {
        var reconciled = new List<string>();
        if (string.IsNullOrWhiteSpace(item.Status))
        {
            try
            {
                await projects.UpdateStatusAsync(config, item, request.Status!, cancellationToken);
                reconciled.Add("status-set");
            }
            catch (Exception exception)
            {
                throw PartialCreate(item.Summary.Id, allocation.Url, attemptId, "status-set", exception);
            }
        }

        if (request.Priority is not null && string.IsNullOrWhiteSpace(item.Priority))
        {
            try
            {
                await projects.UpdatePriorityAsync(config, item, request.Priority, cancellationToken);
                reconciled.Add("priority-set");
            }
            catch (Exception exception)
            {
                throw PartialCreate(item.Summary.Id, allocation.Url, attemptId, "priority-set", exception);
            }
        }

        if (operation.ArchiveAfterCreate && !item.Summary.Archived)
        {
            try
            {
                await projects.ArchiveAsync(config, item, cancellationToken);
                reconciled.Add("archive");
            }
            catch (Exception exception)
            {
                throw PartialCreate(item.Summary.Id, allocation.Url, attemptId, "archive", exception);
            }
        }

        WorkItemDetail detail;
        try
        {
            detail = await GetAsync(config, item.Summary.Id, cancellationToken)
                ?? throw new TrackerException(
                    "WORK_ITEM_NOT_FOUND",
                    "The resumed issue was not found in the configured Project.",
                    5);
        }
        catch (Exception exception)
        {
            throw PartialCreate(item.Summary.Id, allocation.Url, attemptId, "final-read", exception);
        }

        try
        {
            await CleanupTemporaryLabelAsync(
                config,
                allocation.Number,
                TemporaryLabelName(attemptId),
                intentHash,
                cancellationToken);
        }
        catch (Exception exception)
        {
            throw PartialCreate(
                item.Summary.Id,
                allocation.Url,
                attemptId,
                "temporary-label-delete",
                exception);
        }

        return new CreateWorkItemResult(
            item.Summary.Id,
            allocation.Url,
            detail,
            attemptId,
            CreateDisposition.Resumed,
            reconciled);
    }

    private async Task CleanupTemporaryLabelAsync(
        TrackerConfig config,
        int issueNumber,
        string labelName,
        string intentHash,
        CancellationToken cancellationToken)
    {
        using (var label = await api.GetAsync(
                   config.GitHubHost,
                   $"repos/{config.RepositoryOwner}/{config.RepositoryName}/labels/{Uri.EscapeDataString(labelName)}",
                   cancellationToken))
        {
            var description = label.RootElement.TryGetProperty("description", out var value)
                ? value.GetString()
                : null;
            if (!string.Equals(description, TemporaryLabelDescription(intentHash), StringComparison.Ordinal))
            {
                throw new TrackerException(
                    "CREATION_ATTEMPT_CONFLICT",
                    $"Temporary label '{labelName}' no longer contains tracker-owned metadata.",
                    9);
            }
        }

        await api.DeleteAsync(
            config.GitHubHost,
            $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/{issueNumber}/labels/{Uri.EscapeDataString(labelName)}",
            cancellationToken);
        var remaining = await FindIssuesByLabelAsync(config, labelName, cancellationToken);
        if (remaining.Count > 0)
        {
            throw DuplicateAttempt(
                labelName["sit-create-".Length..],
                remaining.Select(issue => resolver.FromIssueNumber(config, issue.Number).Value));
        }

        await api.DeleteAsync(
            config.GitHubHost,
            $"repos/{config.RepositoryOwner}/{config.RepositoryName}/labels/{Uri.EscapeDataString(labelName)}",
            cancellationToken);
    }

    private async Task EnsureLabelPermissionAsync(
        TrackerConfig config,
        CancellationToken cancellationToken)
    {
        using var repository = await api.GetAsync(
            config.GitHubHost,
            $"repos/{config.RepositoryOwner}/{config.RepositoryName}",
            cancellationToken);
        if (!repository.RootElement.TryGetProperty("permissions", out var permissions) ||
            !permissions.TryGetProperty("push", out var push) ||
            !push.GetBoolean())
        {
            throw new TrackerException(
                "GITHUB_PERMISSION_REQUIRED",
                "Retry-safe creation requires repository permission to apply labels during issue creation.",
                3,
                new Dictionary<string, object?> { ["requiredPermission"] = "push" });
        }
    }

    private async Task EnsureTemporaryLabelAsync(
        TrackerConfig config,
        string labelName,
        string intentHash,
        CancellationToken cancellationToken)
    {
        var description = TemporaryLabelDescription(intentHash);
        try
        {
            using var _ = await api.SendJsonAsync(
                config.GitHubHost,
                "POST",
                $"repos/{config.RepositoryOwner}/{config.RepositoryName}/labels",
                new { name = labelName, color = "BFDADC", description },
                cancellationToken);
            return;
        }
        catch (TrackerException exception) when (exception.Code == "GH_API_ERROR")
        {
            using var existing = await api.GetAsync(
                config.GitHubHost,
                $"repos/{config.RepositoryOwner}/{config.RepositoryName}/labels/{Uri.EscapeDataString(labelName)}",
                cancellationToken);
            var existingDescription = existing.RootElement.TryGetProperty("description", out var value)
                ? value.GetString()
                : null;
            if (!string.Equals(existingDescription, description, StringComparison.Ordinal))
            {
                throw new TrackerException(
                    "CREATION_ATTEMPT_CONFLICT",
                    $"Temporary label '{labelName}' exists with incompatible creation metadata.",
                    9,
                    new Dictionary<string, object?> { ["label"] = labelName },
                    exception);
            }
        }
    }

    private async Task<IReadOnlyList<IssueAllocation>> FindIssuesByLabelAsync(
        TrackerConfig config,
        string labelName,
        CancellationToken cancellationToken)
    {
        using var document = await api.GetPaginatedAsync(
            config.GitHubHost,
            $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues?state=all&labels={Uri.EscapeDataString(labelName)}&per_page=100",
            cancellationToken);
        var matches = new List<IssueAllocation>();
        foreach (var page in document.RootElement.EnumerateArray())
        {
            foreach (var issue in page.EnumerateArray())
            {
                if (!issue.TryGetProperty("pull_request", out _))
                {
                    matches.Add(ParseIssue(issue));
                }
            }
        }

        return matches;
    }

    private async Task<IReadOnlyList<IssueAllocation>> FindIssuesWithRetryAsync(
        TrackerConfig config,
        string labelName,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<IssueAllocation> matches = [];
        for (var attempt = 0; attempt < 5; attempt++)
        {
            matches = await FindIssuesByLabelAsync(config, labelName, cancellationToken);
            if (matches.Count > 0)
            {
                return matches;
            }

            if (attempt < 4)
            {
                await retryDelay(TimeSpan.FromMilliseconds(200 * (1 << attempt)), cancellationToken);
            }
        }

        return matches;
    }

    private static IssueAllocation ParseIssue(JsonElement root)
    {
        var labels = root.TryGetProperty("labels", out var labelElements)
            ? labelElements.EnumerateArray()
                .Select(label => label.ValueKind == JsonValueKind.String
                    ? label.GetString()
                    : label.TryGetProperty("name", out var name) ? name.GetString() : null)
                .Where(name => name is not null)
                .Cast<string>()
                .ToArray()
            : [];
        return new IssueAllocation(
            root.GetProperty("number").GetInt32(),
            root.GetProperty("node_id").GetString()!,
            root.GetProperty("html_url").GetString(),
            labels);
    }

    private static string TemporaryLabelName(string attemptId) => $"sit-create-{attemptId}";

    private static string TemporaryLabelDescription(string intentHash) =>
        $"SIT create sha256:{intentHash}";

    private static TrackerException DuplicateAttempt(
        string attemptId,
        IEnumerable<string> ids) => new(
        "CREATION_ATTEMPT_DUPLICATE",
        $"Creation attempt '{attemptId}' identifies multiple work items.",
        9,
        new Dictionary<string, object?>
        {
            ["creationAttemptId"] = attemptId,
            ["ids"] = ids.ToArray()
        });

    public async Task<UpdateWorkItemResult> UpdateAsync(
        TrackerConfig config,
        WorkItemId id,
        WorkItemPatch patch,
        CancellationToken cancellationToken)
    {
        WorkItemPatchValidator.Validate(patch);
        var address = resolver.Decode(id, config);
        var projectItem = (await projects.ListAsync(config, null, null, cancellationToken))
            .SingleOrDefault(item => item.Number == address.IssueNumber)
            ?? throw new TrackerException(
                "PROJECT_ITEM_NOT_FOUND",
                $"Work item '{id}' was not found in the configured Project.",
                5,
                new Dictionary<string, object?> { ["id"] = id.Value });

        using var issue = await api.GetAsync(
            config.GitHubHost,
            $"repos/{address.Owner}/{address.Repository}/issues/{address.IssueNumber}",
            cancellationToken);
        var root = issue.RootElement;
        if (root.TryGetProperty("pull_request", out _))
        {
            throw new TrackerException(
                "WORK_ITEM_NOT_FOUND",
                $"Work item '{id}' is not a real issue tracked by this backend.",
                5);
        }

        var current = new WorkItemDetail(
            id,
            root.GetProperty("title").GetString() ?? projectItem.Title,
            root.GetProperty("body").GetString() ?? string.Empty,
            root.GetProperty("html_url").GetString() ?? projectItem.Url,
            projectItem.Status,
            projectItem.Priority);
        var changes = ChangedFields(current, patch);
        var changedStatus = changes.Contains("status");
        var changedPriority = changes.Contains("priority");

        if (changedStatus || changedPriority)
        {
            await projects.ValidateUpdateFieldsAsync(
                config,
                changedStatus ? patch.Status.Value : null,
                changedPriority && patch.Priority.Value is not null ? patch.Priority.Value : null,
                changedPriority && patch.Priority.Value is null,
                cancellationToken);
        }

        if (changes.Count == 0)
        {
            return new UpdateWorkItemResult(current, false, []);
        }

        var applied = new List<string>();
        try
        {
            var issueFields = changes
                .Where(field => field is "title" or "body")
                .ToArray();
            if (issueFields.Length > 0)
            {
                await mutationGuard.EnsureOwnedAsync(config, id, cancellationToken);
                var body = new Dictionary<string, object?>();
                if (issueFields.Contains("title"))
                {
                    body["title"] = patch.Title.Value;
                }

                if (issueFields.Contains("body"))
                {
                    body["body"] = patch.Body.Value;
                }

                using var _ = await api.SendJsonAsync(
                    config.GitHubHost,
                    "PATCH",
                    $"repos/{address.Owner}/{address.Repository}/issues/{address.IssueNumber}",
                    body,
                    cancellationToken);
                applied.AddRange(issueFields);
            }

            if (changedPriority)
            {
                await mutationGuard.EnsureOwnedAsync(config, id, cancellationToken);
                if (patch.Priority.Value is null)
                {
                    await projects.ClearPriorityAsync(config, projectItem, cancellationToken);
                }
                else
                {
                    await projects.UpdatePriorityAsync(
                        config,
                        projectItem,
                        patch.Priority.Value,
                        cancellationToken);
                }

                applied.Add("priority");
            }

            if (changedStatus)
            {
                await mutationGuard.EnsureOwnedAsync(config, id, cancellationToken);
                await projects.UpdateStatusAsync(
                    config,
                    projectItem,
                    patch.Status.Value!,
                    cancellationToken);
                applied.Add("status");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || applied.Count > 0)
        {
            if (applied.Count == 0)
            {
                throw;
            }

            var stage = NextStage(changes, applied, patch);
            throw PartialUpdate(id, current.Url, stage, changes, applied, exception);
        }

        try
        {
            var updated = await GetAsync(config, id, cancellationToken)
                ?? throw new TrackerException(
                    "WORK_ITEM_NOT_FOUND",
                    "The updated issue was not found in the configured Project.",
                    5);
            return new UpdateWorkItemResult(updated, true, applied);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || applied.Count > 0)
        {
            throw PartialUpdate(id, current.Url, "final-read", changes, applied, exception);
        }
    }

    private static void ValidateRequest(CreateWorkItemRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title) ||
            request.Title.Length > 256 ||
            request.Title.Contains('\r') ||
            request.Title.Contains('\n'))
        {
            throw new TrackerException(
                "ARGUMENT_INVALID",
                "title must be a non-empty single line of at most 256 characters.",
                2);
        }

        if (request.Status is not null && string.IsNullOrWhiteSpace(request.Status))
        {
            throw new TrackerException("ARGUMENT_INVALID", "status cannot be empty.", 2);
        }

        if (request.Priority is not null && string.IsNullOrWhiteSpace(request.Priority))
        {
            throw new TrackerException("ARGUMENT_INVALID", "priority cannot be empty.", 2);
        }
    }

    private static IReadOnlyList<string> ChangedFields(
        WorkItemDetail current,
        WorkItemPatch patch)
    {
        var changes = new List<string>();
        if (patch.Title.IsSpecified &&
            !string.Equals(current.Title, patch.Title.Value, StringComparison.Ordinal))
        {
            changes.Add("title");
        }

        if (patch.Body.IsSpecified &&
            !string.Equals(current.Body, patch.Body.Value, StringComparison.Ordinal))
        {
            changes.Add("body");
        }

        if (patch.Priority.IsSpecified &&
            !string.Equals(current.Priority, patch.Priority.Value, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add("priority");
        }

        if (patch.Status.IsSpecified &&
            !string.Equals(current.Status, patch.Status.Value, StringComparison.OrdinalIgnoreCase))
        {
            changes.Add("status");
        }

        return changes;
    }

    private static string NextStage(
        IReadOnlyList<string> changes,
        IReadOnlyCollection<string> applied,
        WorkItemPatch patch)
    {
        var next = changes.First(field => !applied.Contains(field));
        return next switch
        {
            "title" or "body" => "issue-update",
            "priority" when patch.Priority.Value is null => "priority-clear",
            "priority" => "priority-set",
            "status" => "status-set",
            _ => "update"
        };
    }

    private static TrackerException PartialUpdate(
        WorkItemId id,
        string? url,
        string stage,
        IReadOnlyList<string> requested,
        IReadOnlyCollection<string> applied,
        Exception exception)
    {
        var causeCode = exception switch
        {
            TrackerException trackerException => trackerException.Code,
            OperationCanceledException => "OPERATION_CANCELED",
            _ => "UNEXPECTED_ERROR"
        };
        return new TrackerException(
            "PARTIAL_UPDATE",
            "The work item was updated only partially.",
            10,
            new Dictionary<string, object?>
            {
                ["id"] = id.Value,
                ["displayId"] = $"#{id.Value[(id.Value.LastIndexOf('#') + 1)..]}",
                ["url"] = url,
                ["failedStage"] = stage,
                ["appliedFields"] = applied.ToArray(),
                ["pendingFields"] = requested.Where(field => !applied.Contains(field)).ToArray(),
                ["causeCode"] = causeCode
            },
            exception);
    }

    private static TrackerException PartialCreate(
        WorkItemId id,
        string? url,
        string creationAttemptId,
        string stage,
        Exception exception)
    {
        var displayId = $"#{id.Value[(id.Value.LastIndexOf('#') + 1)..]}";
        return new TrackerException(
            "PARTIAL_CREATE",
            "The issue was created but could not be fully added to the tracker.",
            10,
            new Dictionary<string, object?>
            {
                ["id"] = id.Value,
                ["displayId"] = displayId,
                ["url"] = url,
                ["creationAttemptId"] = creationAttemptId,
                ["failedStage"] = stage
            },
            exception);
    }

    private sealed record IssueAllocation(
        int Number,
        string NodeId,
        string? Url,
        IReadOnlyList<string> Labels);
}

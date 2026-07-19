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
                projectItem.Summary.Archived,
                Labels: root.GetProperty("labels").EnumerateArray()
                    .Select(label => label.GetProperty("name").GetString())
                    .Where(name => name is not null)
                    .Cast<string>()
                    .ToArray(),
                AutomationEligible: root.GetProperty("labels").EnumerateArray()
                    .Any(label => string.Equals(label.GetProperty("name").GetString(), "wrighty:auto",
                        StringComparison.OrdinalIgnoreCase)),
                PreferredAgent: PreferredAgent(root),
                WorkerState: WorkerState(root));
        }
        catch (TrackerException exception) when (
            exception.Code == "GH_API_ERROR" &&
            (exception.Message.Contains("404", StringComparison.OrdinalIgnoreCase) ||
             exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
    }

    private static string? PreferredAgent(JsonElement issue)
    {
        const string prefix = "wrighty:agent=";
        var label = issue.GetProperty("labels").EnumerateArray()
            .Select(value => value.GetProperty("name").GetString())
            .FirstOrDefault(value => value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true);
        return label is null ? null : label[prefix.Length..];
    }

    private static string? WorkerState(JsonElement issue)
    {
        const string prefix = "wrighty:worker-state=";
        var label = issue.GetProperty("labels").EnumerateArray()
            .Select(value => value.GetProperty("name").GetString())
            .FirstOrDefault(value => value?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true);
        var state = label is null ? null : label[prefix.Length..].ToLowerInvariant();
        WorkerDispatchStates.Validate(state);
        return state;
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
        var context = await PrepareCreateAsync(config, operation, cancellationToken);
        var resumed = await TryResumeProjectMatchAsync(
            config, operation, context, cancellationToken);
        if (resumed is not null)
        {
            return resumed;
        }

        await EnsureLabelPermissionAsync(config, cancellationToken);
        await EnsureTemporaryLabelAsync(
            config, context.LabelName, context.IntentHash, cancellationToken);
        var allocation = await AllocateIssueAsync(config, context, cancellationToken);
        var id = resolver.FromIssueNumber(config, allocation.Issue.Number);
        var url = allocation.Issue.Url;
        var reconciled = new List<string>();
        EnsureTemporaryLabelAttached(context, allocation.Issue, id);
        var operationalItem = await EnsureProjectItemAsync(
            config, context, allocation.Issue, id, reconciled, cancellationToken);
        await ReconcileCreatedProjectItemAsync(
            config, operation, context, operationalItem, id, url, reconciled, cancellationToken);
        var detail = await ReadCreatedDetailAsync(
            config, id, url, context.AttemptId, cancellationToken);
        await CleanupCreatedLabelAsync(
            config, allocation.Issue.Number, context, id, url, cancellationToken);
        if (operation.Request.AutomationEligible || operation.Request.PreferredAgent is not null)
        {
            await ApplyWorkerLabelsAsync(config, id, operation.Request.AutomationEligible,
                operation.Request.PreferredAgent, cancellationToken);
            detail = await GetAsync(config, id, cancellationToken) ?? detail;
        }

        return new CreateWorkItemResult(
            id,
            url,
            detail,
            context.AttemptId,
            allocation.Disposition,
            reconciled);
    }

    private async Task<CreateContext> PrepareCreateAsync(
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
            string.IsNullOrWhiteSpace(operation.CreationAttemptId)
                ? null
                : operation.CreationAttemptId);
        var resolvedRequest = request with { Status = status };
        var intentHash = CreationAttempt.ComputeIntentHash(
            resolvedRequest, operation.ArchiveAfterCreate);
        await projects.ValidateCreateFieldsAsync(
            config, status, request.Priority, cancellationToken);
        return new CreateContext(
            resolvedRequest,
            status,
            attemptId,
            intentHash,
            TemporaryLabelName(attemptId));
    }

    private async Task<CreateWorkItemResult?> TryResumeProjectMatchAsync(
        TrackerConfig config,
        CreateWorkItemOperation operation,
        CreateContext context,
        CancellationToken cancellationToken)
    {
        var matches = await projects.FindByCreationAttemptIdAsync(
            config, context.AttemptId, cancellationToken);
        if (matches.Count > 1)
        {
            throw DuplicateAttempt(
                context.AttemptId, matches.Select(item => item.Summary.Id.Value));
        }

        if (matches.Count == 0)
        {
            return null;
        }

        var matched = matches[0];
        using var issue = await api.GetAsync(
            config.GitHubHost,
            $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues/{matched.Number}",
            cancellationToken);
        var allocation = ParseIssue(issue.RootElement);
        if (allocation.Labels.Contains(context.LabelName, StringComparer.OrdinalIgnoreCase))
        {
            return await ResumeProjectMatchAsync(
                config,
                operation,
                context.Request,
                matched,
                allocation,
                context.AttemptId,
                context.IntentHash,
                cancellationToken);
        }

        var detail = new WorkItemDetail(
            matched.Summary.Id,
            issue.RootElement.GetProperty("title").GetString() ?? matched.Title,
            issue.RootElement.GetProperty("body").GetString() ?? string.Empty,
            allocation.Url ?? matched.Url,
            matched.Status,
            matched.Priority,
            matched.Summary.Archived);
        return new CreateWorkItemResult(
            detail.Id,
            detail.Url,
            detail,
            context.AttemptId,
            CreateDisposition.Resumed,
            []);
    }

    private async Task<AllocationResult> AllocateIssueAsync(
        TrackerConfig config,
        CreateContext context,
        CancellationToken cancellationToken)
    {
        var matches = await FindIssuesByLabelAsync(
            config, context.LabelName, cancellationToken);
        ThrowIfDuplicateLabelMatches(config, context.AttemptId, matches);
        if (matches.Count == 1)
        {
            return new AllocationResult(matches[0], CreateDisposition.Resumed);
        }

        try
        {
            using var issue = await api.SendJsonAsync(
                config.GitHubHost,
                "POST",
                $"repos/{config.RepositoryOwner}/{config.RepositoryName}/issues",
                new
                {
                    title = context.Request.Title,
                    body = context.Request.Body,
                    labels = new[] { context.LabelName }
                },
                cancellationToken);
            return new AllocationResult(ParseIssue(issue.RootElement), CreateDisposition.Created);
        }
        catch (Exception exception)
        {
            return await RecoverIssueAllocationAsync(
                config, context, exception, cancellationToken);
        }
    }

    private async Task<AllocationResult> RecoverIssueAllocationAsync(
        TrackerConfig config,
        CreateContext context,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var recoveryToken = cancellationToken.IsCancellationRequested
            ? CancellationToken.None
            : cancellationToken;
        var matches = await FindIssuesWithRetryAsync(
            config, context.LabelName, recoveryToken);
        if (matches.Count == 0)
        {
            throw new TrackerException(
                "CREATION_OUTCOME_UNKNOWN",
                "GitHub issue creation had an ambiguous outcome and no labelled issue became visible.",
                10,
                new Dictionary<string, object?>
                {
                    ["creationAttemptId"] = context.AttemptId,
                    ["failedStage"] = "issue-recovery"
                },
                exception);
        }

        ThrowIfDuplicateLabelMatches(config, context.AttemptId, matches);
        return new AllocationResult(matches[0], CreateDisposition.Resumed);
    }

    private void ThrowIfDuplicateLabelMatches(
        TrackerConfig config,
        string attemptId,
        IReadOnlyCollection<IssueAllocation> matches)
    {
        if (matches.Count > 1)
        {
            throw DuplicateAttempt(attemptId, matches.Select(issue =>
                resolver.FromIssueNumber(config, issue.Number).Value));
        }
    }

    private static void EnsureTemporaryLabelAttached(
        CreateContext context,
        IssueAllocation allocation,
        WorkItemId id)
    {
        if (!allocation.Labels.Contains(context.LabelName, StringComparer.OrdinalIgnoreCase))
        {
            throw PartialCreate(
                id,
                allocation.Url,
                context.AttemptId,
                "temporary-label-verify",
                new TrackerException(
                    "GITHUB_PERMISSION_REQUIRED",
                    "GitHub did not attach the temporary creation label atomically.",
                    3));
        }
    }

    private async Task<GitHubProjectItem> EnsureProjectItemAsync(
        TrackerConfig config,
        CreateContext context,
        IssueAllocation allocation,
        WorkItemId id,
        ICollection<string> reconciled,
        CancellationToken cancellationToken)
    {
        var matches = (await projects.ListAsync(
                config, null, null, ArchiveScope.All, cancellationToken))
            .Where(item => item.Number == allocation.Number)
            .ToArray();
        if (matches.Length > 1)
        {
            throw DuplicateAttempt(
                context.AttemptId, matches.Select(item => item.Summary.Id.Value));
        }

        var projectItemId = matches.Length == 1
            ? matches[0].ProjectItemId
            : await AddIssueToProjectAsync(
                config, context, allocation, id, reconciled, cancellationToken);
        return new GitHubProjectItem(
            resolver.Decode(id, config),
            new WorkItemSummary(
                id,
                context.Request.Title,
                allocation.Url,
                context.Status,
                context.Request.Priority),
            allocation.NodeId,
            projectItemId);
    }

    private async Task<string> AddIssueToProjectAsync(
        TrackerConfig config,
        CreateContext context,
        IssueAllocation allocation,
        WorkItemId id,
        ICollection<string> reconciled,
        CancellationToken cancellationToken)
    {
        try
        {
            var projectItemId = await projects.AddIssueAsync(
                config, allocation.NodeId, cancellationToken);
            reconciled.Add("project-add");
            return projectItemId;
        }
        catch (Exception exception)
        {
            throw PartialCreate(
                id, allocation.Url, context.AttemptId, "project-add", exception);
        }
    }

    private async Task ReconcileCreatedProjectItemAsync(
        TrackerConfig config,
        CreateWorkItemOperation operation,
        CreateContext context,
        GitHubProjectItem item,
        WorkItemId id,
        string? url,
        ICollection<string> reconciled,
        CancellationToken cancellationToken)
    {
        await RunCreateStageAsync(
            () => projects.UpdateCreationAttemptIdAsync(
                config, item, context.AttemptId, cancellationToken),
            "creation-id-set",
            id,
            url,
            context.AttemptId,
            reconciled);
        await RunCreateStageAsync(
            () => projects.UpdateStatusAsync(
                config, item, context.Status, cancellationToken),
            "status-set",
            id,
            url,
            context.AttemptId,
            reconciled);
        if (context.Request.Priority is not null)
        {
            await RunCreateStageAsync(
                () => projects.UpdatePriorityAsync(
                    config, item, context.Request.Priority, cancellationToken),
                "priority-set",
                id,
                url,
                context.AttemptId,
                reconciled);
        }

        if (operation.ArchiveAfterCreate)
        {
            await RunCreateStageAsync(
                () => projects.ArchiveAsync(config, item, cancellationToken),
                "archive",
                id,
                url,
                context.AttemptId,
                reconciled);
        }
    }

    private static async Task RunCreateStageAsync(
        Func<Task> action,
        string stage,
        WorkItemId id,
        string? url,
        string attemptId,
        ICollection<string> reconciled)
    {
        try
        {
            await action();
            reconciled.Add(stage);
        }
        catch (Exception exception)
        {
            throw PartialCreate(id, url, attemptId, stage, exception);
        }
    }

    private async Task<WorkItemDetail> ReadCreatedDetailAsync(
        TrackerConfig config,
        WorkItemId id,
        string? url,
        string attemptId,
        CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                var found = await GetAsync(config, id, cancellationToken);
                if (found is not null)
                {
                    return found;
                }

                if (attempt < 4)
                {
                    await retryDelay(
                        TimeSpan.FromMilliseconds(250 * (1 << attempt)),
                        cancellationToken);
                }
            }

            throw new TrackerException(
                "WORK_ITEM_NOT_FOUND",
                "The created issue was not found in the configured Project.",
                5);
        }
        catch (Exception exception)
        {
            throw PartialCreate(id, url, attemptId, "final-read", exception);
        }
    }

    private async Task CleanupCreatedLabelAsync(
        TrackerConfig config,
        int issueNumber,
        CreateContext context,
        WorkItemId id,
        string? url,
        CancellationToken cancellationToken)
    {
        try
        {
            await CleanupTemporaryLabelAsync(
                config,
                issueNumber,
                context.LabelName,
                context.IntentHash,
                cancellationToken);
        }
        catch (Exception exception)
        {
            throw PartialCreate(
                id, url, context.AttemptId, "temporary-label-delete", exception);
        }
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

        if (request.AutomationEligible || request.PreferredAgent is not null)
        {
            await ApplyWorkerLabelsAsync(config, item.Summary.Id, request.AutomationEligible,
                request.PreferredAgent, cancellationToken);
            detail = await GetAsync(config, item.Summary.Id, cancellationToken) ?? detail;
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

    private async Task ApplyWorkerLabelsAsync(
        TrackerConfig config,
        WorkItemId id,
        bool eligible,
        string? preferredAgent,
        CancellationToken cancellationToken)
    {
        var address = resolver.Decode(id, config);
        var desired = new List<string>();
        if (eligible) desired.Add("wrighty:auto");
        if (!string.IsNullOrWhiteSpace(preferredAgent))
            desired.Add($"wrighty:agent={preferredAgent.Trim().ToLowerInvariant()}");
        foreach (var label in desired)
            await EnsureWorkerLabelAsync(config, label, cancellationToken);

        using var issue = await api.GetAsync(config.GitHubHost,
            $"repos/{address.Owner}/{address.Repository}/issues/{address.IssueNumber}",
            cancellationToken);
        var labels = issue.RootElement.GetProperty("labels").EnumerateArray()
            .Select(value => value.GetProperty("name").GetString())
            .Where(value => value is not null &&
                            !string.Equals(value, "wrighty:auto", StringComparison.OrdinalIgnoreCase) &&
                            !value.StartsWith("wrighty:agent=", StringComparison.OrdinalIgnoreCase))
            .Cast<string>()
            .Concat(desired)
            .ToArray();
        using var ignored = await api.SendJsonAsync(config.GitHubHost, "PATCH",
            $"repos/{address.Owner}/{address.Repository}/issues/{address.IssueNumber}",
            new { labels }, cancellationToken);
    }

    private async Task EnsureWorkerLabelAsync(
        TrackerConfig config,
        string label,
        CancellationToken cancellationToken)
    {
        try
        {
            using var _ = await api.SendJsonAsync(config.GitHubHost, "POST",
                $"repos/{config.RepositoryOwner}/{config.RepositoryName}/labels",
                new { name = label, color = "6F42C1", description = "Managed by Wrighty worker mode" },
                cancellationToken);
        }
        catch (TrackerException exception) when (exception.Code == "GH_API_ERROR")
        {
            // GitHub reports an error when the label already exists. Applying it to an issue below
            // remains idempotent and surfaces a genuinely unavailable label.
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
        => await UpdateAsync(config, id, patch,
            new ClaimHandle(Highbyte.Wrighty.AgentContext.AgentExecutionContext.None, null), cancellationToken);

    public async Task<UpdateWorkItemResult> UpdateAsync(
        TrackerConfig config,
        WorkItemId id,
        WorkItemPatch patch,
        ClaimHandle claimHandle,
        CancellationToken cancellationToken)
    {
        WorkItemPatchValidator.Validate(patch);
        if (patch.Fields.IsSpecified)
        {
            throw CustomFieldsNotSupported();
        }
        var target = await GetUpdateTargetAsync(config, id, cancellationToken);
        var changes = ChangedFields(target.Current, patch);
        await ValidateUpdateFieldsAsync(config, patch, changes, cancellationToken);
        if (changes.Count == 0)
        {
            return new UpdateWorkItemResult(target.Current, false, []);
        }

        var applied = await ApplyUpdateChangesAsync(
            config, id, patch, target, changes, claimHandle, cancellationToken);
        return await ReadUpdatedItemAsync(
            config, id, target.Current.Url, changes, applied, cancellationToken);
    }

    private async Task<UpdateTarget> GetUpdateTargetAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
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
            projectItem.Priority,
            Labels: root.GetProperty("labels").EnumerateArray()
                .Select(label => label.GetProperty("name").GetString())
                .Where(name => name is not null).Cast<string>().ToArray(),
            AutomationEligible: root.GetProperty("labels").EnumerateArray()
                .Any(label => string.Equals(label.GetProperty("name").GetString(), "wrighty:auto",
                    StringComparison.OrdinalIgnoreCase)),
            PreferredAgent: PreferredAgent(root),
            WorkerState: WorkerState(root));
        return new UpdateTarget(address, projectItem, current);
    }

    private async Task ValidateUpdateFieldsAsync(
        TrackerConfig config,
        WorkItemPatch patch,
        IReadOnlyCollection<string> changes,
        CancellationToken cancellationToken)
    {
        var changedStatus = changes.Contains("status");
        var changedPriority = changes.Contains("priority");
        if (!changedStatus && !changedPriority)
        {
            return;
        }

        await projects.ValidateUpdateFieldsAsync(
            config,
            changedStatus ? patch.Status.Value : null,
            changedPriority && patch.Priority.Value is not null ? patch.Priority.Value : null,
            changedPriority && patch.Priority.Value is null,
            cancellationToken);
    }

    private async Task<IReadOnlyList<string>> ApplyUpdateChangesAsync(
        TrackerConfig config,
        WorkItemId id,
        WorkItemPatch patch,
        UpdateTarget target,
        IReadOnlyList<string> changes,
        ClaimHandle claimHandle,
        CancellationToken cancellationToken)
    {
        var applied = new List<string>();
        try
        {
            var issueFields = changes
                .Where(field => field is "title" or "body" or "wrighty-auto" or "wrighty-agent" or
                                "wrighty-worker-state")
                .ToArray();
            if (issueFields.Length > 0)
            {
                await UpdateIssueFieldsAsync(
                    config, id, patch, target.Address, target.Current, issueFields, claimHandle, cancellationToken);
                applied.AddRange(issueFields);
            }

            if (changes.Contains("priority"))
            {
                await UpdatePriorityAsync(
                    config, id, patch, target.ProjectItem, claimHandle, cancellationToken);
                applied.Add("priority");
            }

            if (changes.Contains("status"))
            {
                await mutationGuard.EnsureOwnedAsync(config, id, claimHandle, cancellationToken);
                await projects.UpdateStatusAsync(
                    config,
                    target.ProjectItem,
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
            throw PartialUpdate(id, target.Current.Url, stage, changes, applied, exception);
        }

        return applied;
    }

    private async Task UpdateIssueFieldsAsync(
        TrackerConfig config,
        WorkItemId id,
        WorkItemPatch patch,
        GitHubWorkItemAddress address,
        WorkItemDetail current,
        IReadOnlyCollection<string> issueFields,
        ClaimHandle claimHandle,
        CancellationToken cancellationToken)
    {
        await mutationGuard.EnsureOwnedAsync(config, id, claimHandle, cancellationToken);
        var body = new Dictionary<string, object?>();
        if (issueFields.Contains("title"))
        {
            body["title"] = patch.Title.Value;
        }

        if (issueFields.Contains("body"))
        {
            body["body"] = patch.Body.Value;
        }
        if (issueFields.Contains("wrighty-auto") || issueFields.Contains("wrighty-agent") ||
            issueFields.Contains("wrighty-worker-state"))
        {
            var labels = (current.Labels ?? [])
                .Where(label => !string.Equals(label, "wrighty:auto", StringComparison.OrdinalIgnoreCase) &&
                                !label.StartsWith("wrighty:agent=", StringComparison.OrdinalIgnoreCase) &&
                                !label.StartsWith("wrighty:worker-state=", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var eligible = patch.AutomationEligible.IsSpecified
                ? patch.AutomationEligible.Value : current.AutomationEligible;
            var preferred = patch.PreferredAgent.IsSpecified
                ? patch.PreferredAgent.Value : current.PreferredAgent;
            var workerState = patch.WorkerState.IsSpecified
                ? patch.WorkerState.Value : current.WorkerState;
            if (eligible) labels.Add("wrighty:auto");
            if (!string.IsNullOrWhiteSpace(preferred)) labels.Add($"wrighty:agent={preferred!.ToLowerInvariant()}");
            if (!string.IsNullOrWhiteSpace(workerState))
                labels.Add($"wrighty:worker-state={workerState!.ToLowerInvariant()}");
            foreach (var label in labels.Where(value =>
                         value.Equals("wrighty:auto", StringComparison.OrdinalIgnoreCase) ||
                         value.StartsWith("wrighty:agent=", StringComparison.OrdinalIgnoreCase) ||
                         value.StartsWith("wrighty:worker-state=", StringComparison.OrdinalIgnoreCase)))
                await EnsureWorkerLabelAsync(config, label, cancellationToken);
            body["labels"] = labels;
        }

        using var _ = await api.SendJsonAsync(
            config.GitHubHost,
            "PATCH",
            $"repos/{address.Owner}/{address.Repository}/issues/{address.IssueNumber}",
            body,
            cancellationToken);
    }

    private async Task UpdatePriorityAsync(
        TrackerConfig config,
        WorkItemId id,
        WorkItemPatch patch,
        GitHubProjectItem projectItem,
        ClaimHandle claimHandle,
        CancellationToken cancellationToken)
    {
        await mutationGuard.EnsureOwnedAsync(config, id, claimHandle, cancellationToken);
        if (patch.Priority.Value is null)
        {
            await projects.ClearPriorityAsync(config, projectItem, cancellationToken);
            return;
        }

        await projects.UpdatePriorityAsync(
            config, projectItem, patch.Priority.Value, cancellationToken);
    }

    private async Task<UpdateWorkItemResult> ReadUpdatedItemAsync(
        TrackerConfig config,
        WorkItemId id,
        string? currentUrl,
        IReadOnlyList<string> changes,
        IReadOnlyList<string> applied,
        CancellationToken cancellationToken)
    {
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
            throw PartialUpdate(id, currentUrl, "final-read", changes, applied, exception);
        }
    }

    private static void ValidateRequest(CreateWorkItemRequest request)
    {
        if (request.Fields is { Count: > 0 })
        {
            throw CustomFieldsNotSupported();
        }

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
        if (request.PreferredAgent is not null &&
            request.PreferredAgent.ToLowerInvariant() is not ("claude" or "codex" or "copilot"))
            throw new TrackerException("ARGUMENT_INVALID",
                "worker agent must be claude, codex, or copilot.", 2);
    }

    private static TrackerException CustomFieldsNotSupported() => new(
        "NOT_SUPPORTED",
        "Custom fields are supported only by the Local Markdown backend.",
        3);

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
        if (patch.AutomationEligible.IsSpecified &&
            current.AutomationEligible != patch.AutomationEligible.Value)
            changes.Add("wrighty-auto");
        if (patch.PreferredAgent.IsSpecified &&
            !string.Equals(current.PreferredAgent, patch.PreferredAgent.Value,
                StringComparison.OrdinalIgnoreCase))
            changes.Add("wrighty-agent");
        if (patch.WorkerState.IsSpecified &&
            !string.Equals(current.WorkerState, patch.WorkerState.Value,
                StringComparison.OrdinalIgnoreCase))
            changes.Add("wrighty-worker-state");

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
            "title" or "body" or "wrighty-auto" or "wrighty-agent" or
                "wrighty-worker-state" => "issue-update",
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

    private sealed record AllocationResult(
        IssueAllocation Issue,
        CreateDisposition Disposition);

    private sealed record CreateContext(
        CreateWorkItemRequest Request,
        string Status,
        string AttemptId,
        string IntentHash,
        string LabelName);

    private sealed record UpdateTarget(
        GitHubWorkItemAddress Address,
        GitHubProjectItem ProjectItem,
        WorkItemDetail Current);
}

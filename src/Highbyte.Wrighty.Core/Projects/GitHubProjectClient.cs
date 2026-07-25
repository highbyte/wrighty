using System.Text.Json;
using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Projects;

public sealed class GitHubProjectClient(GhApi api, INodeIdCache cache) : IProjectClient
{
    private const string DiscoveryQuery = """
        query($owner: String!, $number: Int!) {
          repositoryOwner(login: $owner) {
            ... on User {
              projectV2(number: $number) {
                id
                fields(first: 100) {
                  nodes {
                    __typename
                    ... on ProjectV2Field { id name dataType }
                    ... on ProjectV2SingleSelectField {
                      id
                      name
                      dataType
                      options { id name description color }
                    }
                  }
                }
              }
            }
            ... on Organization {
              projectV2(number: $number) {
                id
                fields(first: 100) {
                  nodes {
                    __typename
                    ... on ProjectV2Field { id name dataType }
                    ... on ProjectV2SingleSelectField {
                      id
                      name
                      dataType
                      options { id name description color }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private const string ListQuery = """
        query(
          $projectId: ID!,
          $cursor: String,
          $archivedStates: [ProjectV2ItemArchivedState!],
          $statusField: String!,
          $priorityField: String!,
          $executionPolicyField: String!,
          $agentPolicyField: String!
        ) {
          node(id: $projectId) {
            ... on ProjectV2 {
              items(first: 100, after: $cursor, archivedStates: $archivedStates) {
                nodes {
                  id
                  type
                  isArchived
                  content {
                    ... on Issue {
                      id
                      number
                      title
                      url
                      repository { nameWithOwner }
                    }
                  }
                  status: fieldValueByName(name: $statusField) {
                    ... on ProjectV2ItemFieldSingleSelectValue { name }
                  }
                  priority: fieldValueByName(name: $priorityField) {
                    ... on ProjectV2ItemFieldSingleSelectValue { name }
                  }
                  executionPolicy: fieldValueByName(name: $executionPolicyField) {
                    ... on ProjectV2ItemFieldSingleSelectValue { name }
                  }
                  agentPolicy: fieldValueByName(name: $agentPolicyField) {
                    ... on ProjectV2ItemFieldSingleSelectValue { name }
                  }
                }
                pageInfo { hasNextPage endCursor }
              }
            }
          }
        }
        """;

    private const string CreationLookupQuery = """
        query(
          $projectId: ID!,
          $cursor: String,
          $creationField: String!,
          $statusField: String!,
          $priorityField: String!,
          $executionPolicyField: String!,
          $agentPolicyField: String!
        ) {
          node(id: $projectId) {
            ... on ProjectV2 {
              items(first: 100, after: $cursor, archivedStates: [ARCHIVED, NOT_ARCHIVED]) {
                nodes {
                  id
                  type
                  isArchived
                  content {
                    ... on Issue {
                      id number title url repository { nameWithOwner }
                    }
                  }
                  creationAttempt: fieldValueByName(name: $creationField) {
                    ... on ProjectV2ItemFieldTextValue { text }
                  }
                  status: fieldValueByName(name: $statusField) {
                    ... on ProjectV2ItemFieldSingleSelectValue { name }
                  }
                  priority: fieldValueByName(name: $priorityField) {
                    ... on ProjectV2ItemFieldSingleSelectValue { name }
                  }
                  executionPolicy: fieldValueByName(name: $executionPolicyField) {
                    ... on ProjectV2ItemFieldSingleSelectValue { name }
                  }
                  agentPolicy: fieldValueByName(name: $agentPolicyField) {
                    ... on ProjectV2ItemFieldSingleSelectValue { name }
                  }
                }
                pageInfo { hasNextPage endCursor }
              }
            }
          }
        }
        """;

    private const string ArchiveItemMutation = """
        mutation($projectId: ID!, $itemId: ID!) {
          archiveProjectV2Item(input: { projectId: $projectId, itemId: $itemId }) {
            item { id isArchived }
          }
        }
        """;

    private const string UnarchiveItemMutation = """
        mutation($projectId: ID!, $itemId: ID!) {
          unarchiveProjectV2Item(input: { projectId: $projectId, itemId: $itemId }) {
            item { id isArchived }
          }
        }
        """;

    private const string UpdateStatusMutation = """
        mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $optionId: String!) {
          updateProjectV2ItemFieldValue(input: {
            projectId: $projectId,
            itemId: $itemId,
            fieldId: $fieldId,
            value: { singleSelectOptionId: $optionId }
          }) {
            projectV2Item { id }
          }
        }
        """;

    private const string AddIssueMutation = """
        mutation($projectId: ID!, $contentId: ID!) {
          addProjectV2ItemById(input: { projectId: $projectId, contentId: $contentId }) {
            item { id }
          }
        }
        """;

    private const string CreateFieldMutation = """
        mutation(
          $projectId: ID!,
          $name: String!,
          $dataType: ProjectV2CustomFieldType!,
          $options: [ProjectV2SingleSelectFieldOptionInput!]
        ) {
          createProjectV2Field(input: {
            projectId: $projectId,
            name: $name,
            dataType: $dataType,
            singleSelectOptions: $options
          }) {
            projectV2Field { ... on ProjectV2FieldCommon { id name } }
          }
        }
        """;

    private const string UpdateSingleSelectFieldMutation = """
        mutation($fieldId: ID!, $options: [ProjectV2SingleSelectFieldOptionInput!]!) {
          updateProjectV2Field(input: {
            fieldId: $fieldId,
            singleSelectOptions: $options
          }) {
            projectV2Field { ... on ProjectV2FieldCommon { id name } }
          }
        }
        """;

    private const string UpdateSingleSelectValueMutation = """
        mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $optionId: String!) {
          updateProjectV2ItemFieldValue(input: {
            projectId: $projectId,
            itemId: $itemId,
            fieldId: $fieldId,
            value: { singleSelectOptionId: $optionId }
          }) {
            projectV2Item { id }
          }
        }
        """;

    private const string UpdateTextValueMutation = """
        mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $text: String!) {
          updateProjectV2ItemFieldValue(input: {
            projectId: $projectId,
            itemId: $itemId,
            fieldId: $fieldId,
            value: { text: $text }
          }) {
            projectV2Item { id }
          }
        }
        """;

    private const string ClearFieldValueMutation = """
        mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!) {
          clearProjectV2ItemFieldValue(input: {
            projectId: $projectId,
            itemId: $itemId,
            fieldId: $fieldId
          }) {
            projectV2Item { id }
          }
        }
        """;

    private const string OrangeOptionColor = "ORANGE";

    private static readonly RequiredAgentOption[] RequiredAgentOptions =
    [
        new("Codex", "OpenAI Codex agent", "GREEN"),
        new("Claude", "Anthropic Claude Code agent", OrangeOptionColor),
        new("Copilot", "GitHub Copilot agent", "BLUE"),
        new("Other", "Another agent runtime", "GRAY")
    ];
    private static readonly RequiredAgentOption[] RequiredClaimantOptions =
    [
        new("Agent", "Agent claimant", "GREEN"),
        new("Human", "Human claimant", "BLUE"),
        new("Automation", "Automation claimant", OrangeOptionColor),
        new("Unknown", "Unknown claimant", "GRAY")
    ];
    private static readonly RequiredAgentOption[] RequiredExecutionPolicyOptions =
    [
        new("Manual only", "No unattended Wrighty worker launch is allowed", "GRAY"),
        new("Automatic allowed", "A Wrighty worker may claim and run this item", "GREEN")
    ];
    private static readonly RequiredAgentOption[] RequiredAgentPolicyOptions =
    [
        new("Repository default", "Use the configured default agent", "GRAY"),
        new("Claude", "Use Anthropic Claude Code", OrangeOptionColor),
        new("Codex", "Use OpenAI Codex", "GREEN"),
        new("Copilot", "Use GitHub Copilot", "BLUE")
    ];
    private static readonly RequiredAgentOption[] RequiredDispatchStateOptions =
    [
        new("Needs attention", "Automatic processing stopped for an operator decision", "RED"),
        new("Resume queued", "The recorded vendor session is ready to resume", "BLUE"),
        new("Retry scheduled", "The recorded vendor session is waiting for a bounded retry", OrangeOptionColor),
        new("Handoff queued", "A cross-agent continuation is queued", "PURPLE")
    ];

    public async Task<ProjectInitializationResult> InitializeAsync(
        TrackerConfig config,
        bool checkOnly,
        CancellationToken cancellationToken)
    {
        var schema = await DiscoverSchemaAsync(config, cancellationToken);
        _ = BuildMetadata(
            config,
            schema,
            requireAgentContext: false,
            requirePolicy: false);
        var actions = ValidateAndPlanInitialization(config, schema);

        if (checkOnly)
        {
            if (actions.Count > 0)
            {
                throw new TrackerException(
                    "PROJECT_SCHEMA_INVALID",
                    $"Project initialization is required: {string.Join("; ", actions)}. Run 'wrighty init'.",
                    5);
            }

            return new ProjectInitializationResult(false, ["Project schema is valid."]);
        }

        await ApplyInitializationAsync(config, schema, actions, cancellationToken);
        var refreshed = actions.Count > 0
            ? await DiscoverSchemaAsync(config, cancellationToken)
            : schema;
        var metadata = BuildMetadata(
            config,
            refreshed,
            requireAgentContext: true,
            requirePolicy: true);
        await cache.PutAsync(CacheKey(config), metadata, cancellationToken);
        return new ProjectInitializationResult(
            actions.Count > 0,
            actions.Count > 0 ? actions : ["Project schema was already initialized."]);
    }

    public async Task EnsureAgentContextSchemaAsync(
        TrackerConfig config,
        CancellationToken cancellationToken)
    {
        _ = await GetProjectionMetadataAsync(config, cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubProjectItem>> ListAsync(
        TrackerConfig config,
        string? status,
        int? limit,
        CancellationToken cancellationToken)
    {
        return await ListAsync(config, status, limit, ArchiveScope.Active, cancellationToken);
    }

    public async Task<IReadOnlyList<GitHubProjectItem>> ListAsync(
        TrackerConfig config,
        string? status,
        int? limit,
        ArchiveScope archiveScope,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ListCoreAsync(config, status, limit, archiveScope, cancellationToken);
        }
        catch (TrackerException exception) when (IsStaleNodeError(exception))
        {
            await cache.InvalidateAsync(CacheKey(config), cancellationToken);
            return await ListCoreAsync(config, status, limit, archiveScope, cancellationToken);
        }
    }

    private async Task<IReadOnlyList<GitHubProjectItem>> ListCoreAsync(
        TrackerConfig config,
        string? status,
        int? limit,
        ArchiveScope archiveScope,
        CancellationToken cancellationToken)
    {
        ValidateLimit(limit);
        var metadata = await GetMetadataAsync(config, cancellationToken);
        var items = new List<GitHubProjectItem>();
        string? cursor = null;
        do
        {
            using var document = await GetItemsPageAsync(
                config, metadata.ProjectId, cursor, archiveScope, cancellationToken);
            var connection = GetItemsConnection(document.RootElement);
            AddMatchingItems(config, connection, status, items);
            cursor = GetNextCursor(connection);
        }
        while (cursor is not null && (!limit.HasValue || items.Count < limit.Value));

        return items
            .OrderBy(item => PriorityRank(item.Priority))
            .ThenBy(item => item.Number)
            .Take(limit ?? int.MaxValue)
            .ToArray();
    }

    private static void ValidateLimit(int? limit)
    {
        if (limit <= 0)
        {
            throw new TrackerException("ARGUMENT_INVALID", "limit must be positive.", 2);
        }
    }

    private Task<JsonDocument> GetItemsPageAsync(
        TrackerConfig config,
        string projectId,
        string? cursor,
        ArchiveScope archiveScope,
        CancellationToken cancellationToken) => api.GraphQlAsync(
        config.GitHubHost,
        ListQuery,
        new
        {
            projectId,
            cursor,
            statusField = config.StatusField,
            priorityField = config.PriorityField,
            executionPolicyField = config.ExecutionPolicyField,
            agentPolicyField = config.AgentPolicyField,
            archivedStates = ArchivedStates(archiveScope)
        },
        cancellationToken);

    private static string[] ArchivedStates(ArchiveScope archiveScope) => archiveScope switch
    {
        ArchiveScope.Active => ["NOT_ARCHIVED"],
        ArchiveScope.Archived => ["ARCHIVED"],
        _ => ["ARCHIVED", "NOT_ARCHIVED"]
    };

    private static JsonElement GetItemsConnection(JsonElement root)
    {
        ThrowIfGraphQlErrors(root);
        return root.GetProperty("data")
            .GetProperty("node")
            .GetProperty("items");
    }

    private static void AddMatchingItems(
        TrackerConfig config,
        JsonElement connection,
        string? status,
        ICollection<GitHubProjectItem> items)
    {
        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            if (TryParseIssue(config, node, out var item) && MatchesStatus(item, status))
            {
                items.Add(item);
            }
        }
    }

    private static bool MatchesStatus(GitHubProjectItem item, string? status) =>
        status is null || string.Equals(
            item.Status, status, StringComparison.OrdinalIgnoreCase);

    private static string? GetNextCursor(JsonElement connection)
    {
        var pageInfo = connection.GetProperty("pageInfo");
        return pageInfo.GetProperty("hasNextPage").GetBoolean()
            ? pageInfo.GetProperty("endCursor").GetString()
            : null;
    }

    public async Task<IReadOnlyList<GitHubProjectItem>> FindByCreationAttemptIdAsync(
        TrackerConfig config,
        string creationAttemptId,
        CancellationToken cancellationToken)
    {
        var metadata = await GetCreationMetadataAsync(config, cancellationToken);
        var matches = new List<GitHubProjectItem>();
        string? cursor = null;
        do
        {
            using var document = await api.GraphQlAsync(
                config.GitHubHost,
                CreationLookupQuery,
                new
                {
                    projectId = metadata.ProjectId,
                    cursor,
                    creationField = config.CreationAttemptIdField,
                    statusField = config.StatusField,
                    priorityField = config.PriorityField,
                    executionPolicyField = config.ExecutionPolicyField,
                    agentPolicyField = config.AgentPolicyField
                },
                cancellationToken);
            ThrowIfGraphQlErrors(document.RootElement);
            var connection = document.RootElement.GetProperty("data")
                .GetProperty("node")
                .GetProperty("items");
            foreach (var node in connection.GetProperty("nodes").EnumerateArray())
            {
                var value = node.TryGetProperty("creationAttempt", out var creation) &&
                            creation.ValueKind != JsonValueKind.Null &&
                            creation.TryGetProperty("text", out var text)
                    ? text.GetString()
                    : null;
                if (string.Equals(value, creationAttemptId, StringComparison.Ordinal) &&
                    TryParseIssue(config, node, out var item))
                {
                    matches.Add(item with { CreationAttemptId = value });
                }
            }

            var pageInfo = connection.GetProperty("pageInfo");
            cursor = pageInfo.GetProperty("hasNextPage").GetBoolean()
                ? pageInfo.GetProperty("endCursor").GetString()
                : null;
        }
        while (cursor is not null);

        return matches;
    }

    public async Task UpdateCreationAttemptIdAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        string creationAttemptId,
        CancellationToken cancellationToken)
    {
        var metadata = await GetCreationMetadataAsync(config, cancellationToken);
        using var document = await api.GraphQlAsync(
            config.GitHubHost,
            UpdateTextValueMutation,
            new
            {
                projectId = metadata.ProjectId,
                itemId = item.ProjectItemId,
                fieldId = metadata.CreationAttemptIdFieldId,
                text = creationAttemptId
            },
            cancellationToken);
        ThrowIfGraphQlErrors(document.RootElement);
    }

    public async Task ArchiveAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(config, cancellationToken);
        using var document = await api.GraphQlAsync(
            config.GitHubHost,
            ArchiveItemMutation,
            new { projectId = metadata.ProjectId, itemId = item.ProjectItemId },
            cancellationToken);
        ThrowIfGraphQlErrors(document.RootElement);
    }

    public async Task UnarchiveAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(config, cancellationToken);
        using var document = await api.GraphQlAsync(
            config.GitHubHost,
            UnarchiveItemMutation,
            new { projectId = metadata.ProjectId, itemId = item.ProjectItemId },
            cancellationToken);
        ThrowIfGraphQlErrors(document.RootElement);
    }

    public async Task UpdateStatusAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        string status,
        CancellationToken cancellationToken)
    {
        try
        {
            await UpdateStatusCoreAsync(config, item, status, cancellationToken);
        }
        catch (TrackerException exception) when (IsStaleNodeError(exception))
        {
            await cache.InvalidateAsync(CacheKey(config), cancellationToken);
            await UpdateStatusCoreAsync(config, item, status, cancellationToken);
        }
    }

    public async Task ValidateCreateFieldsAsync(
        TrackerConfig config,
        string status,
        string? priority,
        CancellationToken cancellationToken)
    {
        await cache.InvalidateAsync(CacheKey(config), cancellationToken);
        var metadata = await GetMetadataAsync(config, cancellationToken);
        if (metadata.CreationAttemptIdFieldId is null)
        {
            throw NotInitialized(config);
        }
        if (!metadata.StatusOptions.ContainsKey(status))
        {
            throw new TrackerException(
                "STATUS_NOT_FOUND",
                $"Project status option '{status}' was not found.",
                5);
        }

        if (priority is not null &&
            (metadata.PriorityFieldId is null ||
             metadata.PriorityOptions is null ||
             !metadata.PriorityOptions.ContainsKey(priority)))
        {
            throw new TrackerException(
                "PRIORITY_NOT_FOUND",
                $"Project priority option '{priority}' was not found.",
                5);
        }
    }

    public async Task ValidateUpdateFieldsAsync(
        TrackerConfig config,
        string? status,
        string? priority,
        bool clearPriority,
        CancellationToken cancellationToken)
    {
        await cache.InvalidateAsync(CacheKey(config), cancellationToken);
        var metadata = await GetMetadataAsync(config, cancellationToken);
        if (status is not null && !metadata.StatusOptions.ContainsKey(status))
        {
            throw new TrackerException(
                "STATUS_NOT_FOUND",
                $"Project status option '{status}' was not found.",
                5);
        }

        if ((priority is not null || clearPriority) && metadata.PriorityFieldId is null)
        {
            throw new TrackerException(
                "PRIORITY_NOT_FOUND",
                $"Project priority field '{config.PriorityField}' was not found.",
                5);
        }

        if (priority is not null &&
            (metadata.PriorityOptions is null || !metadata.PriorityOptions.ContainsKey(priority)))
        {
            throw new TrackerException(
                "PRIORITY_NOT_FOUND",
                $"Project priority option '{priority}' was not found.",
                5);
        }
    }

    public async Task ValidatePolicyAsync(
        TrackerConfig config,
        bool automaticExecutionAllowed,
        string? agentPolicy,
        CancellationToken cancellationToken)
    {
        await cache.InvalidateAsync(CacheKey(config), cancellationToken);
        _ = await GetPolicyMetadataAsync(config, cancellationToken);
        if (agentPolicy is not null)
        {
            _ = CanonicalAgentName(agentPolicy);
        }
    }

    public async Task<string> AddIssueAsync(
        TrackerConfig config,
        string issueNodeId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await AddIssueCoreAsync(config, issueNodeId, cancellationToken);
        }
        catch (TrackerException exception) when (IsStaleNodeError(exception))
        {
            await cache.InvalidateAsync(CacheKey(config), cancellationToken);
            return await AddIssueCoreAsync(config, issueNodeId, cancellationToken);
        }
    }

    private async Task<string> AddIssueCoreAsync(
        TrackerConfig config,
        string issueNodeId,
        CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(config, cancellationToken);
        using var document = await api.GraphQlAsync(
            config.GitHubHost,
            AddIssueMutation,
            new { projectId = metadata.ProjectId, contentId = issueNodeId },
            cancellationToken);
        ThrowIfGraphQlErrors(document.RootElement);
        return document.RootElement.GetProperty("data")
            .GetProperty("addProjectV2ItemById")
            .GetProperty("item")
            .GetProperty("id")
            .GetString()!;
    }

    public async Task UpdatePriorityAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        string priority,
        CancellationToken cancellationToken)
    {
        try
        {
            await UpdatePriorityCoreAsync(config, item, priority, cancellationToken);
        }
        catch (TrackerException exception) when (IsStaleNodeError(exception))
        {
            await cache.InvalidateAsync(CacheKey(config), cancellationToken);
            await UpdatePriorityCoreAsync(config, item, priority, cancellationToken);
        }
    }

    public async Task UpdatePolicyAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        bool automaticExecutionAllowed,
        string? agentPolicy,
        CancellationToken cancellationToken)
    {
        try
        {
            await UpdatePolicyCoreAsync(
                config, item, automaticExecutionAllowed, agentPolicy, cancellationToken);
        }
        catch (TrackerException exception) when (IsStaleNodeError(exception))
        {
            await cache.InvalidateAsync(CacheKey(config), cancellationToken);
            await UpdatePolicyCoreAsync(
                config, item, automaticExecutionAllowed, agentPolicy, cancellationToken);
        }
    }

    public async Task UpdateDispatchStateProjectionAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        string? dispatchState,
        CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(config, cancellationToken);
        if (!HasRecoveryPresentationSchema(metadata))
            return;

        var dispatchOption = DispatchStateOption(dispatchState);
        if (dispatchOption is null)
        {
            await ClearValueAsync(
                config, metadata.ProjectId, item.ProjectItemId,
                metadata.DispatchStateFieldId!, cancellationToken);
        }
        else
        {
            await UpdateSingleSelectValueAsync(
                config, metadata.ProjectId, item.ProjectItemId,
                metadata.DispatchStateFieldId!,
                metadata.DispatchStateOptionOptions![dispatchOption],
                cancellationToken);
        }

        if (dispatchState is DispatchStates.RetryScheduled or
            DispatchStates.HandoffQueued)
            return;

        await ClearDispatchDetailsAsync(config, metadata, item, cancellationToken);
    }

    public async Task UpdateDispatchProjectionAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        DispatchInfo dispatch,
        CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(config, cancellationToken);
        if (!HasRecoveryPresentationSchema(metadata))
            return;

        var dispatchOption = DispatchStateOption(dispatch.State)
            ?? throw new TrackerException(
                "ARGUMENT_INVALID",
                $"Unsupported worker dispatch state '{dispatch.State}'.",
                2);
        await UpdateSingleSelectValueAsync(
            config, metadata.ProjectId, item.ProjectItemId,
            metadata.DispatchStateFieldId!,
            metadata.DispatchStateOptionOptions![dispatchOption],
            cancellationToken);

        if (dispatch.State == DispatchStates.RetryScheduled)
        {
            await UpdateTextValueAsync(
                config, metadata.ProjectId, item.ProjectItemId,
                metadata.DispatchNotBeforeFieldId!, dispatch.NotBefore.ToString("O"),
                cancellationToken);
        }
        else
        {
            await ClearValueAsync(
                config, metadata.ProjectId, item.ProjectItemId,
                metadata.DispatchNotBeforeFieldId!, cancellationToken);
        }

        var targetAgent = dispatch.Agent ?? dispatch.SessionAgent;
        if (string.IsNullOrWhiteSpace(targetAgent))
        {
            await ClearValueAsync(
                config, metadata.ProjectId, item.ProjectItemId,
                metadata.DispatchAgentFieldId!, cancellationToken);
        }
        else
        {
            var targetOption = CanonicalProjectionAgentName(targetAgent);
            await UpdateSingleSelectValueAsync(
                config, metadata.ProjectId, item.ProjectItemId,
                metadata.DispatchAgentFieldId!,
                metadata.DispatchAgentOptions![targetOption],
                cancellationToken);
        }

        await UpdateTextValueAsync(
            config, metadata.ProjectId, item.ProjectItemId,
            metadata.DispatchDetailFieldId!,
            DispatchDetail(item, dispatch),
            cancellationToken);
    }

    private async Task ClearDispatchDetailsAsync(
        TrackerConfig config,
        ProjectMetadata metadata,
        GitHubProjectItem item,
        CancellationToken cancellationToken)
    {
        foreach (var fieldId in new[]
                 {
                     metadata.DispatchNotBeforeFieldId!,
                     metadata.DispatchAgentFieldId!,
                     metadata.DispatchDetailFieldId!
                 })
        {
            await ClearValueAsync(
                config, metadata.ProjectId, item.ProjectItemId, fieldId, cancellationToken);
        }
    }

    private async Task UpdatePolicyCoreAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        bool automaticExecutionAllowed,
        string? agentPolicy,
        CancellationToken cancellationToken)
    {
        var metadata = await GetPolicyMetadataAsync(config, cancellationToken);
        var executionPolicyName = automaticExecutionAllowed ? "Automatic allowed" : "Manual only";
        var agentPolicyName = string.IsNullOrWhiteSpace(agentPolicy)
            ? "Repository default"
            : CanonicalAgentName(agentPolicy);
        if (!metadata.ExecutionPolicyOptions!.TryGetValue(executionPolicyName, out var executionOptionId) ||
            !metadata.AgentPolicyOptions!.TryGetValue(agentPolicyName, out var agentPolicyOptionId))
        {
            throw NotInitialized(config);
        }

        if (!automaticExecutionAllowed)
        {
            await UpdateSingleSelectValueAsync(
                config, metadata.ProjectId, item.ProjectItemId,
                metadata.ExecutionPolicyFieldId!, executionOptionId, cancellationToken);
        }

        await UpdateSingleSelectValueAsync(
            config, metadata.ProjectId, item.ProjectItemId,
            metadata.AgentPolicyFieldId!, agentPolicyOptionId, cancellationToken);

        if (automaticExecutionAllowed)
        {
            await UpdateSingleSelectValueAsync(
                config, metadata.ProjectId, item.ProjectItemId,
                metadata.ExecutionPolicyFieldId!, executionOptionId, cancellationToken);
        }
    }

    private async Task UpdateSingleSelectValueAsync(
        TrackerConfig config,
        string projectId,
        string itemId,
        string fieldId,
        string optionId,
        CancellationToken cancellationToken)
    {
        using var document = await api.GraphQlAsync(
            config.GitHubHost,
            UpdateSingleSelectValueMutation,
            new { projectId, itemId, fieldId, optionId },
            cancellationToken);
        ThrowIfGraphQlErrors(document.RootElement);
    }

    private async Task UpdateTextValueAsync(
        TrackerConfig config,
        string projectId,
        string itemId,
        string fieldId,
        string text,
        CancellationToken cancellationToken)
    {
        using var document = await api.GraphQlAsync(
            config.GitHubHost,
            UpdateTextValueMutation,
            new { projectId, itemId, fieldId, text },
            cancellationToken);
        ThrowIfGraphQlErrors(document.RootElement);
    }

    public async Task ClearPriorityAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        CancellationToken cancellationToken)
    {
        try
        {
            await ClearPriorityCoreAsync(config, item, cancellationToken);
        }
        catch (TrackerException exception) when (IsStaleNodeError(exception))
        {
            await cache.InvalidateAsync(CacheKey(config), cancellationToken);
            await ClearPriorityCoreAsync(config, item, cancellationToken);
        }
    }

    private async Task ClearPriorityCoreAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(config, cancellationToken);
        if (metadata.PriorityFieldId is null)
        {
            throw new TrackerException(
                "PRIORITY_NOT_FOUND",
                $"Project priority field '{config.PriorityField}' was not found.",
                5);
        }

        await ClearValueAsync(
            config,
            metadata.ProjectId,
            item.ProjectItemId,
            metadata.PriorityFieldId,
            cancellationToken);
    }

    private async Task UpdatePriorityCoreAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        string priority,
        CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(config, cancellationToken);
        if (metadata.PriorityFieldId is null ||
            metadata.PriorityOptions is null ||
            !metadata.PriorityOptions.TryGetValue(priority, out var optionId))
        {
            throw new TrackerException(
                "PRIORITY_NOT_FOUND",
                $"Project priority option '{priority}' was not found.",
                5);
        }

        using var document = await api.GraphQlAsync(
            config.GitHubHost,
            UpdateSingleSelectValueMutation,
            new
            {
                projectId = metadata.ProjectId,
                itemId = item.ProjectItemId,
                fieldId = metadata.PriorityFieldId,
                optionId
            },
            cancellationToken);
        ThrowIfGraphQlErrors(document.RootElement);
    }

    public async Task UpdateAgentContextAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        string? agentType,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            await UpdateAgentContextCoreAsync(
                config,
                item,
                agentType,
                sessionId,
                cancellationToken);
        }
        catch (TrackerException exception) when (IsStaleNodeError(exception))
        {
            await cache.InvalidateAsync(CacheKey(config), cancellationToken);
            await UpdateAgentContextCoreAsync(
                config,
                item,
                agentType,
                sessionId,
                cancellationToken);
        }
    }

    public async Task UpdateClaimantProjectionAsync(TrackerConfig config, GitHubProjectItem item,
        string? claimantKind, string? claimantId, string? agentType, string? sessionId,
        CancellationToken cancellationToken)
    {
        await UpdateAgentContextAsync(config, item, agentType, sessionId, cancellationToken);
        var metadata = await GetProjectionMetadataAsync(config, cancellationToken);
        if (metadata.ClaimantTypeFieldId is null || metadata.ClaimantFieldId is null ||
            metadata.ClaimantKindOptions is null) throw NotInitialized(config);
        if (string.IsNullOrWhiteSpace(claimantKind))
            await ClearValueAsync(config, metadata.ProjectId, item.ProjectItemId, metadata.ClaimantTypeFieldId, cancellationToken);
        else
        {
            var name = char.ToUpperInvariant(claimantKind[0]) + claimantKind[1..].ToLowerInvariant();
            if (!metadata.ClaimantKindOptions.TryGetValue(name, out var optionId)) throw NotInitialized(config);
            using var document = await api.GraphQlAsync(config.GitHubHost, UpdateSingleSelectValueMutation,
                new { projectId = metadata.ProjectId, itemId = item.ProjectItemId, fieldId = metadata.ClaimantTypeFieldId, optionId }, cancellationToken);
            ThrowIfGraphQlErrors(document.RootElement);
        }
        if (string.IsNullOrWhiteSpace(claimantId))
            await ClearValueAsync(config, metadata.ProjectId, item.ProjectItemId, metadata.ClaimantFieldId, cancellationToken);
        else
        {
            var display = claimantId.Length <= 24 ? claimantId : $"{claimantId[..24]}…";
            using var document = await api.GraphQlAsync(config.GitHubHost, UpdateTextValueMutation,
                new { projectId = metadata.ProjectId, itemId = item.ProjectItemId, fieldId = metadata.ClaimantFieldId, text = display }, cancellationToken);
            ThrowIfGraphQlErrors(document.RootElement);
        }
    }

    public async Task UpdateWorkspacePathAsync(TrackerConfig config, GitHubProjectItem item,
        string? workspacePath, CancellationToken cancellationToken)
    {
        var metadata = await GetProjectionMetadataAsync(config, cancellationToken);
        if (metadata.ClaimWorkspacePathFieldId is null) throw NotInitialized(config);
        if (string.IsNullOrWhiteSpace(workspacePath))
            await ClearValueAsync(config, metadata.ProjectId, item.ProjectItemId,
                metadata.ClaimWorkspacePathFieldId, cancellationToken);
        else
        {
            using var document = await api.GraphQlAsync(config.GitHubHost, UpdateTextValueMutation,
                new
                {
                    projectId = metadata.ProjectId,
                    itemId = item.ProjectItemId,
                    fieldId = metadata.ClaimWorkspacePathFieldId,
                    text = workspacePath
                }, cancellationToken);
            ThrowIfGraphQlErrors(document.RootElement);
        }
    }

    private async Task UpdateAgentContextCoreAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        string? agentType,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var metadata = await GetProjectionMetadataAsync(config, cancellationToken);
        if (string.IsNullOrWhiteSpace(agentType))
        {
            await ClearValueAsync(
                config,
                metadata.ProjectId,
                item.ProjectItemId,
                metadata.ClaimAgentFieldId!,
                cancellationToken);
        }
        else
        {
            var optionName = agentType switch
            {
                "codex" => "Codex",
                "claude" => "Claude",
                "copilot" => "Copilot",
                _ => "Other"
            };
            if (!metadata.AgentOptions!.TryGetValue(optionName, out var optionId))
            {
                throw NotInitialized(config);
            }

            using var document = await api.GraphQlAsync(
                config.GitHubHost,
                UpdateSingleSelectValueMutation,
                new
                {
                    projectId = metadata.ProjectId,
                    itemId = item.ProjectItemId,
                    fieldId = metadata.ClaimAgentFieldId,
                    optionId
                },
                cancellationToken);
            ThrowIfGraphQlErrors(document.RootElement);
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            await ClearValueAsync(
                config,
                metadata.ProjectId,
                item.ProjectItemId,
                metadata.ClaimSessionIdFieldId!,
                cancellationToken);
        }
        else
        {
            using var document = await api.GraphQlAsync(
                config.GitHubHost,
                UpdateTextValueMutation,
                new
                {
                    projectId = metadata.ProjectId,
                    itemId = item.ProjectItemId,
                    fieldId = metadata.ClaimSessionIdFieldId,
                    text = sessionId
                },
                cancellationToken);
            ThrowIfGraphQlErrors(document.RootElement);
        }
    }

    private async Task ClearValueAsync(
        TrackerConfig config,
        string projectId,
        string itemId,
        string fieldId,
        CancellationToken cancellationToken)
    {
        using var document = await api.GraphQlAsync(
            config.GitHubHost,
            ClearFieldValueMutation,
            new { projectId, itemId, fieldId },
            cancellationToken);
        ThrowIfGraphQlErrors(document.RootElement);
    }

    private async Task UpdateStatusCoreAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        string status,
        CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(config, cancellationToken);
        if (!metadata.StatusOptions.TryGetValue(status, out var optionId))
        {
            throw new TrackerException(
                "STATUS_NOT_FOUND",
                $"Project status option '{status}' was not found.",
                5);
        }

        using var document = await api.GraphQlAsync(
            config.GitHubHost,
            UpdateStatusMutation,
            new
            {
                projectId = metadata.ProjectId,
                itemId = item.ProjectItemId,
                fieldId = metadata.StatusFieldId,
                optionId
            },
            cancellationToken);
        ThrowIfGraphQlErrors(document.RootElement);
    }

    private async Task<ProjectMetadata> GetMetadataAsync(
        TrackerConfig config,
        CancellationToken cancellationToken)
    {
        var key = CacheKey(config);
        var cached = await cache.GetAsync(key, cancellationToken);
        if (cached is not null)
        {
            return cached;
        }

        var schema = await DiscoverSchemaAsync(config, cancellationToken);
        var discovered = BuildMetadata(
            config,
            schema,
            requireAgentContext: false,
            requirePolicy: false);
        await cache.PutAsync(key, discovered, cancellationToken);
        return discovered;
    }

    private async Task<ProjectMetadata> GetProjectionMetadataAsync(
        TrackerConfig config,
        CancellationToken cancellationToken)
    {
        var key = CacheKey(config);
        var cached = await cache.GetAsync(key, cancellationToken);
        if (cached is not null && HasAgentContextSchema(cached))
        {
            return cached;
        }

        if (cached is not null)
        {
            await cache.InvalidateAsync(key, cancellationToken);
        }

        var schema = await DiscoverSchemaAsync(config, cancellationToken);
        var metadata = BuildMetadata(
            config,
            schema,
            requireAgentContext: true,
            requirePolicy: true);
        await cache.PutAsync(key, metadata, cancellationToken);
        return metadata;
    }

    private async Task<ProjectSchema> DiscoverSchemaAsync(
        TrackerConfig config,
        CancellationToken cancellationToken)
    {
        using var document = await api.GraphQlAsync(
            config.GitHubHost,
            DiscoveryQuery,
            new { owner = config.EffectiveProjectOwner, number = config.ProjectNumber },
            cancellationToken);
        ThrowIfGraphQlErrors(document.RootElement);

        var owner = document.RootElement.GetProperty("data").GetProperty("repositoryOwner");
        if (owner.ValueKind == JsonValueKind.Null ||
            !owner.TryGetProperty("projectV2", out var project) ||
            project.ValueKind == JsonValueKind.Null)
        {
            throw new TrackerException(
                "PROJECT_NOT_FOUND",
                $"Project {config.EffectiveProjectOwner}/{config.ProjectNumber} was not found.",
                5);
        }

        var fields = new List<ProjectFieldSchema>();
        foreach (var field in project.GetProperty("fields").GetProperty("nodes").EnumerateArray())
        {
            if (!field.TryGetProperty("id", out var idElement) ||
                !field.TryGetProperty("name", out var nameElement) ||
                !field.TryGetProperty("dataType", out var dataTypeElement))
            {
                continue;
            }

            var options = new List<ProjectOptionSchema>();
            if (field.TryGetProperty("options", out var optionElements))
            {
                foreach (var option in optionElements.EnumerateArray())
                {
                    options.Add(new ProjectOptionSchema(
                        option.GetProperty("id").GetString()!,
                        option.GetProperty("name").GetString()!,
                        option.GetProperty("description").GetString() ?? string.Empty,
                        option.GetProperty("color").GetString() ?? "GRAY"));
                }
            }

            fields.Add(new ProjectFieldSchema(
                idElement.GetString()!,
                nameElement.GetString()!,
                dataTypeElement.GetString()!,
                options));
        }

        return new ProjectSchema(project.GetProperty("id").GetString()!, fields);
    }

    private static ProjectMetadata BuildMetadata(
        TrackerConfig config,
        ProjectSchema schema,
        bool requireAgentContext,
        bool requirePolicy)
    {
        var status = GetUniqueField(schema, config.StatusField);
        if (status is null || status.DataType != "SINGLE_SELECT")
        {
            throw new TrackerException(
                "PROJECT_SCHEMA_INVALID",
                $"Project field '{config.StatusField}' must exist and be a single-select field.",
                5);
        }

        EnsureNoDuplicateOptions(status);

        var priority = GetUniqueField(schema, config.PriorityField);
        if (priority is not null)
        {
            EnsureNoDuplicateOptions(priority);
        }
        var agentType = GetUniqueField(schema, config.ClaimAgentField);
        var claimantKind = GetUniqueField(schema, config.ClaimantTypeField);
        var claimantId = GetUniqueField(schema, config.ClaimantField);
        var sessionId = GetUniqueField(schema, config.ClaimSessionIdField);
        var creationAttemptId = GetUniqueField(schema, config.CreationAttemptIdField);
        var workspacePath = GetUniqueField(schema, config.ClaimWorkspacePathField);
        var executionPolicy = GetUniqueField(schema, config.ExecutionPolicyField);
        var agentPolicy = GetUniqueField(schema, config.AgentPolicyField);
        var workerActivity = GetUniqueField(schema, config.DispatchStateField);
        var workerRetryAt = GetUniqueField(schema, config.DispatchNotBeforeField);
        var workerAgent = GetUniqueField(schema, config.DispatchAgentField);
        var workerStatus = GetUniqueField(schema, config.DispatchDetailField);
        if (agentType is not null)
        {
            EnsureNoDuplicateOptions(agentType);
        }
        ValidatePolicyField(
            executionPolicy,
            config.ExecutionPolicyField,
            RequiredExecutionPolicyOptions,
            requirePolicy);
        ValidatePolicyField(
            agentPolicy,
            config.AgentPolicyField,
            RequiredAgentPolicyOptions,
            requirePolicy);
        ValidatePolicyField(
            workerActivity,
            config.DispatchStateField,
            RequiredDispatchStateOptions,
            required: false);
        ValidatePolicyField(
            workerAgent,
            config.DispatchAgentField,
            RequiredAgentOptions,
            required: false);
        ValidateOptionalTextField(workerRetryAt, config.DispatchNotBeforeField);
        ValidateOptionalTextField(workerStatus, config.DispatchDetailField);
        var agentOptions = agentType?.Options.ToDictionary(
            option => option.Name,
            option => option.Id,
            StringComparer.OrdinalIgnoreCase);

        var metadata = new ProjectMetadata(
            schema.ProjectId,
            status.Id,
            status.Options.ToDictionary(
                option => option.Name,
                option => option.Id,
                StringComparer.OrdinalIgnoreCase),
            priority?.Id,
            agentType?.Id,
            agentOptions,
            sessionId?.Id,
            priority?.Options.ToDictionary(
                option => option.Name,
                option => option.Id,
                StringComparer.OrdinalIgnoreCase),
            creationAttemptId?.Id);
        metadata = metadata with
        {
            ClaimantTypeFieldId = claimantKind?.Id,
            ClaimantKindOptions = claimantKind?.Options.ToDictionary(option => option.Name, option => option.Id, StringComparer.OrdinalIgnoreCase),
            ClaimantFieldId = claimantId?.Id,
            ClaimWorkspacePathFieldId = workspacePath?.Id,
            ExecutionPolicyFieldId = executionPolicy?.Id,
            ExecutionPolicyOptions = OptionsByName(executionPolicy),
            AgentPolicyFieldId = agentPolicy?.Id,
            AgentPolicyOptions = OptionsByName(agentPolicy),
            DispatchStateFieldId = workerActivity?.Id,
            DispatchStateOptionOptions = OptionsByName(workerActivity),
            DispatchNotBeforeFieldId = workerRetryAt?.Id,
            DispatchAgentFieldId = workerAgent?.Id,
            DispatchAgentOptions = OptionsByName(workerAgent),
            DispatchDetailFieldId = workerStatus?.Id
        };

        if (requireAgentContext && !HasAgentContextSchema(metadata))
        {
            throw NotInitialized(config);
        }

        return metadata;
    }

    private static List<string> ValidateAndPlanInitialization(
        TrackerConfig config,
        ProjectSchema schema)
    {
        RejectLegacyProjectSchema(schema);
        var actions = new List<string>();
        var agentType = GetUniqueField(schema, config.ClaimAgentField);
        var claimantKind = GetUniqueField(schema, config.ClaimantTypeField);
        var claimantId = GetUniqueField(schema, config.ClaimantField);
        var sessionId = GetUniqueField(schema, config.ClaimSessionIdField);
        var creationAttemptId = GetUniqueField(schema, config.CreationAttemptIdField);
        var workspacePath = GetUniqueField(schema, config.ClaimWorkspacePathField);
        var executionPolicy = GetUniqueField(schema, config.ExecutionPolicyField);
        var agentPolicy = GetUniqueField(schema, config.AgentPolicyField);
        var workerActivity = GetUniqueField(schema, config.DispatchStateField);
        var workerRetryAt = GetUniqueField(schema, config.DispatchNotBeforeField);
        var workerAgent = GetUniqueField(schema, config.DispatchAgentField);
        var workerStatus = GetUniqueField(schema, config.DispatchDetailField);

        PlanSingleSelectField(
            actions,
            executionPolicy,
            config.ExecutionPolicyField,
            RequiredExecutionPolicyOptions);
        PlanSingleSelectField(
            actions,
            agentPolicy,
            config.AgentPolicyField,
            RequiredAgentPolicyOptions);
        PlanSingleSelectField(
            actions,
            workerActivity,
            config.DispatchStateField,
            RequiredDispatchStateOptions);
        PlanTextField(actions, workerRetryAt, config.DispatchNotBeforeField);
        PlanSingleSelectField(
            actions,
            workerAgent,
            config.DispatchAgentField,
            RequiredAgentOptions);
        PlanTextField(actions, workerStatus, config.DispatchDetailField);
        PlanSingleSelectField(actions, agentType, config.ClaimAgentField, RequiredAgentOptions);
        PlanTextField(actions, sessionId, config.ClaimSessionIdField);
        PlanSingleSelectField(actions, claimantKind, config.ClaimantTypeField, RequiredClaimantOptions);
        PlanTextField(actions, claimantId, config.ClaimantField);
        PlanTextField(actions, creationAttemptId, config.CreationAttemptIdField);
        PlanTextField(actions, workspacePath, config.ClaimWorkspacePathField);

        return actions;
    }

    private static void RejectLegacyProjectSchema(ProjectSchema schema)
    {
        // TODO(post-1.0): Remove pre-overhaul Project-field detection once pre-1.0 Projects are no
        // longer expected. This guard is intentionally read-only and must never rename or copy
        // values from legacy fields.
        string[] legacyNames =
        [
            "Worker execution",
            "Preferred agent",
            "Worker activity",
            "Worker retry at",
            "Worker target agent",
            "Worker status",
            "Current agent type",
            "Current claimant kind",
            "Current claimant",
            "Current session ID",
            "Current workspace path",
            "Creation attempt ID",
            "Wrighty policy: execution",
            "Wrighty policy: agent",
            "Wrighty dispatch: state",
            "Wrighty dispatch: not before",
            "Wrighty dispatch: agent",
            "Wrighty dispatch: detail",
            "Wrighty claim: agent",
            "Wrighty claim: claimant type",
            "Wrighty claim: claimant",
            "Wrighty claim: session ID",
            "Wrighty claim: workspace path",
            "Wrighty creation: attempt ID"
        ];
        var found = schema.Fields
            .Select(field => field.Name)
            .Where(name => legacyNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (found.Length == 0)
        {
            return;
        }

        throw new TrackerException(
            "PROJECT_SCHEMA_UNSUPPORTED",
            "The GitHub Project uses Wrighty's pre-overhaul field schema. Create a fresh Project " +
            $"for this pre-release version. Legacy fields: {string.Join(", ", found)}.",
            5,
            new Dictionary<string, object?> { ["legacyFields"] = found });
    }

    private static void PlanSingleSelectField(
        ICollection<string> actions,
        ProjectFieldSchema? field,
        string fieldName,
        IReadOnlyList<RequiredAgentOption> requiredOptions)
    {
        if (field is null)
        {
            actions.Add($"create single-select field '{fieldName}'");
            return;
        }

        if (field.DataType != "SINGLE_SELECT")
        {
            throw WrongFieldType(fieldName, "single-select");
        }

        EnsureNoDuplicateOptions(field);
        var missing = requiredOptions
            .Where(required => !field.Options.Any(option =>
                string.Equals(option.Name, required.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(required => required.Name)
            .ToArray();
        if (missing.Length > 0)
        {
            actions.Add($"add options {string.Join(", ", missing)} to '{fieldName}'");
        }
    }

    private static void PlanTextField(
        ICollection<string> actions,
        ProjectFieldSchema? field,
        string fieldName)
    {
        if (field is null)
        {
            actions.Add($"create text field '{fieldName}'");
        }
        else if (field.DataType != "TEXT")
        {
            throw WrongFieldType(fieldName, "text");
        }
    }

    private async Task ApplyInitializationAsync(
        TrackerConfig config,
        ProjectSchema schema,
        IReadOnlyList<string> actions,
        CancellationToken cancellationToken)
    {
        if (actions.Count == 0)
        {
            return;
        }

        await EnsureSingleSelectFieldAsync(
            config, schema, config.ExecutionPolicyField, RequiredExecutionPolicyOptions, cancellationToken);
        await EnsureSingleSelectFieldAsync(
            config, schema, config.AgentPolicyField, RequiredAgentPolicyOptions, cancellationToken);
        await EnsureSingleSelectFieldAsync(
            config, schema, config.DispatchStateField, RequiredDispatchStateOptions, cancellationToken);
        await EnsureTextFieldAsync(config, schema, config.DispatchNotBeforeField, cancellationToken);
        await EnsureSingleSelectFieldAsync(
            config, schema, config.DispatchAgentField, RequiredAgentOptions, cancellationToken);
        await EnsureTextFieldAsync(config, schema, config.DispatchDetailField, cancellationToken);
        await EnsureSingleSelectFieldAsync(
            config, schema, config.ClaimAgentField, RequiredAgentOptions, cancellationToken);
        await EnsureTextFieldAsync(config, schema, config.ClaimSessionIdField, cancellationToken);
        await EnsureSingleSelectFieldAsync(
            config, schema, config.ClaimantTypeField, RequiredClaimantOptions, cancellationToken);
        await EnsureTextFieldAsync(config, schema, config.ClaimantField, cancellationToken);
        await EnsureTextFieldAsync(config, schema, config.CreationAttemptIdField, cancellationToken);
        await EnsureTextFieldAsync(config, schema, config.ClaimWorkspacePathField, cancellationToken);
    }

    private async Task EnsureSingleSelectFieldAsync(
        TrackerConfig config,
        ProjectSchema schema,
        string fieldName,
        IReadOnlyList<RequiredAgentOption> requiredOptions,
        CancellationToken cancellationToken)
    {
        var field = GetUniqueField(schema, fieldName);
        if (field is null)
        {
            await CreateFieldAsync(
                config,
                schema.ProjectId,
                fieldName,
                "SINGLE_SELECT",
                requiredOptions.Select(required => new ProjectOptionInput(
                    null,
                    required.Name,
                    required.Description,
                    required.Color)).ToArray(),
                cancellationToken);
            return;
        }

        var options = field.Options
            .Select(option => new ProjectOptionInput(
                option.Id,
                option.Name,
                option.Description,
                option.Color))
            .ToList();
        foreach (var required in requiredOptions.Where(required =>
                     !field.Options.Any(option => string.Equals(
                         option.Name,
                         required.Name,
                         StringComparison.OrdinalIgnoreCase))))
        {
            options.Add(new ProjectOptionInput(
                null,
                required.Name,
                required.Description,
                required.Color));
        }

        if (options.Count == field.Options.Count)
        {
            return;
        }

        using var document = await api.GraphQlAsync(
            config.GitHubHost,
            UpdateSingleSelectFieldMutation,
            new { fieldId = field.Id, options },
            cancellationToken);
        ThrowIfGraphQlErrors(document.RootElement);
    }

    private async Task EnsureTextFieldAsync(
        TrackerConfig config,
        ProjectSchema schema,
        string fieldName,
        CancellationToken cancellationToken)
    {
        if (GetUniqueField(schema, fieldName) is null)
        {
            await CreateFieldAsync(
                config,
                schema.ProjectId,
                fieldName,
                "TEXT",
                null,
                cancellationToken);
        }
    }

    private async Task CreateFieldAsync(
        TrackerConfig config,
        string projectId,
        string name,
        string dataType,
        IReadOnlyList<ProjectOptionInput>? options,
        CancellationToken cancellationToken)
    {
        using var document = await api.GraphQlAsync(
            config.GitHubHost,
            CreateFieldMutation,
            new { projectId, name, dataType, options },
            cancellationToken);
        ThrowIfGraphQlErrors(document.RootElement);
    }

    private static ProjectFieldSchema? GetUniqueField(ProjectSchema schema, string name)
    {
        var matches = schema.Fields
            .Where(field => string.Equals(field.Name, name, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length > 1)
        {
            throw new TrackerException(
                "PROJECT_SCHEMA_INVALID",
                $"Project contains multiple fields named '{name}'. Remove or rename duplicates before initialization.",
                5);
        }

        return matches.SingleOrDefault();
    }

    private static void EnsureNoDuplicateOptions(ProjectFieldSchema field)
    {
        var duplicate = field.Options
            .GroupBy(option => option.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new TrackerException(
                "PROJECT_SCHEMA_INVALID",
                $"Project field '{field.Name}' contains duplicate option '{duplicate.Key}'.",
                5);
        }
    }

    private static bool HasAgentContextSchema(ProjectMetadata metadata) =>
        metadata.ClaimAgentFieldId is not null &&
        metadata.ClaimSessionIdFieldId is not null &&
        metadata.ClaimWorkspacePathFieldId is not null &&
        metadata.AgentOptions is not null &&
        RequiredAgentOptions.All(required => metadata.AgentOptions.ContainsKey(required.Name));

    private static bool HasPolicySchema(ProjectMetadata metadata) =>
        metadata.ExecutionPolicyFieldId is not null &&
        metadata.AgentPolicyFieldId is not null &&
        metadata.ExecutionPolicyOptions is not null &&
        metadata.AgentPolicyOptions is not null &&
        RequiredExecutionPolicyOptions.All(
            required => metadata.ExecutionPolicyOptions.ContainsKey(required.Name)) &&
        RequiredAgentPolicyOptions.All(
            required => metadata.AgentPolicyOptions.ContainsKey(required.Name));

    private static bool HasRecoveryPresentationSchema(ProjectMetadata metadata) =>
        metadata.DispatchStateFieldId is not null &&
        metadata.DispatchStateOptionOptions is not null &&
        metadata.DispatchNotBeforeFieldId is not null &&
        metadata.DispatchAgentFieldId is not null &&
        metadata.DispatchAgentOptions is not null &&
        metadata.DispatchDetailFieldId is not null &&
        RequiredDispatchStateOptions.All(
            required => metadata.DispatchStateOptionOptions.ContainsKey(required.Name)) &&
        RequiredAgentOptions.All(
            required => metadata.DispatchAgentOptions.ContainsKey(required.Name));

    private async Task<ProjectMetadata> GetPolicyMetadataAsync(
        TrackerConfig config,
        CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(config, cancellationToken);
        return HasPolicySchema(metadata)
            ? metadata
            : throw NotInitialized(config);
    }

    private async Task<ProjectMetadata> GetCreationMetadataAsync(
        TrackerConfig config,
        CancellationToken cancellationToken)
    {
        var metadata = await GetMetadataAsync(config, cancellationToken);
        if (metadata.CreationAttemptIdFieldId is not null)
        {
            return metadata;
        }

        await cache.InvalidateAsync(CacheKey(config), cancellationToken);
        metadata = await GetMetadataAsync(config, cancellationToken);
        return metadata.CreationAttemptIdFieldId is not null
            ? metadata
            : throw NotInitialized(config);
    }

    private static TrackerException NotInitialized(TrackerConfig config) => new(
        "PROJECT_NOT_INITIALIZED",
        $"Required Project fields, including '{config.ExecutionPolicyField}', " +
        $"'{config.AgentPolicyField}', and '{config.CreationAttemptIdField}', are not initialized. " +
        "Run 'wrighty init'.",
        5);

    private static TrackerException WrongFieldType(string name, string expectedType) => new(
        "PROJECT_SCHEMA_INVALID",
        $"Project field '{name}' exists but is not a {expectedType} field.",
        5);

    private static bool TryParseIssue(
        TrackerConfig config,
        JsonElement node,
        out GitHubProjectItem item)
    {
        item = null!;
        if (!TryGetRepositoryIssue(config, node, out var content))
        {
            return false;
        }

        var fields = ReadProjectFields(config, node);
        var number = content.GetProperty("number").GetInt32();
        var id = new GitHubWorkItemAddressResolver().FromIssueNumber(config, number);
        item = new GitHubProjectItem(
            new GitHubWorkItemAddress(
                config.GitHubHost,
                config.RepositoryOwner,
                config.RepositoryName,
                number),
            new WorkItemSummary(
                id,
                content.GetProperty("title").GetString()!,
                content.GetProperty("url").GetString(),
                fields.Status,
                fields.Priority,
                node.TryGetProperty("isArchived", out var archived) && archived.GetBoolean(),
                AutomaticExecutionAllowed: DecodeExecutionPolicy(fields.ExecutionPolicy),
                AgentPolicy: DecodeAgentPolicy(fields.AgentPolicy)),
            content.GetProperty("id").GetString()!,
            node.GetProperty("id").GetString()!,
            ExecutionPolicyValue: fields.ExecutionPolicy,
            AgentPolicyValue: fields.AgentPolicy);
        return true;
    }

    private static bool TryGetRepositoryIssue(
        TrackerConfig config,
        JsonElement node,
        out JsonElement content)
    {
        if (!node.TryGetProperty("content", out content) ||
            content.ValueKind == JsonValueKind.Null ||
            !content.TryGetProperty("repository", out var repository))
        {
            return false;
        }

        var repositoryName = repository.GetProperty("nameWithOwner").GetString();
        return string.Equals(
            repositoryName, config.Repository, StringComparison.OrdinalIgnoreCase);
    }

    private static ProjectFieldValues ReadProjectFields(
        TrackerConfig config,
        JsonElement node)
    {
        var fields = new ProjectFieldValues(
            ReadNamedField(node, "status"),
            ReadNamedField(node, "priority"),
            ReadNamedField(node, "executionPolicy"),
            ReadNamedField(node, "agentPolicy"));
        if (!node.TryGetProperty("fieldValues", out var additionalFieldValues))
        {
            return fields;
        }

        foreach (var value in additionalFieldValues.GetProperty("nodes").EnumerateArray())
        {
            fields = ApplyAdditionalField(config, value, fields);
        }

        return fields;
    }

    private static string? ReadNamedField(JsonElement node, string propertyName) =>
        node.TryGetProperty(propertyName, out var field) &&
        field.ValueKind != JsonValueKind.Null &&
        field.TryGetProperty("name", out var name)
            ? name.GetString()
            : null;

    private static ProjectFieldValues ApplyAdditionalField(
        TrackerConfig config,
        JsonElement value,
        ProjectFieldValues fields)
    {
        if (!value.TryGetProperty("field", out var field) ||
            !field.TryGetProperty("name", out var fieldNameElement))
        {
            return fields;
        }

        var fieldName = fieldNameElement.GetString();
        var displayValue = ReadAdditionalDisplayValue(value);
        if (string.Equals(fieldName, config.StatusField, StringComparison.OrdinalIgnoreCase))
        {
            return fields with { Status = displayValue };
        }

        return string.Equals(fieldName, config.PriorityField, StringComparison.OrdinalIgnoreCase)
            ? fields with { Priority = displayValue }
            : fields;
    }

    private static string? ReadAdditionalDisplayValue(JsonElement value)
    {
        if (value.TryGetProperty("name", out var name))
        {
            return name.GetString();
        }

        if (value.TryGetProperty("text", out var text))
        {
            return text.GetString();
        }

        return value.TryGetProperty("number", out var number)
            ? number.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : null;
    }

    private static int PriorityRank(string? priority)
    {
        if (priority is null)
        {
            return int.MaxValue;
        }

        var digits = new string(priority.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var rank) ? rank : int.MaxValue - 1;
    }

    private static string CacheKey(TrackerConfig config) =>
        $"{config.GitHubHost}/{config.EffectiveProjectOwner}/{config.ProjectNumber}";

    private static bool IsStaleNodeError(TrackerException exception)
    {
        return exception.Code == "GH_API_ERROR" &&
               (exception.Message.Contains("Could not resolve", StringComparison.OrdinalIgnoreCase) ||
                exception.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
                exception.Message.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    private static void ThrowIfGraphQlErrors(JsonElement root)
    {
        if (!root.TryGetProperty("errors", out var errors) || errors.GetArrayLength() == 0)
        {
            return;
        }

        var messages = errors.EnumerateArray()
            .Select(error => error.GetProperty("message").GetString())
            .Where(message => message is not null);
        throw new TrackerException("GH_API_ERROR", string.Join("; ", messages));
    }

    private sealed record ProjectSchema(
        string ProjectId,
        IReadOnlyList<ProjectFieldSchema> Fields);

    private sealed record ProjectFieldSchema(
        string Id,
        string Name,
        string DataType,
        IReadOnlyList<ProjectOptionSchema> Options);

    private sealed record ProjectOptionSchema(
        string Id,
        string Name,
        string Description,
        string Color);

    private sealed record RequiredAgentOption(
        string Name,
        string Description,
        string Color);

    private sealed record ProjectOptionInput(
        string? Id,
        string Name,
        string Description,
        string Color);

    private static void ValidatePolicyField(
        ProjectFieldSchema? field,
        string fieldName,
        IReadOnlyList<RequiredAgentOption> requiredOptions,
        bool required)
    {
        if (field is null)
        {
            if (required)
            {
                throw NotInitializedForField(fieldName);
            }

            return;
        }

        if (field.DataType != "SINGLE_SELECT")
        {
            throw WrongFieldType(fieldName, "single-select");
        }

        EnsureNoDuplicateOptions(field);
        var missing = requiredOptions
            .Where(requiredOption => !field.Options.Any(option =>
                string.Equals(
                    option.Name.Trim(),
                    requiredOption.Name,
                    StringComparison.OrdinalIgnoreCase)))
            .Select(option => option.Name)
            .ToArray();
        if (required && missing.Length > 0)
        {
            throw new TrackerException(
                "PROJECT_SCHEMA_INVALID",
                $"Project field '{fieldName}' is missing required options: {string.Join(", ", missing)}. " +
                "Run 'wrighty init'.",
                5);
        }
    }

    private static void ValidateOptionalTextField(
        ProjectFieldSchema? field,
        string fieldName)
    {
        if (field is not null && field.DataType != "TEXT")
            throw WrongFieldType(fieldName, "text");
    }

    private static IReadOnlyDictionary<string, string>? OptionsByName(ProjectFieldSchema? field) =>
        field?.Options.ToDictionary(
            option => option.Name.Trim(),
            option => option.Id,
            StringComparer.OrdinalIgnoreCase);

    private static TrackerException NotInitializedForField(string fieldName) => new(
        "PROJECT_NOT_INITIALIZED",
        $"Required Project field '{fieldName}' is not initialized. Run 'wrighty init'.",
        5);

    private static bool DecodeExecutionPolicy(string? value)
    {
        if (value is null ||
            string.Equals(value.Trim(), "Manual only", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(value.Trim(), "Automatic allowed", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        throw InvalidPolicyValue("Wrighty policy - execution", value);
    }

    private static string? DecodeAgentPolicy(string? value)
    {
        if (value is null ||
            string.Equals(value.Trim(), "Repository default", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "claude" => "claude",
            "codex" => "codex",
            "copilot" => "copilot",
            _ => throw InvalidPolicyValue("Wrighty policy - agent", value)
        };
    }

    private static TrackerException InvalidPolicyValue(string fieldName, string value) => new(
        "PROJECT_SCHEMA_INVALID",
        $"Project field '{fieldName}' contains unsupported value '{value}'. " +
        "Automatic execution is disabled until the value is corrected.",
        5);

    private static string CanonicalAgentName(string agentPolicy) =>
        agentPolicy.Trim().ToLowerInvariant() switch
        {
            "claude" => "Claude",
            "codex" => "Codex",
            "copilot" => "Copilot",
            _ => throw new TrackerException(
                "ARGUMENT_INVALID",
                "worker agent must be claude, codex, or copilot.",
                2)
        };

    private static string CanonicalProjectionAgentName(string agent) =>
        agent.Trim().ToLowerInvariant() switch
        {
            "claude" => "Claude",
            "codex" => "Codex",
            "copilot" => "Copilot",
            _ => "Other"
        };

    private static string? DispatchStateOption(string? dispatchState) => dispatchState switch
    {
        DispatchStates.NeedsAttention => "Needs attention",
        DispatchStates.Queued => "Resume queued",
        DispatchStates.RetryScheduled => "Retry scheduled",
        DispatchStates.HandoffQueued => "Handoff queued",
        _ => null
    };

    private static string DispatchDetail(
        GitHubProjectItem item,
        DispatchInfo dispatch)
    {
        var attempt = $"attempt {dispatch.Attempt} of {dispatch.MaxAttempts}";
        var reason = dispatch.Reason.Trim().TrimEnd('.');
        if (!item.Summary.AutomaticExecutionAllowed)
            return $"{reason}; automatic execution disabled; {attempt}";

        if (item.Summary.AgentPolicy is { } policyAgent &&
            !string.Equals(policyAgent, dispatch.Agent, StringComparison.OrdinalIgnoreCase))
        {
            return $"{reason}; agent policy changed; {attempt}";
        }

        return $"{reason}; {attempt}";
    }

    private sealed record ProjectFieldValues(
        string? Status,
        string? Priority,
        string? ExecutionPolicy,
        string? AgentPolicy);
}

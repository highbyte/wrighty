using System.Collections.Concurrent;
using System.Globalization;
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
    private const string RestApiVersion = "2026-03-10";
    private readonly ConcurrentDictionary<string, RestProjectOwner> restOwners = new();
    private readonly ConcurrentDictionary<string, byte> restUnavailable = new();

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
          $query: String!,
          $archivedStates: [ProjectV2ItemArchivedState!],
          $statusField: String!,
          $priorityField: String!,
          $executionPolicyField: String!,
          $agentPolicyField: String!,
          $contextApprovalField: String!
        ) {
          node(id: $projectId) {
            ... on ProjectV2 {
              items(first: 100, after: $cursor, query: $query, archivedStates: $archivedStates) {
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
                  contextApproval: fieldValueByName(name: $contextApprovalField) {
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
          $query: String!,
          $creationField: String!,
          $statusField: String!,
          $priorityField: String!,
          $executionPolicyField: String!,
          $agentPolicyField: String!,
          $contextApprovalField: String!
        ) {
          node(id: $projectId) {
            ... on ProjectV2 {
              items(
                first: 100,
                after: $cursor,
                query: $query,
                archivedStates: [ARCHIVED, NOT_ARCHIVED]
              ) {
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
                  contextApproval: fieldValueByName(name: $contextApprovalField) {
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
    private const string OtherAgentOption = "Other";
    private const string NodeIdProperty = "node_id";
    private const string ProjectNotInitializedCode = "PROJECT_NOT_INITIALIZED";

    private static readonly RequiredAgentOption[] RequiredAgentOptions =
    [
        new("Codex", "OpenAI Codex agent", "GREEN"),
        new("Claude", "Anthropic Claude Code agent", OrangeOptionColor),
        new("Copilot", "GitHub Copilot agent", "BLUE"),
        new(OtherAgentOption, "Another agent runtime", "GRAY")
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
    private static readonly RequiredAgentOption[] RequiredContextApprovalOptions =
        GitHubContextApprovalReader.Options
            .Select(option => new RequiredAgentOption(
                option.Name,
                option.Description,
                option.Color))
            .ToArray();
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
        if (archiveScope == ArchiveScope.Active)
        {
            var restItems = await TryListRestAsync(
                config, metadata, status, limit, archiveScope, cancellationToken);
            if (restItems is not null)
            {
                return restItems;
            }
        }

        var items = new List<GitHubProjectItem>();
        string? cursor = null;
        do
        {
            using var document = await GetItemsPageAsync(
                config, metadata.ProjectId, cursor, status, archiveScope, cancellationToken);
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
        string? status,
        ArchiveScope archiveScope,
        CancellationToken cancellationToken) => api.GraphQlAsync(
        config.GitHubHost,
        ListQuery,
        new
        {
            projectId,
            cursor,
            query = ProjectItemQuery(config, status),
            statusField = config.StatusField,
            priorityField = config.PriorityField,
            executionPolicyField = config.ExecutionPolicyField,
            agentPolicyField = config.AgentPolicyField,
            contextApprovalField = config.ContextApprovalField,
            archivedStates = ArchivedStates(archiveScope)
        },
        cancellationToken);

    private static string[] ArchivedStates(ArchiveScope archiveScope) => archiveScope switch
    {
        ArchiveScope.Active => ["NOT_ARCHIVED"],
        ArchiveScope.Archived => ["ARCHIVED"],
        _ => ["ARCHIVED", "NOT_ARCHIVED"]
    };

    private static string ProjectItemQuery(
        TrackerConfig config,
        string? status)
    {
        var terms = new List<string>
        {
            $"repo:{config.Repository}",
            "is:issue"
        };
        if (!string.IsNullOrWhiteSpace(status) &&
            string.Equals(config.StatusField, "Status", StringComparison.OrdinalIgnoreCase))
        {
            terms.Add($"status:\"{EscapeProjectFilterValue(status)}\"");
        }

        return string.Join(' ', terms);
    }

    private static string EscapeProjectFilterValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);

    private async Task<IReadOnlyList<GitHubProjectItem>?> TryListRestAsync(
        TrackerConfig config,
        ProjectMetadata metadata,
        string? status,
        int? limit,
        ArchiveScope archiveScope,
        CancellationToken cancellationToken)
    {
        var owner = await TryGetRestOwnerAsync(config, cancellationToken);
        if (owner is null || metadata.RestFieldIds is null)
        {
            return null;
        }

        try
        {
            var fieldIds = RestListFieldNames(config)
                .Select(name => TryGetRestFieldId(metadata, name))
                .Where(id => id.HasValue)
                .Select(id => id!.Value.ToString(CultureInfo.InvariantCulture));
            var query = Uri.EscapeDataString(ProjectItemQuery(config, status));
            var endpoint = $"{owner.Path}/items?per_page=100&q={query}";
            var fields = string.Join(',', fieldIds);
            if (fields.Length > 0)
            {
                endpoint += $"&fields={fields}";
            }

            using var document = await api.GetVersionedPaginatedAsync(
                config.GitHubHost,
                endpoint,
                RestApiVersion,
                cancellationToken);
            var items = new List<GitHubProjectItem>();
            foreach (var node in EnumerateRestItems(document.RootElement))
            {
                if (TryParseRestIssue(config, node, out var item) &&
                    MatchesStatus(item, status) &&
                    MatchesArchiveScope(item, archiveScope))
                {
                    items.Add(item);
                }
            }

            return items
                .OrderBy(item => PriorityRank(item.Priority))
                .ThenBy(item => item.Number)
                .Take(limit ?? int.MaxValue)
                .ToArray();
        }
        catch (Exception exception) when (IsRestFallbackError(exception))
        {
            DisableRest(config);
            return null;
        }
    }

    private static IEnumerable<string> RestListFieldNames(TrackerConfig config)
    {
        yield return config.StatusField;
        yield return config.PriorityField;
        yield return config.ExecutionPolicyField;
        yield return config.AgentPolicyField;
        yield return config.ContextApprovalField;
        yield return config.CreationAttemptIdField;
    }

    private static IEnumerable<JsonElement> EnumerateRestItems(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("GitHub REST Project items response was not an array.");
        }

        foreach (var element in root.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    yield return item;
                }
            }
            else
            {
                yield return element;
            }
        }
    }

    private static bool TryParseRestIssue(
        TrackerConfig config,
        JsonElement node,
        out GitHubProjectItem item)
    {
        item = null!;
        if (!node.TryGetProperty("content_type", out var contentType) ||
            !string.Equals(contentType.GetString(), "Issue", StringComparison.OrdinalIgnoreCase) ||
            !node.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("repository", out var repository) ||
            !repository.TryGetProperty("full_name", out var fullName) ||
            !string.Equals(fullName.GetString(), config.Repository, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fields = ReadRestProjectFields(config, node);
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
                content.TryGetProperty("html_url", out var url) ? url.GetString() : null,
                fields.Status,
                fields.Priority,
                node.TryGetProperty("archived_at", out var archived) &&
                archived.ValueKind != JsonValueKind.Null,
                AutomaticExecutionAllowed: DecodeExecutionPolicy(fields.ExecutionPolicy),
                AgentPolicy: DecodeAgentPolicy(fields.AgentPolicy)),
            content.GetProperty(NodeIdProperty).GetString()!,
            node.GetProperty(NodeIdProperty).GetString()!,
            fields.CreationAttemptId,
            fields.ExecutionPolicy,
            fields.AgentPolicy,
            node.GetProperty("id").GetInt64(),
            fields.ContextApproval);
        return true;
    }

    private static RestProjectFieldValues ReadRestProjectFields(
        TrackerConfig config,
        JsonElement node)
    {
        var values = new RestProjectFieldValues();
        if (!node.TryGetProperty("fields", out var fields) ||
            fields.ValueKind != JsonValueKind.Array)
        {
            return values;
        }

        foreach (var field in fields.EnumerateArray())
        {
            if (!field.TryGetProperty("name", out var nameElement))
            {
                continue;
            }

            var name = nameElement.GetString();
            var value = ReadRestFieldValue(field);
            if (string.Equals(name, config.StatusField, StringComparison.OrdinalIgnoreCase))
            {
                values = values with { Status = value };
            }
            else if (string.Equals(name, config.PriorityField, StringComparison.OrdinalIgnoreCase))
            {
                values = values with { Priority = value };
            }
            else if (string.Equals(name, config.ExecutionPolicyField, StringComparison.OrdinalIgnoreCase))
            {
                values = values with { ExecutionPolicy = value };
            }
            else if (string.Equals(name, config.AgentPolicyField, StringComparison.OrdinalIgnoreCase))
            {
                values = values with { AgentPolicy = value };
            }
            else if (string.Equals(name, config.ContextApprovalField, StringComparison.OrdinalIgnoreCase))
            {
                values = values with { ContextApproval = value };
            }
            else if (string.Equals(name, config.CreationAttemptIdField, StringComparison.OrdinalIgnoreCase))
            {
                values = values with { CreationAttemptId = value };
            }
        }

        return values;
    }

    private static string? ReadRestFieldValue(JsonElement field)
    {
        if (!field.TryGetProperty("value", out var value) ||
            value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble().ToString(CultureInfo.InvariantCulture);
        }
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("name", out var name))
        {
            if (name.ValueKind == JsonValueKind.String)
            {
                return name.GetString();
            }
            if (name.ValueKind == JsonValueKind.Object &&
                name.TryGetProperty("raw", out var raw))
            {
                return raw.GetString();
            }
        }
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("raw", out var directRaw) &&
            directRaw.ValueKind == JsonValueKind.String)
        {
            return directRaw.GetString();
        }

        return null;
    }

    private static bool MatchesArchiveScope(
        GitHubProjectItem item,
        ArchiveScope archiveScope) => archiveScope switch
        {
            ArchiveScope.Active => !item.Summary.Archived,
            ArchiveScope.Archived => item.Summary.Archived,
            _ => true
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
        var restItems = await TryListRestAsync(
            config, metadata, null, null, ArchiveScope.Active, cancellationToken);
        if (restItems is not null)
        {
            var activeMatches = FindCreationMatches(restItems, creationAttemptId);
            if (activeMatches.Length > 0)
            {
                return activeMatches;
            }
        }

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
                    query = ProjectItemQuery(config, null),
                    creationField = config.CreationAttemptIdField,
                    statusField = config.StatusField,
                    priorityField = config.PriorityField,
                    executionPolicyField = config.ExecutionPolicyField,
                    agentPolicyField = config.AgentPolicyField,
                    contextApprovalField = config.ContextApprovalField
                },
                cancellationToken);
            ThrowIfGraphQlErrors(document.RootElement);
            var connection = document.RootElement.GetProperty("data")
                .GetProperty("node")
                .GetProperty("items");
            AddCreationMatches(config, connection, creationAttemptId, matches);
            cursor = GetNextCursor(connection);
        }
        while (cursor is not null);

        return matches;
    }

    private static GitHubProjectItem[] FindCreationMatches(
        IEnumerable<GitHubProjectItem> items,
        string creationAttemptId) =>
        items
            .Where(item => string.Equals(
                item.CreationAttemptId,
                creationAttemptId,
                StringComparison.Ordinal))
            .ToArray();

    private static void AddCreationMatches(
        TrackerConfig config,
        JsonElement connection,
        string creationAttemptId,
        ICollection<GitHubProjectItem> matches)
    {
        foreach (var node in connection.GetProperty("nodes").EnumerateArray())
        {
            var value = ReadCreationAttempt(node);
            if (string.Equals(value, creationAttemptId, StringComparison.Ordinal) &&
                TryParseIssue(config, node, out var item))
            {
                matches.Add(item with { CreationAttemptId = value });
            }
        }
    }

    private static string? ReadCreationAttempt(JsonElement node) =>
        node.TryGetProperty("creationAttempt", out var creation) &&
        creation.ValueKind != JsonValueKind.Null &&
        creation.TryGetProperty("text", out var text)
            ? text.GetString()
            : null;

    public async Task UpdateCreationAttemptIdAsync(
        TrackerConfig config,
        GitHubProjectItem item,
        string creationAttemptId,
        CancellationToken cancellationToken)
    {
        var metadata = await GetCreationMetadataAsync(config, cancellationToken);
        if (await TryUpdateRestFieldsAsync(
                config,
                metadata,
                item,
                [new(config.CreationAttemptIdField, creationAttemptId)],
                cancellationToken))
        {
            return;
        }

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
        var metadata = await GetMetadataAsync(config, cancellationToken);
        if (!HasValidCreateFields(metadata, status, priority))
        {
            await cache.InvalidateAsync(CacheKey(config), cancellationToken);
            metadata = await GetMetadataAsync(config, cancellationToken);
        }
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
        var metadata = await GetMetadataAsync(config, cancellationToken);
        if (!HasValidUpdateFields(metadata, status, priority, clearPriority))
        {
            await cache.InvalidateAsync(CacheKey(config), cancellationToken);
            metadata = await GetMetadataAsync(config, cancellationToken);
        }
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
        var metadata = await GetMetadataAsync(config, cancellationToken);
        if (!HasPolicySchema(metadata))
        {
            await cache.InvalidateAsync(CacheKey(config), cancellationToken);
            metadata = await GetMetadataAsync(config, cancellationToken);
        }
        if (!HasPolicySchema(metadata))
        {
            throw NotInitialized(config);
        }
        if (agentPolicy is not null)
        {
            _ = CanonicalAgentName(agentPolicy);
        }
    }

    private static bool HasValidCreateFields(
        ProjectMetadata metadata,
        string status,
        string? priority) =>
        metadata.CreationAttemptIdFieldId is not null &&
        metadata.StatusOptions.ContainsKey(status) &&
        (priority is null ||
         metadata.PriorityFieldId is not null &&
         metadata.PriorityOptions?.ContainsKey(priority) == true);

    private static bool HasValidUpdateFields(
        ProjectMetadata metadata,
        string? status,
        string? priority,
        bool clearPriority) =>
        (status is null || metadata.StatusOptions.ContainsKey(status)) &&
        (!clearPriority && priority is null || metadata.PriorityFieldId is not null) &&
        (priority is null || metadata.PriorityOptions?.ContainsKey(priority) == true);

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

    public async Task<ProjectItemReference> AddIssueAsync(
        TrackerConfig config,
        string issueNodeId,
        long? issueDatabaseId,
        CancellationToken cancellationToken)
    {
        if (issueDatabaseId.HasValue)
        {
            var owner = await TryGetRestOwnerAsync(config, cancellationToken);
            if (owner is not null)
            {
                try
                {
                    using var document = await api.SendVersionedJsonAsync(
                        config.GitHubHost,
                        "POST",
                        $"{owner.Path}/items",
                        RestApiVersion,
                        new { type = "Issue", id = issueDatabaseId.Value },
                        cancellationToken);
                    var value = document.RootElement.GetProperty("value");
                    return new ProjectItemReference(
                        value.GetProperty(NodeIdProperty).GetString()!,
                        value.GetProperty("id").GetInt64());
                }
                catch (Exception exception) when (IsRestFallbackError(exception))
                {
                    DisableRest(config);
                }
            }
        }

        return new ProjectItemReference(
            await AddIssueAsync(config, issueNodeId, cancellationToken),
            null);
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
        var updates = new List<RestFieldUpdate>
        {
            new(
                config.DispatchStateField,
                dispatchOption is null
                    ? null
                    : metadata.DispatchStateOptionOptions![dispatchOption])
        };
        if (dispatchState is not (DispatchStates.RetryScheduled or DispatchStates.HandoffQueued))
        {
            updates.Add(new(config.DispatchNotBeforeField, null));
            updates.Add(new(config.DispatchAgentField, null));
            updates.Add(new(config.DispatchDetailField, null));
        }
        if (await TryUpdateRestFieldsAsync(
                config, metadata, item, updates, cancellationToken))
        {
            return;
        }

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
        var targetAgent = dispatch.Agent ?? dispatch.SessionAgent;
        var targetAgentOption = string.IsNullOrWhiteSpace(targetAgent)
            ? null
            : metadata.DispatchAgentOptions![CanonicalProjectionAgentName(targetAgent)];
        if (await TryUpdateRestFieldsAsync(
                config,
                metadata,
                item,
                [
                    new(
                        config.DispatchStateField,
                        metadata.DispatchStateOptionOptions![dispatchOption]),
                    new(
                        config.DispatchNotBeforeField,
                        dispatch.State == DispatchStates.RetryScheduled
                            ? dispatch.NotBefore.ToString("O")
                            : null),
                    new(config.DispatchAgentField, targetAgentOption),
                    new(config.DispatchDetailField, DispatchDetail(item, dispatch))
                ],
                cancellationToken))
        {
            return;
        }

        await UpdateSingleSelectValueAsync(
            config, metadata.ProjectId, item.ProjectItemId,
            metadata.DispatchStateFieldId!,
            metadata.DispatchStateOptionOptions[dispatchOption],
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

        if (await TryUpdateRestFieldsAsync(
                config,
                metadata,
                item,
                [
                    new(config.ExecutionPolicyField, executionOptionId),
                    new(config.AgentPolicyField, agentPolicyOptionId)
                ],
                cancellationToken))
        {
            return;
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

    private async Task<bool> TryUpdateRestFieldsAsync(
        TrackerConfig config,
        ProjectMetadata metadata,
        GitHubProjectItem item,
        IReadOnlyList<RestFieldUpdate> updates,
        CancellationToken cancellationToken)
    {
        if (!item.ProjectItemDatabaseId.HasValue ||
            metadata.RestFieldIds is null ||
            updates.Count == 0)
        {
            return false;
        }

        var fields = new List<RestFieldValue>(updates.Count);
        foreach (var update in updates)
        {
            var fieldId = TryGetRestFieldId(metadata, update.FieldName);
            if (!fieldId.HasValue)
            {
                return false;
            }
            fields.Add(new RestFieldValue(fieldId.Value, update.Value));
        }

        var owner = await TryGetRestOwnerAsync(config, cancellationToken);
        if (owner is null)
        {
            return false;
        }

        try
        {
            using var document = await api.SendVersionedJsonAsync(
                config.GitHubHost,
                "PATCH",
                $"{owner.Path}/items/{item.ProjectItemDatabaseId.Value}",
                RestApiVersion,
                new { fields },
                cancellationToken);
            return true;
        }
        catch (Exception exception) when (IsRestFallbackError(exception))
        {
            DisableRest(config);
            return false;
        }
    }

    private static long? TryGetRestFieldId(
        ProjectMetadata metadata,
        string fieldName)
    {
        if (metadata.RestFieldIds is null)
        {
            return null;
        }
        if (metadata.RestFieldIds.TryGetValue(fieldName, out var exact))
        {
            return exact;
        }

        foreach (var pair in metadata.RestFieldIds)
        {
            if (string.Equals(pair.Key, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
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

        if (await TryUpdateRestFieldsAsync(
                config,
                metadata,
                item,
                [new(config.PriorityField, null)],
                cancellationToken))
        {
            return;
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

        if (await TryUpdateRestFieldsAsync(
                config,
                metadata,
                item,
                [new(config.PriorityField, optionId)],
                cancellationToken))
        {
            return;
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
        var metadata = await GetProjectionMetadataAsync(config, cancellationToken);
        if (await TryUpdateRestFieldsAsync(
                config,
                metadata,
                item,
                [
                    new(config.ClaimAgentField, ResolveAgentOptionId(metadata, agentType)),
                    new(config.ClaimSessionIdField, NullIfWhiteSpace(sessionId))
                ],
                cancellationToken))
        {
            return;
        }

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
        var metadata = await GetProjectionMetadataAsync(config, cancellationToken);
        if (metadata.ClaimantTypeFieldId is null || metadata.ClaimantFieldId is null ||
            metadata.ClaimantKindOptions is null)
        {
            throw NotInitialized(config);
        }

        var claimantKindOption = ResolveClaimantKindOptionId(metadata, claimantKind);
        var claimantDisplay = ClaimantDisplay(claimantId);
        if (await TryUpdateRestFieldsAsync(
                config,
                metadata,
                item,
                [
                    new(config.ClaimAgentField, ResolveAgentOptionId(metadata, agentType)),
                    new(config.ClaimSessionIdField, NullIfWhiteSpace(sessionId)),
                    new(config.ClaimantTypeField, claimantKindOption),
                    new(config.ClaimantField, claimantDisplay)
                ],
                cancellationToken))
        {
            return;
        }

        await UpdateAgentContextAsync(config, item, agentType, sessionId, cancellationToken);
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

    private static string? ResolveAgentOptionId(
        ProjectMetadata metadata,
        string? agentType)
    {
        if (string.IsNullOrWhiteSpace(agentType))
        {
            return null;
        }

        var optionName = agentType switch
        {
            "codex" => "Codex",
            "claude" => "Claude",
            "copilot" => "Copilot",
            _ => OtherAgentOption
        };
        return metadata.AgentOptions!.TryGetValue(optionName, out var optionId)
            ? optionId
            : throw new TrackerException(
                ProjectNotInitializedCode,
                "The Project agent projection options are not initialized.",
                5);
    }

    private static string? ResolveClaimantKindOptionId(
        ProjectMetadata metadata,
        string? claimantKind)
    {
        if (string.IsNullOrWhiteSpace(claimantKind))
        {
            return null;
        }

        var name = char.ToUpperInvariant(claimantKind[0]) +
                   claimantKind[1..].ToLowerInvariant();
        return metadata.ClaimantKindOptions!.TryGetValue(name, out var optionId)
            ? optionId
            : throw new TrackerException(
                ProjectNotInitializedCode,
                "The Project claimant projection options are not initialized.",
                5);
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? ClaimantDisplay(string? claimantId)
    {
        if (string.IsNullOrWhiteSpace(claimantId))
        {
            return null;
        }

        return claimantId.Length <= 24 ? claimantId : $"{claimantId[..24]}…";
    }

    public async Task UpdateWorkspacePathAsync(TrackerConfig config, GitHubProjectItem item,
        string? workspacePath, CancellationToken cancellationToken)
    {
        var metadata = await GetProjectionMetadataAsync(config, cancellationToken);
        if (metadata.ClaimWorkspacePathFieldId is null) throw NotInitialized(config);
        if (await TryUpdateRestFieldsAsync(
                config,
                metadata,
                item,
                [new(config.ClaimWorkspacePathField, NullIfWhiteSpace(workspacePath))],
                cancellationToken))
        {
            return;
        }
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
                _ => OtherAgentOption
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

        if (await TryUpdateRestFieldsAsync(
                config,
                metadata,
                item,
                [new(config.StatusField, optionId)],
                cancellationToken))
        {
            return;
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
            if (cached.RestFieldIds is null && !restUnavailable.ContainsKey(key))
            {
                var restSchema = await TryDiscoverRestSchemaAsync(config, cancellationToken);
                if (restSchema is not null)
                {
                    cached = BuildMetadata(
                        config,
                        restSchema,
                        requireAgentContext: false,
                        requirePolicy: false);
                    await cache.PutAsync(key, cached, cancellationToken);
                }
            }
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
        var cached = await GetMetadataAsync(config, cancellationToken);
        if (HasAgentContextSchema(cached))
        {
            return cached;
        }

        await cache.InvalidateAsync(key, cancellationToken);

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
        var restSchema = await TryDiscoverRestSchemaAsync(config, cancellationToken);
        return restSchema ?? await DiscoverGraphQlSchemaAsync(config, cancellationToken);
    }

    private async Task<ProjectSchema?> TryDiscoverRestSchemaAsync(
        TrackerConfig config,
        CancellationToken cancellationToken)
    {
        var owner = await TryGetRestOwnerAsync(config, cancellationToken);
        if (owner is null)
        {
            return null;
        }

        try
        {
            using var document = await api.GetVersionedPaginatedAsync(
                config.GitHubHost,
                $"{owner.Path}/fields?per_page=100",
                RestApiVersion,
                cancellationToken);
            var fields = new List<ProjectFieldSchema>();
            foreach (var field in EnumerateRestItems(document.RootElement))
            {
                if (TryParseRestField(field) is { } parsed)
                {
                    fields.Add(parsed);
                }
            }

            return new ProjectSchema(owner.ProjectNodeId, fields);
        }
        catch (Exception exception) when (IsRestFallbackError(exception))
        {
            DisableRest(config);
            return null;
        }
    }

    private static ProjectFieldSchema? TryParseRestField(JsonElement field)
    {
        if (!field.TryGetProperty("id", out var databaseId) ||
            !field.TryGetProperty(NodeIdProperty, out var nodeId) ||
            !field.TryGetProperty("name", out var name) ||
            !field.TryGetProperty("data_type", out var dataType))
        {
            return null;
        }

        return new ProjectFieldSchema(
            nodeId.GetString()!,
            name.GetString()!,
            dataType.GetString()!.ToUpperInvariant(),
            ReadRestOptions(field),
            databaseId.GetInt64());
    }

    private static IReadOnlyList<ProjectOptionSchema> ReadRestOptions(JsonElement field)
    {
        if (!field.TryGetProperty("options", out var optionElements) ||
            optionElements.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return optionElements.EnumerateArray()
            .Select(ReadRestOption)
            .ToArray();
    }

    private static ProjectOptionSchema ReadRestOption(JsonElement option) =>
        new(
            option.GetProperty("id").GetString()!,
            ReadRestRawText(option.GetProperty("name")) ?? string.Empty,
            option.TryGetProperty("description", out var description)
                ? ReadRestRawText(description) ?? string.Empty
                : string.Empty,
            option.TryGetProperty("color", out var color)
                ? color.GetString() ?? "GRAY"
                : "GRAY");

    private async Task<RestProjectOwner?> TryGetRestOwnerAsync(
        TrackerConfig config,
        CancellationToken cancellationToken)
    {
        var key = CacheKey(config);
        if (restUnavailable.ContainsKey(key))
        {
            return null;
        }
        if (restOwners.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var escapedOwner = Uri.EscapeDataString(config.EffectiveProjectOwner);
        foreach (var prefix in new[] { "users", "orgs" })
        {
            var path = $"{prefix}/{escapedOwner}/projectsV2/{config.ProjectNumber}";
            try
            {
                using var document = await api.GetVersionedAsync(
                    config.GitHubHost,
                    path,
                    RestApiVersion,
                    cancellationToken);
                var projectNodeId = document.RootElement.GetProperty(NodeIdProperty).GetString();
                if (string.IsNullOrWhiteSpace(projectNodeId))
                {
                    throw new JsonException("GitHub REST Project response omitted node_id.");
                }

                var owner = new RestProjectOwner(path, projectNodeId);
                restOwners[key] = owner;
                return owner;
            }
            catch (Exception exception) when (IsRestFallbackError(exception))
            {
                // A Project owner can be either a user or an organization. Try the other route.
            }
        }

        DisableRest(config);
        return null;
    }

    private void DisableRest(TrackerConfig config)
    {
        var key = CacheKey(config);
        restUnavailable[key] = 0;
        restOwners.TryRemove(key, out _);
    }

    private static bool IsRestFallbackError(Exception exception) =>
        exception is TrackerException or JsonException or InvalidOperationException or
            KeyNotFoundException or FormatException;

    private static string? ReadRestRawText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty("raw", out var raw)
            ? raw.GetString()
            : null;
    }

    private async Task<ProjectSchema> DiscoverGraphQlSchemaAsync(
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
            DispatchDetailFieldId = workerStatus?.Id,
            RestFieldIds = schema.Fields
                .Where(field => field.DatabaseId.HasValue)
                .ToDictionary(
                    field => field.Name,
                    field => field.DatabaseId!.Value,
                    StringComparer.OrdinalIgnoreCase)
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
        var contextApproval = GetUniqueField(schema, config.ContextApprovalField);
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
            contextApproval,
            config.ContextApprovalField,
            RequiredContextApprovalOptions);
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
            config, schema, config.ContextApprovalField, RequiredContextApprovalOptions, cancellationToken);
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
        ProjectNotInitializedCode,
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
            AgentPolicyValue: fields.AgentPolicy,
            ContextApprovalValue: fields.ContextApproval);
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
            ReadNamedField(node, "agentPolicy"),
            ReadNamedField(node, "contextApproval"));
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
        IReadOnlyList<ProjectOptionSchema> Options,
        long? DatabaseId = null);

    private sealed record RestProjectOwner(
        string Path,
        string ProjectNodeId);

    private sealed record RestProjectFieldValues(
        string? Status = null,
        string? Priority = null,
        string? ExecutionPolicy = null,
        string? AgentPolicy = null,
        string? ContextApproval = null,
        string? CreationAttemptId = null);

    private sealed record RestFieldUpdate(
        string FieldName,
        object? Value);

    private sealed record RestFieldValue(
        long Id,
        object? Value);

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
        ProjectNotInitializedCode,
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
            _ => OtherAgentOption
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
        string? AgentPolicy,
        string? ContextApproval);
}

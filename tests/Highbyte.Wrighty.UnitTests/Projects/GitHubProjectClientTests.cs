using System.Text.Json;
using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Projects;

namespace Highbyte.Wrighty.UnitTests.Projects;

public sealed class GitHubProjectClientTests
{
    private static readonly TrackerConfig Config = new()
    {
        Repository = "owner/repo",
        ProjectNumber = 1
    };

    [Fact]
    public async Task ListAsync_discovers_ids_filters_repository_and_orders_by_priority()
    {
        var process = new QueueGhProcess(
            DiscoveryResponse,
            ListResponse);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());
        var config = new TrackerConfig
        {
            Repository = "owner/repo",
            ProjectNumber = 1
        };

        var items = await client.ListAsync(config, "Todo", null, CancellationToken.None);

        Assert.Equal([2, 1], items.Select(item => item.Number));
        Assert.All(items, item => Assert.Equal("owner", item.Address.Owner));
        Assert.Equal(2, process.Calls.Count);
        Assert.Contains("fieldValueByName", process.Calls[1].StandardInput);
        Assert.DoesNotContain("fieldValues(first: 50)", process.Calls[1].StandardInput);
        Assert.Contains("query: $query", process.Calls[1].StandardInput);
        Assert.Contains("repo:owner/repo", process.Calls[1].StandardInput);
        Assert.Contains("status:\\u0022Todo\\u0022", process.Calls[1].StandardInput);
    }

    [Fact]
    public async Task ListAsync_uses_rest_projects_without_graphql()
    {
        var process = new RestQueueGhProcess(
            RestProjectResponse,
            RestFieldsResponse,
            RestItemsResponse);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var item = Assert.Single(await client.ListAsync(
            Config,
            "Todo",
            null,
            CancellationToken.None));

        Assert.Equal(42, item.Number);
        Assert.Equal("P1", item.Priority);
        Assert.True(item.Summary.AutomaticExecutionAllowed);
        Assert.Equal("codex", item.Summary.AgentPolicy);
        Assert.Equal("Approved", item.ContextApprovalValue);
        Assert.Equal(7001, item.ProjectItemDatabaseId);
        Assert.Equal(3, process.Calls.Count);
        Assert.All(process.Calls, call => Assert.DoesNotContain("graphql", call.Arguments));
        Assert.Contains("q=repo%3Aowner%2Frepo%20is%3Aissue", process.Calls[2].Arguments[^1]);
        Assert.Contains("fields=101,102,103,104,106,105", process.Calls[2].Arguments[^1]);
    }

    [Fact]
    public async Task ListAsync_reads_flat_rest_pages_and_scalar_field_values()
    {
        var process = new RestQueueGhProcess(
            RestProjectResponse,
            RestFieldsResponse,
            RestScalarItemsResponse);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var item = Assert.Single(await client.ListAsync(
            Config,
            "Todo",
            null,
            CancellationToken.None));

        Assert.Equal("1", item.Priority);
        Assert.True(item.Summary.AutomaticExecutionAllowed);
        Assert.Equal("codex", item.Summary.AgentPolicy);
        Assert.Null(item.CreationAttemptId);
    }

    [Fact]
    public async Task ListAsync_falls_back_to_graphql_when_rest_items_are_malformed()
    {
        var process = new RestQueueGhProcess(
            RestProjectResponse,
            RestFieldsResponse,
            "{}",
            ListResponse);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var items = await client.ListAsync(
            Config,
            "Todo",
            null,
            CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Contains("graphql", process.Calls[3].Arguments);
    }

    [Fact]
    public async Task FindByCreationAttemptIdAsync_uses_rest_projects_without_graphql()
    {
        var process = new RestQueueGhProcess(
            RestProjectResponse,
            RestFieldsResponse,
            RestItemsResponse);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var item = Assert.Single(await client.FindByCreationAttemptIdAsync(
            Config,
            "attempt-1",
            CancellationToken.None));

        Assert.Equal(42, item.Number);
        Assert.Equal("attempt-1", item.CreationAttemptId);
        Assert.Equal(3, process.Calls.Count);
        Assert.All(process.Calls, call => Assert.DoesNotContain("graphql", call.Arguments));
        Assert.Contains("q=repo%3Aowner%2Frepo%20is%3Aissue", process.Calls[2].Arguments[^1]);
        Assert.Contains("fields=101,102,103,104,106,105", process.Calls[2].Arguments[^1]);
    }

    [Fact]
    public async Task Claimant_projection_batches_rest_field_updates()
    {
        var process = new RestQueueGhProcess(RestProjectResponse, "{}");
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata() with
            {
                RestFieldIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                {
                    [Config.ClaimAgentField] = 201,
                    [Config.ClaimSessionIdField] = 202,
                    [Config.ClaimantTypeField] = 203,
                    [Config.ClaimantField] = 204
                }
            },
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);

        await client.UpdateClaimantProjectionAsync(
            Config,
            ProjectItem() with { ProjectItemDatabaseId = 7001 },
            "agent",
            "worker-1",
            "codex",
            "session-1",
            CancellationToken.None);

        Assert.Equal(2, process.Calls.Count);
        var patch = process.Calls[1];
        Assert.Contains("PATCH", patch.Arguments);
        Assert.Equal("users/owner/projectsV2/1/items/7001", patch.Arguments[^1]);
        Assert.Contains("\"id\":201", patch.StandardInput);
        Assert.Contains("\"id\":202", patch.StandardInput);
        Assert.Contains("\"id\":203", patch.StandardInput);
        Assert.Contains("\"id\":204", patch.StandardInput);
        Assert.Contains("\"value\":\"CODEX\"", patch.StandardInput);
        Assert.Contains("\"value\":\"session-1\"", patch.StandardInput);
        Assert.Contains("\"value\":\"AGENT\"", patch.StandardInput);
        Assert.Contains("\"value\":\"worker-1\"", patch.StandardInput);
    }

    [Fact]
    public async Task Create_and_policy_validation_share_rest_schema_discovery()
    {
        var process = new RestQueueGhProcess(
            RestProjectResponse,
            RestFieldsResponse);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        await client.ValidateCreateFieldsAsync(
            Config,
            "Todo",
            "P1",
            CancellationToken.None);
        await client.ValidatePolicyAsync(
            Config,
            true,
            "codex",
            CancellationToken.None);

        Assert.Equal(2, process.Calls.Count);
        Assert.All(process.Calls, call => Assert.DoesNotContain("graphql", call.Arguments));
    }

    [Fact]
    public async Task Create_names_the_priority_field_when_the_field_itself_is_missing()
    {
        // Collapsed into one message, a Project with no priority field told the operator that
        // option 'P1' was not found — sending them to inspect a priority string and an option
        // list when neither existed to be wrong. This is exactly what a board initialized before
        // the priority field was provisioned reports.
        var withoutPriority = RestFieldsResponse.Replace(
            "\"name\": \"Priority\"",
            "\"name\": \"Unrelated field\"",
            StringComparison.Ordinal);
        // Resolution failure invalidates the cache and re-fetches before throwing, so the
        // schema is served twice.
        var process = new RestQueueGhProcess(
            RestProjectResponse, withoutPriority, withoutPriority);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(
            () => client.ValidateCreateFieldsAsync(Config, "Todo", "P1", CancellationToken.None));

        Assert.Equal("PRIORITY_NOT_FOUND", exception.Code);
        Assert.Equal("Project priority field 'Priority' was not found.", exception.Message);
    }

    [Fact]
    public async Task Create_names_the_option_when_the_field_exists_but_the_value_does_not()
    {
        // The other half of the split: with the field present, the priority string really is the
        // problem, and the message that names it is the useful one.
        var process = new RestQueueGhProcess(
            RestProjectResponse, RestFieldsResponse, RestFieldsResponse);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(
            () => client.ValidateCreateFieldsAsync(Config, "Todo", "P9", CancellationToken.None));

        Assert.Equal("PRIORITY_NOT_FOUND", exception.Code);
        Assert.Equal("Project priority option 'P9' was not found.", exception.Message);
    }

    [Fact]
    public async Task AddIssueAsync_uses_rest_and_returns_both_item_ids()
    {
        var process = new RestQueueGhProcess(
            RestProjectResponse,
            RestAddedItemResponse);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var item = await client.AddIssueAsync(
            Config,
            "ISSUE_NODE",
            5001,
            CancellationToken.None);

        Assert.Equal("ITEM_NODE", item.NodeId);
        Assert.Equal(7001, item.DatabaseId);
        Assert.Equal(2, process.Calls.Count);
        var request = process.Calls[1];
        Assert.Contains("POST", request.Arguments);
        Assert.Equal("users/owner/projectsV2/1/items", request.Arguments[^1]);
        Assert.Contains("\"type\":\"Issue\"", request.StandardInput);
        Assert.Contains("\"id\":5001", request.StandardInput);
        Assert.DoesNotContain("graphql", request.Arguments);
    }

    [Fact]
    public async Task AddIssueAsync_falls_back_to_graphql_when_rest_is_unavailable()
    {
        var process = new QueueGhProcess(AddIssueGraphQlResponse);
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata(),
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);

        var item = await client.AddIssueAsync(
            Config,
            "ISSUE_NODE",
            5001,
            CancellationToken.None);

        Assert.Equal("ITEM_NODE", item.NodeId);
        Assert.Null(item.DatabaseId);
        var mutation = Assert.Single(process.Calls);
        Assert.Contains("addProjectV2ItemById", mutation.StandardInput);
    }

    [Fact]
    public async Task UpdateCreationAttemptIdAsync_uses_rest_when_database_ids_are_available()
    {
        var process = new RestQueueGhProcess(RestProjectResponse, "{}");
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata() with
            {
                RestFieldIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                {
                    [Config.CreationAttemptIdField] = 105
                }
            },
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);

        await client.UpdateCreationAttemptIdAsync(
            Config,
            ProjectItem() with { ProjectItemDatabaseId = 7001 },
            "attempt-2",
            CancellationToken.None);

        Assert.Equal(2, process.Calls.Count);
        var patch = process.Calls[1];
        Assert.Contains("PATCH", patch.Arguments);
        Assert.Contains("\"id\":105", patch.StandardInput);
        Assert.Contains("\"value\":\"attempt-2\"", patch.StandardInput);
    }

    [Fact]
    public async Task UpdateCreationAttemptIdAsync_falls_back_to_graphql_without_database_id()
    {
        var process = new QueueGhProcess(MutationResponse);
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata(),
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);

        await client.UpdateCreationAttemptIdAsync(
            Config,
            ProjectItem(),
            "attempt-2",
            CancellationToken.None);

        var mutation = Assert.Single(process.Calls);
        Assert.Contains("updateProjectV2ItemFieldValue", mutation.StandardInput);
        Assert.Contains("\"fieldId\":\"CREATION_FIELD\"", mutation.StandardInput);
        Assert.Contains("\"text\":\"attempt-2\"", mutation.StandardInput);
    }

    [Fact]
    public async Task UpdateStatusAsync_uses_rest_when_database_ids_are_available()
    {
        var process = new RestQueueGhProcess(RestProjectResponse, "{}");
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata() with
            {
                RestFieldIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                {
                    [Config.StatusField] = 101
                }
            },
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);

        await client.UpdateStatusAsync(
            Config,
            ProjectItem() with { ProjectItemDatabaseId = 7001 },
            "Todo",
            CancellationToken.None);

        Assert.Equal(2, process.Calls.Count);
        var patch = process.Calls[1];
        Assert.Contains("\"id\":101", patch.StandardInput);
        Assert.Contains("\"value\":\"TODO\"", patch.StandardInput);
    }

    [Fact]
    public async Task UpdatePriorityAsync_uses_rest_when_database_ids_are_available()
    {
        var process = new RestQueueGhProcess(RestProjectResponse, "{}");
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata() with
            {
                PriorityFieldId = "PRIORITY_FIELD",
                PriorityOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["P1"] = "P1"
                },
                RestFieldIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
                {
                    [Config.PriorityField] = 102
                }
            },
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);

        await client.UpdatePriorityAsync(
            Config,
            ProjectItem() with { ProjectItemDatabaseId = 7001 },
            "P1",
            CancellationToken.None);

        Assert.Equal(2, process.Calls.Count);
        var patch = process.Calls[1];
        Assert.Contains("\"id\":102", patch.StandardInput);
        Assert.Contains("\"value\":\"P1\"", patch.StandardInput);
    }

    [Fact]
    public async Task UpdatePriorityAsync_falls_back_to_graphql_without_database_id()
    {
        var process = new QueueGhProcess(MutationResponse);
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata() with
            {
                PriorityFieldId = "PRIORITY_FIELD",
                PriorityOptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["P1"] = "P1"
                }
            },
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);

        await client.UpdatePriorityAsync(
            Config,
            ProjectItem(),
            "P1",
            CancellationToken.None);

        var mutation = Assert.Single(process.Calls);
        Assert.Contains("updateProjectV2ItemFieldValue", mutation.StandardInput);
        Assert.Contains("\"fieldId\":\"PRIORITY_FIELD\"", mutation.StandardInput);
        Assert.Contains("\"optionId\":\"P1\"", mutation.StandardInput);
    }

    [Fact]
    public async Task ListAsync_paginates_project_items_and_reads_direct_fields()
    {
        var firstPage = ProjectPage(
            101,
            "First page",
            "P2",
            hasNextPage: true,
            endCursor: "CURSOR-1");
        var secondPage = ProjectPage(
            202,
            "Second page",
            "P1",
            hasNextPage: false,
            endCursor: null);
        var process = new QueueGhProcess(DiscoveryResponse, firstPage, secondPage);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var items = await client.ListAsync(Config, "Todo", null, CancellationToken.None);

        Assert.Equal([202, 101], items.Select(item => item.Number));
        Assert.Equal(3, process.Calls.Count);
        Assert.Contains("CURSOR-1", process.Calls[2].StandardInput);
    }

    [Fact]
    public async Task ListAsync_decodes_authoritative_worker_policy_fields()
    {
        var process = new QueueGhProcess(PolicyListResponse);
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata(),
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);

        var item = Assert.Single(await client.ListAsync(
            Config,
            null,
            null,
            CancellationToken.None));

        Assert.True(item.Summary.AutomaticExecutionAllowed);
        Assert.Equal("codex", item.Summary.AgentPolicy);
        Assert.Equal("Automatic allowed", item.ExecutionPolicyValue);
        Assert.Equal("Codex", item.AgentPolicyValue);
        Assert.Equal("Approved", item.ContextApprovalValue);
        Assert.Contains("executionPolicyField", process.Calls[0].StandardInput);
        Assert.Contains("agentPolicyField", process.Calls[0].StandardInput);
        Assert.Contains("contextApprovalField", process.Calls[0].StandardInput);
    }

    [Fact]
    public async Task ListAsync_fails_closed_for_unknown_policy_value()
    {
        var process = new QueueGhProcess(
            PolicyListResponse.Replace(
                "\"Automatic allowed\"",
                "\"Surprise\"",
                StringComparison.Ordinal));
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata(),
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(
            () => client.ListAsync(Config, null, null, CancellationToken.None));

        Assert.Equal("PROJECT_SCHEMA_INVALID", exception.Code);
        Assert.Contains("Surprise", exception.Message);
    }

    [Fact]
    public async Task UpdatePolicy_writes_preference_before_enabling_automatic()
    {
        var process = new QueueGhProcess(MutationResponse, MutationResponse);
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata(),
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);

        await client.UpdatePolicyAsync(
            Config,
            ProjectItem(),
            true,
            "codex",
            CancellationToken.None);

        Assert.Contains("PREFERRED_AGENT_FIELD", process.Calls[0].StandardInput);
        Assert.Contains("PREFERRED_CODEX", process.Calls[0].StandardInput);
        Assert.Contains("EXECUTION_FIELD", process.Calls[1].StandardInput);
        Assert.Contains("AUTOMATIC", process.Calls[1].StandardInput);
    }

    [Fact]
    public async Task UpdatePolicy_writes_manual_before_changing_preference()
    {
        var process = new QueueGhProcess(MutationResponse, MutationResponse);
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata(),
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);

        await client.UpdatePolicyAsync(
            Config,
            ProjectItem(),
            false,
            null,
            CancellationToken.None);

        Assert.Contains("EXECUTION_FIELD", process.Calls[0].StandardInput);
        Assert.Contains("MANUAL", process.Calls[0].StandardInput);
        Assert.Contains("PREFERRED_AGENT_FIELD", process.Calls[1].StandardInput);
        Assert.Contains("REPOSITORY_DEFAULT", process.Calls[1].StandardInput);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(500)]
    public async Task ListAsync_scales_through_complete_100_item_pages(int itemCount)
    {
        var responses = new[] { DiscoveryResponse }
            .Concat(ProjectPages(itemCount))
            .ToArray();
        var process = new QueueGhProcess(responses);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var items = await client.ListAsync(
            Config,
            null,
            null,
            ArchiveScope.All,
            CancellationToken.None);

        Assert.Equal(itemCount, items.Count);
        Assert.Equal(itemCount, items.Select(item => item.Number).Distinct().Count());
        Assert.Equal(itemCount / 10, items.Count(item => item.Summary.Archived));
        Assert.Equal(1 + Math.Max(1, (itemCount + 99) / 100), process.Calls.Count);
        Assert.All(process.Calls.Skip(1), call =>
        {
            Assert.Contains("fieldValueByName", call.StandardInput);
            Assert.DoesNotContain("fieldValues(first: 50)", call.StandardInput);
        });
    }

    [Fact]
    public async Task ListAsync_finds_a_filtered_item_only_present_on_the_fifth_page()
    {
        var responses = new[] { DiscoveryResponse }
            .Concat(ProjectPages(500, lateStatusNumber: 450))
            .ToArray();
        var process = new QueueGhProcess(responses);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var items = await client.ListAsync(
            Config,
            "In Progress",
            null,
            ArchiveScope.All,
            CancellationToken.None);

        Assert.Equal(450, Assert.Single(items).Number);
        Assert.Equal(6, process.Calls.Count);
        Assert.Contains("CURSOR-4", process.Calls[5].StandardInput);
    }

    [Fact]
    public async Task FindByCreationAttemptIdAsync_reads_all_matching_project_items()
    {
        const string attemptId = "019f5c485c2b7862aeac80eb638a7b5c";
        var process = new QueueGhProcess(CreationLookupResponse);
        var cache = new MemoryCache();
        await cache.PutAsync("github.com/owner/1", InitializedMetadata(), CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);

        var matches = await client.FindByCreationAttemptIdAsync(
            Config,
            attemptId,
            CancellationToken.None);

        var item = Assert.Single(matches);
        Assert.Equal(42, item.Number);
        Assert.Equal("Todo", item.Status);
        Assert.Equal("P1", item.Priority);
        Assert.Equal(attemptId, item.CreationAttemptId);
        Assert.Contains("Wrighty creation - attempt ID", process.Calls[0].StandardInput);
        Assert.Contains("Status", process.Calls[0].StandardInput);
        Assert.Contains("Priority", process.Calls[0].StandardInput);
    }

    [Fact]
    public async Task ListAsync_invalidates_a_stale_project_node_and_retries_once()
    {
        var process = new QueueGhProcess(
            """{ "errors": [{ "message": "Could not resolve to a node with the global id of 'STALE'" }] }""",
            DiscoveryResponse,
            ListResponse);
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            new ProjectMetadata(
                "STALE",
                "OLD_STATUS",
                new Dictionary<string, string>(),
                null),
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);
        var config = new TrackerConfig
        {
            Repository = "owner/repo",
            ProjectNumber = 1
        };

        var items = await client.ListAsync(config, "Todo", null, CancellationToken.None);

        Assert.Equal(2, items.Count);
        Assert.Equal(3, process.Calls.Count);
        Assert.Equal(1, cache.Invalidations);
    }

    [Fact]
    public async Task InitializeAsync_creates_missing_fields_and_refreshes_the_cache()
    {
        var process = new QueueGhProcess(
            DiscoveryResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse,
            InitializedDiscoveryResponse);
        var cache = new MemoryCache();
        var client = new GitHubProjectClient(new GhApi(process), cache);

        var result = await client.InitializeAsync(Config, checkOnly: false, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(13, result.Actions.Count);
        Assert.Equal(15, process.Calls.Count);
        Assert.Contains("Wrighty policy - execution", process.Calls[1].StandardInput);
        Assert.Contains("Wrighty policy - agent", process.Calls[2].StandardInput);
        Assert.Contains("Wrighty policy - context approval", process.Calls[3].StandardInput);
        Assert.Contains("\"name\":\"Needs review\"", process.Calls[3].StandardInput);
        Assert.Contains("\"name\":\"Approved\"", process.Calls[3].StandardInput);
        Assert.Contains("Wrighty dispatch - state", process.Calls[4].StandardInput);
        Assert.Contains("Wrighty dispatch - not before", process.Calls[5].StandardInput);
        Assert.Contains("Wrighty dispatch - agent", process.Calls[6].StandardInput);
        Assert.Contains("Wrighty dispatch - detail", process.Calls[7].StandardInput);
        Assert.Contains("Wrighty claim - agent", process.Calls[8].StandardInput);
        Assert.Contains("SINGLE_SELECT", process.Calls[8].StandardInput);
        Assert.Contains("Wrighty claim - session ID", process.Calls[9].StandardInput);
        Assert.Contains("TEXT", process.Calls[9].StandardInput);
        Assert.Contains("Wrighty claim - claimant type", process.Calls[10].StandardInput);
        Assert.Contains("Wrighty claim - claimant", process.Calls[11].StandardInput);
        Assert.Contains("Wrighty creation - attempt ID", process.Calls[12].StandardInput);
        Assert.Contains("Wrighty claim - workspace path", process.Calls[13].StandardInput);
        Assert.Equal(1, cache.Puts);
        Assert.Equal("AGENT_FIELD", cache.LastValue!.ClaimAgentFieldId);
        Assert.Equal("SESSION_FIELD", cache.LastValue.ClaimSessionIdFieldId);
        Assert.Equal("CREATION_FIELD", cache.LastValue.CreationAttemptIdFieldId);
        Assert.Equal("WORKER_ACTIVITY_FIELD", cache.LastValue.DispatchStateFieldId);
    }

    [Fact]
    public async Task InitializeAsync_is_idempotent_when_schema_is_already_valid()
    {
        var process = new QueueGhProcess(InitializedDiscoveryResponse);
        var cache = new MemoryCache();
        var client = new GitHubProjectClient(new GhApi(process), cache);

        var result = await client.InitializeAsync(Config, checkOnly: false, CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Single(process.Calls);
        Assert.Equal(1, cache.Puts);
    }

    [Fact]
    public async Task InitializeAsync_adds_missing_agent_options_without_replacing_existing_ids()
    {
        var process = new QueueGhProcess(
            MissingAgentOptionsDiscoveryResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse,
            InitializedDiscoveryResponse);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var result = await client.InitializeAsync(Config, checkOnly: false, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(8, process.Calls.Count);
        Assert.Contains(result.Actions, action =>
            action.Contains("add options Claude, Copilot, Other", StringComparison.Ordinal));
        var agentUpdate = Assert.Single(
            process.Calls,
            call => call.StandardInput!.Contains(
                "updateProjectV2Field", StringComparison.Ordinal));
        Assert.Contains("\"id\":\"CODEX\"", agentUpdate.StandardInput);
        Assert.Contains("\"name\":\"Claude\"", agentUpdate.StandardInput);
    }

    [Fact]
    public async Task Initialize_check_reports_missing_schema_without_writes_or_cache_changes()
    {
        var process = new QueueGhProcess(DiscoveryResponse);
        var cache = new MemoryCache();
        var client = new GitHubProjectClient(new GhApi(process), cache);

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(
            () => client.InitializeAsync(Config, checkOnly: true, CancellationToken.None));

        Assert.Equal("PROJECT_SCHEMA_INVALID", exception.Code);
        Assert.Contains("wrighty init", exception.Message);
        Assert.Single(process.Calls);
        Assert.Equal(0, cache.Puts);
    }

    [Fact]
    public async Task InitializeAsync_adds_context_approval_to_an_existing_supported_schema()
    {
        var missingContextApproval = InitializedDiscoveryResponse.Replace(
            "\"name\": \"Wrighty policy - context approval\"",
            "\"name\": \"Unrelated context field\"",
            StringComparison.Ordinal);
        var process = new QueueGhProcess(
            missingContextApproval,
            MutationResponse,
            InitializedDiscoveryResponse);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var result = await client.InitializeAsync(Config, checkOnly: false, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(
            "create single-select field 'Wrighty policy - context approval'",
            Assert.Single(result.Actions));
        Assert.Equal(3, process.Calls.Count);
        Assert.Contains("Wrighty policy - context approval", process.Calls[1].StandardInput);
        Assert.Contains("\"name\":\"Needs review\"", process.Calls[1].StandardInput);
        Assert.Contains("\"name\":\"Approved\"", process.Calls[1].StandardInput);
    }

    [Fact]
    public async Task InitializeAsync_provisions_the_priority_scale_on_a_project_it_created()
    {
        // GitHub gives a new Project exactly one single-select field — Status. Every other field
        // Wrighty needs it creates, and priority was the one it configured but never created, so a
        // board Wrighty owned end to end could not satisfy the configuration Wrighty itself wrote.
        var process = new QueueGhProcess(
            InitializedDiscoveryResponse,
            MutationResponse,
            InitializedDiscoveryResponse);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var result = await client.InitializeAsync(
            Config, checkOnly: false, CancellationToken.None, projectCreated: true);

        Assert.True(result.Changed);
        Assert.Equal("create single-select field 'Priority'", Assert.Single(result.Actions));
        var creation = process.Calls[1].StandardInput;
        Assert.Contains("Priority", creation, StringComparison.Ordinal);
        foreach (var option in new[] { "P0", "P1", "P2", "P3" })
            Assert.Contains($"\"name\":\"{option}\"", creation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializeAsync_leaves_an_adopted_projects_priority_alone()
    {
        // The same schema, adopted rather than created. A board's priority scale belongs to
        // whoever set it up: it may be High/Medium/Low, or absent on purpose, and Wrighty imposing
        // P0–P3 on it would be a change nobody asked for. Absent priority is documented as a
        // supported state for an adopted board.
        var process = new QueueGhProcess(InitializedDiscoveryResponse);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var result = await client.InitializeAsync(Config, checkOnly: false, CancellationToken.None);

        Assert.False(result.Changed);
        Assert.DoesNotContain(result.Actions, action => action.Contains("Priority", StringComparison.Ordinal));
        // Discovery only: no mutation was sent.
        Assert.Single(process.Calls);
    }

    [Fact]
    public async Task InitializeAsync_never_extends_a_priority_field_that_already_exists()
    {
        // Guarded separately from the ordinary field ensure, which adds any missing required
        // options to an existing field. That is right for Wrighty's own fields and wrong here: a
        // board that already has a priority scale must not gain a P0–P3 beside it, even on the
        // created path.
        // An existing single-select field renamed to the configured priority name, carrying a
        // scale that is not Wrighty's. The context-approval field it displaces is then missing,
        // so the run still writes — which is the point: a mutation happens, and none of it is
        // priority.
        var withCustomPriority = InitializedDiscoveryResponse.Replace(
            "\"name\": \"Wrighty policy - context approval\"",
            "\"name\": \"Priority\"",
            StringComparison.Ordinal);
        var process = new QueueGhProcess(
            withCustomPriority,
            MutationResponse,
            withCustomPriority);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var result = await client.InitializeAsync(
            Config, checkOnly: false, CancellationToken.None, projectCreated: true);

        Assert.DoesNotContain(result.Actions, action => action.Contains("Priority", StringComparison.Ordinal));
        // Exactly one mutation, and it is the displaced context-approval field — the existing
        // priority scale is never touched, and no P0–P3 is added beside it.
        Assert.Equal(3, process.Calls.Count);
        var mutation = process.Calls[1].StandardInput;
        Assert.Contains("Wrighty policy - context approval", mutation, StringComparison.Ordinal);
        foreach (var option in new[] { "P0", "P1", "P2", "P3" })
            Assert.DoesNotContain($"\"name\":\"{option}\"", mutation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_check_reports_a_missing_context_approval_field()
    {
        var missingContextApproval = InitializedDiscoveryResponse.Replace(
            "\"name\": \"Wrighty policy - context approval\"",
            "\"name\": \"Unrelated context field\"",
            StringComparison.Ordinal);
        var process = new QueueGhProcess(missingContextApproval);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(
            () => client.InitializeAsync(Config, checkOnly: true, CancellationToken.None));

        Assert.Equal("PROJECT_SCHEMA_INVALID", exception.Code);
        Assert.Contains(
            "create single-select field 'Wrighty policy - context approval'",
            exception.Message);
        Assert.Single(process.Calls);
    }

    [Fact]
    public async Task Initialize_check_rejects_a_context_approval_field_with_the_wrong_type()
    {
        var wrongType = InitializedDiscoveryResponse.Replace(
            "\"id\": \"CONTEXT_APPROVAL_FIELD\", \"name\": \"Wrighty policy - context approval\", \"dataType\": \"SINGLE_SELECT\"",
            "\"id\": \"CONTEXT_APPROVAL_FIELD\", \"name\": \"Wrighty policy - context approval\", \"dataType\": \"TEXT\"",
            StringComparison.Ordinal);
        var process = new QueueGhProcess(wrongType);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(
            () => client.InitializeAsync(Config, checkOnly: true, CancellationToken.None));

        Assert.Equal("PROJECT_SCHEMA_INVALID", exception.Code);
        Assert.Contains(
            "Project field 'Wrighty policy - context approval' exists but is not a single-select field.",
            exception.Message);
        Assert.Single(process.Calls);
    }

    [Fact]
    public async Task Initialize_check_reports_a_missing_context_approval_option()
    {
        var missingApprovedOption = InitializedDiscoveryResponse.Replace(
            "\"name\": \"Approved\"",
            "\"name\": \"Awaiting approval\"",
            StringComparison.Ordinal);
        var process = new QueueGhProcess(missingApprovedOption);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(
            () => client.InitializeAsync(Config, checkOnly: true, CancellationToken.None));

        Assert.Equal("PROJECT_SCHEMA_INVALID", exception.Code);
        Assert.Contains(
            "add options Approved to 'Wrighty policy - context approval'",
            exception.Message);
        Assert.Single(process.Calls);
    }

    [Fact]
    public async Task Initialize_check_validates_authoritative_schema_without_updating_cache()
    {
        var process = new QueueGhProcess(InitializedDiscoveryResponse);
        var cache = new MemoryCache();
        var client = new GitHubProjectClient(new GhApi(process), cache);

        var result = await client.InitializeAsync(Config, checkOnly: true, CancellationToken.None);

        Assert.False(result.Changed);
        Assert.Single(process.Calls);
        Assert.Equal(0, cache.Puts);
    }

    [Fact]
    public async Task Initialize_rejects_pre_overhaul_project_fields_without_migrating()
    {
        var response = InitializedDiscoveryResponse.Replace(
            "\"Wrighty policy - execution\"",
            "\"Worker execution\"",
            StringComparison.Ordinal);
        var process = new QueueGhProcess(response);
        var cache = new MemoryCache();
        var client = new GitHubProjectClient(new GhApi(process), cache);

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(
            () => client.InitializeAsync(Config, checkOnly: false, CancellationToken.None));

        Assert.Equal("PROJECT_SCHEMA_UNSUPPORTED", exception.Code);
        Assert.Contains("Worker execution", exception.Message);
        Assert.Single(process.Calls);
        Assert.Equal(0, cache.Puts);
    }

    [Fact]
    public async Task UpdateAgentContextAsync_uses_cached_field_and_option_ids()
    {
        var process = new QueueGhProcess(MutationResponse, MutationResponse);
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata(),
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);
        var item = new GitHubProjectItem(
            new GitHubWorkItemAddress("github.com", "owner", "repo", 1),
            new WorkItemSummary(
                new WorkItemId("github:owner/repo#1"),
                "Item",
                "https://github.com/owner/repo/issues/1",
                "Todo",
                "P1"),
            "ISSUE",
            "ITEM");

        await client.UpdateAgentContextAsync(
            Config,
            item,
            "codex",
            "session-1",
            CancellationToken.None);

        Assert.Equal(2, process.Calls.Count);
        Assert.Contains("AGENT_FIELD", process.Calls[0].StandardInput);
        Assert.Contains("CODEX", process.Calls[0].StandardInput);
        Assert.Contains("SESSION_FIELD", process.Calls[1].StandardInput);
        Assert.Contains("session-1", process.Calls[1].StandardInput);
    }

    [Fact]
    public async Task Claimant_projection_can_be_set_and_cleared()
    {
        var process = new QueueGhProcess(
            MutationResponse, MutationResponse, MutationResponse, MutationResponse,
            MutationResponse, MutationResponse, MutationResponse, MutationResponse);
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata(),
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);
        var item = ProjectItem();

        await client.UpdateClaimantProjectionAsync(
            Config,
            item,
            "agent",
            "agent:worker:claimant-with-a-long-identifier",
            "claude",
            "session-1",
            CancellationToken.None);
        await client.UpdateClaimantProjectionAsync(
            Config,
            item,
            null,
            null,
            null,
            null,
            CancellationToken.None);

        Assert.Equal(8, process.Calls.Count);
        Assert.Contains("CLAUDE", process.Calls[0].StandardInput);
        Assert.Contains("session-1", process.Calls[1].StandardInput);
        Assert.Contains("CLAIMANT_KIND_FIELD", process.Calls[2].StandardInput);
        Assert.Contains("AGENT", process.Calls[2].StandardInput);
        Assert.Contains("CLAIMANT_ID_FIELD", process.Calls[3].StandardInput);
        Assert.Contains("agent:worker:claimant-wi", process.Calls[3].StandardInput);
        Assert.DoesNotContain("long-identifier", process.Calls[3].StandardInput);
        Assert.All(
            process.Calls.Skip(4),
            call => Assert.Contains("clearProjectV2ItemFieldValue", call.StandardInput));
    }

    [Fact]
    public async Task Workspace_projection_can_be_set_and_cleared()
    {
        var process = new QueueGhProcess(MutationResponse, MutationResponse);
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata(),
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);
        var item = ProjectItem();

        await client.UpdateWorkspacePathAsync(
            Config, item, "/tmp/wrighty-item", CancellationToken.None);
        await client.UpdateWorkspacePathAsync(
            Config, item, null, CancellationToken.None);

        Assert.Contains("WORKSPACE_FIELD", process.Calls[0].StandardInput);
        Assert.Contains("/tmp/wrighty-item", process.Calls[0].StandardInput);
        Assert.Contains("clearProjectV2ItemFieldValue", process.Calls[1].StandardInput);
        Assert.Contains("WORKSPACE_FIELD", process.Calls[1].StandardInput);
    }

    [Fact]
    public async Task Recovery_projection_uses_dispatch_and_authoritative_project_policy()
    {
        var process = new QueueGhProcess(
            MutationResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse);
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata(),
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);
        var item = ProjectItem() with
        {
            Summary = ProjectItem().Summary with
            {
                AutomaticExecutionAllowed = true,
                AgentPolicy = "codex"
            }
        };
        var dispatch = new Highbyte.Wrighty.Workers.DispatchInfo(
            DispatchStates.RetryScheduled,
            "Agent usage is exhausted.",
            "claude",
            null,
            DateTimeOffset.Parse("2026-07-24T04:02:00Z"),
            2,
            5,
            DateTimeOffset.Parse("2026-07-23T22:00:00Z"),
            true);

        await client.UpdateDispatchProjectionAsync(
            Config, item, dispatch, CancellationToken.None);

        Assert.Equal(4, process.Calls.Count);
        Assert.Contains("WORKER_ACTIVITY_FIELD", process.Calls[0].StandardInput);
        Assert.Contains("RETRY_SCHEDULED", process.Calls[0].StandardInput);
        Assert.Contains("WORKER_RETRY_AT_FIELD", process.Calls[1].StandardInput);
        using (var retryInput = JsonDocument.Parse(process.Calls[1].StandardInput!))
        {
            Assert.Equal(
                "2026-07-24T04:02:00.0000000+00:00",
                retryInput.RootElement.GetProperty("variables").GetProperty("text").GetString());
        }
        Assert.Contains("WORKER_TARGET_AGENT_FIELD", process.Calls[2].StandardInput);
        Assert.Contains("TARGET_CLAUDE", process.Calls[2].StandardInput);
        Assert.Contains("WORKER_STATUS_FIELD", process.Calls[3].StandardInput);
        Assert.Contains(
            "Agent usage is exhausted; agent policy changed; attempt 2 of 5",
            process.Calls[3].StandardInput);
    }

    [Fact]
    public async Task Recovery_projection_is_a_no_op_for_an_older_project_schema()
    {
        var process = new QueueGhProcess();
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata() with
            {
                DispatchStateFieldId = null,
                DispatchStateOptionOptions = null,
                DispatchNotBeforeFieldId = null,
                DispatchAgentFieldId = null,
                DispatchAgentOptions = null,
                DispatchDetailFieldId = null
            },
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);

        await client.UpdateDispatchStateProjectionAsync(
            Config,
            ProjectItem(),
            DispatchStates.RetryScheduled,
            CancellationToken.None);

        Assert.Empty(process.Calls);
    }

    [Fact]
    public async Task Non_deferred_activity_clears_stale_recovery_details()
    {
        var process = new QueueGhProcess(
            MutationResponse,
            MutationResponse,
            MutationResponse,
            MutationResponse);
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata(),
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);

        await client.UpdateDispatchStateProjectionAsync(
            Config,
            ProjectItem(),
            DispatchStates.NeedsAttention,
            CancellationToken.None);

        Assert.Contains("NEEDS_ATTENTION", process.Calls[0].StandardInput);
        Assert.All(
            process.Calls.Skip(1),
            call => Assert.Contains("clearProjectV2ItemFieldValue", call.StandardInput));
        Assert.Contains("WORKER_RETRY_AT_FIELD", process.Calls[1].StandardInput);
        Assert.Contains("WORKER_TARGET_AGENT_FIELD", process.Calls[2].StandardInput);
        Assert.Contains("WORKER_STATUS_FIELD", process.Calls[3].StandardInput);
    }

    [Fact]
    public async Task ClearPriorityAsync_uses_the_project_item_and_cached_priority_field()
    {
        var process = new QueueGhProcess(MutationResponse);
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            new ProjectMetadata(
                "PROJECT",
                "STATUS_FIELD",
                new Dictionary<string, string>(),
                "PRIORITY_FIELD",
                PriorityOptions: new Dictionary<string, string>()),
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);
        var item = new GitHubProjectItem(
            new GitHubWorkItemAddress("github.com", "owner", "repo", 1),
            new WorkItemSummary(
                new WorkItemId("github:owner/repo#1"),
                "Item",
                null,
                "Todo",
                "P1"),
            "ISSUE",
            "ITEM");

        await client.ClearPriorityAsync(Config, item, CancellationToken.None);

        var input = Assert.Single(process.Calls).StandardInput!;
        Assert.Contains("clearProjectV2ItemFieldValue", input);
        Assert.Contains("PRIORITY_FIELD", input);
        Assert.Contains("ITEM", input);
    }

    [Fact]
    public async Task ListAsync_can_return_archived_items()
    {
        var archivedResponse = ListResponse
            .Replace("\"id\": \"ITEM1\"", "\"id\": \"ITEM1\", \"isArchived\": true")
            .Replace("\"id\": \"ITEM2\"", "\"id\": \"ITEM2\", \"isArchived\": true");
        var process = new QueueGhProcess(DiscoveryResponse, archivedResponse);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var items = await client.ListAsync(
            Config,
            null,
            null,
            ArchiveScope.Archived,
            CancellationToken.None);

        Assert.All(items, item => Assert.True(item.Summary.Archived));
        Assert.Contains("ARCHIVED", process.Calls[1].StandardInput);
    }

    [Fact]
    public async Task Archive_and_unarchive_use_native_project_mutations()
    {
        var process = new QueueGhProcess(MutationResponse, MutationResponse);
        var cache = new MemoryCache();
        await cache.PutAsync(
            "github.com/owner/1",
            InitializedMetadata(),
            CancellationToken.None);
        var client = new GitHubProjectClient(new GhApi(process), cache);
        var item = new GitHubProjectItem(
            new GitHubWorkItemAddress("github.com", "owner", "repo", 1),
            new WorkItemSummary(new WorkItemId("github:owner/repo#1"), "Item", null, "Done", "P1"),
            "ISSUE",
            "ITEM");

        await client.ArchiveAsync(Config, item, CancellationToken.None);
        await client.UnarchiveAsync(Config, item, CancellationToken.None);

        Assert.Contains("archiveProjectV2Item", process.Calls[0].StandardInput);
        Assert.Contains("unarchiveProjectV2Item", process.Calls[1].StandardInput);
        Assert.All(process.Calls, call => Assert.Contains("ITEM", call.StandardInput));
    }

    private const string RestProjectResponse = """
        {
          "id": 9001,
          "node_id": "PROJECT",
          "number": 1,
          "title": "Wrighty"
        }
        """;

    private const string RestFieldsResponse = """
        [[
          {
            "id": 101,
            "node_id": "STATUS_FIELD",
            "name": "Status",
            "data_type": "single_select",
            "options": [
              {
                "id": "TODO",
                "name": { "raw": "Todo", "html": "Todo" },
                "description": { "raw": "", "html": "" },
                "color": "GRAY"
              }
            ]
          },
          {
            "id": 102,
            "node_id": "PRIORITY_FIELD",
            "name": "Priority",
            "data_type": "single_select",
            "options": [
              {
                "id": "P1",
                "name": { "raw": "P1", "html": "P1" },
                "description": { "raw": "", "html": "" },
                "color": "RED"
              }
            ]
          },
          {
            "id": 103,
            "node_id": "EXECUTION_FIELD",
            "name": "Wrighty policy - execution",
            "data_type": "single_select",
            "options": [
              {
                "id": "MANUAL",
                "name": { "raw": "Manual only", "html": "Manual only" },
                "description": { "raw": "", "html": "" },
                "color": "GRAY"
              },
              {
                "id": "AUTOMATIC",
                "name": { "raw": "Automatic allowed", "html": "Automatic allowed" },
                "description": { "raw": "", "html": "" },
                "color": "GREEN"
              }
            ]
          },
          {
            "id": 104,
            "node_id": "PREFERRED_AGENT_FIELD",
            "name": "Wrighty policy - agent",
            "data_type": "single_select",
            "options": [
              {
                "id": "PREFERRED_DEFAULT",
                "name": { "raw": "Repository default", "html": "Repository default" },
                "description": { "raw": "", "html": "" },
                "color": "GRAY"
              },
              {
                "id": "PREFERRED_CLAUDE",
                "name": { "raw": "Claude", "html": "Claude" },
                "description": { "raw": "", "html": "" },
                "color": "ORANGE"
              },
              {
                "id": "PREFERRED_CODEX",
                "name": { "raw": "Codex", "html": "Codex" },
                "description": { "raw": "", "html": "" },
                "color": "GREEN"
              },
              {
                "id": "PREFERRED_COPILOT",
                "name": { "raw": "Copilot", "html": "Copilot" },
                "description": { "raw": "", "html": "" },
                "color": "BLUE"
              }
            ]
          },
          {
            "id": 105,
            "node_id": "CREATION_FIELD",
            "name": "Wrighty creation - attempt ID",
            "data_type": "text"
          },
          {
            "id": 106,
            "node_id": "CONTEXT_APPROVAL_FIELD",
            "name": "Wrighty policy - context approval",
            "data_type": "single_select",
            "options": [
              {
                "id": "NEEDS_REVIEW",
                "name": { "raw": "Needs review", "html": "Needs review" },
                "description": { "raw": "", "html": "" },
                "color": "GRAY"
              },
              {
                "id": "APPROVED",
                "name": { "raw": "Approved", "html": "Approved" },
                "description": { "raw": "", "html": "" },
                "color": "GREEN"
              }
            ]
          }
        ]]
        """;

    private const string RestItemsResponse = """
        [[
          {
            "id": 7001,
            "node_id": "ITEM",
            "content_type": "Issue",
            "archived_at": null,
            "content": {
              "node_id": "ISSUE",
              "number": 42,
              "title": "REST item",
              "html_url": "https://github.com/owner/repo/issues/42",
              "repository": { "full_name": "owner/repo" }
            },
            "fields": [
              {
                "id": 101,
                "name": "Status",
                "data_type": "single_select",
                "value": { "id": "TODO", "name": { "raw": "Todo", "html": "Todo" } }
              },
              {
                "id": 102,
                "name": "Priority",
                "data_type": "single_select",
                "value": { "id": "P1", "name": { "raw": "P1", "html": "P1" } }
              },
              {
                "id": 103,
                "name": "Wrighty policy - execution",
                "data_type": "single_select",
                "value": {
                  "id": "AUTOMATIC",
                  "name": { "raw": "Automatic allowed", "html": "Automatic allowed" }
                }
              },
              {
                "id": 104,
                "name": "Wrighty policy - agent",
                "data_type": "single_select",
                "value": { "id": "PREFERRED_CODEX", "name": { "raw": "Codex", "html": "Codex" } }
              },
              {
                "id": 106,
                "name": "Wrighty policy - context approval",
                "data_type": "single_select",
                "value": { "id": "APPROVED", "name": { "raw": "Approved", "html": "Approved" } }
              },
              {
                "id": 105,
                "name": "Wrighty creation - attempt ID",
                "data_type": "text",
                "value": { "raw": "attempt-1", "html": "attempt-1" }
              }
            ]
          }
        ]]
        """;

    private const string RestScalarItemsResponse = """
        [
          {
            "id": 7000,
            "node_id": "DRAFT",
            "content_type": "DraftIssue",
            "archived_at": null,
            "content": {
              "node_id": "DRAFT_CONTENT",
              "number": 41,
              "title": "Draft item",
              "repository": { "full_name": "owner/repo" }
            }
          },
          {
            "id": 7001,
            "node_id": "ITEM",
            "content_type": "Issue",
            "archived_at": null,
            "content": {
              "node_id": "ISSUE",
              "number": 42,
              "title": "REST item",
              "repository": { "full_name": "owner/repo" }
            },
            "fields": [
              { "id": 999, "value": "ignored" },
              { "id": 101, "name": "Status", "value": "Todo" },
              { "id": 102, "name": "Priority", "value": 1 },
              {
                "id": 103,
                "name": "Wrighty policy - execution",
                "value": { "name": "Automatic allowed" }
              },
              {
                "id": 104,
                "name": "Wrighty policy - agent",
                "value": { "raw": "Codex" }
              },
              {
                "id": 106,
                "name": "Wrighty policy - context approval",
                "value": { "name": "Needs review" }
              },
              {
                "id": 105,
                "name": "Wrighty creation - attempt ID",
                "value": null
              }
            ]
          }
        ]
        """;

    private const string RestAddedItemResponse = """
        {
          "value": {
            "id": 7001,
            "node_id": "ITEM_NODE"
          }
        }
        """;

    private const string AddIssueGraphQlResponse = """
        {
          "data": {
            "addProjectV2ItemById": {
              "item": { "id": "ITEM_NODE" }
            }
          }
        }
        """;

    private const string DiscoveryResponse = """
        {
          "data": {
            "repositoryOwner": {
              "projectV2": {
                "id": "PROJECT",
                "fields": {
                  "nodes": [
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "STATUS_FIELD",
                      "name": "Status",
                      "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "TODO", "name": "Todo", "description": "", "color": "GRAY" },
                        { "id": "DOING", "name": "In Progress", "description": "", "color": "BLUE" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "PRIORITY_FIELD",
                      "name": "Priority",
                      "dataType": "SINGLE_SELECT",
                      "options": []
                    }
                  ]
                }
              }
            }
          }
        }
        """;

    private const string ListResponse = """
        {
          "data": {
            "node": {
              "items": {
                "nodes": [
                  {
                    "id": "ITEM1",
                    "type": "ISSUE",
                    "content": {
                      "id": "ISSUE1", "number": 1, "title": "Second",
                      "url": "https://github.com/owner/repo/issues/1",
                      "repository": { "nameWithOwner": "owner/repo" }
                    },
                    "fieldValues": { "nodes": [
                      { "name": "Todo", "field": { "name": "Status" } },
                      { "name": "P2", "field": { "name": "Priority" } }
                    ] }
                  },
                  {
                    "id": "ITEM2",
                    "type": "ISSUE",
                    "content": {
                      "id": "ISSUE2", "number": 2, "title": "First",
                      "url": "https://github.com/owner/repo/issues/2",
                      "repository": { "nameWithOwner": "owner/repo" }
                    },
                    "fieldValues": { "nodes": [
                      { "name": "Todo", "field": { "name": "Status" } },
                      { "name": "P1", "field": { "name": "Priority" } }
                    ] }
                  },
                  {
                    "id": "OTHER",
                    "type": "ISSUE",
                    "content": {
                      "id": "OTHER_ISSUE", "number": 3, "title": "Other repo",
                      "url": "https://github.com/owner/other/issues/3",
                      "repository": { "nameWithOwner": "owner/other" }
                    },
                    "fieldValues": { "nodes": [] }
                  },
                  { "id": "DRAFT", "type": "DRAFT_ISSUE", "content": null, "fieldValues": { "nodes": [] } }
                ],
                "pageInfo": { "hasNextPage": false, "endCursor": null }
              }
            }
          }
        }
        """;

    private const string InitializedDiscoveryResponse = """
        {
          "data": {
            "repositoryOwner": {
              "projectV2": {
                "id": "PROJECT",
                "fields": {
                  "nodes": [
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "STATUS_FIELD", "name": "Status", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "TODO", "name": "Todo", "description": "", "color": "GRAY" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "EXECUTION_FIELD", "name": "Wrighty policy - execution", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "MANUAL", "name": "Manual only", "description": "", "color": "GRAY" },
                        { "id": "AUTOMATIC", "name": "Automatic allowed", "description": "", "color": "GREEN" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "PREFERRED_AGENT_FIELD", "name": "Wrighty policy - agent", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "REPOSITORY_DEFAULT", "name": "Repository default", "description": "", "color": "GRAY" },
                        { "id": "PREFERRED_CLAUDE", "name": "Claude", "description": "", "color": "ORANGE" },
                        { "id": "PREFERRED_CODEX", "name": "Codex", "description": "", "color": "GREEN" },
                        { "id": "PREFERRED_COPILOT", "name": "Copilot", "description": "", "color": "BLUE" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "CONTEXT_APPROVAL_FIELD", "name": "Wrighty policy - context approval", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "NEEDS_REVIEW", "name": "Needs review", "description": "", "color": "GRAY" },
                        { "id": "APPROVED", "name": "Approved", "description": "", "color": "GREEN" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "WORKER_ACTIVITY_FIELD", "name": "Wrighty dispatch - state", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "NEEDS_ATTENTION", "name": "Needs attention", "description": "", "color": "RED" },
                        { "id": "QUEUED_TO_RESUME", "name": "Resume queued", "description": "", "color": "BLUE" },
                        { "id": "RETRY_SCHEDULED", "name": "Retry scheduled", "description": "", "color": "ORANGE" },
                        { "id": "HANDOFF_QUEUED", "name": "Handoff queued", "description": "", "color": "PURPLE" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2Field",
                      "id": "WORKER_RETRY_AT_FIELD", "name": "Wrighty dispatch - not before", "dataType": "TEXT"
                    },
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "WORKER_TARGET_AGENT_FIELD", "name": "Wrighty dispatch - agent", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "TARGET_CODEX", "name": "Codex", "description": "", "color": "GREEN" },
                        { "id": "TARGET_CLAUDE", "name": "Claude", "description": "", "color": "ORANGE" },
                        { "id": "TARGET_COPILOT", "name": "Copilot", "description": "", "color": "BLUE" },
                        { "id": "TARGET_OTHER", "name": "Other", "description": "", "color": "GRAY" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2Field",
                      "id": "WORKER_STATUS_FIELD", "name": "Wrighty dispatch - detail", "dataType": "TEXT"
                    },
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "AGENT_FIELD", "name": "Wrighty claim - agent", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "CODEX", "name": "Codex", "description": "", "color": "GREEN" },
                        { "id": "CLAUDE", "name": "Claude", "description": "", "color": "ORANGE" },
                        { "id": "COPILOT", "name": "Copilot", "description": "", "color": "BLUE" },
                        { "id": "OTHER", "name": "Other", "description": "", "color": "GRAY" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2Field",
                      "id": "SESSION_FIELD", "name": "Wrighty claim - session ID", "dataType": "TEXT"
                    },
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "CLAIMANT_KIND_FIELD", "name": "Wrighty claim - claimant type", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "AGENT", "name": "Agent", "description": "", "color": "GREEN" },
                        { "id": "HUMAN", "name": "Human", "description": "", "color": "BLUE" },
                        { "id": "AUTOMATION", "name": "Automation", "description": "", "color": "ORANGE" },
                        { "id": "UNKNOWN", "name": "Unknown", "description": "", "color": "GRAY" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2Field",
                      "id": "CLAIMANT_ID_FIELD", "name": "Wrighty claim - claimant", "dataType": "TEXT"
                    },
                    {
                      "__typename": "ProjectV2Field",
                      "id": "CREATION_FIELD", "name": "Wrighty creation - attempt ID", "dataType": "TEXT"
                    },
                    {
                      "__typename": "ProjectV2Field",
                      "id": "WORKSPACE_FIELD", "name": "Wrighty claim - workspace path", "dataType": "TEXT"
                    }
                  ]
                }
              }
            }
          }
        }
        """;

    private const string MutationResponse = """
        { "data": { "projectV2Item": { "id": "ITEM" } } }
        """;

    private const string PolicyListResponse = """
        {
          "data": {
            "node": {
              "items": {
                "nodes": [{
                  "id": "ITEM42", "type": "ISSUE", "isArchived": false,
                  "content": {
                    "id": "ISSUE42", "number": 42, "title": "Policy item",
                    "url": "https://github.com/owner/repo/issues/42",
                    "repository": { "nameWithOwner": "owner/repo" }
                  },
                  "status": { "name": "Todo" },
                  "priority": { "name": "P1" },
                  "executionPolicy": { "name": "Automatic allowed" },
                  "agentPolicy": { "name": "Codex" },
                  "contextApproval": { "name": "Approved" }
                }],
                "pageInfo": { "hasNextPage": false, "endCursor": null }
              }
            }
          }
        }
        """;

    private const string CreationLookupResponse = """
        {
          "data": {
            "node": {
              "items": {
                "nodes": [{
                  "id": "ITEM42", "type": "ISSUE", "isArchived": false,
                  "content": {
                    "id": "ISSUE42", "number": 42, "title": "Retry",
                    "url": "https://github.com/owner/repo/issues/42",
                    "repository": { "nameWithOwner": "owner/repo" }
                  },
                  "creationAttempt": { "text": "019f5c485c2b7862aeac80eb638a7b5c" },
                  "status": { "name": "Todo" },
                  "priority": { "name": "P1" }
                }],
                "pageInfo": { "hasNextPage": false, "endCursor": null }
              }
            }
          }
        }
        """;

    private static string ProjectPage(
        int number,
        string title,
        string priority,
        bool hasNextPage,
        string? endCursor) => JsonSerializer.Serialize(new
        {
            data = new
            {
                node = new
                {
                    items = new
                    {
                        nodes = new[]
                        {
                            new
                            {
                                id = $"ITEM{number}",
                                type = "ISSUE",
                                isArchived = false,
                                content = new
                                {
                                    id = $"ISSUE{number}",
                                    number,
                                    title,
                                    url = $"https://github.com/owner/repo/issues/{number}",
                                    repository = new { nameWithOwner = "owner/repo" }
                                },
                                status = new { name = "Todo" },
                                priority = new { name = priority }
                            }
                        },
                        pageInfo = new { hasNextPage, endCursor }
                    }
                }
            }
        });

    private static IEnumerable<string> ProjectPages(
        int itemCount,
        int? lateStatusNumber = null)
    {
        var pageCount = Math.Max(1, (itemCount + 99) / 100);
        for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var start = (pageIndex * 100) + 1;
            var count = Math.Min(100, Math.Max(0, itemCount - (pageIndex * 100)));
            var hasNextPage = pageIndex < pageCount - 1;
            yield return JsonSerializer.Serialize(new
            {
                data = new
                {
                    node = new
                    {
                        items = new
                        {
                            nodes = Enumerable.Range(start, count).Select(number => new
                            {
                                id = $"ITEM{number}",
                                type = "ISSUE",
                                isArchived = number % 10 == 0,
                                content = new
                                {
                                    id = $"ISSUE{number}",
                                    number,
                                    title = $"Synthetic item {number}",
                                    url = $"https://github.com/owner/repo/issues/{number}",
                                    repository = new { nameWithOwner = "owner/repo" }
                                },
                                status = new
                                {
                                    name = number == lateStatusNumber ? "In Progress" : "Todo"
                                },
                                priority = new { name = $"P{number % 4}" }
                            }).ToArray(),
                            pageInfo = new
                            {
                                hasNextPage,
                                endCursor = hasNextPage ? $"CURSOR-{pageIndex + 1}" : null
                            }
                        }
                    }
                }
            });
        }
    }

    private const string MissingAgentOptionsDiscoveryResponse = """
        {
          "data": {
            "repositoryOwner": {
              "projectV2": {
                "id": "PROJECT",
                "fields": {
                  "nodes": [
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "STATUS_FIELD", "name": "Status", "dataType": "SINGLE_SELECT",
                      "options": []
                    },
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "EXECUTION_FIELD", "name": "Wrighty policy - execution", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "MANUAL", "name": "Manual only", "description": "", "color": "GRAY" },
                        { "id": "AUTOMATIC", "name": "Automatic allowed", "description": "", "color": "GREEN" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "PREFERRED_AGENT_FIELD", "name": "Wrighty policy - agent", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "REPOSITORY_DEFAULT", "name": "Repository default", "description": "", "color": "GRAY" },
                        { "id": "PREFERRED_CLAUDE", "name": "Claude", "description": "", "color": "ORANGE" },
                        { "id": "PREFERRED_CODEX", "name": "Codex", "description": "", "color": "GREEN" },
                        { "id": "PREFERRED_COPILOT", "name": "Copilot", "description": "", "color": "BLUE" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "CONTEXT_APPROVAL_FIELD", "name": "Wrighty policy - context approval", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "NEEDS_REVIEW", "name": "Needs review", "description": "", "color": "GRAY" },
                        { "id": "APPROVED", "name": "Approved", "description": "", "color": "GREEN" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "AGENT_FIELD", "name": "Wrighty claim - agent", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "CODEX", "name": "Codex", "description": "Keep me", "color": "GREEN" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2Field",
                      "id": "SESSION_FIELD", "name": "Wrighty claim - session ID", "dataType": "TEXT"
                    },
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "CLAIMANT_KIND_FIELD", "name": "Wrighty claim - claimant type", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "AGENT", "name": "Agent", "description": "", "color": "GREEN" },
                        { "id": "HUMAN", "name": "Human", "description": "", "color": "BLUE" },
                        { "id": "AUTOMATION", "name": "Automation", "description": "", "color": "ORANGE" },
                        { "id": "UNKNOWN", "name": "Unknown", "description": "", "color": "GRAY" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2Field",
                      "id": "CLAIMANT_ID_FIELD", "name": "Wrighty claim - claimant", "dataType": "TEXT"
                    },
                    {
                      "__typename": "ProjectV2Field",
                      "id": "WORKSPACE_FIELD", "name": "Wrighty claim - workspace path", "dataType": "TEXT"
                    }
                  ]
                }
              }
            }
          }
        }
        """;

    private static ProjectMetadata InitializedMetadata() => new(
        "PROJECT",
        "STATUS_FIELD",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Todo"] = "TODO"
        },
        null,
        "AGENT_FIELD",
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Codex"] = "CODEX",
            ["Claude"] = "CLAUDE",
            ["Copilot"] = "COPILOT",
            ["Other"] = "OTHER"
        },
        "SESSION_FIELD",
        CreationAttemptIdFieldId: "CREATION_FIELD",
        ClaimantTypeFieldId: "CLAIMANT_KIND_FIELD",
        ClaimantKindOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent"] = "AGENT",
            ["Human"] = "HUMAN",
            ["Automation"] = "AUTOMATION",
            ["Unknown"] = "UNKNOWN"
        },
        ClaimantFieldId: "CLAIMANT_ID_FIELD",
        ClaimWorkspacePathFieldId: "WORKSPACE_FIELD",
        ExecutionPolicyFieldId: "EXECUTION_FIELD",
        ExecutionPolicyOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Manual only"] = "MANUAL",
            ["Automatic allowed"] = "AUTOMATIC"
        },
        AgentPolicyFieldId: "PREFERRED_AGENT_FIELD",
        AgentPolicyOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Repository default"] = "REPOSITORY_DEFAULT",
            ["Claude"] = "PREFERRED_CLAUDE",
            ["Codex"] = "PREFERRED_CODEX",
            ["Copilot"] = "PREFERRED_COPILOT"
        },
        DispatchStateFieldId: "WORKER_ACTIVITY_FIELD",
        DispatchStateOptionOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Needs attention"] = "NEEDS_ATTENTION",
            ["Resume queued"] = "QUEUED_TO_RESUME",
            ["Retry scheduled"] = "RETRY_SCHEDULED",
            ["Handoff queued"] = "HANDOFF_QUEUED"
        },
        DispatchNotBeforeFieldId: "WORKER_RETRY_AT_FIELD",
        DispatchAgentFieldId: "WORKER_TARGET_AGENT_FIELD",
        DispatchAgentOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Claude"] = "TARGET_CLAUDE",
            ["Codex"] = "TARGET_CODEX",
            ["Copilot"] = "TARGET_COPILOT",
            ["Other"] = "TARGET_OTHER"
        },
        DispatchDetailFieldId: "WORKER_STATUS_FIELD");

    private static GitHubProjectItem ProjectItem() => new(
        new GitHubWorkItemAddress("github.com", "owner", "repo", 1),
        new WorkItemSummary(
            new WorkItemId("github:owner/repo#1"),
            "Item",
            "https://github.com/owner/repo/issues/1",
            "Todo",
            "P1"),
        "ISSUE",
        "ITEM");

    private sealed class RestQueueGhProcess(params string[] responses) : IGhProcess
    {
        private readonly Queue<string> responses = new(responses);

        public List<Call> Calls { get; } = [];

        public Task<GhProcessResult> RunAsync(
            IReadOnlyList<string> arguments,
            string? standardInput,
            CancellationToken cancellationToken)
        {
            Calls.Add(new Call(arguments, standardInput));
            return Task.FromResult(new GhProcessResult(0, responses.Dequeue(), string.Empty));
        }

        public sealed record Call(
            IReadOnlyList<string> Arguments,
            string? StandardInput);
    }

    private sealed class QueueGhProcess(params string[] responses) : IGhProcess
    {
        private readonly Queue<string> responses = new(responses);

        public List<Call> Calls { get; } = [];

        public Task<GhProcessResult> RunAsync(
            IReadOnlyList<string> arguments,
            string? standardInput,
            CancellationToken cancellationToken)
        {
            if (arguments.Contains("--header"))
            {
                return Task.FromResult(new GhProcessResult(
                    1,
                    string.Empty,
                    "HTTP 404: REST Projects API unavailable"));
            }

            Calls.Add(new Call(arguments, standardInput));
            return Task.FromResult(new GhProcessResult(0, responses.Dequeue(), string.Empty));
        }

        public sealed record Call(
            IReadOnlyList<string> Arguments,
            string? StandardInput);
    }

    private sealed class MemoryCache : INodeIdCache
    {
        private readonly Dictionary<string, ProjectMetadata> entries = [];

        public int Invalidations { get; private set; }

        public int Puts { get; private set; }

        public ProjectMetadata? LastValue { get; private set; }

        public Task<ProjectMetadata?> GetAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(entries.GetValueOrDefault(key));

        public Task PutAsync(
            string key,
            ProjectMetadata value,
            CancellationToken cancellationToken)
        {
            Puts++;
            LastValue = value;
            entries[key] = value;
            return Task.CompletedTask;
        }

        public Task InvalidateAsync(string key, CancellationToken cancellationToken)
        {
            Invalidations++;
            entries.Remove(key);
            return Task.CompletedTask;
        }
    }
}

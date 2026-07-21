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
        Assert.Contains("Creation attempt ID", process.Calls[0].StandardInput);
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
            InitializedDiscoveryResponse);
        var cache = new MemoryCache();
        var client = new GitHubProjectClient(new GhApi(process), cache);

        var result = await client.InitializeAsync(Config, checkOnly: false, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(6, result.Actions.Count);
        Assert.Equal(8, process.Calls.Count);
        Assert.Contains("Current agent type", process.Calls[1].StandardInput);
        Assert.Contains("SINGLE_SELECT", process.Calls[1].StandardInput);
        Assert.Contains("Current session ID", process.Calls[2].StandardInput);
        Assert.Contains("TEXT", process.Calls[2].StandardInput);
        Assert.Contains("Current claimant kind", process.Calls[3].StandardInput);
        Assert.Contains("Current claimant", process.Calls[4].StandardInput);
        Assert.Contains("Creation attempt ID", process.Calls[5].StandardInput);
        Assert.Contains("Current workspace path", process.Calls[6].StandardInput);
        Assert.Equal(1, cache.Puts);
        Assert.Equal("AGENT_FIELD", cache.LastValue!.AgentTypeFieldId);
        Assert.Equal("SESSION_FIELD", cache.LastValue.SessionIdFieldId);
        Assert.Equal("CREATION_FIELD", cache.LastValue.CreationAttemptIdFieldId);
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
            InitializedDiscoveryResponse);
        var client = new GitHubProjectClient(new GhApi(process), new MemoryCache());

        var result = await client.InitializeAsync(Config, checkOnly: false, CancellationToken.None);

        Assert.True(result.Changed);
        Assert.Equal(4, process.Calls.Count);
        Assert.Contains(result.Actions, action =>
            action.Contains("add options Claude, Copilot, Other", StringComparison.Ordinal));
        Assert.Contains("\"id\":\"CODEX\"", process.Calls[1].StandardInput);
        Assert.Contains("\"name\":\"Claude\"", process.Calls[1].StandardInput);
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
                      "id": "AGENT_FIELD", "name": "Current agent type", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "CODEX", "name": "Codex", "description": "", "color": "GREEN" },
                        { "id": "CLAUDE", "name": "Claude", "description": "", "color": "ORANGE" },
                        { "id": "COPILOT", "name": "Copilot", "description": "", "color": "BLUE" },
                        { "id": "OTHER", "name": "Other", "description": "", "color": "GRAY" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2Field",
                      "id": "SESSION_FIELD", "name": "Current session ID", "dataType": "TEXT"
                    },
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "CLAIMANT_KIND_FIELD", "name": "Current claimant kind", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "AGENT", "name": "Agent", "description": "", "color": "GREEN" },
                        { "id": "HUMAN", "name": "Human", "description": "", "color": "BLUE" },
                        { "id": "AUTOMATION", "name": "Automation", "description": "", "color": "ORANGE" },
                        { "id": "UNKNOWN", "name": "Unknown", "description": "", "color": "GRAY" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2Field",
                      "id": "CLAIMANT_ID_FIELD", "name": "Current claimant", "dataType": "TEXT"
                    },
                    {
                      "__typename": "ProjectV2Field",
                      "id": "CREATION_FIELD", "name": "Creation attempt ID", "dataType": "TEXT"
                    },
                    {
                      "__typename": "ProjectV2Field",
                      "id": "WORKSPACE_FIELD", "name": "Current workspace path", "dataType": "TEXT"
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
                      "id": "AGENT_FIELD", "name": "Current agent type", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "CODEX", "name": "Codex", "description": "Keep me", "color": "GREEN" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2Field",
                      "id": "SESSION_FIELD", "name": "Current session ID", "dataType": "TEXT"
                    },
                    {
                      "__typename": "ProjectV2SingleSelectField",
                      "id": "CLAIMANT_KIND_FIELD", "name": "Current claimant kind", "dataType": "SINGLE_SELECT",
                      "options": [
                        { "id": "AGENT", "name": "Agent", "description": "", "color": "GREEN" },
                        { "id": "HUMAN", "name": "Human", "description": "", "color": "BLUE" },
                        { "id": "AUTOMATION", "name": "Automation", "description": "", "color": "ORANGE" },
                        { "id": "UNKNOWN", "name": "Unknown", "description": "", "color": "GRAY" }
                      ]
                    },
                    {
                      "__typename": "ProjectV2Field",
                      "id": "CLAIMANT_ID_FIELD", "name": "Current claimant", "dataType": "TEXT"
                    },
                    {
                      "__typename": "ProjectV2Field",
                      "id": "WORKSPACE_FIELD", "name": "Current workspace path", "dataType": "TEXT"
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
        ClaimantKindFieldId: "CLAIMANT_KIND_FIELD",
        ClaimantKindOptions: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent"] = "AGENT",
            ["Human"] = "HUMAN",
            ["Automation"] = "AUTOMATION",
            ["Unknown"] = "UNKNOWN"
        },
        ClaimantIdFieldId: "CLAIMANT_ID_FIELD",
        WorkspacePathFieldId: "WORKSPACE_FIELD");

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

    private sealed class QueueGhProcess(params string[] responses) : IGhProcess
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

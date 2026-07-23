using System.Text.Json;
using Highbyte.Wrighty.Cli;
using Highbyte.Wrighty.Cli.Output;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Projects;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Initialization;
using Highbyte.Wrighty.Importing;
using Highbyte.Wrighty.Cli.Skills;

namespace Highbyte.Wrighty.UnitTests.Output;

public sealed class OutputWriterTests
{
    private static readonly WorkItemId ItemId = new("github:owner/repo#42");

    [Fact]
    public async Task Workspaces_output_lists_state_or_reports_none()
    {
        var output = new StringWriter();
        var writer = new OutputWriter(output, new StringWriter());
        await writer.WriteWorkspacesAsync([], json: false);
        Assert.Contains("No retained worker worktrees.", output.ToString());

        var entries = new List<(Highbyte.Wrighty.Workers.WorkerWorkspaceInfo, string?)>
        {
            (new Highbyte.Wrighty.Workers.WorkerWorkspaceInfo(
                "/tmp/repo.worktrees/local-1-abc", "wrighty-worker/local-1-abc", true, false),
                "local:1"),
            (new Highbyte.Wrighty.Workers.WorkerWorkspaceInfo(
                "/tmp/repo.worktrees/stray", null, false, false), null)
        };
        output.GetStringBuilder().Clear();
        await writer.WriteWorkspacesAsync(entries, json: false);
        var human = output.ToString();
        Assert.Contains("[dirty, unmerged] branch wrighty-worker/local-1-abc item local:1", human);
        Assert.Contains("/tmp/repo.worktrees/stray [clean, unmerged]", human);

        output.GetStringBuilder().Clear();
        await writer.WriteWorkspacesAsync(entries, json: true);
        using var document = JsonDocument.Parse(output.ToString());
        var workspaces = document.RootElement.GetProperty("result").GetProperty("workspaces");
        Assert.Equal(2, workspaces.GetArrayLength());
        Assert.Equal("local:1", workspaces[0].GetProperty("itemId").GetString());
        Assert.True(workspaces[0].GetProperty("dirty").GetBoolean());
        Assert.False(workspaces[1].TryGetProperty("branch", out _));
    }

    [Fact]
    public async Task Status_groups_items_and_shows_last_run_and_worktree_state()
    {
        var output = new StringWriter();
        var writer = new OutputWriter(output, new StringWriter());

        var attention = Operational(
            "local:1", "Blocked item", "In Progress", WorkItemActivities.NeedsAttention,
            new AgentSessionRecord("claude", "s1", "/tmp/ws1", DateTimeOffset.UnixEpoch, true,
                "feature/a", RunOutcome.Succeeded, "Need the API key.\nSecond line.",
                DateTimeOffset.UnixEpoch));
        var completed = Operational(
            "local:2", "Done item", "Done", WorkItemActivities.Completed,
            new AgentSessionRecord("claude", "s2", "/tmp/ws2", DateTimeOffset.UnixEpoch, true,
                "feature/b", RunOutcome.Succeeded, "All done.", DateTimeOffset.UnixEpoch));
        var queued = Operational(
            "local:3", "Queued item", "In Progress", WorkItemActivities.Queued, null);

        var statuses = new Dictionary<string, Highbyte.Wrighty.Workers.WorkspaceStatusResult>
        {
            ["local:2"] = new(new Highbyte.Wrighty.Workers.WorkspaceStatus(
                Dirty: true, MergedIntoHead: false), null)
        };

        await writer.WriteStatusAsync(
            [attention, completed, queued], statuses, integration: "merge-local",
            json: false, formatShort: id => $"#{id.Value.Split(':')[^1]}");
        var human = output.ToString();
        Assert.Contains("Needs attention (1)", human);
        Assert.Contains("last run: succeeded — Need the API key.", human);
        Assert.Contains("wrighty edit local:1 --takeover", human);
        Assert.Contains("Completed — retained worktree (1)", human);
        Assert.Contains("branch feature/b (dirty, unmerged)", human);
        Assert.Contains("Queued (1)", human);

        output.GetStringBuilder().Clear();
        await writer.WriteStatusAsync(
            [attention, completed, queued], statuses, integration: "merge-local",
            json: true, formatShort: id => $"#{id.Value.Split(':')[^1]}");
        using var document = JsonDocument.Parse(output.ToString());
        var result = document.RootElement.GetProperty("result");
        Assert.Equal(1, result.GetProperty("needsAttention").GetArrayLength());
        Assert.Equal(1, result.GetProperty("completed").GetArrayLength());
        Assert.Equal(1, result.GetProperty("queued").GetArrayLength());
        Assert.Equal("succeeded",
            result.GetProperty("needsAttention")[0].GetProperty("lastRun")
                .GetProperty("outcome").GetString());
        Assert.True(result.GetProperty("completed")[0].GetProperty("worktree")
            .GetProperty("dirty").GetBoolean());
    }

    [Fact]
    public async Task Status_renders_a_failed_run_label_and_a_removed_worktree()
    {
        var output = new StringWriter();
        var writer = new OutputWriter(output, new StringWriter());

        // Failed outcome with no final message exercises the failed label and the empty-excerpt path.
        var paused = Operational(
            "local:4", "Paused item", "In Progress", WorkItemActivities.PausedSession,
            new AgentSessionRecord("codex", "s4", "/tmp/ws4", DateTimeOffset.UnixEpoch, true,
                "feature/p", RunOutcome.Failed, null, DateTimeOffset.UnixEpoch));
        // A completed item whose worktree is gone exercises the "removed" branch.
        var completedGone = Operational(
            "local:5", "Removed item", "Done", WorkItemActivities.Completed,
            new AgentSessionRecord("claude", "s5", "/tmp/ws5", DateTimeOffset.UnixEpoch, true,
                "feature/m", RunOutcome.Succeeded, "done", DateTimeOffset.UnixEpoch));

        var statuses = new Dictionary<string, Highbyte.Wrighty.Workers.WorkspaceStatusResult>
        {
            ["local:5"] = new(null, null, WorktreeAbsent: true)
        };

        await writer.WriteStatusAsync(
            [paused, completedGone], statuses, integration: null,
            json: false, formatShort: id => id.Value);
        var human = output.ToString();
        Assert.Contains("last run: failed", human);
        Assert.Contains("worktree: removed", human);
    }

    [Fact]
    public async Task Status_reports_nothing_when_all_groups_are_empty()
    {
        var output = new StringWriter();
        var writer = new OutputWriter(output, new StringWriter());
        await writer.WriteStatusAsync(
            [Operational("local:1", "Ready item", "Todo", WorkItemActivities.Ready, null)],
            new Dictionary<string, Highbyte.Wrighty.Workers.WorkspaceStatusResult>(),
            integration: null, json: false, formatShort: id => id.Value);
        Assert.Contains("Nothing needs attention", output.ToString());
    }

    private static WorkItemOperationalState Operational(
        string id, string title, string status, string activity, AgentSessionRecord? session) =>
        new(
            new WorkItemDetail(new WorkItemId(id), title, "Body", null, status, null),
            new WorkItemClaimSummary(ClaimOwnershipState.Unclaimed),
            session,
            activity);

    [Fact]
    public async Task Workspace_cleanup_output_reports_what_happened()
    {
        var output = new StringWriter();
        var writer = new OutputWriter(output, new StringWriter());
        await writer.WriteWorkspaceCleanupAsync(
            new WorkItemId("local:1"), "#1", "/tmp/ws", "wrighty-worker/x",
            workspaceRemoved: true, branchDeleted: true, json: false);
        Assert.Contains(
            "cleaned up #1: workspace removed, branch deleted (wrighty-worker/x)",
            output.ToString());

        output.GetStringBuilder().Clear();
        await writer.WriteWorkspaceCleanupAsync(
            new WorkItemId("local:2"), "#2", null, null,
            workspaceRemoved: false, branchDeleted: false, json: false);
        Assert.Contains(
            "cleaned up #2: workspace already absent, branch not recorded",
            output.ToString());

        output.GetStringBuilder().Clear();
        await writer.WriteWorkspaceCleanupAsync(
            new WorkItemId("local:3"), "#3", "/tmp/ws3", "wrighty-worker/y",
            workspaceRemoved: false, branchDeleted: false, json: false);
        Assert.Contains("branch already absent", output.ToString());

        output.GetStringBuilder().Clear();
        await writer.WriteWorkspaceCleanupAsync(
            new WorkItemId("local:1"), "#1", "/tmp/ws", "wrighty-worker/x",
            workspaceRemoved: true, branchDeleted: false, json: true);
        using var document = JsonDocument.Parse(output.ToString());
        var result = document.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("workspaceRemoved").GetBoolean());
        Assert.False(result.GetProperty("branchDeleted").GetBoolean());
        Assert.Equal("wrighty-worker/x", result.GetProperty("branch").GetString());
    }

    [Fact]
    public async Task Json_initialization_output_reports_validation_without_changes()
    {
        var output = new StringWriter();
        var writer = new OutputWriter(output, new StringWriter());

        await writer.WriteInitializationAsync(
            new TrackerInitializationResult(
                new TrackerConfig
                {
                    Repository = "owner/repo",
                    ProjectOwner = "owner",
                    ProjectNumber = 1,
                    Worker = new WorkerConfig { DefaultAgent = "claude" }
                },
                "/tmp/.wrighty.json",
                "Tracker",
                "https://github.com/users/owner/projects/1",
                false,
                true,
                false,
                ["Project schema is valid."]),
            checkOnly: true,
            json: true);

        using var document = JsonDocument.Parse(output.ToString());
        var result = document.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("valid").GetBoolean());
        Assert.False(result.GetProperty("initialized").GetBoolean());
        Assert.False(result.GetProperty("changed").GetBoolean());
        Assert.Equal(
            "claude",
            result.GetProperty("worker").GetProperty("defaultAgent").GetString());
    }

    [Fact]
    public async Task Compact_output_is_one_stable_line_per_item()
    {
        var output = new StringWriter();
        var writer = new OutputWriter(output, new StringWriter());
        var item = new WorkItemSummary(
            new WorkItemId("github:owner/repo#42"),
            "Fix timer\nrollover",
            "https://example.test/42",
            "In Progress",
            "P1");

        await writer.WriteItemsAsync([item], compact: true, json: false, _ => "#42");

        Assert.Equal($"#42 inprogress p1 Fix timer rollover{Environment.NewLine}", output.ToString());
    }

    [Fact]
    public async Task Json_errors_have_a_versioned_machine_readable_envelope()
    {
        var error = new StringWriter();
        var writer = new OutputWriter(new StringWriter(), error);

        var exitCode = await writer.WriteErrorAsync(
            new TrackerException("CLAIM_HELD", "Held", 6),
            json: true);

        using var document = JsonDocument.Parse(error.ToString());
        Assert.Equal(6, exitCode);
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(
            "CLAIM_HELD",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Json_claim_output_uses_standard_identity_and_id_names()
    {
        var output = new StringWriter();
        var writer = new OutputWriter(output, new StringWriter());
        var claim = new ClaimResult(
            ClaimOutcome.Acquired,
            "worker-1",
            DateTimeOffset.Parse("2026-07-13T11:00:00Z"),
            "claim-attempt-1",
            "codex",
            "session-1",
            "agent");

        await writer.WriteClaimAsync(
            new WorkItemId("github:owner/repo#42"),
            "#42",
            claim,
            json: true);

        using var document = JsonDocument.Parse(output.ToString());
        var result = document.RootElement.GetProperty("result");
        Assert.Equal("worker-1", result.GetProperty("workerIdentity").GetString());
        Assert.Equal("claim-attempt-1", result.GetProperty("claimAttemptId").GetString());
        Assert.Equal("codex", result.GetProperty("agentType").GetString());
        Assert.Equal("session-1", result.GetProperty("sessionId").GetString());
        Assert.Equal("agent", result.GetProperty("claimantKind").GetString());
        Assert.False(result.TryGetProperty("agent", out _));
        Assert.False(result.TryGetProperty("attempt", out _));
    }

    [Fact]
    public async Task Json_claim_output_omits_unavailable_optional_context()
    {
        var output = new StringWriter();
        var writer = new OutputWriter(output, new StringWriter());
        var claim = new ClaimResult(
            ClaimOutcome.Acquired,
            "worker-1",
            DateTimeOffset.Parse("2026-07-13T11:00:00Z"),
            "claim-attempt-1");

        await writer.WriteClaimAsync(
            new WorkItemId("github:owner/repo#42"),
            "#42",
            claim,
            json: true);

        using var document = JsonDocument.Parse(output.ToString());
        var result = document.RootElement.GetProperty("result");
        Assert.False(result.TryGetProperty("agentType", out _));
        Assert.False(result.TryGetProperty("sessionId", out _));
        Assert.Equal("unknown", result.GetProperty("claimantKind").GetString());
    }

    [Theory]
    [InlineData(ClaimOutcome.Acquired, "claimed")]
    [InlineData(ClaimOutcome.AlreadyOwned, "already own")]
    public async Task Human_claim_output_returns_the_handle_needed_for_mutations(
        ClaimOutcome outcome,
        string expectedVerb)
    {
        var output = new StringWriter();
        var claim = new ClaimResult(
            outcome,
            "worker-1",
            DateTimeOffset.Parse("2026-07-13T11:00:00Z"),
            ClaimantId: "agent:session-1",
            ClaimToken: "token-1",
            AgentType: "codex",
            SessionId: "session-1",
            ClaimantKind: "agent",
            TakeoverAvailable: true);

        await new OutputWriter(output, new StringWriter()).WriteClaimAsync(
            ItemId,
            "#42",
            claim,
            json: false);

        Assert.Contains($"{expectedVerb} #42 as claimant agent:session-1", output.ToString());
        Assert.Contains("Claim token: token-1", output.ToString());
        Assert.Contains("--claim-token or WRIGHTY_CLAIM_TOKEN", output.ToString());
    }

    [Fact]
    public async Task Human_claim_output_does_not_invent_an_unavailable_token()
    {
        var output = new StringWriter();
        var claim = new ClaimResult(
            ClaimOutcome.HeldByOther,
            "worker-1",
            DateTimeOffset.Parse("2026-07-13T11:00:00Z"),
            ClaimantId: "agent:session-1",
            ClaimantKind: "agent");

        await new OutputWriter(output, new StringWriter()).WriteClaimAsync(
            ItemId,
            "#42",
            claim,
            json: false);

        Assert.Contains("claimed #42 as claimant agent:session-1", output.ToString());
        Assert.DoesNotContain("Claim token:", output.ToString());
        Assert.DoesNotContain("WRIGHTY_CLAIM_TOKEN", output.ToString());
    }

    [Fact]
    public async Task Pick_output_returns_the_complete_claim_handle_in_human_and_json_formats()
    {
        var item = new WorkItemSummary(ItemId, "Picked item", null, "In Progress", "P1");
        var claim = new ClaimResult(
            ClaimOutcome.Acquired,
            "worker-1",
            DateTimeOffset.Parse("2026-07-13T11:00:00Z"),
            ClaimantId: "agent:session-1",
            ClaimToken: "token-1",
            AgentType: "codex",
            SessionId: "session-1",
            ClaimantKind: "agent",
            TakeoverAvailable: true);
        var picked = new PickWorkItemResult(item, claim);
        var human = new StringWriter();
        var json = new StringWriter();

        await new OutputWriter(human, new StringWriter()).WritePickedAsync(picked, false, _ => "#42");
        await new OutputWriter(json, new StringWriter()).WritePickedAsync(picked, true, _ => "#42");

        Assert.Contains("#42 inprogress p1 Picked item", human.ToString());
        Assert.Contains("Claimant ID: agent:session-1", human.ToString());
        Assert.Contains("Claim token: token-1", human.ToString());
        Assert.Contains("Pass both values on every later mutation.", human.ToString());

        using var document = JsonDocument.Parse(json.ToString());
        var result = document.RootElement.GetProperty("result");
        Assert.Equal("github:owner/repo#42", result.GetProperty("item").GetProperty("id").GetString());
        Assert.Equal("agent:session-1", result.GetProperty("claimantId").GetString());
        Assert.Equal("token-1", result.GetProperty("claimToken").GetString());
        Assert.Equal("codex", result.GetProperty("agentType").GetString());
        Assert.Equal("session-1", result.GetProperty("sessionId").GetString());
        Assert.Equal("agent", result.GetProperty("claimantKind").GetString());
        Assert.True(result.GetProperty("takeoverAvailable").GetBoolean());
    }

    [Fact]
    public async Task Json_list_output_uses_canonical_ids_and_cannot_leak_node_ids()
    {
        var output = new StringWriter();
        var writer = new OutputWriter(output, new StringWriter());
        var item = new WorkItemSummary(
            new WorkItemId("github:owner/repo#42"),
            "Example",
            "https://github.com/owner/repo/issues/42",
            "Todo",
            "P1");

        await writer.WriteItemsAsync([item], false, true, _ => "#42");

        using var document = JsonDocument.Parse(output.ToString());
        var result = Assert.Single(document.RootElement.GetProperty("result").EnumerateArray());
        Assert.Equal("github:owner/repo#42", result.GetProperty("id").GetString());
        Assert.Equal("#42", result.GetProperty("displayId").GetString());
        Assert.False(result.TryGetProperty("issueNodeId", out _));
        Assert.False(result.TryGetProperty("projectItemId", out _));
        Assert.False(result.TryGetProperty("issueNumber", out _));
        Assert.False(result.TryGetProperty("repository", out _));
    }

    [Fact]
    public async Task Human_partial_create_error_includes_recovery_identifiers()
    {
        var error = new StringWriter();
        var writer = new OutputWriter(new StringWriter(), error);
        var exception = new TrackerException(
            "PARTIAL_CREATE",
            "Partially created.",
            10,
            new Dictionary<string, object?>
            {
                ["id"] = "github:owner/repo#43",
                ["displayId"] = "#43",
                ["url"] = "https://github.com/owner/repo/issues/43",
                ["failedStage"] = "project-add"
            });

        await writer.WriteErrorAsync(exception, json: false);

        Assert.Contains("github:owner/repo#43", error.ToString());
        Assert.Contains("project-add", error.ToString());
    }

    [Fact]
    public async Task Json_update_output_contains_changed_fields_and_final_detail()
    {
        var output = new StringWriter();
        var writer = new OutputWriter(output, new StringWriter());
        var id = new WorkItemId("github:owner/repo#42");
        var result = new UpdateWorkItemResult(
            new WorkItemDetail(
                id,
                "Example",
                "Body",
                "https://github.com/owner/repo/issues/42",
                "Done",
                null),
            true,
            ["priority", "status"]);

        await writer.WriteUpdateAsync(result, move: false, json: true, _ => "#42");

        using var document = JsonDocument.Parse(output.ToString());
        var payload = document.RootElement.GetProperty("result");
        Assert.Equal("github:owner/repo#42", payload.GetProperty("id").GetString());
        Assert.Equal(2, payload.GetProperty("changedFields").GetArrayLength());
        Assert.Equal("Done", payload.GetProperty("item").GetProperty("status").GetString());
        Assert.DoesNotContain("NodeId", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Human_partial_update_error_formats_progress_lists()
    {
        var error = new StringWriter();
        var writer = new OutputWriter(new StringWriter(), error);
        var exception = new TrackerException(
            "PARTIAL_UPDATE",
            "Partially updated.",
            10,
            new Dictionary<string, object?>
            {
                ["appliedFields"] = new[] { "title", "body" },
                ["pendingFields"] = new[] { "priority", "status" },
                ["causeCode"] = "GH_API_ERROR"
            });

        await writer.WriteErrorAsync(exception, json: false);

        Assert.Contains("appliedFields: title, body", error.ToString());
        Assert.Contains("pendingFields: priority, status", error.ToString());
    }

    [Fact]
    public async Task Human_detail_output_formats_optional_values_and_preserves_body()
    {
        var output = new StringWriter();
        var writer = new OutputWriter(output, new StringWriter());

        await writer.WriteDetailAsync(
            new WorkItemDetail(ItemId, "Title\ncontinued", "Body without newline", null, null, null, true),
            json: false,
            _ => "#42");

        Assert.Equal(
            $"#42 Title continued{Environment.NewLine}" +
            $"Status: -{Environment.NewLine}" +
            $"Priority: -{Environment.NewLine}" +
            $"Archived: yes{Environment.NewLine}{Environment.NewLine}" +
            $"Body without newline{Environment.NewLine}",
            output.ToString());
    }

    [Fact]
    public async Task Json_detail_output_contains_public_work_item_fields()
    {
        var output = new StringWriter();
        var writer = new OutputWriter(output, new StringWriter());

        await writer.WriteDetailAsync(
            new WorkItemDetail(ItemId, "Title", "Body\n", "https://example.test/42", "Todo", "P2"),
            json: true,
            _ => "#42");

        using var document = JsonDocument.Parse(output.ToString());
        var result = document.RootElement.GetProperty("result");
        Assert.Equal("github:owner/repo#42", result.GetProperty("id").GetString());
        Assert.Equal("#42", result.GetProperty("displayId").GetString());
        Assert.Equal("Body\n", result.GetProperty("body").GetString());
        Assert.False(result.GetProperty("archived").GetBoolean());
    }

    [Fact]
    public async Task Human_operational_detail_shows_worker_claim_session_and_actions()
    {
        var output = new StringWriter();
        var now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        var item = new WorkItemDetail(
            ItemId,
            "Needs clarification",
            "Body without newline",
            "https://example.test/42",
            "In Progress",
            "P1",
            AutomationEligible: true,
            PreferredAgent: "claude",
            WorkerState: WorkerDispatchStates.NeedsAttention,
            Fields: new Dictionary<string, JsonElement>
            {
                ["epic"] = JsonSerializer.SerializeToElement("PLAT-3")
            });
        var claim = new WorkItemClaimSummary(
            ClaimOwnershipState.OwnedByCurrent,
            "worker-1",
            now.AddMinutes(30),
            "claude",
            "session-1",
            "agent",
            "agent:worker:one",
            true,
            "/tmp/worktree");
        var session = new AgentSessionRecord(
            "claude", "session-1", "/tmp/worktree", now.AddMinutes(30), true);

        await new OutputWriter(output, new StringWriter(), () => now)
            .WriteOperationalDetailAsync(
                new WorkItemOperationalState(
                    item, claim, session, WorkItemActivities.NeedsAttention),
                json: false,
                _ => "#42");

        var text = output.ToString();
        Assert.Contains("#42 Needs clarification", text);
        Assert.Contains("Eligible: yes", text);
        Assert.Contains("Claimant: Agent (Claude)", text);
        Assert.Contains("Lease remaining: 30m left", text);
        Assert.Contains("Resume address complete: yes", text);
        Assert.Contains("Resumable here: yes", text);
        Assert.Contains("epic: PLAT-3", text);
        Assert.Contains("Next actions", text);
        Assert.Contains("wrighty worker --item github:owner/repo#42 --yes", text);
        Assert.EndsWith($"Body without newline{Environment.NewLine}", text);
    }

    [Fact]
    public async Task Operational_detail_renders_calculated_workspace_status()
    {
        var now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        var session = new AgentSessionRecord("claude", "session-1", "/tmp/worktree", now.AddMinutes(30), true);
        var state = State(WorkItemActivities.PausedSession, ClaimOwnershipState.Unclaimed, null, null, session);
        var status = new Highbyte.Wrighty.Workers.WorkspaceStatusResult(
            new Highbyte.Wrighty.Workers.WorkspaceStatus(Dirty: true, MergedIntoHead: false), null);
        var human = new StringWriter();
        var json = new StringWriter();

        await new OutputWriter(human, new StringWriter(), () => now)
            .WriteOperationalDetailAsync(state, json: false, _ => "#42", status);
        await new OutputWriter(json, new StringWriter(), () => now)
            .WriteOperationalDetailAsync(state, json: true, _ => "#42", status);

        var text = human.ToString();
        Assert.Contains("Working tree: dirty", text);
        Assert.Contains("Branch state: unmerged", text);

        using var document = JsonDocument.Parse(json.ToString());
        var workspaceStatus = document.RootElement.GetProperty("result")
            .GetProperty("session").GetProperty("workspaceStatus");
        Assert.True(workspaceStatus.GetProperty("available").GetBoolean());
        Assert.True(workspaceStatus.GetProperty("dirty").GetBoolean());
        Assert.False(workspaceStatus.GetProperty("mergedIntoHead").GetBoolean());
    }

    [Fact]
    public async Task Operational_detail_reports_unavailable_workspace_status()
    {
        var now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        var session = new AgentSessionRecord("claude", "session-1", "/tmp/worktree", now.AddMinutes(30), true);
        var state = State(WorkItemActivities.PausedSession, ClaimOwnershipState.Unclaimed, null, null, session);
        var status = new Highbyte.Wrighty.Workers.WorkspaceStatusResult(
            null, "The recorded worktree is not present on this host.");
        var human = new StringWriter();

        await new OutputWriter(human, new StringWriter(), () => now)
            .WriteOperationalDetailAsync(state, json: false, _ => "#42", status);

        Assert.Contains(
            "Worktree status: The recorded worktree is not present on this host.",
            human.ToString());
    }

    [Fact]
    public async Task Operational_detail_collapses_a_removed_worktree_to_one_line()
    {
        var now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        var session = new AgentSessionRecord(
            "codex", "session-1", "/tmp/worktrees/local-1", now.AddMinutes(30), true)
        {
            Branch = "wrighty-worker/local-1-abcd"
        };
        var state = State(WorkItemActivities.PausedSession, ClaimOwnershipState.Unclaimed, null, null, session);
        var status = new Highbyte.Wrighty.Workers.WorkspaceStatusResult(
            null, "The recorded worktree is not present on this host.", WorktreeAbsent: true);
        var human = new StringWriter();

        await new OutputWriter(human, new StringWriter(), () => now)
            .WriteOperationalDetailAsync(state, json: false, _ => "#42", status);

        var text = human.ToString();
        Assert.Contains("Worktree: removed", text);
        // The dead path and branch are collapsed away rather than shown as if they still exist.
        Assert.DoesNotContain("/tmp/worktrees/local-1", text);
        Assert.DoesNotContain("wrighty-worker/local-1-abcd", text);
        // A removed worktree cannot be resumed into, so it is reported as not resumable here.
        Assert.Contains("Resumable here: no", text);
    }

    [Fact]
    public async Task Json_operational_detail_omits_claim_and_session_data_when_unclaimed()
    {
        var output = new StringWriter();
        var item = new WorkItemDetail(
            ItemId, "Ready", "Body\n", null, "Todo", null,
            AutomationEligible: true);
        var claim = new WorkItemClaimSummary(ClaimOwnershipState.Unclaimed);

        await new OutputWriter(output, new StringWriter())
            .WriteOperationalDetailAsync(
                new WorkItemOperationalState(item, claim, null, WorkItemActivities.Ready),
                json: true,
                _ => "#42");

        using var document = JsonDocument.Parse(output.ToString());
        var result = document.RootElement.GetProperty("result");
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(WorkItemActivities.Ready, result.GetProperty("worker").GetProperty("activity").GetString());
        Assert.False(result.TryGetProperty("session", out _));
        Assert.False(
            result.GetProperty("claim").TryGetProperty("workerIdentity", out _));
    }

    [Fact]
    public async Task Operational_lists_cover_every_activity_and_lease_shape()
    {
        var now = DateTimeOffset.Parse("2026-07-19T12:00:00Z");
        var session = new AgentSessionRecord(
            "claude", "session-1", "/tmp/repo", now.AddHours(2), true);
        var activities = new[]
        {
            State(WorkItemActivities.NeedsAttention, ClaimOwnershipState.OwnedByCurrent,
                now.AddMinutes(-1), "agent:worker:attention", session),
            State(WorkItemActivities.AgentActive, ClaimOwnershipState.OwnedByCurrent,
                now.AddMinutes(30), "agent:worker:active", session),
            State(WorkItemActivities.AgentActive, ClaimOwnershipState.HeldByOther,
                now.AddHours(1), "agent:interactive", session),
            State(WorkItemActivities.Queued, ClaimOwnershipState.Unclaimed,
                null, null, session),
            State(WorkItemActivities.PausedSession, ClaimOwnershipState.Unclaimed,
                null, null, session),
            State(WorkItemActivities.HumanEditing, ClaimOwnershipState.OwnedByCurrent,
                now.AddMinutes(75), "human-cli", null),
            State(WorkItemActivities.AutomationActive, ClaimOwnershipState.HeldByOther,
                now.AddHours(2), "automation:one", null),
            State(WorkItemActivities.Ready, ClaimOwnershipState.Unclaimed,
                null, null, null),
            State(WorkItemActivities.None, ClaimOwnershipState.Unclaimed,
                null, null, null)
        };
        var compact = new StringWriter();
        var table = new StringWriter();
        var json = new StringWriter();

        await new OutputWriter(compact, new StringWriter(), () => now)
            .WriteOperationalItemsAsync(activities, compact: true, json: false, _ => "#42");
        await new OutputWriter(table, new StringWriter(), () => now)
            .WriteOperationalItemsAsync(activities, compact: false, json: false, _ => "#42");
        await new OutputWriter(json, new StringWriter(), () => now)
            .WriteOperationalItemsAsync(activities, compact: false, json: true, _ => "#42");

        var compactText = compact.ToString();
        Assert.Contains("!attention lease:expired", compactText);
        Assert.Contains("processing:claude lease:30m", compactText);
        Assert.Contains("claimed:claude lease:1h", compactText);
        Assert.Contains("queued:claude", compactText);
        Assert.Contains("paused:claude", compactText);
        Assert.Contains("human lease:1h15m", compactText);
        Assert.Contains("automation lease:2h", compactText);
        Assert.Contains(" ready ", compactText);
        Assert.Contains(" - ", compactText);

        var tableText = table.ToString();
        Assert.Contains("Needs attention", tableText);
        Assert.Contains("Claude processing", tableText);
        Assert.Contains("Claude claimed", tableText);
        Assert.Contains("Queued to resume", tableText);
        Assert.Contains("Paused session available", tableText);
        Assert.Contains("Human editing", tableText);
        Assert.Contains("Automation active", tableText);
        Assert.Contains("expired", tableText);
        Assert.Contains("1h15m left", tableText);

        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal(activities.Length, document.RootElement.GetProperty("result").GetArrayLength());
    }

    private static WorkItemOperationalState State(
        string activity,
        ClaimOwnershipState claimState,
        DateTimeOffset? expiresAt,
        string? claimantId,
        AgentSessionRecord? session,
        string? url = null)
    {
        var item = new WorkItemDetail(
            ItemId,
            $"Item {activity}",
            "Body",
            url,
            "In Progress",
            "P1",
            AutomationEligible: activity != WorkItemActivities.None,
            PreferredAgent: activity == WorkItemActivities.Ready ? null : "claude");
        var claim = new WorkItemClaimSummary(
            claimState,
            claimState == ClaimOwnershipState.Unclaimed ? null : "worker-1",
            expiresAt,
            "claude",
            session?.SessionId,
            activity == WorkItemActivities.HumanEditing ? "human" : "agent",
            claimantId,
            claimState == ClaimOwnershipState.OwnedByCurrent,
            session?.WorkspacePath);
        return new WorkItemOperationalState(item, claim, session, activity);
    }

    [Fact]
    public async Task Operational_actions_point_github_items_at_the_issue_url_not_the_web_ui()
    {
        var now = DateTimeOffset.Parse("2026-07-22T12:00:00Z");
        var session = new AgentSessionRecord("codex", "s1", "/tmp/ws", now.AddMinutes(30), true);

        // A GitHub item (carries a URL) must point at the issue, never the Local-Markdown-only web UI.
        var githubOut = new StringWriter();
        await new OutputWriter(githubOut, new StringWriter(), () => now).WriteOperationalDetailAsync(
            State(WorkItemActivities.NeedsAttention, ClaimOwnershipState.OwnedByCurrent,
                now.AddMinutes(30), "agent:worker:1", session, url: "https://github.com/o/r/issues/1"),
            json: false, _ => "#1");
        var githubText = githubOut.ToString();
        Assert.Contains("Review on GitHub: https://github.com/o/r/issues/1", githubText);
        Assert.DoesNotContain("wrighty web", githubText);

        // A Local Markdown item (no URL) keeps the web-UI action.
        var localOut = new StringWriter();
        await new OutputWriter(localOut, new StringWriter(), () => now).WriteOperationalDetailAsync(
            State(WorkItemActivities.NeedsAttention, ClaimOwnershipState.OwnedByCurrent,
                now.AddMinutes(30), "agent:worker:1", session),
            json: false, _ => "#1");
        Assert.Contains("Open web UI: wrighty web", localOut.ToString());
    }

    [Fact]
    public async Task Detail_output_surfaces_backend_neutral_custom_fields()
    {
        var jsonOutput = new StringWriter();
        var humanOutput = new StringWriter();
        var fields = new Dictionary<string, JsonElement>
        {
            ["epic"] = JsonSerializer.SerializeToElement("PLAT-3"),
            ["estimate"] = JsonSerializer.SerializeToElement(5)
        };
        var item = new WorkItemDetail(ItemId, "Title", "Body\n", null, "Todo", null, Fields: fields);

        await new OutputWriter(jsonOutput, new StringWriter()).WriteDetailAsync(item, true, _ => "#42");
        await new OutputWriter(humanOutput, new StringWriter()).WriteDetailAsync(item, false, _ => "#42");

        using var document = JsonDocument.Parse(jsonOutput.ToString());
        var outputFields = document.RootElement.GetProperty("result").GetProperty("fields");
        Assert.Equal("PLAT-3", outputFields.GetProperty("epic").GetString());
        Assert.Equal(5, outputFields.GetProperty("estimate").GetInt32());
        Assert.Contains("epic: PLAT-3", humanOutput.ToString());
        Assert.Contains("estimate: 5", humanOutput.ToString());
    }

    [Theory]
    [InlineData(true, true, "archived #42")]
    [InlineData(false, true, "#42 is already archived")]
    [InlineData(true, false, "unarchived #42")]
    [InlineData(false, false, "#42 is already active")]
    public async Task Human_archive_output_describes_change(
        bool changed,
        bool archived,
        string expected)
    {
        var output = new StringWriter();
        var writer = new OutputWriter(output, new StringWriter());
        var item = new WorkItemDetail(ItemId, "Title", "Body", null, "Todo", null, archived);

        await writer.WriteArchiveAsync(
            new ArchiveWorkItemResult(item, changed, archived),
            json: false,
            _ => "#42");

        Assert.Equal(expected + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task Json_archive_output_includes_final_item()
    {
        var output = new StringWriter();
        var writer = new OutputWriter(output, new StringWriter());
        var item = new WorkItemDetail(ItemId, "Title", "Body", null, "Todo", null, true);

        await writer.WriteArchiveAsync(
            new ArchiveWorkItemResult(item, true, true),
            json: true,
            _ => "#42");

        using var document = JsonDocument.Parse(output.ToString());
        var result = document.RootElement.GetProperty("result");
        Assert.True(result.GetProperty("archived").GetBoolean());
        Assert.True(result.GetProperty("changed").GetBoolean());
        Assert.True(result.GetProperty("item").GetProperty("archived").GetBoolean());
    }

    [Fact]
    public async Task Skill_operation_output_supports_human_and_json_formats()
    {
        var result = new SkillOperationResult(
            "codex", "project", "/tmp/SKILL.md", SkillInstallationState.Outdated,
            SkillInstallationState.Current, true, "1.0", "2.0", true);
        var human = new StringWriter();
        var json = new StringWriter();

        await new OutputWriter(human, new StringWriter()).WriteSkillOperationsAsync(
            [result], "update", json: false);
        await new OutputWriter(json, new StringWriter()).WriteSkillOperationsAsync(
            [result], "update", json: true);

        Assert.Equal($"codex: current /tmp/SKILL.md (changed){Environment.NewLine}", human.ToString());
        using var document = JsonDocument.Parse(json.ToString());
        var payload = document.RootElement.GetProperty("result");
        Assert.Equal("update", payload.GetProperty("operation").GetString());
        var installation = Assert.Single(payload.GetProperty("installations").EnumerateArray());
        Assert.Equal("outdated", installation.GetProperty("previousState").GetString());
        Assert.True(installation.GetProperty("descriptionPreserved").GetBoolean());
    }

    [Fact]
    public async Task Create_output_reports_resumed_reconciliation_in_human_and_json_formats()
    {
        var detail = new WorkItemDetail(ItemId, "Title", "Body", "https://example.test/42", "Todo", "P1");
        var result = new CreateWorkItemResult(
            ItemId,
            detail.Url,
            detail,
            "attempt-1",
            CreateDisposition.Resumed,
            ["issue", "project-add"]);
        var human = new StringWriter();
        var json = new StringWriter();

        await new OutputWriter(human, new StringWriter()).WriteCreateAsync(result, false, _ => "#42");
        await new OutputWriter(json, new StringWriter()).WriteCreateAsync(result, true, _ => "#42");

        Assert.Contains("resumed #42 https://example.test/42", human.ToString());
        Assert.Contains("reconciled: issue, project-add", human.ToString());
        using var document = JsonDocument.Parse(json.ToString());
        var payload = document.RootElement.GetProperty("result");
        Assert.Equal("resumed", payload.GetProperty("disposition").GetString());
        Assert.Equal(2, payload.GetProperty("reconciledStages").GetArrayLength());
    }

    [Theory]
    [InlineData(FinishDisposition.Finished, "finished #42 with status Done")]
    [InlineData(FinishDisposition.AlreadyFinished, "#42 is already finished")]
    public async Task Human_finish_output_describes_disposition(
        FinishDisposition disposition,
        string expected)
    {
        var output = new StringWriter();
        var item = new WorkItemDetail(ItemId, "Title", "Body", null, "Done", null);

        await new OutputWriter(output, new StringWriter()).WriteFinishAsync(
            new FinishWorkItemResult(item, disposition, true, true),
            json: false,
            _ => "#42");

        Assert.Equal(expected + Environment.NewLine, output.ToString());
    }

    [Fact]
    public async Task Human_initialization_output_distinguishes_local_store()
    {
        var output = new StringWriter();
        var result = new TrackerInitializationResult(
            new TrackerConfig { Backend = "local-markdown" },
            "/tmp/.wrighty.json",
            "explicit",
            "/tmp/items",
            false,
            false,
            true,
            ["Created store."]);

        await new OutputWriter(output, new StringWriter()).WriteInitializationAsync(
            result, checkOnly: false, json: false);

        Assert.Contains("Backend: local-markdown", output.ToString());
        Assert.Contains("Store: /tmp/items", output.ToString());
        Assert.Contains("Wrighty initialized", output.ToString());
        Assert.Contains("- Created store.", output.ToString());
    }

    [Fact]
    public async Task Portable_import_plan_supports_human_and_json_formats()
    {
        var source = new PortableImportSource(
            "/tmp/example.md",
            "Example",
            "Body",
            "Todo",
            null,
            ["epic"],
            "epic: PLAT-3");
        var human = new StringWriter();
        var json = new StringWriter();

        await new OutputWriter(human, new StringWriter())
            .WritePortableImportPlanAsync(source, "Todo", json: false);
        await new OutputWriter(json, new StringWriter())
            .WritePortableImportPlanAsync(source, "Todo", json: true);

        Assert.Contains("would import /tmp/example.md", human.ToString());
        Assert.Contains("source and tracker unchanged", human.ToString());
        using var document = JsonDocument.Parse(json.ToString());
        Assert.True(document.RootElement.GetProperty("result").GetProperty("dryRun").GetBoolean());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Whole_store_output_supports_dry_run_and_result_formats(bool json)
    {
        var output = new StringWriter();
        var summary = new WholeStoreImportSummary(
            DryRun: !json,
            ManifestPath: "/tmp/manifest.json",
            Planned: 1,
            EstimatedRemoteOperations: 3,
            PlannedItems: ["local:1 -> Todo / - / Example"],
            Created: 1,
            Resumed: 0,
            Skipped: 0,
            Failed: 0,
            ReferenceWarnings: ["#1"],
            BackendSwitchGuidance: "switch explicitly");

        await new OutputWriter(output, new StringWriter())
            .WriteWholeStoreImportAsync(summary, json);

        if (json)
        {
            using var document = JsonDocument.Parse(output.ToString());
            Assert.Equal(1, document.RootElement.GetProperty("result").GetProperty("created").GetInt32());
        }
        else
        {
            Assert.Contains("planned 1 item(s)", output.ToString());
            Assert.Contains("would import local:1", output.ToString());
            Assert.Contains("reference warning: #1", output.ToString());
        }
    }

    [Theory]
    [InlineData(AdoptDisposition.Adopted, "adopted")]
    [InlineData(AdoptDisposition.Reconciled, "reconciled")]
    [InlineData(AdoptDisposition.AlreadyAdopted, "already-adopted")]
    public async Task Adopt_output_supports_every_disposition(
        AdoptDisposition disposition,
        string expected)
    {
        var result = new AdoptWorkItemResult(
            ItemId,
            "42",
            "https://example.test/42",
            disposition,
            ["status"],
            []);
        var human = new StringWriter();
        var json = new StringWriter();

        await new OutputWriter(human, new StringWriter())
            .WriteAdoptAsync([result], json: false, _ => "#42");
        await new OutputWriter(json, new StringWriter())
            .WriteAdoptAsync([result], json: true, _ => "#42");

        Assert.Contains($"{expected} #42", human.ToString());
        Assert.Contains("applied: status", human.ToString());
        using var document = JsonDocument.Parse(json.ToString());
        Assert.Equal(
            expected,
            document.RootElement.GetProperty("result")[0].GetProperty("disposition").GetString());
    }
}

using System.Text.Json;
using Highbyte.Wrighty.Cli.Output;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Projects;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Initialization;

namespace Highbyte.Wrighty.UnitTests.Output;

public sealed class OutputWriterTests
{
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
                    ProjectNumber = 1
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
            "session-1");

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
}

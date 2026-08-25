using Highbyte.Wrighty.Workers;
using Highbyte.Wrighty.Processes;

namespace Highbyte.Wrighty.UnitTests.Workers;

public sealed class SessionExportTests : IDisposable
{
    private readonly string root = Path.Combine(
        Path.GetTempPath(), $"wrighty-session-export-tests-{Guid.NewGuid():N}");

    public SessionExportTests()
    {
        Directory.CreateDirectory(root);
    }

    [Fact]
    public async Task Exports_user_and_assistant_text_from_the_session_transcript()
    {
        var sessionId = WriteTranscript("project-a",
            """{"type":"user","message":{"role":"user","content":"Fix the login bug."}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Looking at the auth flow."},{"type":"tool_use","id":"t1","name":"Read","input":{}}]}}""",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"Fixed in LoginService."}]}}""");

        var result = await new ClaudeSessionExporter(root)
            .ExportAsync(sessionId, CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal("claude-local-transcript", result.Source);
        Assert.Collection(result.Messages!,
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Equal("Fix the login bug.", message.Text);
            },
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("Looking at the auth flow.", message.Text);
            },
            message => Assert.Equal("Fixed in LoginService.", message.Text));
    }

    [Fact]
    public async Task Finds_the_session_in_any_project_directory()
    {
        WriteTranscript("project-a",
            """{"type":"user","message":{"role":"user","content":"other session"}}""");
        var sessionId = WriteTranscript("project-b",
            """{"type":"user","message":{"role":"user","content":"wanted session"}}""");

        var result = await new ClaudeSessionExporter(root)
            .ExportAsync(sessionId, CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal("wanted session", Assert.Single(result.Messages!).Text);
    }

    [Fact]
    public async Task Skips_bookkeeping_entries_and_malformed_lines()
    {
        var sessionId = WriteTranscript("project-a",
            """{"type":"summary","summary":"Earlier work"}""",
            """{"type":"user","isMeta":true,"message":{"role":"user","content":"meta entry"}}""",
            """{"type":"user","isSidechain":true,"message":{"role":"user","content":"subagent traffic"}}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","content":"tool output"}]}}""",
            "not json at all",
            """{"type":"user","message":{"role":"user","content":"the real prompt"}}""");

        var result = await new ClaudeSessionExporter(root)
            .ExportAsync(sessionId, CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal("the real prompt", Assert.Single(result.Messages!).Text);
    }

    [Fact]
    public async Task Reports_a_missing_store_without_throwing()
    {
        var result = await new ClaudeSessionExporter(Path.Combine(root, "absent"))
            .ExportAsync("11111111-2222-3333-4444-555555555555", CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("No local Claude transcript store", result.Unavailable);
    }

    [Fact]
    public async Task Reports_a_missing_session_without_throwing()
    {
        WriteTranscript("project-a",
            """{"type":"user","message":{"role":"user","content":"other"}}""");

        var result = await new ClaudeSessionExporter(root)
            .ExportAsync("11111111-2222-3333-4444-555555555555", CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("No transcript for session", result.Unavailable);
    }

    [Fact]
    public async Task Rejects_a_session_id_that_is_not_a_safe_file_name()
    {
        var result = await new ClaudeSessionExporter(root)
            .ExportAsync("../escape", CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("not a valid session file name", result.Unavailable);
    }

    [Fact]
    public async Task Vendors_without_an_integrated_surface_report_the_workspace_fallback()
    {
        var exporter = new UnsupportedSessionExporter(
            "some-future-agent",
            "No session export surface is registered; the handoff continues from the work item " +
            "and workspace.");

        var result = await exporter.ExportAsync("session-1", CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("continues from the work item and workspace", result.Unavailable);
    }

    [Fact]
    public async Task Codex_exports_conversation_from_response_items_and_skips_session_mechanics()
    {
        var threadId = "019ed6ec-c153-7550-b1e2-b63bede79935";
        WriteRollout("2026/08/05", threadId,
            """{"timestamp":"t","type":"session_meta","payload":{"id":"019ed6ec-c153-7550-b1e2-b63bede79935"}}""",
            """{"timestamp":"t","type":"response_item","payload":{"type":"message","role":"developer","content":[{"type":"input_text","text":"policy prose"}]}}""",
            """{"timestamp":"t","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<environment_context>\n<cwd>/x</cwd>"}]}}""",
            """{"timestamp":"t","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<recommended_plugins>\nHere is a list of plugins."}]}}""",
            """{"timestamp":"t","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<permissions instructions>\nFilesystem sandboxing."}]}}""",
            """{"timestamp":"t","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Fix the login bug."}]}}""",
            """{"timestamp":"t","type":"event_msg","payload":{"type":"user_message","message":"Fix the login bug."}}""",
            """{"timestamp":"t","type":"response_item","payload":{"type":"reasoning","summary":[],"encrypted_content":"xxx"}}""",
            """{"timestamp":"t","type":"response_item","payload":{"type":"function_call","name":"exec_command","arguments":"{}"}}""",
            """{"timestamp":"t","type":"event_msg","payload":{"type":"agent_message","message":"Fixed it."}}""",
            """{"timestamp":"t","type":"response_item","payload":{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Fixed it."}]}}""");

        var result = await new CodexSessionExporter(root)
            .ExportAsync(threadId, CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal("codex-local-rollout", result.Source);
        Assert.Collection(result.Messages!,
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Equal("Fix the login bug.", message.Text);
            },
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal("Fixed it.", message.Text);
            });
    }

    [Fact]
    public async Task Codex_falls_back_to_event_messages_when_no_response_items_exist()
    {
        var threadId = "11111111-2222-3333-4444-555555555555";
        WriteRollout("2026/01/01", threadId,
            """{"timestamp":"t","type":"event_msg","payload":{"type":"user_message","message":"older format prompt"}}""",
            """{"timestamp":"t","type":"event_msg","payload":{"type":"agent_message","message":"older format answer"}}""");

        var result = await new CodexSessionExporter(root)
            .ExportAsync(threadId, CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal(2, result.Messages!.Count);
        Assert.Equal("older format prompt", result.Messages[0].Text);
    }

    [Fact]
    public async Task Copilot_exports_conversation_from_the_share_export()
    {
        var sessionId = "09c158ce-d90d-478a-8e55-41c1f4386e2b";
        Directory.CreateDirectory(root);
        File.WriteAllLines(Path.Combine(root, sessionId + ".md"),
        [
            "# Copilot CLI Session",
            "",
            "> [!NOTE]",
            $"> - **Session ID:** `{sessionId}`  ",
            "> - **Started:** 8/5/2026, 3:48:34 PM  ",
            "",
            "---",
            "",
            "<sub>1s</sub>",
            "",
            "### User",
            "",
            "Fix the login bug.",
            "",
            "---",
            "",
            "<sub>4s</sub>",
            "",
            "### Copilot",
            "",
            "Fixed it in LoginService.",
            "It needed a null check.",
            "",
            "---",
            "",
            "<sub>Generated by [GitHub Copilot CLI](https://github.com/features/copilot/cli)</sub>"
        ]);

        var result = await new CopilotSessionExporter(root)
            .ExportAsync(sessionId, CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal("copilot-share-export", result.Source);
        Assert.Collection(result.Messages!,
            message =>
            {
                Assert.Equal("user", message.Role);
                Assert.Equal("Fix the login bug.", message.Text);
            },
            message =>
            {
                Assert.Equal("assistant", message.Role);
                Assert.Equal($"Fixed it in LoginService.{Environment.NewLine}It needed a null check.",
                    message.Text);
            });
    }

    [Fact]
    public async Task Copilot_finds_a_share_named_by_handle_through_its_embedded_session_id()
    {
        var sessionId = "54bd8c5e-9a2a-427e-9e39-692ddbaae3ef";
        Directory.CreateDirectory(root);
        File.WriteAllLines(Path.Combine(root, "wrighty-local-3-0470b13fba8b.md"),
        [
            "# Copilot CLI Session",
            "",
            "> [!NOTE]",
            $"> - **Session ID:** `{sessionId}`  ",
            "",
            "---",
            "### User",
            "The real prompt.",
            "---"
        ]);

        var result = await new CopilotSessionExporter(root)
            .ExportAsync(sessionId, CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Equal("The real prompt.", Assert.Single(result.Messages!).Text);
    }

    [Fact]
    public async Task Copilot_reports_a_missing_share_without_throwing()
    {
        var result = await new CopilotSessionExporter(root)
            .ExportAsync("11111111-2222-3333-4444-555555555555", CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("No session export", result.Unavailable);
    }

    [Fact]
    public void Copilot_adapter_requests_the_share_export_on_worker_owned_runs_only()
    {
        var adapter = new CopilotAgentAdapter(shareDirectory: root);
        var handle = new SessionHandle("wrighty-local-1-abcdef123456");
        var workspace = new Workspace("/tmp/ws");
        var expected = $"--share={Path.Combine(root, handle.Value + ".md")}";

        Assert.Contains(expected, adapter.BuildStartWithPrompt(
            handle, workspace, AgentPermissionProfile.Workspace, "prompt").Arguments);
        Assert.Contains(expected, adapter.BuildResumeWithPrompt(
            handle, workspace, AgentPermissionProfile.Workspace, "prompt").Arguments);
        Assert.Contains(expected, adapter.BuildResume(
            handle, workspace, "prompt", AgentPermissionProfile.Workspace).Arguments);
        Assert.DoesNotContain(expected, adapter.BuildCheck(handle, workspace).Arguments);
        Assert.DoesNotContain(
            adapter.BuildInteractiveInvocation(handle, workspace).Arguments,
            argument => argument.StartsWith("--share", StringComparison.Ordinal));

        var withoutShare = new CopilotAgentAdapter();
        Assert.DoesNotContain(
            withoutShare.BuildResume(
                handle, workspace, "prompt", AgentPermissionProfile.Workspace).Arguments,
            argument => argument.StartsWith("--share", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Codex_reports_a_missing_thread_without_throwing()
    {
        WriteRollout("2026/01/01", "99999999-0000-0000-0000-000000000000",
            """{"timestamp":"t","type":"event_msg","payload":{"type":"user_message","message":"other"}}""");

        var result = await new CodexSessionExporter(root)
            .ExportAsync("11111111-2222-3333-4444-555555555555", CancellationToken.None);

        Assert.False(result.IsAvailable);
        Assert.Contains("No rollout for thread", result.Unavailable);
    }

    [Fact]
    public void Every_agent_resolves_to_an_exporter()
    {
        var registry = BuiltInAgentRegistry.Create(new PathExecutableResolver());

        Assert.Equal("claude", registry.GetRequired("claude").SessionExporter!.Agent);
        Assert.Equal("codex", registry.GetRequired("codex").SessionExporter!.Agent);
        Assert.Equal("copilot", registry.GetRequired("copilot").SessionExporter!.Agent);
    }

    [Fact]
    public async Task Claude_skips_non_object_lines_and_text_free_messages()
    {
        var sessionId = WriteTranscript("project-a",
            "[1,2,3]",
            """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"Read","input":{}}]}}""",
            """{"type":"user","message":{"role":"user","content":""}}""",
            """{"type":"user","message":{"role":"user","content":"kept"}}""");

        var result = await new ClaudeSessionExporter(root)
            .ExportAsync(sessionId, CancellationToken.None);

        Assert.Equal("kept", Assert.Single(result.Messages!).Text);
    }

    [Fact]
    public async Task Codex_rejects_an_unsafe_thread_id_and_a_missing_store()
    {
        var unsafeId = await new CodexSessionExporter(root)
            .ExportAsync("../escape", CancellationToken.None);
        Assert.Contains("not a valid session file name", unsafeId.Unavailable);

        var missingStore = await new CodexSessionExporter(Path.Combine(root, "absent"))
            .ExportAsync("11111111-2222-3333-4444-555555555555", CancellationToken.None);
        Assert.Contains("No local codex session store", missingStore.Unavailable);
    }

    [Fact]
    public async Task Codex_keeps_a_user_message_whose_first_line_is_not_a_bare_tag()
    {
        var threadId = "22222222-2222-3333-4444-555555555555";
        WriteRollout("2026/02/02", threadId,
            """{"timestamp":"t","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"<started with a bracket but is prose, not a wrapper>"}]}}""",
            """{"timestamp":"t","type":"event_msg","payload":{"type":"task_started"}}""",
            """{"timestamp":"t","type":"event_msg","payload":{"no_type_at_all":true}}""",
            """{"timestamp":"t","type":"turn_context","payload":{"cwd":"/x"}}""",
            "not json");

        var result = await new CodexSessionExporter(root)
            .ExportAsync(threadId, CancellationToken.None);

        Assert.True(result.IsAvailable);
        Assert.Single(result.Messages!);
    }

    [Fact]
    public async Task Copilot_rejects_an_unsafe_session_id_and_reports_a_missing_store()
    {
        var unsafeId = await new CopilotSessionExporter(root)
            .ExportAsync("../escape", CancellationToken.None);
        Assert.Contains("not a valid session file name", unsafeId.Unavailable);

        var missingStore = await new CopilotSessionExporter(Path.Combine(root, "absent"))
            .ExportAsync("33333333-2222-3333-4444-555555555555", CancellationToken.None);
        Assert.Contains("No session export", missingStore.Unavailable);
    }

    [Fact]
    public async Task Copilot_ignores_shares_of_other_sessions_and_unknown_headings()
    {
        Directory.CreateDirectory(root);
        // A share whose metadata names a different session: skipped by the content lookup.
        File.WriteAllLines(Path.Combine(root, "wrighty-other.md"),
        [
            "# Copilot CLI Session",
            "> - **Session ID:** `99999999-0000-0000-0000-000000000000`  ",
            "---",
            "### User",
            "other prompt",
            "---"
        ]);
        var sessionId = "44444444-2222-3333-4444-555555555555";
        File.WriteAllLines(Path.Combine(root, sessionId + ".md"),
        [
            "# Copilot CLI Session",
            $"> - **Session ID:** `{sessionId}`  ",
            "---",
            "### Tools",
            "not conversation",
            "---",
            "### Copilot",
            "the answer"
        ]);

        var result = await new CopilotSessionExporter(root)
            .ExportAsync(sessionId, CancellationToken.None);

        Assert.True(result.IsAvailable);
        var message = Assert.Single(result.Messages!);
        Assert.Equal("assistant", message.Role);
        Assert.Equal("the answer", message.Text);
    }

    private void WriteRollout(string datePath, string threadId, params string[] lines)
    {
        var directory = Path.Combine(
            root, datePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        File.WriteAllLines(
            Path.Combine(directory, $"rollout-2026-08-05T10-00-00-{threadId}.jsonl"), lines);
    }

    private string WriteTranscript(string project, params string[] lines)
    {
        var sessionId = Guid.NewGuid().ToString();
        var directory = Path.Combine(root, project);
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, sessionId + ".jsonl"), lines);
        return sessionId;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: a leaked temp directory must not fail the suite.
        }
    }
}

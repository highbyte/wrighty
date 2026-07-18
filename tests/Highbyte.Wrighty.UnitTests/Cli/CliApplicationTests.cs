using Highbyte.Wrighty.Cli;
using Highbyte.Wrighty;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Projects;
using Highbyte.Wrighty.Initialization;
using Highbyte.Wrighty.Cli.Skills;
using Highbyte.Wrighty.Web;
using Highbyte.Wrighty.Workers;
using System.Text.Json;

namespace Highbyte.Wrighty.UnitTests.Cli;

public sealed class CliApplicationTests
{
    private static readonly TrackerConfig Config = new()
    {
        Repository = "owner/repo",
        ProjectNumber = 1
    };

    [Fact]
    public async Task Create_reads_a_multiline_body_from_stdin()
    {
        var backend = new RecordingBackend();
        var output = new StringWriter();
        var application = Application(backend, new StringReader("line one\n\nline two\n"), output);

        var exitCode = await application.InvokeAsync(
            ["create", "--title", "Example", "--body-file", "-", "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Equal("line one\n\nline two\n", backend.Request!.Body);
        Assert.Contains("github:owner/repo#44", output.ToString());
    }

    [Fact]
    public async Task Create_rejects_body_and_body_file_together_before_calling_backend()
    {
        var backend = new RecordingBackend();
        var error = new StringWriter();
        var application = Application(
            backend,
            new StringReader("ignored"),
            new StringWriter(),
            error);

        var exitCode = await application.InvokeAsync(
            ["create", "--title", "Example", "--body", "one", "--body-file", "-"]);

        Assert.Equal(2, exitCode);
        Assert.Null(backend.Request);
        Assert.Contains("ARGUMENT_INVALID", error.ToString());
    }

    [Fact]
    public async Task Create_normalizes_explicit_creation_attempt_id()
    {
        var backend = new RecordingBackend();
        var output = new StringWriter();
        var application = Application(backend, new StringReader(string.Empty), output);

        var exitCode = await application.InvokeAsync([
            "create", "--title", "Example",
            "--creation-attempt-id", "019f5c48-5c2b-7862-aeac-80eb638a7b5c",
            "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Equal("019f5c485c2b7862aeac80eb638a7b5c", backend.Operation!.CreationAttemptId);
        Assert.Contains("creationAttemptId", output.ToString());
        Assert.Contains("019f5c485c2b7862aeac80eb638a7b5c", output.ToString());
    }

    [Theory]
    [InlineData("create", "--title", "Example", "--field", "epic=PLAT-3", "--field", "owner=ana")]
    [InlineData("edit", "42", "--field", "epic=PLAT-3", "--field", "owner=")]
    [InlineData("list", "--field", "epic=PLAT-3", "--field", "owner=ana")]
    public async Task Repeatable_field_options_reach_the_GitHub_capability_guard(params string[] arguments)
    {
        var error = new StringWriter();
        var application = Application(
            new RecordingBackend(),
            new StringReader(string.Empty),
            new StringWriter(),
            error);

        var exitCode = await application.InvokeAsync(arguments);

        Assert.Equal(3, exitCode);
        Assert.Contains("NOT_SUPPORTED", error.ToString());
    }

    [Fact]
    public async Task Field_option_rejects_reserved_names_before_backend_access()
    {
        var error = new StringWriter();
        var application = Application(
            new RecordingBackend(),
            new StringReader(string.Empty),
            new StringWriter(),
            error);

        var exitCode = await application.InvokeAsync(
            ["create", "--title", "Example", "--field", "status=custom"]);

        Assert.Equal(2, exitCode);
        Assert.Contains("RESERVED_FIELD_COLLISION", error.ToString());
    }

    [Fact]
    public async Task Edit_reads_body_from_stdin_and_preserves_clear_priority()
    {
        var backend = new RecordingBackend();
        var output = new StringWriter();
        var application = Application(backend, new StringReader("new\nbody\n"), output);

        var exitCode = await application.InvokeAsync(
            ["edit", "42", "--body-file", "-", "--clear-priority", "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Equal("new\nbody\n", backend.Patch!.Body.Value);
        Assert.True(backend.Patch.Priority.IsSpecified);
        Assert.Null(backend.Patch.Priority.Value);
        Assert.Contains("changedFields", output.ToString());
    }

    [Fact]
    public async Task Edit_rejects_priority_and_clear_priority_before_backend_access()
    {
        var backend = new RecordingBackend();
        var error = new StringWriter();
        var application = Application(
            backend,
            new StringReader(string.Empty),
            new StringWriter(),
            error);

        var exitCode = await application.InvokeAsync(
            ["edit", "42", "--priority", "P0", "--clear-priority"]);

        Assert.Equal(2, exitCode);
        Assert.Null(backend.Patch);
        Assert.Contains("ARGUMENT_INVALID", error.ToString());
    }

    [Fact]
    public async Task Move_builds_a_status_only_patch()
    {
        var backend = new RecordingBackend();
        var application = Application(
            backend,
            new StringReader(string.Empty),
            new StringWriter());

        var exitCode = await application.InvokeAsync(["move", "#42", "Done"]);

        Assert.Equal(0, exitCode);
        Assert.Equal("Done", backend.Patch!.Status.Value);
        Assert.False(backend.Patch.Title.IsSpecified);
    }

    [Fact]
    public async Task Creation_attempt_new_emits_a_normalized_id_without_backend_access()
    {
        var output = new StringWriter();
        var application = Application(
            new RecordingBackend(),
            new StringReader(string.Empty),
            output);

        var exitCode = await application.InvokeAsync(["creation-attempt", "new", "--json"]);

        Assert.Equal(0, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var value = document.RootElement.GetProperty("result").GetProperty("creationAttemptId").GetString();
        Assert.NotNull(value);
        Assert.Matches("^[0-9a-f]{32}$", value);
    }

    [Fact]
    public async Task Finish_moves_to_default_status_and_releases_the_claim()
    {
        var backend = new RecordingBackend();
        var output = new StringWriter();
        var application = Application(backend, new StringReader(string.Empty), output);

        var exitCode = await application.InvokeAsync(["finish", "42", "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Equal("Done", backend.Patch!.Status.Value);
        Assert.Contains("\"disposition\": \"finished\"", output.ToString());
    }

    [Fact]
    public async Task List_supports_json_compact_and_archive_scopes()
    {
        var json = new StringWriter();
        var compact = new StringWriter();

        Assert.Equal(0, await Application(
            new RecordingBackend(), new StringReader(string.Empty), json).InvokeAsync(
            ["list", "--status", "Done", "--limit", "5", "--include-archived", "--json"]));
        Assert.Equal(0, await Application(
            new RecordingBackend(), new StringReader(string.Empty), compact).InvokeAsync(
            ["list", "--compact"]));

        using var document = JsonDocument.Parse(json.ToString());
        Assert.Single(document.RootElement.GetProperty("result").EnumerateArray());
        Assert.Contains("#42 done p1 Example", compact.ToString());
    }

    [Theory]
    [InlineData("--compact", "--json")]
    [InlineData("--archived", "--include-archived")]
    public async Task List_rejects_conflicting_output_or_archive_options(string first, string second)
    {
        var error = new StringWriter();
        var application = Application(
            new RecordingBackend(), new StringReader(string.Empty), new StringWriter(), error);

        var exitCode = await application.InvokeAsync(["list", first, second]);

        Assert.Equal(2, exitCode);
        Assert.Contains("ARGUMENT_INVALID", error.ToString());
    }

    [Fact]
    public async Task Get_claim_and_release_commands_emit_json_results()
    {
        var getOutput = new StringWriter();
        var claimOutput = new StringWriter();
        var releaseOutput = new StringWriter();

        Assert.Equal(0, await Application(
            new RecordingBackend(), new StringReader(string.Empty), getOutput).InvokeAsync(
            ["get", "42", "--json"]));
        Assert.Equal(0, await Application(
            new RecordingBackend(), new StringReader(string.Empty), claimOutput).InvokeAsync(
            ["claim", "42", "--agent-type", "codex", "--session-id", "session-1", "--json"]));
        Assert.Equal(0, await Application(
            new RecordingBackend(), new StringReader(string.Empty), releaseOutput).InvokeAsync(
            ["release", "42", "--json"]));

        Assert.Contains("\"title\": \"Example\"", getOutput.ToString());
        Assert.Contains("\"outcome\": \"Acquired\"", claimOutput.ToString());
        Assert.Contains("\"claimantKind\": \"agent\"", claimOutput.ToString());
        Assert.Contains("\"released\": true", releaseOutput.ToString());
    }

    [Fact]
    public async Task Claim_accepts_explicit_automation_attribution()
    {
        var output = new StringWriter();

        var exitCode = await Application(
            new RecordingBackend(), new StringReader(string.Empty), output).InvokeAsync(
            ["claim", "42", "--claimant-kind", "automation", "--claimant-id", "automation:test", "--json"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("\"claimantKind\": \"automation\"", output.ToString());
    }

    [Fact]
    public async Task Takeover_json_requires_yes_and_success_returns_a_new_handle()
    {
        var refused = new StringWriter();
        var refusedExit = await Application(new RecordingBackend(), new StringReader(string.Empty), refused, refused)
            .InvokeAsync(["takeover", "42", "--claimant-kind", "human", "--json"]);
        Assert.Equal(2, refusedExit);
        Assert.Contains("CLAIM_CONFIRMATION_REQUIRED", refused.ToString());

        var accepted = new StringWriter();
        var acceptedExit = await Application(new RecordingBackend(), new StringReader(string.Empty), accepted)
            .InvokeAsync(["takeover", "42", "--claimant-kind", "human", "--json", "--yes"]);
        Assert.Equal(0, acceptedExit);
        Assert.Contains("\"outcome\": \"TakenOver\"", accepted.ToString());
        Assert.Contains("\"claimToken\": \"takeover-token\"", accepted.ToString());
    }

    [Fact]
    public async Task Takeover_resume_command_applies_claim_environment_after_changing_directory()
    {
        var output = new StringWriter();

        var exitCode = await Application(
            new RecordingBackend(),
            new StringReader(string.Empty),
            output).InvokeAsync([
                "takeover", "42", "--yes", "--print-resume-command",
                "--claimant-kind", "agent",
                "--claimant-id", "agent:test",
                "--agent-type", "claude",
                "--session-id", "session-one"
            ]);

        Assert.Equal(0, exitCode);
        Assert.Contains(
            $"cd '{Directory.GetCurrentDirectory()}' && WRIGHTY_CLAIMANT_ID='agent:test' " +
            "WRIGHTY_CLAIM_TOKEN='takeover-token' claude --resume 'session-one'",
            output.ToString());
        Assert.Contains("Headless worker resume:", output.ToString());
        Assert.Contains(
            "WRIGHTY_CLAIM_TOKEN='takeover-token' wrighty worker --resume " +
            "'github:owner/repo#42' --yes",
            output.ToString());
    }

    [Fact]
    public async Task Human_takeover_prints_headless_worker_continuation_not_human_scoped_interactive_resume()
    {
        var output = new StringWriter();

        var exitCode = await Application(
            new RecordingBackend(),
            new StringReader(string.Empty),
            output).InvokeAsync([
                "takeover", "42", "--yes", "--print-resume-command",
                "--claimant-kind", "human",
                "--claimant-id", "human-cli"
            ]);

        Assert.Equal(0, exitCode);
        Assert.Contains("Headless worker resume:", output.ToString());
        Assert.Contains("wrighty worker --resume 'github:owner/repo#42' --yes", output.ToString());
        Assert.DoesNotContain("Interactive resume:", output.ToString());
        Assert.DoesNotContain("codex resume", output.ToString());
    }

    [Fact]
    public async Task Create_reads_body_from_relative_file_and_reports_missing_file()
    {
        var bodyPath = Path.Combine(Directory.GetCurrentDirectory(), $"body-{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(bodyPath, "body from file\n");
            var backend = new RecordingBackend();
            var success = Application(backend, new StringReader(string.Empty), new StringWriter());

            Assert.Equal(0, await success.InvokeAsync(
                ["create", "--title", "Example", "--body-file", Path.GetFileName(bodyPath)]));
            Assert.Equal("body from file\n", backend.Request!.Body);

            var error = new StringWriter();
            var failure = Application(
                new RecordingBackend(), new StringReader(string.Empty), new StringWriter(), error);
            Assert.Equal(2, await failure.InvokeAsync(
                ["create", "--title", "Example", "--body-file", "missing-body.md"]));
            Assert.Contains("Could not read body file", error.ToString());
        }
        finally
        {
            if (File.Exists(bodyPath)) File.Delete(bodyPath);
        }
    }

    [Theory]
    [InlineData("install")]
    [InlineData("check")]
    [InlineData("update")]
    public async Task Skill_commands_dispatch_options_and_write_results(string operation)
    {
        var skills = new RecordingSkillManager();
        var output = new StringWriter();
        var args = new List<string>
        {
            "skill", operation, "--agent", "codex", "--scope", "user",
            "--project-dir", "/tmp/project", "--json"
        };
        if (operation is "install" or "update") args.Add("--force");
        if (operation == "check") args.Add("--check-tracker");

        var exitCode = await Application(
            new RecordingBackend(),
            new StringReader(string.Empty),
            output,
            skillManager: skills).InvokeAsync([.. args]);

        Assert.Equal(0, exitCode);
        Assert.Equal(operation, skills.Operation);
        Assert.Equal("codex", skills.Agent);
        Assert.Equal(SkillScope.User, skills.Scope);
        Assert.Equal("/tmp/project", skills.ProjectDirectory);
        Assert.Equal(operation is "install" or "update", skills.Force);
        Assert.Contains($"\"operation\": \"{operation}\"", output.ToString());
    }

    [Fact]
    public async Task Skill_command_rejects_invalid_scope_and_maps_manager_errors()
    {
        var scopeError = new StringWriter();
        var invalidScope = Application(
            new RecordingBackend(),
            new StringReader(string.Empty),
            new StringWriter(),
            scopeError,
            new RecordingSkillManager());
        Assert.Equal(2, await invalidScope.InvokeAsync(
            ["skill", "check", "--agent", "codex", "--scope", "machine"]));
        Assert.Contains("ARGUMENT_INVALID", scopeError.ToString());

        var managerError = new StringWriter();
        var manager = new RecordingSkillManager
        {
            Failure = new TrackerException("SKILL_MODIFIED", "modified", 9)
        };
        var failed = Application(
            new RecordingBackend(),
            new StringReader(string.Empty),
            new StringWriter(),
            managerError,
            manager);
        Assert.Equal(9, await failed.InvokeAsync(
            ["skill", "update", "--agent", "codex"]));
        Assert.Contains("SKILL_MODIFIED", managerError.ToString());
    }

    [Fact]
    public async Task Worker_dry_run_does_not_warn_or_prompt()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var application = Application(
            new RecordingBackend(),
            new StringReader(string.Empty),
            output,
            error,
            inputRedirected: true);

        var exitCode = await application.InvokeAsync(["worker", "--dry-run", "--once"]);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.DoesNotContain("Continue?", output.ToString());
        Assert.Contains("no-item:", output.ToString());
    }

    [Fact]
    public async Task Worker_resume_dry_run_builds_recorded_headless_invocation_without_prompting()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var application = Application(
            new RecordingBackend(),
            new StringReader(string.Empty),
            output,
            error,
            inputRedirected: true);

        var exitCode = await application.InvokeAsync(["worker", "--resume", "42", "--dry-run"]);

        Assert.Equal(0, exitCode);
        Assert.Empty(error.ToString());
        Assert.Contains("dry-run: github:owner/repo#42 [codex]", output.ToString());
        Assert.Contains("codex exec resume old", output.ToString());
        Assert.Contains("Item github:owner/repo#42 has been clarified", output.ToString());
    }

    [Fact]
    public async Task Worker_resume_preflight_rejects_missing_claim_token_before_warning()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var application = Application(
            new RecordingBackend(),
            new StringReader(string.Empty),
            output,
            error,
            inputRedirected: true);

        var exitCode = await application.InvokeAsync(["worker", "--resume", "42", "--yes"]);

        Assert.Equal(6, exitCode);
        Assert.Empty(output.ToString());
        Assert.Contains("CLAIM_TOKEN_REQUIRED", error.ToString());
        Assert.DoesNotContain("broad tool permissions", error.ToString());
    }

    [Fact]
    public async Task Worker_live_run_with_no_candidates_exits_without_warning_or_prompt()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var application = Application(
            new RecordingBackend(),
            new StringReader(string.Empty),
            output,
            error,
            inputRedirected: true);

        var exitCode = await application.InvokeAsync(["worker", "--once"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("no-item:", output.ToString());
        Assert.DoesNotContain("Continue?", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task Continuous_worker_shows_full_initial_waiting_snapshot_before_one_time_confirmation()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var application = Application(
            new RecordingBackend(),
            new StringReader("n\n"),
            output,
            error);

        var exitCode = await application.InvokeAsync(["worker"]);

        Assert.Equal(2, exitCode);
        Assert.Contains("waiting: -", output.ToString());
        Assert.Contains("No worker item is currently claimable from status 'Todo'", output.ToString());
        Assert.Contains("Candidates must be active in 'Todo'", output.ToString());
        Assert.Contains("Continue? [y/N]", output.ToString());
        Assert.Contains("may start unattended agents", error.ToString());
        Assert.Contains("Live worker execution was cancelled", error.ToString());
    }

    [Fact]
    public async Task Worker_live_noninteractive_candidate_requires_yes_after_ready_event()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var application = Application(
            new RecordingBackend(workerEligible: true),
            new StringReader(string.Empty),
            output,
            error,
            inputRedirected: true,
            workerCandidate: true);

        var exitCode = await application.InvokeAsync(["worker", "--once"]);

        Assert.Equal(2, exitCode);
        Assert.Contains("ready: github:owner/repo#42 [claude]", output.ToString());
        Assert.Contains("1 currently claimable worker item", output.ToString());
        Assert.Contains("broad tool permissions", error.ToString());
        Assert.Contains("WORKER_CONFIRMATION_REQUIRED", error.ToString());
        Assert.Contains("--yes", error.ToString());
    }

    [Fact]
    public async Task Worker_live_interactive_run_can_be_declined()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var application = Application(
            new RecordingBackend(workerEligible: true),
            new StringReader("n\n"),
            output,
            error,
            workerCandidate: true);

        var exitCode = await application.InvokeAsync(["worker", "--once"]);

        Assert.Equal(2, exitCode);
        Assert.Contains("ready: github:owner/repo#42 [claude]", output.ToString());
        Assert.Contains("Continue? [y/N]", output.ToString());
        Assert.DoesNotContain("no-item:", output.ToString());
        Assert.Contains("broad tool permissions", error.ToString());
        Assert.Contains("Live worker execution was cancelled", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_live_run_proceeds_after_explicit_confirmation(bool useYes)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var application = Application(
            new RecordingBackend(workerEligible: true),
            new StringReader(useYes ? string.Empty : "yes\n"),
            output,
            error,
            inputRedirected: useYes,
            workerCandidate: true,
            candidateDisappearsAfterPreflight: true);
        var arguments = new List<string> { "worker", "--once" };
        if (useYes) arguments.Add("--yes");

        var exitCode = await application.InvokeAsync([.. arguments]);

        Assert.Equal(0, exitCode);
        Assert.Contains("broad tool permissions", error.ToString());
        Assert.Contains("ready: github:owner/repo#42 [claude]", output.ToString());
        Assert.Contains("no-item:", output.ToString());
        Assert.Equal(!useYes, output.ToString().Contains("Continue? [y/N]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Shared_workspace_mode_is_accepted_and_prints_an_additional_collision_warning()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var application = Application(
            new RecordingBackend(workerEligible: true),
            new StringReader(string.Empty),
            output,
            error,
            inputRedirected: true,
            workerCandidate: true,
            candidateDisappearsAfterPreflight: true);

        var exitCode = await application.InvokeAsync(
            ["worker", "--once", "--workspace-mode", "shared", "--yes"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("shared workspace mode", error.ToString());
        Assert.Contains("concurrently modify, stage, or commit", error.ToString());
        Assert.Contains("no-item:", output.ToString());
    }

    [Fact]
    public async Task Configured_shared_workspace_mode_is_used_when_the_option_is_absent()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var application = Application(
            new RecordingBackend(workerEligible: true),
            new StringReader(string.Empty),
            output,
            error,
            inputRedirected: true,
            workerCandidate: true,
            candidateDisappearsAfterPreflight: true,
            config: Config with
            {
                Worker = new WorkerConfig { WorkspaceMode = "shared" }
            });

        var exitCode = await application.InvokeAsync(["worker", "--once", "--yes"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("shared workspace mode", error.ToString());
    }

    [Fact]
    public async Task Explicit_workspace_mode_overrides_the_configured_default()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var application = Application(
            new RecordingBackend(workerEligible: true),
            new StringReader(string.Empty),
            output,
            error,
            inputRedirected: true,
            workerCandidate: true,
            candidateDisappearsAfterPreflight: true,
            config: Config with
            {
                Worker = new WorkerConfig { WorkspaceMode = "shared" }
            });

        var exitCode = await application.InvokeAsync(
            ["worker", "--once", "--workspace-mode", "current", "--yes"]);

        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("shared workspace mode", error.ToString());
    }

    [Fact]
    public async Task Web_command_dispatches_port_and_browser_options()
    {
        var webServer = new RecordingWebServer();
        var output = new StringWriter();
        var application = Application(
            new RecordingBackend(),
            new StringReader(string.Empty),
            output,
            webServer: webServer);

        var exitCode = await application.InvokeAsync(["web", "--port", "8123", "--no-open"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(new WebServerOptions(8123, false), webServer.Options);
        Assert.Same(output, webServer.Output);
    }

    [Fact]
    public async Task Web_command_uses_safe_defaults()
    {
        var webServer = new RecordingWebServer();
        var application = Application(
            new RecordingBackend(),
            new StringReader(string.Empty),
            new StringWriter(),
            webServer: webServer);

        var exitCode = await application.InvokeAsync(["web"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(new WebServerOptions(0, true), webServer.Options);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Web_command_maps_server_failures(bool cancellation)
    {
        var error = new StringWriter();
        var webServer = new RecordingWebServer
        {
            Failure = cancellation
                ? new OperationCanceledException()
                : new InvalidOperationException("Startup failed")
        };
        var application = Application(
            new RecordingBackend(),
            new StringReader(string.Empty),
            new StringWriter(),
            error,
            webServer: webServer);

        var exitCode = await application.InvokeAsync(["web"]);

        Assert.Equal(cancellation ? 130 : 10, exitCode);
        if (!cancellation)
        {
            Assert.Contains("UNEXPECTED_ERROR", error.ToString());
            Assert.Contains("Startup failed", error.ToString());
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    public async Task Web_command_rejects_invalid_ports(int port)
    {
        var webServer = new RecordingWebServer();
        var error = new StringWriter();
        var application = Application(
            new RecordingBackend(),
            new StringReader(string.Empty),
            new StringWriter(),
            error,
            webServer: webServer);

        var exitCode = await application.InvokeAsync(["web", "--port", port.ToString()]);

        Assert.Equal(2, exitCode);
        Assert.Null(webServer.Options);
        Assert.Contains("ARGUMENT_INVALID", error.ToString());
    }

    private static CliApplication Application(
        RecordingBackend backend,
        TextReader input,
        TextWriter output,
        TextWriter? error = null,
        ISkillManager? skillManager = null,
        IWrightyWebServer? webServer = null,
        bool inputRedirected = false,
        bool workerCandidate = false,
        bool candidateDisappearsAfterPreflight = false,
        TrackerConfig? config = null)
    {
        var projects = new UnusedProjects(workerCandidate, candidateDisappearsAfterPreflight);
        var claims = new OwnedClaims(workerCandidate);
        var resolver = new GitHubWorkItemAddressResolver();
        var trackerBackend = new GitHubTrackerBackend(
            projects,
            claims,
            resolver,
            backend);
        var tracker = new TrackerService(new TrackerBackendRegistry([trackerBackend]));
        return new CliApplication(
            new FixedConfigLoader(config ?? Config),
            new TrackerInitializationService(
                new TrackerConfigLoader(),
                new UnusedDiscovery(),
                new UnusedGitHubInitialization(),
                projects),
            tracker,
            new AgentExecutionContextProvider(new Dictionary<string, string?>()),
            skillManager ?? SkillManager.CreateDefault(),
            webServer ?? new RecordingWebServer(),
            input,
            output,
            error ?? new StringWriter(),
            Directory.GetCurrentDirectory(),
            new WorkerService(
                tracker,
                new FailIfRunRunner(),
                new FailIfPrepareWorkspace(),
                [new ClaudeAgentAdapter(), new CodexAgentAdapter(), new CopilotAgentAdapter()]),
            () => inputRedirected);
    }

    private sealed class FailIfRunRunner : IAgentProcessRunner
    {
        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("No vendor process should have been started.");
    }

    private sealed class FailIfPrepareWorkspace : IWorkspaceManager
    {
        public Task<Workspace> PrepareAsync(
            WorkspaceMode mode,
            string repositoryPath,
            WorkItemId itemId,
            string claimantId,
            string? existingPath,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("No workspace should have been prepared.");
    }

    private sealed class RecordingWebServer : IWrightyWebServer
    {
        public Exception? Failure { get; init; }

        public WebServerOptions? Options { get; private set; }

        public TextWriter? Output { get; private set; }

        public Task RunAsync(
            WebServerOptions options,
            TextWriter output,
            CancellationToken cancellationToken)
        {
            Options = options;
            Output = output;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private sealed class FixedConfigLoader(TrackerConfig config) : ITrackerConfigLoader
    {
        public Task<TrackerConfig> LoadAsync(
            string startDirectory,
            CancellationToken cancellationToken) => Task.FromResult(config);
    }

    private sealed class RecordingBackend(bool workerEligible = false) : IWorkItemBackend
    {
        public CreateWorkItemRequest? Request { get; private set; }

        public CreateWorkItemOperation? Operation { get; private set; }

        public WorkItemPatch? Patch { get; private set; }

        public Task<WorkItemDetail?> GetAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) => Task.FromResult<WorkItemDetail?>(new WorkItemDetail(
                id,
                "Example",
                "Body",
                "https://github.com/owner/repo/issues/42",
                "Todo",
                "P1",
                AutomationEligible: workerEligible,
                PreferredAgent: workerEligible ? "claude" : null));

        public Task<CreateWorkItemResult> CreateAsync(
            TrackerConfig config,
            CreateWorkItemOperation operation,
            CancellationToken cancellationToken)
        {
            Operation = operation;
            Request = operation.Request;
            var id = new WorkItemId("github:owner/repo#44");
            return Task.FromResult(new CreateWorkItemResult(
                id,
                null,
                null,
                operation.CreationAttemptId));
        }

        public Task<UpdateWorkItemResult> UpdateAsync(
            TrackerConfig config,
            WorkItemId id,
            WorkItemPatch patch,
            CancellationToken cancellationToken)
        {
            Patch = patch;
            var fields = new List<string>();
            if (patch.Title.IsSpecified) fields.Add("title");
            if (patch.Body.IsSpecified) fields.Add("body");
            if (patch.Priority.IsSpecified) fields.Add("priority");
            if (patch.Status.IsSpecified) fields.Add("status");
            var item = new WorkItemDetail(
                id,
                patch.Title.Value ?? "Example",
                patch.Body.Value ?? "Body",
                "https://github.com/owner/repo/issues/42",
                patch.Status.Value ?? "Todo",
                patch.Priority.IsSpecified ? patch.Priority.Value : "P1");
            return Task.FromResult(new UpdateWorkItemResult(item, fields.Count > 0, fields));
        }
    }

    private sealed class UnusedProjects(
        bool workerCandidate = false,
        bool candidateDisappearsAfterPreflight = false) : IProjectClient
    {
        private int listCalls;

        public Task<ProjectInitializationResult> InitializeAsync(TrackerConfig config, bool checkOnly, CancellationToken cancellationToken) =>
            Task.FromResult(new ProjectInitializationResult(false, ["Project schema is valid."]));
        public Task EnsureAgentContextSchemaAsync(
            TrackerConfig config,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<GitHubProjectItem>> ListAsync(TrackerConfig config, string? status, int? limit, CancellationToken cancellationToken)
        {
            listCalls++;
            if (workerCandidate && candidateDisappearsAfterPreflight && listCalls > 1)
                return Task.FromResult<IReadOnlyList<GitHubProjectItem>>([]);
            var resolver = new GitHubWorkItemAddressResolver();
            var id = resolver.FromIssueNumber(config, 42);
            return Task.FromResult<IReadOnlyList<GitHubProjectItem>>([new GitHubProjectItem(
                resolver.Decode(id, config),
                new WorkItemSummary(id, "Example", "https://github.com/owner/repo/issues/42",
                    workerCandidate ? "Todo" : "Done", "P1"),
                "ISSUE42",
                "ITEM42")]);
        }
        public Task UpdateStatusAsync(TrackerConfig config, GitHubProjectItem item, string status, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateAgentContextAsync(TrackerConfig config, GitHubProjectItem item, string? agentType, string? sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateCreateFieldsAsync(TrackerConfig config, string status, string? priority, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> AddIssueAsync(TrackerConfig config, string issueNodeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdatePriorityAsync(TrackerConfig config, GitHubProjectItem item, string priority, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class OwnedClaims(bool initiallyUnclaimed = false) : IClaimService
    {
        public Task<ClaimResult> TryClaimAsync(
            TrackerConfig config,
            WorkItemId id,
            AgentExecutionContext agentContext,
            CancellationToken cancellationToken) => Task.FromResult(new ClaimResult(
                ClaimOutcome.Acquired,
                "worker-1",
                DateTimeOffset.Parse("2026-07-15T18:00:00Z"),
                "attempt-1",
                agentContext.AgentType,
                agentContext.SessionId,
                ClaimantKinds.ToStorageValue(agentContext.EffectiveClaimantKind),
                agentContext.ClaimantId ?? "human-cli",
                "claim-token",
                true));
        public Task<ClaimResult> TryClaimAsync(TrackerConfig config, WorkItemId id,
            AgentExecutionContext agentContext, CancellationToken cancellationToken,
            string? expectedClaimToken) => TryClaimAsync(config, id, agentContext, cancellationToken);
        public Task<ClaimResult> TakeoverAsync(TrackerConfig config, WorkItemId id,
            AgentExecutionContext claimantContext, string? currentClaimToken, CancellationToken cancellationToken) =>
            Task.FromResult(new ClaimResult(ClaimOutcome.TakenOver, "worker-1",
                DateTimeOffset.Parse("2026-07-15T18:00:00Z"), "event-2",
                claimantContext.AgentType ?? "codex",
                claimantContext.SessionId ?? "old",
                ClaimantKinds.ToStorageValue(claimantContext.EffectiveClaimantKind),
                claimantContext.ClaimantId ?? "human-cli", "takeover-token", true,
                Directory.GetCurrentDirectory()));
        public Task ReleaseAsync(TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ReleaseAsync(TrackerConfig config, WorkItemId id, ClaimHandle claimHandle,
            bool overrideClaimant, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ClaimOwnershipResult> ValidateAsync(TrackerConfig config, WorkItemId id,
            ClaimHandle claimHandle, CancellationToken cancellationToken) =>
            GetOwnershipAsync(config, id, cancellationToken);
        public Task<bool> IsOwnedByCurrentWorkerAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) => Task.FromResult(!initiallyUnclaimed);

        public Task<ClaimOwnershipResult> GetOwnershipAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            Task.FromResult(initiallyUnclaimed
                ? new ClaimOwnershipResult(ClaimOwnershipState.Unclaimed)
                : new ClaimOwnershipResult(ClaimOwnershipState.OwnedByCurrent,
                    "worker-1", DateTimeOffset.Parse("2026-07-15T18:00:00Z"), "agent:old", "codex", "old",
                    "agent", true, Directory.GetCurrentDirectory()));
    }

    private sealed class UnusedDiscovery : IRepositoryDiscovery
    {
        public Task<DiscoveredGitHubRepository?> DiscoverAsync(
            string directory,
            string remoteName,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class UnusedGitHubInitialization : IGitHubInitializationClient
    {
        public Task<GitHubRepositoryInfo> GetRepositoryAsync(string host, string repository, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GitHubProjectInfo?> GetProjectAsync(string host, string owner, int number, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<GitHubProjectInfo>> FindProjectsByTitleAsync(string host, string owner, string title, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GitHubProjectInfo> CreateProjectAsync(string host, string owner, string title, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task LinkRepositoryAsync(string host, string projectNodeId, string repositoryNodeId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingSkillManager : ISkillManager
    {
        public string? Operation { get; private set; }
        public string? Agent { get; private set; }
        public SkillScope Scope { get; private set; }
        public string? ProjectDirectory { get; private set; }
        public bool Force { get; private set; }
        public TrackerException? Failure { get; init; }

        public Task<IReadOnlyList<SkillOperationResult>> InstallAsync(
            string agent,
            SkillScope scope,
            string workingDirectory,
            string? projectDirectory,
            bool force,
            CancellationToken cancellationToken) =>
            Record("install", agent, scope, projectDirectory, force);

        public Task<IReadOnlyList<SkillOperationResult>> CheckAsync(
            string agent,
            SkillScope scope,
            string workingDirectory,
            string? projectDirectory,
            CancellationToken cancellationToken) =>
            Record("check", agent, scope, projectDirectory, false);

        public Task<IReadOnlyList<SkillOperationResult>> UpdateAsync(
            string agent,
            SkillScope scope,
            string workingDirectory,
            string? projectDirectory,
            bool force,
            CancellationToken cancellationToken) =>
            Record("update", agent, scope, projectDirectory, force);

        private Task<IReadOnlyList<SkillOperationResult>> Record(
            string operation,
            string agent,
            SkillScope scope,
            string? projectDirectory,
            bool force)
        {
            if (Failure is not null) throw Failure;
            Operation = operation;
            Agent = agent;
            Scope = scope;
            ProjectDirectory = projectDirectory;
            Force = force;
            return Task.FromResult<IReadOnlyList<SkillOperationResult>>([
                new SkillOperationResult(
                    agent,
                    scope.ToString().ToLowerInvariant(),
                    projectDirectory ?? "/tmp/skill",
                    SkillInstallationState.Missing,
                    SkillInstallationState.Current,
                    true,
                    null,
                    "0.5.0",
                    false)
            ]);
        }
    }
}

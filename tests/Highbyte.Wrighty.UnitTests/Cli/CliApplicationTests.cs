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
        Assert.Contains("\"released\": true", releaseOutput.ToString());
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

    private static CliApplication Application(
        RecordingBackend backend,
        TextReader input,
        TextWriter output,
        TextWriter? error = null,
        ISkillManager? skillManager = null)
    {
        var projects = new UnusedProjects();
        var claims = new OwnedClaims();
        var resolver = new GitHubWorkItemAddressResolver();
        var trackerBackend = new GitHubTrackerBackend(
            projects,
            claims,
            resolver,
            backend,
            new ClaimMutationGuard(claims));
        var tracker = new TrackerService(new TrackerBackendRegistry([trackerBackend]));
        return new CliApplication(
            new FixedConfigLoader(),
            new TrackerInitializationService(
                new TrackerConfigLoader(),
                new UnusedDiscovery(),
                new UnusedGitHubInitialization(),
                projects),
            tracker,
            new AgentExecutionContextProvider(new Dictionary<string, string?>()),
            skillManager ?? SkillManager.CreateDefault(),
            input,
            output,
            error ?? new StringWriter(),
            Directory.GetCurrentDirectory());
    }

    private sealed class FixedConfigLoader : ITrackerConfigLoader
    {
        public Task<TrackerConfig> LoadAsync(
            string startDirectory,
            CancellationToken cancellationToken) => Task.FromResult(Config);
    }

    private sealed class RecordingBackend : IWorkItemBackend
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
                "P1"));

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

    private sealed class UnusedProjects : IProjectClient
    {
        public Task<ProjectInitializationResult> InitializeAsync(TrackerConfig config, bool checkOnly, CancellationToken cancellationToken) =>
            Task.FromResult(new ProjectInitializationResult(false, ["Project schema is valid."]));
        public Task EnsureAgentContextSchemaAsync(
            TrackerConfig config,
            CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<GitHubProjectItem>> ListAsync(TrackerConfig config, string? status, int? limit, CancellationToken cancellationToken)
        {
            var resolver = new GitHubWorkItemAddressResolver();
            var id = resolver.FromIssueNumber(config, 42);
            return Task.FromResult<IReadOnlyList<GitHubProjectItem>>([new GitHubProjectItem(
                resolver.Decode(id, config),
                new WorkItemSummary(id, "Example", "https://github.com/owner/repo/issues/42", "Done", "P1"),
                "ISSUE42",
                "ITEM42")]);
        }
        public Task UpdateStatusAsync(TrackerConfig config, GitHubProjectItem item, string status, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdateAgentContextAsync(TrackerConfig config, GitHubProjectItem item, string? agentType, string? sessionId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateCreateFieldsAsync(TrackerConfig config, string status, string? priority, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<string> AddIssueAsync(TrackerConfig config, string issueNodeId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task UpdatePriorityAsync(TrackerConfig config, GitHubProjectItem item, string priority, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class OwnedClaims : IClaimService
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
                agentContext.SessionId));
        public Task ReleaseAsync(TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> IsOwnedByCurrentWorkerAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) => Task.FromResult(true);

        public Task<ClaimOwnershipResult> GetOwnershipAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ClaimOwnershipResult(ClaimOwnershipState.OwnedByCurrent));
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
                    SkillManager.SkillVersion,
                    false)
            ]);
        }
    }
}

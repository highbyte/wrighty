using System.Text.Json;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Cli;

public sealed partial class CliApplicationTests
{
    private async Task<(TrackerConfig Config, TrackerService Tracker, WorkItemId Id)> WorkflowCliFixture()
    {
        var root = Path.Combine(Path.GetTempPath(), "wrighty-workflow-cli-" + Guid.NewGuid().ToString("N"));
        temporarySettingsRoots.Add(root);
        Directory.CreateDirectory(root);
        var config = new TrackerConfig
        {
            Backend = "local-markdown", SourcePath = Path.Combine(root, ".wrighty.json"),
            DefaultPickFrom = "Automation", DefaultPickTo = "Doing", DefaultFinishTo = "Complete",
            LocalMarkdown = new() { Statuses = ["Ideas", "Automation", "Doing", "Complete"] }
        };
        var backend = new LocalMarkdownTrackerBackend(new WorkflowIdentity(), new SystemClock());
        await backend.InitializeAsync(config, false, default);
        var created = await backend.CreateAsync(config, new CreateWorkItemOperation(new("CLI workflow", "Body", "Ideas", "P1"), false), default);
        return (config, new(new TrackerBackendRegistry([backend])), created.Id);
    }

    [Fact]
    public async Task Workflow_cli_executes_reviewed_queue_and_send_back_with_structured_results()
    {
        var f = await WorkflowCliFixture();
        var output = new StringWriter();
        var error = new StringWriter();
        var app = Application(new RecordingBackend(), new StringReader(""), output, error,
            config: f.Config, trackerOverride: f.Tracker, inputRedirected: true);
        Assert.Equal(0, await app.InvokeAsync(["actions", "1", "queue", "--json"]));
        using var discovery = JsonDocument.Parse(output.ToString());
        var observed = discovery.RootElement.GetProperty("result");
        var version = observed.GetProperty("stateVersion").GetString()!;
        Assert.Equal("supported", observed.GetProperty("actions")[0].GetProperty("execution").GetString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await app.InvokeAsync(["actions", "1", "queue", "--exec", "--yes", "--expected-version", version, "--json"]));
        using var json = JsonDocument.Parse(output.ToString());
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("applied", result.GetProperty("outcome").GetString());
        Assert.Equal("Automation", result.GetProperty("after").GetProperty("status").GetString());
        Assert.True(result.GetProperty("after").GetProperty("automaticExecutionAllowed").GetBoolean());
        Assert.False(result.GetProperty("startsWorker").GetBoolean());
        Assert.Equal("local:1", json.RootElement.GetProperty("workers").GetProperty("itemId").GetString());
        Assert.DoesNotContain("claimToken", output.ToString());
        Assert.Equal("", error.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await app.InvokeAsync(["actions", "1", "send-back", "--exec", "--yes"]));
        Assert.Contains("send-back applied", output.ToString());
        Assert.Equal("Ideas", (await f.Tracker.GetAsync(f.Config, f.Id, default)).Status);
        Assert.Equal(ClaimOwnershipState.Unclaimed, (await f.Tracker.GetClaimOwnershipAsync(f.Config, f.Id, default)).State);
    }

    [Theory]
    [InlineData("confirmation", "ACTION_CONFIRMATION_REQUIRED")]
    [InlineData("stale", "ACTION_STATE_CHANGED")]
    [InlineData("all", "ARGUMENT_INVALID")]
    [InlineData("discovery-yes", "ARGUMENT_INVALID")]
    [InlineData("discovery-version", "ARGUMENT_INVALID")]
    public async Task Workflow_cli_refuses_unapproved_or_invalid_requests_without_mutation(string scenario, string code)
    {
        var f = await WorkflowCliFixture();
        var output = new StringWriter();
        var error = new StringWriter();
        var args = new List<string> { "actions", "1", "queue", "--json" };
        switch (scenario)
        {
            case "confirmation": args.Add("--exec"); break;
            case "stale": args.AddRange(["--exec", "--yes", "--expected-version", "outdated"]); break;
            case "all": args.AddRange(["--exec", "--yes", "--all"]); break;
            case "discovery-yes": args.Add("--yes"); break;
            case "discovery-version": args.AddRange(["--expected-version", "version"]); break;
        }
        var exit = await Application(new RecordingBackend(), new StringReader(""), output, error,
            config: f.Config, trackerOverride: f.Tracker, inputRedirected: true).InvokeAsync(args.ToArray());
        Assert.NotEqual(0, exit);
        using var json = JsonDocument.Parse(error.ToString());
        Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("Ideas", (await f.Tracker.GetAsync(f.Config, f.Id, default)).Status);
        Assert.Equal("", output.ToString());
    }

    [Theory]
    [InlineData("yes", 0, "Automation")]
    [InlineData("no", 2, "Ideas")]
    public async Task Workflow_cli_interactive_confirmation_obeys_the_answer(string answer, int exit, string status)
    {
        var f = await WorkflowCliFixture();
        var output = new StringWriter();
        Assert.Equal(exit, await Application(new RecordingBackend(), new StringReader(answer), output,
            config: f.Config, trackerOverride: f.Tracker).InvokeAsync(["actions", "1", "queue", "--exec"]));
        Assert.Contains("authorizes automatic processing", output.ToString());
        Assert.Equal(status, (await f.Tracker.GetAsync(f.Config, f.Id, default)).Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Workflow_applied_result_survives_worker_refresh_failure(bool jsonOutput)
    {
        var f = await WorkflowCliFixture();
        var output = new StringWriter();
        var args = new List<string> { "actions", "1", "queue", "--exec", "--yes" };
        if (jsonOutput) args.Add("--json");
        Assert.Equal(0, await Application(new RecordingBackend(), new StringReader(""), output,
            config: f.Config, trackerOverride: f.Tracker, workerInstanceRegistry: new FailedWorkflowRegistry())
            .InvokeAsync(args.ToArray()));
        if (jsonOutput)
        {
            using var result = JsonDocument.Parse(output.ToString());
            Assert.Equal("applied", result.RootElement.GetProperty("result").GetProperty("outcome").GetString());
            Assert.Equal("WORKER_REFRESH_UNAVAILABLE", result.RootElement.GetProperty("refreshError").GetString());
        }
        else Assert.Contains("Action applied; worker assessment could not be refreshed", output.ToString());
        Assert.Equal("Automation", (await f.Tracker.GetAsync(f.Config, f.Id, default)).Status);
    }

    private sealed class FailedWorkflowRegistry : IWorkerInstanceRegistry
    {
        public Task<WorkerRegistrySnapshot> InspectAsync(string configurationPath, CancellationToken cancellationToken) =>
            throw new IOException("Unavailable test registry");
        public Task<IReadOnlyList<WorkerInstanceStatus>> ListAsync(string configurationPath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Must use read-only inspection");
        public Task<IWorkerInstanceRegistration> RegisterAsync(string configurationPath, string configurationRevision,
            string invocationSummary, CancellationToken cancellationToken) => throw new InvalidOperationException("Must not launch");
    }

    private sealed class WorkflowIdentity : IInstallationIdentityProvider
    {
        public Task<string> GetInstallationIdAsync(CancellationToken cancellationToken) => Task.FromResult("workflow-cli");
    }
}

using System.Text.Json;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.UnitTests.Cli;

public sealed partial class CliApplicationTests
{
    [Fact]
    public async Task Actions_json_is_read_only_and_resolves_canonical_ids()
    {
        var backend = new RecordingBackend();
        var output = new StringWriter();
        var exit = await Application(backend, new StringReader(""), output, workerCandidate: true)
            .InvokeAsync(["actions", "42", "--json"]);
        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        var result = json.RootElement.GetProperty("result");
        Assert.Equal("github:owner/repo#42", result.GetProperty("itemId").GetString());
        var actions = result.GetProperty("actions").EnumerateArray().ToArray();
        Assert.All(actions, action =>
        {
            Assert.Equal("available", action.GetProperty("availability").GetString());
            Assert.Equal("manual-only", action.GetProperty("execution").GetString());
        });
        Assert.Null(backend.Patch);
        Assert.Null(backend.Operation);
        Assert.DoesNotContain("claimToken", output.ToString());
    }

    [Fact]
    public async Task Actions_all_explains_blocked_alternatives_and_separates_urls()
    {
        var output = new StringWriter();
        var exit = await Application(new RecordingBackend(), new StringReader(""), output, workerCandidate: true)
            .InvokeAsync(["actions", "42", "--all", "--json"]);
        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString());
        var actions = json.RootElement.GetProperty("result").GetProperty("actions").EnumerateArray().ToArray();
        var queue = actions.Single(action => action.GetProperty("name").GetString() == "queue");
        Assert.Equal("NOT_SUPPORTED", queue.GetProperty("unavailableCode").GetString());
        var review = actions.Single(action => action.GetProperty("name").GetString() == "open-item");
        Assert.Equal(0, review.GetProperty("commands").GetArrayLength());
        Assert.Equal("https://github.com/owner/repo/issues/42", review.GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("made-up", "ACTION_UNKNOWN")]
    [InlineData("resume-session", "RESUME_ADDRESS_UNAVAILABLE")]
    [InlineData("queue", "NOT_SUPPORTED")]
    public async Task Selecting_unknown_or_unavailable_action_returns_structured_error(string name, string code)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await Application(new RecordingBackend(), new StringReader(""), output, error, workerCandidate: true)
            .InvokeAsync(["actions", "42", name, "--json"]);
        Assert.NotEqual(0, exit);
        using var json = JsonDocument.Parse(error.ToString());
        Assert.Equal(code, json.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Selected_available_action_is_the_only_result()
    {
        var output = new StringWriter();
        Assert.Equal(0, await Application(new RecordingBackend(), new StringReader(""), output)
            .InvokeAsync(["actions", "42", "open-item", "--json"]));
        using var json = JsonDocument.Parse(output.ToString());
        var action = Assert.Single(json.RootElement.GetProperty("result").GetProperty("actions").EnumerateArray());
        Assert.Equal("open-item", action.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Execution_is_refused_before_any_work_is_attempted(bool selected)
    {
        var backend = new RecordingBackend();
        var output = new StringWriter();
        var error = new StringWriter();
        string[] args = selected ? ["actions", "42", "clarify", "--exec", "--json"]
            : ["actions", "42", "--exec", "--json"];
        Assert.Equal(2, await Application(backend, new StringReader("yes"), output, error).InvokeAsync(args));
        using var json = JsonDocument.Parse(error.ToString());
        Assert.Equal(selected ? "ACTION_EXECUTION_UNSUPPORTED" : "ARGUMENT_INVALID",
            json.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Null(backend.Patch);
    }

    [Fact]
    public async Task Remote_session_is_not_advertised_as_locally_resumable()
    {
        var output = new StringWriter();
        var session = new AgentSessionRecord("claude", "secret-session", Directory.GetCurrentDirectory(),
            DateTimeOffset.UtcNow, false);
        Assert.Equal(0, await Application(new RecordingBackend(), new StringReader(""), output,
            workerCandidate: true, unclaimedSession: session).InvokeAsync(["actions", "42", "--all", "--json"]));
        using var json = JsonDocument.Parse(output.ToString());
        var action = json.RootElement.GetProperty("result").GetProperty("actions").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "resume-session");
        Assert.Equal("RESUME_ADDRESS_NOT_LOCAL", action.GetProperty("unavailableCode").GetString());
        Assert.DoesNotContain("secret-session", output.ToString());
    }

    [Fact]
    public async Task Get_exposes_the_same_discovery_contract()
    {
        var output = new StringWriter();
        var app = Application(new RecordingBackend(), new StringReader(""), output, workerCandidate: true);
        Assert.Equal(0, await app.InvokeAsync(["get", "42", "--json"]));
        using var get = JsonDocument.Parse(output.ToString());
        var expected = get.RootElement.GetProperty("result").GetProperty("actions");
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await app.InvokeAsync(["actions", "42", "--all", "--json"]));
        using var actions = JsonDocument.Parse(output.ToString());
        Assert.True(JsonElement.DeepEquals(expected, actions.RootElement.GetProperty("result")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Runtime_inspection_failure_only_blocks_local_launch_actions(bool denied)
    {
        var output = new StringWriter();
        var session = new AgentSessionRecord("claude", "session", Directory.GetCurrentDirectory(),
            DateTimeOffset.UtcNow, true);
        IAgentRuntimeCatalog runtimes = denied ? new DeniedRuntimeCatalog() : new FixedRuntimeCatalog();
        Assert.Equal(0, await Application(new RecordingBackend(), new StringReader(""), output,
            workerCandidate: true, unclaimedSession: session, runtimeCatalog: runtimes)
            .InvokeAsync(["actions", "42", "--all", "--json"]));
        using var json = JsonDocument.Parse(output.ToString());
        var actions = json.RootElement.GetProperty("result").GetProperty("actions").EnumerateArray().ToArray();
        var resume = actions.Single(value => value.GetProperty("name").GetString() == "resume-session");
        Assert.Equal(denied ? "ACTION_STATE_UNVERIFIED" : "AGENT_NOT_INSTALLED",
            resume.GetProperty("unavailableCode").GetString());
        var review = actions.Single(value => value.GetProperty("name").GetString() == "open-item");
        Assert.Equal("available", review.GetProperty("availability").GetString());
    }

    [Fact]
    public async Task Human_discovery_labels_inert_guidance_and_links()
    {
        var output = new StringWriter();
        Assert.Equal(0, await Application(new RecordingBackend(), new StringReader(""), output,
            workerCandidate: true).InvokeAsync(["actions", "42", "--all"]));
        Assert.Contains("Discovery only", output.ToString());
        Assert.Contains("manual-only", output.ToString());
        Assert.Contains("Link: https://github.com/owner/repo/issues/42", output.ToString());
        Assert.Contains("RESUME_ADDRESS_UNAVAILABLE", output.ToString());
    }

    private sealed class DeniedRuntimeCatalog : IAgentRuntimeCatalog
    {
        public AgentRuntimeSnapshot Snapshot() => throw new UnauthorizedAccessException("Denied");
    }

}

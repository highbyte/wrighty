using System.Text.Json;
using Highbyte.Wrighty.Actions;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Cli;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.Cli;

public sealed partial class CliApplicationTests
{
    [Fact]
    public async Task Batch_cli_freezes_selection_skips_new_claims_and_replays_results()
    {
        var f = await WorkflowCliFixture();
        var backend = f.Tracker.Backend(f.Config);
        var second = await backend.CreateAsync(f.Config, new CreateWorkItemOperation(new("Second", "Body", "Ideas", "P1"), false), default);
        var output = new StringWriter();
        var error = new StringWriter();
        CliApplication App() => Application(new RecordingBackend(), new StringReader(""), output, error,
            config: f.Config, trackerOverride: f.Tracker, storageLocationCatalog: new(new Highbyte.Wrighty.Caching.CachePaths(Path.GetDirectoryName(f.Config.SourcePath))));
        Assert.Equal(0, await App().InvokeAsync(["batch", "preview", "queue", "--status", "Ideas", "--json"]));
        using var preview = JsonDocument.Parse(output.ToString());
        var frozen = preview.RootElement.GetProperty("result").GetProperty("preview");
        Assert.Equal(2, frozen.GetProperty("candidates").GetArrayLength());
        Assert.False(frozen.GetProperty("startsWorker").GetBoolean());
        var id = frozen.GetProperty("id").GetString()!;
        var later = await backend.CreateAsync(f.Config, new CreateWorkItemOperation(new("Later", "Body", "Ideas", "P1"), false), default);
        var claim = await backend.TryClaimAsync(f.Config, second.Id, AgentExecutionContext.Human, default);
        output.GetStringBuilder().Clear();
        Assert.Equal(2, await App().InvokeAsync(["batch", "execute", id, "--json"]));
        Assert.Equal("Ideas", (await f.Tracker.GetAsync(f.Config, f.Id, default)).Status);
        Assert.Equal(6, await App().InvokeAsync(["batch", "execute", id, "--yes", "--json"]));
        using var result = JsonDocument.Parse(output.ToString());
        var items = result.RootElement.GetProperty("result").GetProperty("items");
        Assert.Equal("applied", items[0].GetProperty("outcome").GetString());
        Assert.Equal("skipped", items[1].GetProperty("outcome").GetString());
        Assert.Equal("Ideas", (await f.Tracker.GetAsync(f.Config, later.Id, default)).Status);
        Assert.Equal(claim.ClaimantId, (await backend.GetClaimOwnershipAsync(f.Config, second.Id, default)).ClaimantId);
        var firstResult = output.ToString();
        output.GetStringBuilder().Clear();
        Assert.Equal(6, await App().InvokeAsync(["batch", "execute", id, "--yes", "--json"]));
        Assert.Equal(firstResult, output.ToString());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await App().InvokeAsync(["batch", "show", id]));
        Assert.Contains("skipped", output.ToString());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("mixed")]
    [InlineData("fields-with-ids")]
    [InlineData("unsupported")]
    public async Task Batch_cli_rejects_implicit_mixed_and_unsupported_selections(string scenario)
    {
        var f = await WorkflowCliFixture();
        List<string> args = ["batch", "preview", scenario == "unsupported" ? "launch" : "queue", "--json"];
        if (scenario == "mixed") args.AddRange(["--id", "1", "--status", "Ideas"]);
        if (scenario == "fields-with-ids") args.AddRange(["--id", "1", "--field", "x=y"]);
        var error = new StringWriter();
        Assert.Equal(2, await Application(new RecordingBackend(), new StringReader(""), new StringWriter(), error,
            config: f.Config, trackerOverride: f.Tracker).InvokeAsync(args.ToArray()));
        Assert.Equal("Ideas", (await f.Tracker.GetAsync(f.Config, f.Id, default)).Status);
    }

    [Fact]
    public async Task Batch_cli_explicit_ids_deduplicate_and_support_send_back()
    {
        var f = await WorkflowCliFixture();
        await new WorkflowActionService(f.Tracker).ExecuteAsync(f.Config, f.Id, "queue", null, default);
        var output = new StringWriter();
        var app = Application(new RecordingBackend(), new StringReader(""), output,
            config: f.Config, trackerOverride: f.Tracker, storageLocationCatalog: new(new Highbyte.Wrighty.Caching.CachePaths(Path.GetDirectoryName(f.Config.SourcePath))));
        Assert.Equal(0, await app.InvokeAsync(["batch", "preview", "send-back", "--id", "1", "--id", "local:1", "--json"]));
        using var json = JsonDocument.Parse(output.ToString());
        var preview = json.RootElement.GetProperty("result").GetProperty("preview");
        Assert.Equal(1, preview.GetProperty("selectedCount").GetInt32());
        output.GetStringBuilder().Clear();
        Assert.Equal(0, await app.InvokeAsync(["batch", "execute", preview.GetProperty("id").GetString()!, "--yes"]));
        Assert.Equal("Ideas", (await f.Tracker.GetAsync(f.Config, f.Id, default)).Status);
        Assert.False((await f.Tracker.GetAsync(f.Config, f.Id, default)).AutomaticExecutionAllowed);
    }
}

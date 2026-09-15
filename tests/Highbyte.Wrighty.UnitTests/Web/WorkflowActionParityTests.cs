using System.Net;
using Highbyte.Wrighty.Actions;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Errors;
using System.Text.Json;

namespace Highbyte.Wrighty.UnitTests.Web;

public sealed partial class WrightyWebServerTests
{
    [Theory]
    [InlineData("QueueItem", "queue")]
    [InlineData("DequeueItem", "send-back")]
    public async Task Board_workflow_handlers_refuse_a_paused_item_like_the_shared_executor(string handler, string action)
    {
        var host = await StartServer(openBrowser: false, releaseSeededClaim: true);
        using var client = new HttpClient();
        var before = await StoredState();
        var (config, backend, _) = await StoredBackend();
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var error = await Assert.ThrowsAsync<TrackerException>(() => new WorkflowActionService(tracker)
            .ExecuteAsync(config, before.Item.Id, action, null, default));
        using var response = await PostForm(client, host, handler, new() { ["id"] = before.Item.Id.Value });
        Assert.NotEqual(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(error.Code, await response.Content.ReadAsStringAsync());
        var after = await StoredState();
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        await host.Stop();
    }
}

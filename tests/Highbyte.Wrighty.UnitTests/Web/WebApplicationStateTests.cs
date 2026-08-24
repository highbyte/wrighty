using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Web;

namespace Highbyte.Wrighty.UnitTests.Web;

public sealed class WebApplicationStateTests
{
    [Fact]
    public void A_request_keeps_one_configuration_while_the_next_request_gets_the_update()
    {
        var original = Configuration("claude");
        var updated = Configuration("codex");
        var state = new WebApplicationState(
            original,
            "token",
            Path.GetTempPath(),
            activeConfigurationRevision: "before");

        using (state.CaptureConfigurationForRequest())
        {
            Assert.Same(original, state.Config);

            Assert.True(state.TryApplyConfiguration(updated, "after"));

            Assert.Same(original, state.Config);
            Assert.Same(updated, state.ActiveConfiguration.Config);
            Assert.Equal("after", state.ActiveConfigurationRevision);
        }

        using (state.CaptureConfigurationForRequest())
        {
            Assert.Same(updated, state.Config);
        }
    }

    [Fact]
    public void A_backend_change_remains_a_process_restart_boundary()
    {
        var original = Configuration("claude");
        var state = new WebApplicationState(
            original,
            "token",
            Path.GetTempPath(),
            activeConfigurationRevision: "before");
        var incompatible = original with { Backend = "github" };

        Assert.False(state.TryApplyConfiguration(incompatible, "after"));

        Assert.Same(original, state.ActiveConfiguration.Config);
        Assert.Equal("before", state.ActiveConfigurationRevision);
    }

    [Fact]
    public void Dynamic_only_revisions_do_not_make_running_workers_stale()
    {
        var original = Configuration("claude");
        var state = new WebApplicationState(
            original,
            "token",
            Path.GetTempPath(),
            activeConfigurationRevision: "before");

        Assert.True(state.TryApplyConfiguration(
            Configuration("claude") with
            {
                Testing = new TestingConfig { NotInstalledAgents = ["codex"] }
            },
            "dynamic",
            restartRunningWorkers: false));

        IEnumerable<string> dynamicRevisions = state.ActiveConfiguration.WorkerCompatibleRevisions;
        Assert.Contains("before", dynamicRevisions);
        Assert.Contains("dynamic", dynamicRevisions);

        Assert.True(state.TryApplyConfiguration(Configuration("codex"), "policy"));

        IEnumerable<string> policyRevisions = state.ActiveConfiguration.WorkerCompatibleRevisions;
        Assert.DoesNotContain("before", policyRevisions);
        Assert.DoesNotContain("dynamic", policyRevisions);
        Assert.Contains("policy", policyRevisions);
    }

    private static TrackerConfig Configuration(string defaultAgent) => new()
    {
        Backend = "local-markdown",
        Worker = new WorkerConfig { DefaultAgent = defaultAgent }
    };
}

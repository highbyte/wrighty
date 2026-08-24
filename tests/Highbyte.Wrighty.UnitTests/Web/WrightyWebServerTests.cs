using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Channels;
using Highbyte.Wrighty;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.ApprovedContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Caching;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.Initialization;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;
using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Storage;
using Highbyte.Wrighty.Web;
using Highbyte.Wrighty.Workers;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;

namespace Highbyte.Wrighty.UnitTests.Web;

public sealed partial class WrightyWebServerTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), $"wrighty-web-tests-{Guid.NewGuid():N}");

    [Fact]
    public async Task Server_serves_public_shell_but_requires_launch_token_for_tracker_fragments()
    {
        var host = await StartServer();
        using var client = new HttpClient();

        var shell = await client.GetStringAsync(host.Origin);
        Assert.Contains("<h1>Wrighty</h1>", shell);
        Assert.Contains("class=\"workspace-path\"", shell);
        Assert.Contains($"title=\"{directory}\"", shell);
        Assert.DoesNotContain("Hostile item", shell);
        Assert.Contains("allowEval\":false", shell);
        Assert.Contains("includeIndicatorStyles\":false", shell);
        Assert.Contains("timeout\":3000", shell);
        Assert.Contains("/assets/highlight-yaml.js", shell);
        Assert.Contains("id=\"board-search\"", shell);
        Assert.Contains("id=\"new-board-item\"", shell);
        Assert.Contains("class=\"primary board-create-action\"", shell);
        var boardFilterEnd = shell.IndexOf("</form>", shell.IndexOf("id=\"board-filters\"", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(boardFilterEnd < shell.IndexOf("id=\"new-board-item\"", StringComparison.Ordinal));
        Assert.Contains("name=\"sort\"", shell);
        Assert.Contains("class=\"board-filter-menu\"", shell);
        Assert.Contains("data-board-filter-count", shell);
        Assert.Contains("id=\"close-board-filters\"", shell);
        Assert.Contains("data-close-board-filters", shell);
        Assert.Contains("class=\"board-filter-clear\" type=\"button\" data-clear-board-filters>Clear all</button>", shell);
        Assert.Contains("hx-disabled-elt=\"#reset-board-view\"", shell);
        Assert.Contains("<button id=\"reset-board-view\" type=\"button\" data-reset-board-view>Reset view</button>", shell);
        Assert.DoesNotContain("Reset board controls", shell);
        Assert.Contains("<fieldset id=\"board-filter-fields\" class=\"board-filter-fields\">", shell);
        Assert.Contains("<select id=\"board-agent-filter\" name=\"agent\"", shell);
        Assert.Contains(
            "title=\"Filters by the item's associated agent: active worker, retained session, or configured agent policy.\"",
            shell);
        Assert.Contains("aria-describedby=\"board-agent-filter-help\"", shell);
        Assert.Contains("id=\"board-agent-filter-help\" class=\"visually-hidden\"", shell);
        Assert.Contains("<option value=\"claude\">Claude</option>", shell);
        Assert.Contains("<option value=\"codex\">Codex</option>", shell);
        Assert.Contains("<option value=\"copilot\">Copilot</option>", shell);
        Assert.Contains("<select id=\"board-priority-filter\" name=\"priority\">", shell);
        Assert.Contains("<option value=\"\">Any</option>", shell);
        Assert.DoesNotContain("board-priority-options", shell);
        Assert.Contains("id=\"operations-content\"", shell);
        Assert.Contains(
            "hx-trigger=\"wrighty:ready, wrighty:refresh from:body, wrighty:operations-refresh\"",
            shell);
        Assert.Contains("hx-request='{\"timeout\":130000}'", shell);
        Assert.Contains("id=\"settings-content\"", shell);
        var settingsLoader = shell[shell.IndexOf("<section id=\"settings-content\"", StringComparison.Ordinal)..];
        settingsLoader = settingsLoader[..settingsLoader.IndexOf("</section>", StringComparison.Ordinal)];
        Assert.Contains("hx-request='{\"timeout\":130000}'", settingsLoader);
        Assert.Contains("id=\"provider-capacity-region\"", shell);
        Assert.Contains("id=\"worker-summary-region\"", shell);
        // The page-level tabs: every section is discoverable without scrolling, board first for
        // the local backend.
        Assert.Contains("role=\"tablist\"", shell);
        Assert.Contains("id=\"tab-board\"", shell);
        Assert.Contains("id=\"tab-operations\"", shell);
        Assert.Contains("id=\"tab-settings\"", shell);
        Assert.Contains("id=\"tab-attention-badge\"", shell);
        Assert.True(
            shell.IndexOf("id=\"tab-board\"", StringComparison.Ordinal) <
            shell.IndexOf("id=\"tab-operations\"", StringComparison.Ordinal));
        Assert.Contains("<dialog id=\"confirmation-dialog\"", shell);
        Assert.Contains("id=\"confirmation-dialog-title\"", shell);
        Assert.Contains("id=\"confirmation-dialog-message\"", shell);
        Assert.Contains("id=\"confirmation-dialog-cancel\"", shell);
        Assert.Contains("id=\"confirmation-dialog-accept\"", shell);
        Assert.Contains("<meta name=\"wrighty-auth\" content=\"token\">", shell);
        Assert.Contains("id=\"copy-access-link\"", shell);
        Assert.Contains("<output id=\"copy-access-link-feedback\"", shell);
        Assert.True(
            shell.IndexOf("id=\"worker-summary-region\"", StringComparison.Ordinal) <
            shell.IndexOf("id=\"provider-capacity-region\"", StringComparison.Ordinal));
        Assert.True(
            shell.IndexOf("id=\"provider-capacity-region\"", StringComparison.Ordinal) <
            shell.IndexOf("id=\"connection-status\"", StringComparison.Ordinal));
        Assert.True(
            shell.IndexOf("id=\"connection-status\"", StringComparison.Ordinal) <
            shell.IndexOf("id=\"copy-access-link\"", StringComparison.Ordinal));
        Assert.True(
            shell.IndexOf("id=\"item-panel\"", StringComparison.Ordinal) <
            shell.IndexOf("id=\"confirmation-dialog\"", StringComparison.Ordinal));
        Assert.DoesNotContain("name=\"q\"", shell);
        Assert.DoesNotContain(">Load scope<", shell);

        var unauthorized = await client.GetAsync($"{host.Origin}/?handler=Board");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var settingsUnauthorized = await client.GetAsync($"{host.Origin}/?handler=Settings");
        Assert.Equal(HttpStatusCode.Unauthorized, settingsUnauthorized.StatusCode);

        using var boardRequest = new HttpRequestMessage(HttpMethod.Get, $"{host.Origin}/?handler=Board");
        boardRequest.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        var board = await client.SendAsync(boardRequest);
        var html = await board.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, board.StatusCode);
        Assert.Contains("Hostile item", html);
        Assert.Contains("data-filter-text=", html);
        Assert.Contains("claimed claimed-current", html);
        Assert.Contains("claimed claimed-other", html);
        Assert.Contains("Codex", html);
        Assert.Contains("Claude", html);
        Assert.Contains("Attention required", html);
        Assert.Contains("activity-needs-attention", html);
        Assert.Contains("Needs attention", html);
        Assert.Contains("activity-agent-active", html);
        Assert.Contains("Claude active", html);
        Assert.Contains("class=\"column-count has-tooltip\"", html);
        Assert.Contains("data-visible-count", html);
        Assert.Contains("data-total-count=", html);
        Assert.Contains("items currently shown in this column.", html);
        Assert.Contains("tabindex=\"0\"", html);
        Assert.Contains("data-board-column-sort-index=", html);
        Assert.Contains("class=\"card-timestamp\"", html);
        Assert.NotNull(board.Headers.ETag);

        using var filteredBoardRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Board&claimKind=automation&sort=title%3Adesc");
        filteredBoardRequest.Headers.IfNoneMatch.Add(board.Headers.ETag);
        var filteredBoard = await client.SendAsync(filteredBoardRequest);
        var filteredBoardHtml = await filteredBoard.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, filteredBoard.StatusCode);
        Assert.Contains("Automation claim", filteredBoardHtml);
        Assert.DoesNotContain("Hostile item", filteredBoardHtml);
        Assert.Contains("<button type=\"button\" data-clear-board-filters>Clear all</button>", filteredBoardHtml);
        Assert.NotEqual(board.Headers.ETag, filteredBoard.Headers.ETag);

        using var agentBoardRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Board&agent=codex");
        var agentBoardHtml = await (await client.SendAsync(agentBoardRequest)).Content.ReadAsStringAsync();
        Assert.Contains("Hostile item", agentBoardHtml);

        using var ignoredQueryRequest = new HttpRequestMessage(HttpMethod.Get, $"{host.Origin}/?handler=Board&q=does-not-match");
        ignoredQueryRequest.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        var ignoredQuery = await client.SendAsync(ignoredQueryRequest);
        Assert.Contains("Hostile item", await ignoredQuery.Content.ReadAsStringAsync());

        using var unchangedRequest = new HttpRequestMessage(HttpMethod.Get, $"{host.Origin}/?handler=Board");
        unchangedRequest.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        unchangedRequest.Headers.IfNoneMatch.Add(board.Headers.ETag);
        var unchanged = await client.SendAsync(unchangedRequest);
        Assert.Equal(HttpStatusCode.NoContent, unchanged.StatusCode);

        using var providerRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=ProviderCapacity");
        using var provider = await client.SendAsync(providerRequest);
        var providerHtml = await provider.Content.ReadAsStringAsync();
        Assert.Contains("Agent capacity", providerHtml);
        Assert.Contains("class=\"button-compact\">Probe all</button>", providerHtml);
        Assert.Contains("class=\"button-compact\">Probe Claude</button>", providerHtml);
        Assert.Contains("Available", providerHtml);
        Assert.Contains("provider-capacity-menu has-available", providerHtml);
        Assert.NotNull(provider.Headers.ETag);

        using var workerSummaryRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=WorkerSummary");
        using var workerSummary = await client.SendAsync(workerSummaryRequest);
        var workerSummaryHtml = await workerSummary.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, workerSummary.StatusCode);
        Assert.Contains("id=\"worker-summary-button\"", workerSummaryHtml);
        Assert.Contains(">Workers</span>", workerSummaryHtml);
        Assert.Contains("<strong>0</strong>", workerSummaryHtml);
        Assert.Contains("data-open-worker-processes", workerSummaryHtml);
        Assert.NotNull(workerSummary.Headers.ETag);

        using var unchangedWorkerSummaryRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=WorkerSummary");
        unchangedWorkerSummaryRequest.Headers.IfNoneMatch.Add(workerSummary.Headers.ETag);
        using var unchangedWorkerSummary = await client.SendAsync(unchangedWorkerSummaryRequest);
        Assert.Equal(HttpStatusCode.NoContent, unchangedWorkerSummary.StatusCode);

        using var operationsRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Operations&search=Hostile&agent=codex&priority=P1&workflowStatus=In%20Progress&sort=updated%3Adesc");
        var operations = await client.SendAsync(operationsRequest);
        var operationsHtml = await operations.Content.ReadAsStringAsync();
        Assert.Contains("id=\"operations-filters\"", operationsHtml);
        Assert.Contains("id=\"operations-sort\" type=\"hidden\" name=\"sort\" value=\"updated:desc\"", operationsHtml);
        Assert.DoesNotContain("<label>Sort", operationsHtml);
        Assert.Contains("value=\"Hostile\"", operationsHtml);
        Assert.Contains("<select id=\"operations-agent-filter\" name=\"agent\">", operationsHtml);
        Assert.Contains("<option value=\"claude\">Claude</option>", operationsHtml);
        Assert.Contains("<option value=\"codex\" selected=\"selected\">Codex</option>", operationsHtml);
        Assert.Contains("<option value=\"copilot\">Copilot</option>", operationsHtml);
        Assert.Contains("<select id=\"operations-priority-filter\" name=\"priority\">", operationsHtml);
        Assert.Contains("<option value=\"P1\" selected=\"selected\">P1</option>", operationsHtml);
        Assert.Contains("<select id=\"operations-workflow-status-filter\" name=\"workflowStatus\">", operationsHtml);
        Assert.Contains("<option value=\"In Progress\" selected=\"selected\">In Progress</option>", operationsHtml);
        Assert.DoesNotContain(">Clear</button>", operationsHtml);
        Assert.Contains("<button type=\"button\" data-clear-operations-filters>Clear all</button>", operationsHtml);
        Assert.Contains("data-operations-sort=\"updated:asc\"", operationsHtml);
        Assert.Contains("data-operations-sort=\"agent:asc\"", operationsHtml);
        Assert.Contains("data-operations-sort-field=\"agent\"", operationsHtml);
        Assert.Contains("<col class=\"operations-col-title\">", operationsHtml);
        Assert.Contains("<col class=\"operations-col-recovery\">", operationsHtml);
        Assert.DoesNotContain(">Updated 20", operationsHtml);
        Assert.DoesNotContain("operational items shown.", operationsHtml);
        Assert.Contains("data-operations-sort-field=\"updated\"", operationsHtml);
        Assert.Contains("aria-sort=\"descending\"", operationsHtml);
        Assert.Contains("data-operations-sort=\"default\"", operationsHtml);
        Assert.Contains(">Default order</button>", operationsHtml);
        Assert.True(
            operationsHtml.IndexOf(">Default order</button>", StringComparison.Ordinal) <
            operationsHtml.IndexOf("class=\"operations-item-count\"", StringComparison.Ordinal));
        Assert.Contains("Workflow status", operationsHtml);
        Assert.Contains("Operational status", operationsHtml);
        Assert.DoesNotContain("Newest created", operationsHtml);
        Assert.Contains("Hostile item", operationsHtml);
        Assert.Contains("<td>codex</td>", operationsHtml);
        Assert.Contains("<time datetime=", operationsHtml);
        Assert.DoesNotContain("Claimed elsewhere", operationsHtml);

        await host.Stop();
    }

    [Fact]
    public async Task Hosted_worker_log_is_nested_in_its_worker_card()
    {
        var host = await StartServer(
            openBrowser: false,
            workerConfig: new WorkerConfig
            {
                DefaultAgent = "codex",
                UseWorkerQueue = true
            },
            hostedWorkerAvailable: true);
        using var client = new HttpClient();
        try
        {
            using var settingsRequest = AuthenticatedGet(
                host,
                $"{host.Origin}/?handler=Settings");
            var settingsHtml = await (await client.SendAsync(settingsRequest))
                .Content.ReadAsStringAsync();
            var save = await PostForm(
                client,
                host,
                "Configuration",
                new Dictionary<string, string>
                {
                    ["operation"] = "worker",
                    ["revision"] = HiddenValue(settingsHtml, "revision"),
                    ["workspaceMode"] = "shared",
                    ["useWorkerQueue"] = "true"
                });
            var savedHtml = await save.Content.ReadAsStringAsync();
            Assert.Contains(
                "Configuration saved and applied to this web console.",
                savedHtml);
            Assert.DoesNotContain("configuration-restart-warning", savedHtml);

            var response = await PostForm(
                client,
                host,
                "StartHostedWorker",
                []);
            var html = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var origin = html.IndexOf("Hosted by this web console", StringComparison.Ordinal);
            Assert.True(origin >= 0, html);
            var cardStart = html.LastIndexOf("<article", origin, StringComparison.Ordinal);
            var cardEnd = html.IndexOf("</article>", origin, StringComparison.Ordinal);
            var log = html.IndexOf("class=\"hosted-worker-log-panel\"", origin, StringComparison.Ordinal);
            Assert.True(cardStart >= 0, html);
            Assert.True(cardEnd > cardStart, html);
            Assert.True(log >= cardStart && log <= cardEnd, html);
            Assert.Contains("aria-describedby=\"hosted-worker-log-description-", html);
            Assert.Contains("class=\"hosted-worker-log\"", html);
            Assert.Contains("handler=HostedWorkerLog&amp;runId=", html);
            Assert.Contains("id=\"start-hosted-worker\"", html);
            Assert.Contains("title=\"Shows the latest 200 lifecycle events;", html);
            Assert.DoesNotContain("Hosted worker operational log ·", html);
            Assert.DoesNotContain("class=\"hosted-log-safety\"", html);
        }
        finally
        {
            await host.Stop();
        }
    }

    [Fact]
    public void Worker_summary_counts_only_verified_workers_and_marks_processing()
    {
        var now = DateTimeOffset.UtcNow;
        WorkerInstance Instance(string runId, string? currentItemId) => new(
            runId,
            42,
            null,
            now,
            now,
            "config-hash",
            "revision",
            "test",
            "worker",
            currentItemId,
            currentItemId is null ? WorkerInstanceState.Idle : WorkerInstanceState.RunningItem);
        var summary = WorkerSummaryPageModel.From([
            new WorkerInstanceStatus(Instance("active", "local:20"), WorkerInstanceLiveness.Running, null),
            new WorkerInstanceStatus(Instance("idle", null), WorkerInstanceLiveness.Running, null),
            new WorkerInstanceStatus(Instance("stale", null), WorkerInstanceLiveness.Stale, "Heartbeat expired")
        ]);

        Assert.Equal(2, summary.RunningCount);
        Assert.Equal(1, summary.ProcessingCount);
        Assert.Equal(1, summary.AttentionCount);
    }

    [Fact]
    public async Task Board_agent_filter_matches_an_unclaimed_retained_session()
    {
        var host = await StartServer(openBrowser: false, releaseSeededClaim: true);
        using var client = new HttpClient();

        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Board&agent=codex");
        var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains("Hostile item", html);
    }

    [Fact]
    public async Task Settings_surface_reads_and_updates_typed_repository_configuration()
    {
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();
        using var operationsRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Operations");
        var operationsResponse = await client.SendAsync(operationsRequest);
        var operationsHtml = await operationsResponse.Content.ReadAsStringAsync();

        // The operations fragment carries the live surfaces only; every settings form moved to
        // its own fragment on the Settings tab.
        Assert.Equal(HttpStatusCode.OK, operationsResponse.StatusCode);
        Assert.Contains("Local worker processes", operationsHtml);
        Assert.Contains("<span>Operational priority</span>", operationsHtml);
        Assert.DoesNotContain("id=\"configuration-workflow-form\"", operationsHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("<th>Context</th>", operationsHtml, StringComparison.Ordinal);
        Assert.Contains(">Actions</th>", operationsHtml, StringComparison.Ordinal);
        Assert.Contains("Codex session retained here", operationsHtml, StringComparison.Ordinal);
        Assert.Contains("Open Codex", operationsHtml, StringComparison.Ordinal);
        Assert.Contains("handler=OpenSessionCli", operationsHtml, StringComparison.Ordinal);
        Assert.Contains("handler=OpenSessionDesktop", operationsHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"context-approval-details\"", operationsHtml, StringComparison.Ordinal);

        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Settings");
        var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Repository settings", html);
        Assert.Contains("Agent execution profiles", html);
        Assert.Contains("Agent usage recovery", html);
        Assert.Contains("id=\"configuration-usage-failure-form\"", html);
        Assert.DoesNotContain("data-settings-dirty=\"true\"", html, StringComparison.Ordinal);
        Assert.Matches(
            "<button[^>]*data-settings-save[^>]*disabled[^>]*>Save workflow</button>",
            html);
        Assert.Contains("id=\"configuration-usage-failure-action\"", html);
        Assert.Contains("id=\"configuration-usage-failure-claude-fallbacks\"", html);
        Assert.True(
            html.IndexOf("id=\"configuration-worker-form\"", StringComparison.Ordinal) <
            html.IndexOf("id=\"configuration-profiles-form\"", StringComparison.Ordinal));
        Assert.True(
            html.IndexOf("id=\"configuration-profiles-form\"", StringComparison.Ordinal) <
            html.IndexOf("id=\"configuration-usage-failure-form\"", StringComparison.Ordinal));
        Assert.Contains("hx-request='{\"timeout\":130000}'", html);
        // Named by scope now that the page carries two catalogues; "Settings catalogue" alone
        // no longer says which one.
        Assert.Contains("Repository settings catalogue", html);
        Assert.Contains("id=\"storage-locations\"", html);
        Assert.Contains("<h3>Storage settings</h3>", html);
        Assert.Matches(
            "<h3>Storage settings</h3>\\s*<span class=\"settings-section-subtitle\">",
            html);
        Assert.Contains("Local Markdown runtime state", html);
        Assert.Contains(Path.Combine(directory, ".wrighty", ".wrighty-runtime-v1.json"), html);
        Assert.Contains("Installation cache root", html);
        var revision = HiddenValue(html, "revision");

        var result = await PostForm(
            client,
            host,
            "Configuration",
            new Dictionary<string, string>
            {
                ["operation"] = "workflow",
                ["revision"] = revision,
                ["defaultCreateStatus"] = "Todo",
                ["defaultPickFrom"] = "Ready",
                ["defaultPickTo"] = "Doing",
                ["defaultFinishTo"] = "Complete",
                ["configPath"] = Path.Combine(directory, "must-not-be-used.json")
            });
        var resultHtml = await result.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Contains("Configuration saved", resultHtml);
        var stored = await new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None);
        Assert.Equal("Todo", stored.DefaultCreateStatus);
        Assert.Equal("Ready", stored.DefaultPickFrom);
        Assert.Equal("Doing", stored.DefaultPickTo);
        Assert.Equal("Complete", stored.DefaultFinishTo);
        Assert.False(File.Exists(Path.Combine(directory, "must-not-be-used.json")));
        await host.Stop();
    }

    [Fact]
    public async Task Settings_surface_shows_and_edits_this_machines_own_settings()
    {
        // The console has never surfaced a user-scoped setting — not even the host label, which has
        // existed far longer than this console. This is that scope's first appearance, and the
        // reason the profile editor can be built on top of it later.
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Settings");
        var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains("User settings", html);
        Assert.Contains("User settings catalogue", html);
        // The split has to be legible, not just implemented: an operator needs to know why this
        // panel is separate from the repository one beside it.
        Assert.Contains("never written to", html);
        Assert.Contains("anonymous", html);
        Assert.Contains("class=\"user-configuration-summary\"", html);
        Assert.Contains("class=\"user-configuration-intro\"", html);
        Assert.Contains("class=\"settings-section-subtitle\"", html);
        Assert.Contains("class=\"muted user-configuration-source\"", html);
        Assert.Contains("class=\"user-host-label-controls\"", html);
        Assert.Contains("aria-describedby=\"user-configuration-host-help\"", html);
        Assert.Contains("id=\"user-configuration-host-help\" class=\"muted configuration-help\"", html);
        Assert.Contains(
            "id=\"user-profile-mappings-heading\" class=\"user-profile-mappings-heading\">Agent execution profiles</h3>",
            html);

        var result = await PostForm(
            client,
            host,
            "UserConfiguration",
            new Dictionary<string, string>
            {
                ["operation"] = "hostLabel",
                // Scoped to this form's own field: the page now carries two revisions, and the
                // repository one appears first. Posting it here would be exactly the stale-write
                // the guard exists to catch.
                ["revision"] = ValueOfInput(html, "user-configuration-revision"),
                ["hostLabel"] = "workstation-alpha"
            });
        var saved = await result.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Contains("workstation-alpha", saved);
        // User settings apply immediately, unlike the repository forms beside them.
        Assert.Contains("nothing needs restarting", saved);
        await host.Stop();
    }

    [Fact]
    public async Task Settings_surface_manages_per_agent_availability_and_failure_simulation()
    {
        var store = new TrackerConfigLoader();
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Settings");
        var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains("Advanced/testing", html);
        Assert.Contains("id=\"agent-testing-codex\"", html);
        Assert.Contains("Installed locally", html);
        Assert.Contains("Uses retry / handoff policy", html);
        Assert.Contains(
            "retry this agent up to 5 times; cross-agent handoff is off",
            html);
        var enabled = await PostForm(
            client,
            host,
            "Configuration",
            new Dictionary<string, string>
            {
                ["operation"] = "testing",
                ["revision"] = ValueOfInput(html, "agent-testing-revision-codex"),
                ["agent"] = "codex",
                ["pretendNotInstalled"] = "true",
                ["failureKind"] = "rate-limited",
                ["retryAfterSeconds"] = "2"
            });
        var saved = WebUtility.HtmlDecode(await enabled.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        Assert.Contains("Pretending not installed", saved);
        Assert.Contains("1 active", saved);
        Assert.Matches("value=\"rate-limited\" selected", saved);
        var configured = await store.TryLoadPathAsync(
            Path.Combine(directory, ".wrighty.json"), CancellationToken.None);
        Assert.True(configured!.EffectiveTesting.PretendsAgentIsNotInstalled("codex"));
        Assert.Equal(AgentFailureKind.RateLimited,
            configured.EffectiveTesting.FindAgentFailure("codex")?.Kind);

        var disabled = await PostForm(
            client,
            host,
            "Configuration",
            new Dictionary<string, string>
            {
                ["operation"] = "testing-reset",
                ["revision"] = ValueOfInput(saved, "agent-testing-revision-codex")
            });
        var cleared = WebUtility.HtmlDecode(await disabled.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.DoesNotContain("Pretending not installed", cleared);
        configured = await store.TryLoadPathAsync(
            Path.Combine(directory, ".wrighty.json"), CancellationToken.None);
        Assert.Null(configured!.Testing);
        await host.Stop();
    }

    [Fact]
    public async Task The_console_offers_both_halves_of_a_profile()
    {
        // The split this feature exists to express: the repository agrees on the names, this machine
        // decides what they resolve to. Both must be editable from one page or an operator has to
        // know which half lives where before they can change either.
        var host = await StartServer(openBrowser: false, pickFrom: "Worker queue");
        using var client = new HttpClient();
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Settings");
        var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains("id=\"configuration-profiles-form\"", html);
        Assert.Contains(
            "id=\"configuration-profiles-form\" class=\"configuration-form-wide\"",
            html);
        Assert.Contains("Repository profile names", html);
        Assert.Contains("data-token-label=\"profile\"", html);
        Assert.Contains("data-allow-create=\"true\"", html);
        Assert.Contains("<select id=\"configuration-default-execution-profile\"", html);
        Assert.Contains("Shared policy", html);
        Assert.Matches(
            "class=\"muted configuration-help\">\\s*Shared policy",
            html);
        Assert.Matches(
            "class=\"muted configuration-help\">\\s*The order is used immediately",
            html);
        Assert.Contains("id=\"configuration-archive-form\"", html);
        Assert.Contains("data-token-label=\"archive status\"", html);
        Assert.Contains("data-preserve-case=\"true\"", html);
        Assert.Contains("data-known-values=\"[&quot;Done&quot;", html);
        Assert.DoesNotContain("data-known-values=\"[&quot;Todo&quot;", html);
        Assert.Contains("id=\"configuration-archive-statuses\"", html);
        Assert.Contains("data-token-source", html);
        // One list: stored mappings as editable rows plus an add row, not a form per agent.
        Assert.Contains("id=\"mapping-add-form\"", html);
        Assert.Contains("edit it in place", html);

        // The add row's model control follows its agent selection. The fragment returns a picker
        // for the agent that answered and a free-text field for one that could not be asked —
        // never an empty dropdown that reads as "no models exist".
        using var codexChoices = AuthenticatedGet(
            host, $"{host.Origin}/?handler=MappingModelChoices&agent=codex");
        var picker = await (await client.SendAsync(codexChoices)).Content.ReadAsStringAsync();
        Assert.Contains("<select", picker);
        Assert.Contains("gpt-5.6-sol", picker);

        using var claudeChoices = AuthenticatedGet(
            host, $"{host.Origin}/?handler=MappingModelChoices&agent=claude");
        var freeText = await (await client.SendAsync(claudeChoices)).Content.ReadAsStringAsync();
        Assert.Contains("<input", freeText);
        Assert.DoesNotContain("<select", freeText);
        await host.Stop();
    }

    [Fact]
    public async Task A_repository_vocabulary_saved_from_the_console_reaches_the_file()
    {
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Settings");
        var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        var result = await PostForm(
            client,
            host,
            "Configuration",
            new Dictionary<string, string>
            {
                ["operation"] = "profiles",
                ["revision"] = HiddenValue(html, "revision"),
                ["executionProfiles"] = "Economy, deep , docs-only",
                ["defaultExecutionProfile"] = "deep"
            });

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var resultHtml = WebUtility.HtmlDecode(await result.Content.ReadAsStringAsync());
        Assert.Contains("<option value=\"docs-only\">docs-only</option>", resultHtml);
        var mappingAdd = resultHtml[resultHtml.IndexOf("id=\"mapping-add-form\"", StringComparison.Ordinal)..];
        mappingAdd = mappingAdd[..mappingAdd.IndexOf("</form>", StringComparison.Ordinal)];
        Assert.Contains("<option value=\"docs-only\">docs-only</option>", mappingAdd);
        var stored = await new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None);
        // Lower-cased and trimmed on the way in: the stored vocabulary is the canonical form, and
        // the GitHub board title-cases its own options separately.
        Assert.Equal(["economy", "deep", "docs-only"], stored.EffectiveWorker.EffectiveExecutionProfiles);
        Assert.Equal("deep", stored.EffectiveWorker.DefaultExecutionProfile);

        var removed = await PostForm(
            client,
            host,
            "Configuration",
            new Dictionary<string, string>
            {
                ["operation"] = "profiles",
                ["revision"] = HiddenValue(resultHtml, "revision"),
                ["executionProfiles"] = "economy, deep",
                ["defaultExecutionProfile"] = "economy"
            });
        var removedHtml = WebUtility.HtmlDecode(await removed.Content.ReadAsStringAsync());
        var removedMappingAdd = removedHtml[removedHtml.IndexOf("id=\"mapping-add-form\"", StringComparison.Ordinal)..];
        removedMappingAdd = removedMappingAdd[..removedMappingAdd.IndexOf("</form>", StringComparison.Ordinal)];

        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.DoesNotContain("value=\"docs-only\"", removedMappingAdd);
        stored = await new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None);
        Assert.Equal(["economy", "deep"], stored.EffectiveWorker.EffectiveExecutionProfiles);
        Assert.Equal("economy", stored.EffectiveWorker.DefaultExecutionProfile);
        await host.Stop();
    }

    private static async Task<(HttpResponseMessage Response, string Html)> SaveMappingAsync(
        HttpClient client, RunningServer host, string html, params (string Key, string Value)[] fields)
    {
        var form = new Dictionary<string, string>
        {
            ["revision"] = ValueOfInput(html, "user-configuration-revision")
        };
        foreach (var (key, value) in fields)
        {
            form[key] = value;
        }

        var response = await PostForm(client, host, "ProfileMapping", form);
        // Decoded, because these assertions are about what an operator reads. Razor encodes the
        // apostrophes in "does not accept effort 'medium'" and the arrow in "-> not set", so
        // matching the raw markup would pin the encoding rather than the message.
        return (response, WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
    }

    private async Task<(HttpClient Client, RunningServer Host, string Html)> SettingsAsync()
    {
        var host = await StartServer(openBrowser: false);
        var client = new HttpClient();
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Settings");
        return (client, host, await (await client.SendAsync(request)).Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_profile_mapping_saved_from_the_console_reaches_this_machines_settings()
    {
        var (client, host, html) = await SettingsAsync();

        var (response, saved) = await SaveMappingAsync(
            client, host, html,
            ("profile", "deep"), ("agent", "codex"),
            ("model", "gpt-5.6-sol"), ("effort", "ultra"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // Named per pair, not printed as two maps at the operator.
        Assert.Contains("workerProfiles.deep.codex", saved);
        Assert.Contains("gpt-5.6-sol / ultra", saved);
        // The page that comes back shows the mapping as a row with its stored values selected.
        // The first design had write-only forms and a save re-rendered as pristine dropdowns,
        // which a tester reasonably read as "all my settings were reset".
        Assert.Contains("mapping-row", saved);
        Assert.Matches("selected[^>]*>[^<]*gpt-5.6-sol", saved);
        client.Dispose();
        await host.Stop();
    }

    [Fact]
    public async Task The_console_refuses_an_effort_the_model_itself_rejects()
    {
        // The same refusal as the CLI, for the same reason: without it the pair reaches a launch
        // that fails at the API having already spent a request.
        var (client, host, html) = await SettingsAsync();

        var (response, refused) = await SaveMappingAsync(
            client, host, html,
            ("profile", "deep"), ("agent", "codex"),
            ("model", "gpt-5.6-sol"), ("effort", "medium"));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("does not accept effort 'medium'", refused);
        Assert.Contains("It accepts: low, high, ultra", refused);
        client.Dispose();
        await host.Stop();
    }

    [Fact]
    public async Task The_console_saves_a_model_the_agent_did_not_list_and_says_so()
    {
        // An account may be entitled to something the list read seconds ago did not show; the
        // vendor is the authority on that, not the snapshot.
        var (client, host, html) = await SettingsAsync();

        var (response, saved) = await SaveMappingAsync(
            client, host, html,
            ("profile", "deep"), ("agent", "codex"),
            ("model", "gpt-9-imaginary"), ("effort", "low"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("did not list a model", saved);
        client.Dispose();
        await host.Stop();
    }

    [Fact]
    public async Task The_console_saves_unchecked_when_the_agent_cannot_be_asked()
    {
        // Only codex answers in this harness. Losing discovery must cost a check, not the ability
        // to configure — the guarantee the whole design rests on.
        var (client, host, html) = await SettingsAsync();

        var (response, saved) = await SaveMappingAsync(
            client, host, html,
            ("profile", "deep"), ("agent", "claude"),
            ("model", "opus"), ("effort", "high"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("could not be asked", saved);
        client.Dispose();
        await host.Stop();
    }

    [Fact]
    public async Task The_console_refuses_an_effort_level_that_does_not_exist()
    {
        var (client, host, html) = await SettingsAsync();

        var (response, refused) = await SaveMappingAsync(
            client, host, html,
            ("profile", "deep"), ("agent", "codex"), ("effort", "maximum"));

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("is not a known effort level", refused);
        client.Dispose();
        await host.Stop();
    }

    [Fact]
    public async Task Clearing_both_values_from_the_console_removes_the_mapping()
    {
        var (client, host, html) = await SettingsAsync();
        var (_, afterSave) = await SaveMappingAsync(
            client, host, html,
            ("profile", "deep"), ("agent", "codex"), ("model", "gpt-5.6-sol"), ("effort", "low"));

        var (response, cleared) = await SaveMappingAsync(
            client, host, afterSave,
            ("profile", "deep"), ("agent", "codex"), ("model", ""), ("effort", ""));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("-> not set", cleared);
        client.Dispose();
        await host.Stop();
    }

    [Fact]
    public async Task A_machine_local_save_against_a_stale_revision_is_refused()
    {
        // The reason this scope needed a service rather than a direct store call: the settings file
        // is shared with every CLI on the machine, and a console page can sit open for hours.
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Settings");
        var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        var result = await PostForm(
            client,
            host,
            "UserConfiguration",
            new Dictionary<string, string>
            {
                ["operation"] = "hostLabel",
                ["revision"] = "a-revision-that-was-never-current",
                ["hostLabel"] = "workstation-alpha"
            });

        Assert.NotEqual(HttpStatusCode.OK, result.StatusCode);
        Assert.Contains(
            "changed since they were read",
            await result.Content.ReadAsStringAsync());
        await host.Stop();
    }

    [Fact]
    public async Task Settings_surface_groups_and_updates_high_value_repository_policies()
    {
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Settings");
        var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        Assert.Contains("class=\"workspace-mode-help\"", html);
        Assert.Contains("Current checkout", html);
        Assert.Contains("exclusive", html);
        Assert.Contains("Shared checkout", html);
        Assert.Contains("concurrent, unsafe", html);
        Assert.Contains("Isolated worktree + branch", html);
        Assert.Contains("Additional workers wait for it.", html);
        Assert.True(html.IndexOf("id=\"repository-worker-settings\"", StringComparison.Ordinal) <
                    html.IndexOf("id=\"repository-agent-settings\"", StringComparison.Ordinal));
        Assert.True(html.IndexOf("id=\"repository-agent-settings\"", StringComparison.Ordinal) <
                    html.IndexOf("id=\"repository-usage-recovery-settings\"", StringComparison.Ordinal));
        Assert.True(html.IndexOf("id=\"repository-usage-recovery-settings\"", StringComparison.Ordinal) <
                    html.IndexOf("id=\"repository-workflow-settings\"", StringComparison.Ordinal));
        Assert.DoesNotContain(
            "id=\"repository-usage-recovery-settings\" class=\"repository-setting-group\" open",
            html);
        Assert.DoesNotContain(
            "id=\"repository-workflow-settings\" class=\"repository-setting-group\" open",
            html);
        Assert.Contains("Claim handling", html);
        Assert.Contains("id=\"configuration-default-create-status\"", html);
        Assert.Contains("Separate from worker pickup", html);
        Assert.Contains("Claim expiry (minutes)", html);
        Assert.Contains("A claim is Wrighty’s temporary ownership lock on an item.", html);
        Assert.True(
            html.IndexOf("id=\"repository-workflow-settings\"", StringComparison.Ordinal) <
            html.IndexOf("id=\"configuration-lease-minutes\"", StringComparison.Ordinal));
        Assert.Contains("class=\"settings-field-grid settings-field-grid--fluid\"", html);
        Assert.Contains("class=\"settings-field-grid settings-field-grid--wide\"", html);
        Assert.Contains("class=\"settings-field-grid settings-field-grid--dense\"", html);
        Assert.Contains("class=\"token-picker-setting settings-field--wide\"", html);
        Assert.Contains("class=\"settings-field--dense\"", html);

        html = await SaveAsync(
            html,
            new Dictionary<string, string>
            {
                ["operation"] = "worker",
                ["workspaceMode"] = "worktree",
                ["useWorkerQueue"] = "false"
            });
        Assert.Contains("Configuration saved and applied to this web console.", html);
        Assert.DoesNotContain("configuration-restart-warning", html);
        Assert.DoesNotContain("Restart <code>wrighty web</code>", html);
        html = await SaveAsync(
            html,
            new Dictionary<string, string>
            {
                ["operation"] = "workflow",
                ["defaultCreateStatus"] = "Todo",
                ["defaultPickFrom"] = "Worker queue",
                ["defaultPickTo"] = "In Progress",
                ["defaultFinishTo"] = "Done",
                ["leaseMinutes"] = "90"
            });
        html = await SaveAsync(
            html,
            new Dictionary<string, string>
            {
                ["operation"] = "agent-policy",
                ["defaultAgent"] = "codex",
                ["requirementsAssessmentMode"] = "inline",
                ["agentPermissions"] = "workspace",
                ["claudePermissions"] = "full"
            });
        html = await SaveAsync(
            html,
            new Dictionary<string, string>
            {
                ["operation"] = "usage-failure",
                ["usageFailureAction"] = "handoff",
                ["usageFailureInitialRetryMinutes"] = "5",
                ["usageFailureBackoffMultiplier"] = "1.5",
                ["usageFailureMaxRetryHours"] = "3",
                ["usageFailureMaxAttempts"] = "2",
                ["usageFailureResetGraceMinutes"] = "0.5",
                ["usageFailureAllowCrossAgentHandoff"] = "true",
                ["usageFailureClaudeFallbacks"] = "codex, copilot",
                ["usageFailureCodexFallbacks"] = "copilot, claude",
                ["usageFailureCopilotFallbacks"] = "claude"
            });
        html = await SaveAsync(
            html,
            new Dictionary<string, string>
            {
                ["operation"] = "completion",
                ["completionCommit"] = "agent",
                ["completionIntegration"] = "merge-local",
                ["completionPolicy"] = "user-confirmed"
            });
        html = await SaveAsync(
            html,
            new Dictionary<string, string>
            {
                ["operation"] = "archive",
                ["archiveStatuses"] = "Done, Complete"
            });
        html = await SaveAsync(
            html,
            new Dictionary<string, string>
            {
                ["operation"] = "web",
                ["protectNonHumanClaims"] = "false"
            });

        var stored = await new TrackerConfigLoader().LoadAsync(
            directory,
            CancellationToken.None);
        Assert.Equal("codex", stored.EffectiveWorker.DefaultAgent);
        Assert.Equal("worktree", stored.EffectiveWorker.WorkspaceMode);
        Assert.False(stored.EffectiveWorker.UseWorkerQueue);
        Assert.Equal(90, stored.LeaseMinutes);
        Assert.Equal("inline", stored.EffectiveWorker.EffectiveRequirementsAssessment.EffectiveMode);
        Assert.Equal("full", stored.EffectiveWorker.Agents!["claude"].Permissions);
        var usageFailure = stored.EffectiveWorker.EffectiveUsageFailure;
        Assert.Equal("handoff", usageFailure.Action);
        Assert.Equal(5, usageFailure.InitialRetryMinutes);
        Assert.Equal(1.5, usageFailure.BackoffMultiplier);
        Assert.Equal(3, usageFailure.MaxRetryHours);
        Assert.Equal(2, usageFailure.MaxAttempts);
        Assert.Equal(0.5, usageFailure.ResetGraceMinutes);
        Assert.True(usageFailure.AllowCrossAgentHandoff);
        Assert.Equal(["codex", "copilot"], usageFailure.Fallbacks["claude"]);
        Assert.Equal(["copilot", "claude"], usageFailure.Fallbacks["codex"]);
        Assert.Equal(["claude"], usageFailure.Fallbacks["copilot"]);
        Assert.Equal("agent", stored.EffectiveWorker.Completion?.Commit);
        Assert.Equal("merge-local", stored.EffectiveWorker.Completion?.Integration);
        Assert.Equal("user-confirmed", stored.EffectiveWorker.Completion?.Policy);
        Assert.Equal(["Done", "Complete"], stored.Archive.OnStatuses);
        Assert.False(stored.EffectiveWeb.ProtectNonHumanClaims);
        Assert.Contains("<output id=\"configuration-save-notice\"", html);
        Assert.DoesNotContain("configuration-restart-warning", html);
        await host.Stop();

        async Task<string> SaveAsync(
            string currentHtml,
            Dictionary<string, string> values)
        {
            values["revision"] = HiddenValue(currentHtml, "revision");
            var response = await PostForm(client, host, "Configuration", values);
            var responseHtml = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(
                responseHtml.Contains("Configuration saved", StringComparison.Ordinal),
                responseHtml);
            return responseHtml;
        }
    }

    [Fact]
    public async Task Settings_surface_rejects_unknown_or_incomplete_configuration_updates()
    {
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Settings");
        var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();
        var revision = HiddenValue(html, "revision");

        var unknown = await PostForm(
            client,
            host,
            "Configuration",
            new Dictionary<string, string>
            {
                ["operation"] = "future-operation",
                ["revision"] = revision
            });
        var unknownHtml = await unknown.Content.ReadAsStringAsync();
        Assert.Contains("CONFIG_MUTATION_UNSUPPORTED", unknownHtml);

        var incomplete = await PostForm(
            client,
            host,
            "Configuration",
            new Dictionary<string, string>
            {
                ["operation"] = "workflow",
                ["revision"] = revision,
                ["defaultPickTo"] = "Doing",
                ["defaultFinishTo"] = "Complete"
            });
        var incompleteHtml = await incomplete.Content.ReadAsStringAsync();
        Assert.Contains("CONFIG_INVALID", incompleteHtml);
        Assert.Contains("value=\"Doing\"", incompleteHtml);
        Assert.Contains(
            "id=\"configuration-workflow-form\" method=\"post\" data-settings-dirty=\"true\"",
            incompleteHtml,
            StringComparison.Ordinal);
        Assert.Matches(
            "<button(?=[^>]*data-settings-save)(?![^>]*disabled)[^>]*>Save workflow</button>",
            incompleteHtml);
        await host.Stop();
    }

    [Fact]
    public async Task Configuration_update_reports_revision_conflict_without_overwriting()
    {
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Settings");
        var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();
        var staleRevision = HiddenValue(html, "revision");
        var path = Path.Combine(directory, TrackerConfigLoader.FileName);
        var store = new TrackerConfigLoader();
        var current = await store.LoadAsync(directory, CancellationToken.None);
        // "Ready" stands in for a concurrent manual edit; workflow defaults must name a status
        // from localMarkdown.statuses, so an arbitrary label would no longer save.
        await store.SaveAsync(
            path,
            current with { DefaultPickFrom = "Ready" },
            CancellationToken.None);

        var result = await PostForm(
            client,
            host,
            "Configuration",
            new Dictionary<string, string>
            {
                ["operation"] = "workflow",
                ["revision"] = staleRevision,
                ["defaultCreateStatus"] = "Todo",
                ["defaultPickFrom"] = "Web edit",
                ["defaultPickTo"] = "In Progress",
                ["defaultFinishTo"] = "Done"
            });
        var resultHtml = await result.Content.ReadAsStringAsync();

        Assert.Contains("CONFIG_CONFLICT", resultHtml);
        Assert.Contains("value=\"Web edit\"", resultHtml);
        Assert.Contains(
            "id=\"configuration-workflow-form\" method=\"post\" data-settings-dirty=\"true\"",
            resultHtml,
            StringComparison.Ordinal);
        Assert.Equal(
            "Ready",
            (await store.LoadAsync(directory, CancellationToken.None)).DefaultPickFrom);
        await host.Stop();
    }

    [Fact]
    public async Task Item_page_shows_the_agent_report_separately_from_what_wrighty_observed()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Item&id=local%3A1");
        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Agent report", html, StringComparison.Ordinal);
        Assert.Contains("not verified by Wrighty", html, StringComparison.Ordinal);
        Assert.Contains("Wired the setting through.", html, StringComparison.Ordinal);
        Assert.Contains("Checks the agent says it ran", html, StringComparison.Ordinal);
        Assert.Contains("Per item or per worker?", html, StringComparison.Ordinal);

        // The final message renders without its report block: showing both would put the same
        // account on the page twice, once as prose containing raw JSON.
        Assert.Contains("Paused for a decision.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("wrighty-report", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Board_and_item_explain_provider_block_and_refresh_when_circuit_closes()
    {
        var host = await StartServer(providerUnavailable: true);
        using var client = new HttpClient();
        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        using var board = await client.SendAsync(boardRequest);
        var boardHtml = await board.Content.ReadAsStringAsync();

        Assert.Contains("Codex unavailable", boardHtml);
        Assert.Contains("provider-blocked", boardHtml);
        Assert.DoesNotContain("provider-capacity-menu", boardHtml);
        Assert.DoesNotContain(
            "Provider blocked ready item</span>\n  <span class=\"activity-badge\">Ready for worker",
            boardHtml);

        using var providerRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=ProviderCapacity");
        using var provider = await client.SendAsync(providerRequest);
        var providerHtml = await provider.Content.ReadAsStringAsync();
        Assert.Contains("1 unavailable", providerHtml);
        Assert.Contains("Automatic work is paused", providerHtml);
        Assert.Contains("Synthetic Codex capacity failure.", providerHtml);
        Assert.Contains("Probe Codex</button>", providerHtml);

        using var itemRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Item&id=local%3A9");
        using var item = await client.SendAsync(itemRequest);
        var itemHtml = await item.Content.ReadAsStringAsync();
        Assert.Contains("Codex capacity unavailable", itemHtml);
        Assert.Contains("otherwise-ready item unclaimed", itemHtml);
        Assert.Contains("wrighty worker --item local:9 --yes", itemHtml);
        Assert.Contains("Probe Codex now", itemHtml);

        var previousEtag = board.Headers.ETag;
        await ProviderStore().RecordAvailableAsync(
            "codex",
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        using var refreshedRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Board");
        refreshedRequest.Headers.IfNoneMatch.Add(previousEtag!);
        using var refreshed = await client.SendAsync(refreshedRequest);
        var refreshedHtml = await refreshed.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        Assert.DoesNotContain("provider-blocked", refreshedHtml);
        Assert.Contains("Ready for worker", refreshedHtml);

        using var refreshedProviderRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=ProviderCapacity");
        refreshedProviderRequest.Headers.IfNoneMatch.Add(provider.Headers.ETag!);
        using var refreshedProvider = await client.SendAsync(refreshedProviderRequest);
        var refreshedProviderHtml = await refreshedProvider.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, refreshedProvider.StatusCode);
        Assert.Contains("Available", refreshedProviderHtml);
        Assert.DoesNotContain("1 unavailable", refreshedProviderHtml);
        await host.Stop();
    }

    [Fact]
    public async Task Board_distinguishes_an_active_provider_probe_lease()
    {
        var host = await StartServer(providerProbeInProgress: true);
        using var client = new HttpClient();
        using var providerRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=ProviderCapacity");
        using var provider = await client.SendAsync(providerRequest);
        var html = await provider.Content.ReadAsStringAsync();

        Assert.Contains("1 probing", html);
        Assert.Contains("A single capacity probe is in progress until", html);
        Assert.Contains(
            "<button type=\"button\" class=\"button-compact\" disabled>Probe in progress</button>",
            html);
        Assert.DoesNotContain("Probe Codex</button>", html);
        Assert.Contains("Probe Claude</button>", html);
        Assert.Contains("Probe Copilot</button>", html);
        Assert.Contains(
            "title=\"Wait for the active capacity probe to finish.\"",
            html);
        Assert.Contains(">Probe all</button>", html);
        Assert.DoesNotContain("handler=ProbeAllProviders", html);

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        using var board = await client.SendAsync(boardRequest);
        Assert.Contains("Codex unavailable", await board.Content.ReadAsStringAsync());

        using var itemRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Item&id=local%3A9");
        using var item = await client.SendAsync(itemRequest);
        var itemHtml = await item.Content.ReadAsStringAsync();
        Assert.Contains(
            "Another worker is already performing the single capacity probe",
            itemHtml);
        Assert.Contains(
            "<button type=\"button\" disabled>Probe in progress</button>",
            itemHtml);
        Assert.DoesNotContain("Probe Codex now", itemHtml);
        Assert.DoesNotContain("handler=ProbeProvider", itemHtml);
        await host.Stop();
    }

    [Fact]
    public async Task Header_can_probe_provider_without_an_existing_circuit()
    {
        var host = await StartServer(providerProbeSucceeds: true);
        using var client = new HttpClient();
        using var providerRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=ProviderCapacity");
        using var provider = await client.SendAsync(providerRequest);
        var providerHtml = await provider.Content.ReadAsStringAsync();

        Assert.Contains("Agent capacity", providerHtml);
        Assert.Contains("Available", providerHtml);
        Assert.Contains("Probe Claude</button>", providerHtml);
        Assert.Contains("Probe Codex</button>", providerHtml);
        Assert.Contains("Probe Copilot</button>", providerHtml);
        Assert.Contains("handler=ProbeAllProviders", providerHtml);
        Assert.Contains("Probe all</button>", providerHtml);
        Assert.DoesNotContain("1 unavailable", providerHtml);

        using var response = await PostForm(client, host, "ProbeProvider", new()
        {
            ["agent"] = "codex",
            ["surface"] = "header"
        });
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "Codex capacity is available. Automatic Codex work is enabled.",
            html);
        Assert.Contains("Probe Codex</button>", html);
        Assert.Contains("Available", html);
        Assert.DoesNotContain("1 unavailable", html);
        await host.Stop();
    }

    [Fact]
    public async Task Header_can_probe_all_providers_concurrently()
    {
        var host = await StartServer(providerProbeSucceeds: true);
        using var client = new HttpClient();

        using var response = await PostForm(client, host, "ProbeAllProviders", []);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "Checked 3 agents: 3 available, 0 unavailable.",
            html);
        Assert.Contains("Probe Claude</button>", html);
        Assert.Contains("Probe Codex</button>", html);
        Assert.Contains("Probe Copilot</button>", html);
        Assert.Contains("Probe all</button>", html);
        Assert.Equal("wrighty:refresh", response.Headers.GetValues("HX-Trigger").Single());
        await host.Stop();
    }

    [Fact]
    public async Task Simulated_missing_agent_is_removed_from_provider_probes_without_restart()
    {
        var store = new TrackerConfigLoader();
        var host = await StartServer(providerProbeSucceeds: true);
        var path = Path.Combine(directory, TrackerConfigLoader.FileName);
        var config = await store.TryLoadPathAsync(path, CancellationToken.None);
        await store.SaveAsync(
            path,
            config! with
            {
                Testing = new TestingConfig { NotInstalledAgents = ["copilot"] }
            },
            CancellationToken.None);
        using var client = new HttpClient();

        using var providerRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=ProviderCapacity");
        using var provider = await client.SendAsync(providerRequest);
        var providerHtml = await provider.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, provider.StatusCode);
        Assert.Contains("Probe Claude</button>", providerHtml);
        Assert.Contains("Probe Codex</button>", providerHtml);
        Assert.DoesNotContain("Probe Copilot</button>", providerHtml);

        using var all = await PostForm(client, host, "ProbeAllProviders", []);
        var allHtml = await all.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        Assert.Contains("Checked 2 agents: 2 available, 0 unavailable.", allHtml);
        Assert.DoesNotContain("Probe Copilot</button>", allHtml);

        using var stale = await PostForm(client, host, "ProbeProvider", new()
        {
            ["agent"] = "copilot",
            ["surface"] = "header"
        });
        var staleHtml = await stale.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.Contains("AGENT_NOT_INSTALLED", staleHtml);
        Assert.DoesNotContain("Probe Copilot</button>", staleHtml);
        await host.Stop();
    }

    [Fact]
    public async Task Item_provider_probe_closes_circuit_without_claiming_item()
    {
        var host = await StartServer(
            providerUnavailable: true,
            providerProbeSucceeds: true);
        using var client = new HttpClient();

        using var response = await PostForm(client, host, "ProbeProvider", new()
        {
            ["agent"] = "codex",
            ["id"] = "local:9"
        });
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(
            "Codex capacity is available. Automatic Codex work is enabled.",
            html);
        Assert.DoesNotContain("Codex capacity unavailable", html);
        Assert.Contains("<dt>State</dt><dd>Unclaimed</dd>", html);
        Assert.Equal("wrighty:refresh", response.Headers.GetValues("HX-Trigger").Single());

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        using var board = await client.SendAsync(boardRequest);
        var boardHtml = await board.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Provider capacity unavailable", boardHtml);
        Assert.DoesNotContain("provider-blocked", boardHtml);
        Assert.Contains("Ready for worker", boardHtml);
        await host.Stop();
    }

    [Fact]
    public async Task Rendered_markdown_disables_raw_html_remote_images_and_htmx_attributes()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{host.Origin}/?handler=Item&id=local%3A1");
        request.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("hx-disable", html);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<div hx-get=\"https://evil", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=\"javascript:", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("<details class=\"custom-fields\">", html);
        Assert.DoesNotContain("<details class=\"custom-fields\" open", html);
        Assert.Contains("<summary>Custom fields (2)</summary>", html);
        Assert.Contains("<dt>unsafe</dt>", html);
        Assert.Contains("<dd class=\"custom-field-value\">", html);
        Assert.Contains("<code id=\"custom-field-value-1\">&lt;script&gt;&amp;</code>", html);
        Assert.Contains("data-copy-target=\"custom-field-value-1\"", html);
        Assert.Contains("data-copy-name=\"unsafe custom field\"", html);
        Assert.Contains("<dt>testNode</dt>", html);
        Assert.Contains("&quot;nodefield1&quot;: &quot;a long hierarchical value", html);
        Assert.Contains("&quot;nodefield2&quot;: 42", html);
        Assert.Contains("<summary>Frontmatter</summary>", html);
        Assert.Contains("class=\"language-yaml\"", html);
        Assert.Contains("unsafe: &quot;&lt;script&gt;&amp;&quot;", html);
        Assert.DoesNotContain("unsafe: <script>", html);
        Assert.Contains("<dt>Claimant type</dt><dd>Agent</dd>", html);
        Assert.Contains("<dt>Agent</dt><dd>Codex</dd>", html);
        Assert.Contains("Codex has paused and its headless process has exited.", html);
        Assert.Contains(">Queue for worker</button>", html);
        Assert.DoesNotContain("Takeover does not stop that process", html);
        Assert.Contains("<div class=\"metadata-technical\" data-copy-scope>", html);
        Assert.Contains("<code id=\"claimant-id-value\" class=\"inspectable-value-text\">agent:web-test-session</code>", html);
        Assert.Contains("data-expand-target=\"claimant-id-value\"", html);
        Assert.Contains("data-copy-target=\"claimant-id-value\"", html);
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());

        await host.Stop();
    }

    [Fact]
    public async Task Mutation_requires_exact_origin_and_valid_form_content_type()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{host.Origin}/?handler=Claim");
        request.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        request.Headers.Add("Origin", "http://evil.example");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = "local:1" });
        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await host.Stop();
    }

    [Fact]
    public async Task Mutation_accepts_the_localhost_origin()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        var port = new Uri(host.Origin).Port;
        var authority = $"localhost:{port}";
        var origin = $"http://{authority}";

        using var itemRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Item&id=local%3A3");
        itemRequest.Headers.Host = authority;
        using var itemResponse = await client.SendAsync(itemRequest);
        var antiforgeryToken = HiddenValue(
            await itemResponse.Content.ReadAsStringAsync(),
            "__RequestVerificationToken");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{host.Origin}/?handler=Claim");
        request.Headers.Host = authority;
        request.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        request.Headers.Add("Origin", origin);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = "local:3",
            ["__RequestVerificationToken"] = antiforgeryToken
        });
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await host.Stop();
    }

    [Fact]
    public async Task Mutation_rejects_a_missing_origin()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{host.Origin}/?handler=Claim");
        request.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = "local:3"
        });
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("ORIGIN_INVALID", await ProblemTitle(response));
        await host.Stop();
    }

    [Fact]
    public async Task Create_form_defaults_safely_and_reuses_attempt_on_duplicate_submission()
    {
        var host = await StartServer(
            pickFrom: "Worker queue");
        using var client = new HttpClient();
        using var formRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Create");
        using var formResponse = await client.SendAsync(formRequest);
        var form = await formResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, formResponse.StatusCode);
        Assert.Contains("NEW ITEM", form);
        Assert.Contains("value=\"Todo\" selected", form);
        Assert.DoesNotContain("value=\"In Progress\"", form);
        Assert.DoesNotContain("value=\"Done\"", form);
        Assert.DoesNotContain(
            "type=\"checkbox\" name=\"automaticExecutionAllowed\"",
            form);
        Assert.Contains("Controlled by status.", form);
        Assert.Contains("Worker queue", form);
        Assert.Contains(
            "The agent policy only selects an agent once automatic execution is authorized.",
            form);
        var attempt = HiddenValue(form, "creationAttemptId");
        var before = Directory.GetFiles(
            Path.Combine(directory, ".wrighty", "items"),
            "*.md").Length;

        var values = new Dictionary<string, string>
        {
            ["title"] = "Created from web",
            ["body"] = "Web body",
            ["status"] = "Todo",
            ["priority"] = "P2",
            ["agentPolicy"] = "codex",
            ["creationAttemptId"] = attempt
        };
        using var first = await PostForm(client, host, "Create", new(values));
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(
            "wrighty:refresh, wrighty:close-panel",
            Assert.Single(first.Headers.GetValues("HX-Trigger")));

        using var second = await PostForm(client, host, "Create", new(values));
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
        Assert.Equal(
            "wrighty:refresh, wrighty:close-panel",
            Assert.Single(second.Headers.GetValues("HX-Trigger")));
        Assert.Equal(
            before + 1,
            Directory.GetFiles(
                Path.Combine(directory, ".wrighty", "items"),
                "*.md").Length);

        await host.Stop();
    }

    [Theory]
    [InlineData("In Progress")]
    [InlineData("Done")]
    public async Task Create_rejects_a_forged_non_entry_status(string status)
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var formRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Create");
        using var formResponse = await client.SendAsync(formRequest);
        var form = await formResponse.Content.ReadAsStringAsync();
        var before = Directory.GetFiles(
            Path.Combine(directory, ".wrighty", "items"),
            "*.md").Length;

        using var response = await PostForm(client, host, "Create", new()
        {
            ["title"] = "Invalid lifecycle shortcut",
            ["status"] = status,
            ["creationAttemptId"] = HiddenValue(form, "creationAttemptId")
        });
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ARGUMENT_INVALID", html);
        Assert.Contains("cannot be used to create a work item", html);
        Assert.Equal(
            before,
            Directory.GetFiles(Path.Combine(directory, ".wrighty", "items"), "*.md").Length);
        await host.Stop();
    }

    [Fact]
    public async Task Create_in_the_worker_queue_derives_automatic_execution_from_status()
    {
        var host = await StartServer(pickFrom: "Worker queue");
        using var client = new HttpClient();
        using var formRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Create");
        using var formResponse = await client.SendAsync(formRequest);
        var form = await formResponse.Content.ReadAsStringAsync();

        using var response = await PostForm(client, host, "Create", new()
        {
            ["title"] = "Queued from web",
            ["body"] = "Ready for a worker",
            ["status"] = "Worker queue",
            ["creationAttemptId"] = HiddenValue(form, "creationAttemptId")
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var itemPath = Assert.Single(Directory.GetFiles(
            Path.Combine(directory, ".wrighty", "items"),
            "*queued-from-web.md"));
        Assert.Contains("execution: automatic", await File.ReadAllTextAsync(itemPath));
        await host.Stop();
    }

    [Fact]
    public async Task Create_accepts_an_empty_markdown_body()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var formRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Create");
        var form = await (await client.SendAsync(formRequest)).Content.ReadAsStringAsync();
        var itemsDirectory = Path.Combine(directory, ".wrighty", "items");
        var before = Directory.GetFiles(itemsDirectory, "*.md").Length;

        using var response = await PostForm(client, host, "Create", new Dictionary<string, string>
        {
            ["title"] = "Created without a body",
            ["body"] = string.Empty,
            ["status"] = "Todo",
            ["creationAttemptId"] = HiddenValue(form, "creationAttemptId")
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            "wrighty:refresh, wrighty:close-panel",
            Assert.Single(response.Headers.GetValues("HX-Trigger")));
        Assert.Equal(before + 1, Directory.GetFiles(itemsDirectory, "*.md").Length);
        var newId = await StoredItemId("Created without a body");
        var (config, backend, id) = await StoredBackend(newId);
        Assert.Equal(
            string.Empty,
            (await backend.GetAsync(config, id, CancellationToken.None))!.Body);
        await host.Stop();
    }

    [Fact]
    public async Task Board_queue_button_moves_a_backlog_item_into_the_worker_queue()
    {
        // The board's one-click queue action bundles claim, status move, and release; with the
        // worker queue on (the default) the move is also the execution authorization.
        var host = await StartServer(openBrowser: false, pickFrom: "Worker queue");
        using var client = new HttpClient();
        using var formRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Create");
        var form = await (await client.SendAsync(formRequest)).Content.ReadAsStringAsync();
        using var created = await PostForm(client, host, "Create", new Dictionary<string, string>
        {
            ["title"] = "Queue me",
            ["body"] = "Body",
            ["status"] = "Todo",
            ["priority"] = "P2",
            ["creationAttemptId"] = HiddenValue(form, "creationAttemptId")
        });
        Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);
        var newId = await StoredItemId("Queue me");

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        var queueActionsBefore = board.Split("handler=QueueItem").Length - 1;
        Assert.True(queueActionsBefore > 0, "the backlog card must offer the queue action");

        using var queued = await PostForm(client, host, "QueueItem", new Dictionary<string, string>
        {
            ["id"] = newId
        });

        // The action is fire-and-forget: nothing to render, just a board refresh trigger — the
        // card moving into the queue column is the feedback.
        Assert.Equal(HttpStatusCode.NoContent, queued.StatusCode);
        Assert.Equal("wrighty:refresh", Assert.Single(queued.Headers.GetValues("HX-Trigger")));
        using var detailRequest = AuthenticatedGet(
            host, $"{host.Origin}/?handler=Item&id={Uri.EscapeDataString(newId)}");
        var detail = await (await client.SendAsync(detailRequest)).Content.ReadAsStringAsync();
        Assert.Contains("Worker queue", detail);
        Assert.Contains("Allowed", detail);
        Assert.Contains("Unclaimed", detail);

        // The queued item left the backlog, so exactly its queue action disappeared.
        using var afterRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var after = await (await client.SendAsync(afterRequest)).Content.ReadAsStringAsync();
        Assert.Equal(queueActionsBefore - 1, after.Split("handler=QueueItem").Length - 1);
        await host.Stop();
    }

    [Fact]
    public async Task Dropping_a_card_on_the_queue_column_moves_and_authorizes_it()
    {
        // Drag is the general gesture the buttons specialise: the same bundled move, so the
        // worker-queue rule must ride along exactly as it does for the Queue button.
        var host = await StartServer(openBrowser: false, pickFrom: "Worker queue");
        using var client = new HttpClient();
        using var formRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Create");
        var form = await (await client.SendAsync(formRequest)).Content.ReadAsStringAsync();
        using var created = await PostForm(client, host, "Create", new Dictionary<string, string>
        {
            ["title"] = "Drag me",
            ["body"] = "Body",
            ["status"] = "Todo",
            ["priority"] = "P2",
            ["creationAttemptId"] = HiddenValue(form, "creationAttemptId")
        });
        var newId = await StoredItemId("Drag me");

        using var moved = await PostForm(client, host, "MoveItem", new Dictionary<string, string>
        {
            ["id"] = newId,
            ["status"] = "Worker queue"
        });

        Assert.Equal(HttpStatusCode.NoContent, moved.StatusCode);
        Assert.Equal("wrighty:refresh", Assert.Single(moved.Headers.GetValues("HX-Trigger")));
        using var detailRequest = AuthenticatedGet(
            host, $"{host.Origin}/?handler=Item&id={Uri.EscapeDataString(newId)}");
        var detail = await (await client.SendAsync(detailRequest)).Content.ReadAsStringAsync();
        Assert.Contains("Worker queue", detail);
        Assert.Contains("Allowed", detail);
        Assert.Contains("Unclaimed", detail);

        // Dragging back out revokes it again, the same as the send-back button.
        using var back = await PostForm(client, host, "MoveItem", new Dictionary<string, string>
        {
            ["id"] = newId,
            ["status"] = "Todo"
        });
        Assert.Equal(HttpStatusCode.NoContent, back.StatusCode);
        using var afterRequest = AuthenticatedGet(
            host, $"{host.Origin}/?handler=Item&id={Uri.EscapeDataString(newId)}");
        var after = await (await client.SendAsync(afterRequest)).Content.ReadAsStringAsync();
        Assert.Contains("Manual only", after);
        await host.Stop();
    }

    [Fact]
    public async Task Dragging_into_the_in_progress_column_is_refused()
    {
        // That column is where the worker moves an item when it claims one. A manual move puts
        // the item where the worker does not look while the board claims work is happening.
        var host = await StartServer(openBrowser: false, pickFrom: "Worker queue");
        using var client = new HttpClient();
        using var formRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Create");
        var form = await (await client.SendAsync(formRequest)).Content.ReadAsStringAsync();
        using var created = await PostForm(client, host, "Create", new Dictionary<string, string>
        {
            ["title"] = "Not by hand",
            ["body"] = "Body",
            ["status"] = "Todo",
            ["priority"] = "P2",
            ["creationAttemptId"] = HiddenValue(form, "creationAttemptId")
        });
        var newId = await StoredItemId("Not by hand");

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        var card = CardMarkup(board, newId);
        Assert.Contains("draggable=\"true\"", card);
        Assert.Contains("Worker queue", card);
        Assert.DoesNotContain("In Progress", card);

        using var refused = await PostForm(client, host, "MoveItem", new Dictionary<string, string>
        {
            ["id"] = newId,
            ["status"] = "In Progress"
        });

        // The browser is not the authority: the rule holds even when the post is made directly.
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("queue the item instead", await refused.Content.ReadAsStringAsync());
        using var detailRequest = AuthenticatedGet(
            host, $"{host.Origin}/?handler=Item&id={Uri.EscapeDataString(newId)}");
        var detail = await (await client.SendAsync(detailRequest)).Content.ReadAsStringAsync();
        Assert.Contains("Todo", detail);
        await host.Stop();
    }

    [Fact]
    public async Task Finished_cards_offer_a_confirmed_archive()
    {
        // Filing a finished item away is the last routine gesture, and the one card action that
        // is destructive-adjacent — so it keeps the confirmation the panel has always shown.
        var host = await StartServer(openBrowser: false, pickFrom: "Worker queue");
        using var client = new HttpClient();
        using var formRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Create");
        var form = await (await client.SendAsync(formRequest)).Content.ReadAsStringAsync();
        using var created = await PostForm(client, host, "Create", new Dictionary<string, string>
        {
            ["title"] = "All done",
            ["body"] = "Body",
            ["status"] = "Todo",
            ["priority"] = "P2",
            ["creationAttemptId"] = HiddenValue(form, "creationAttemptId")
        });
        var newId = await StoredItemId("All done");
        using var claim = await PostForm(client, host, "Claim", new()
        {
            ["id"] = newId
        });
        var edit = await claim.Content.ReadAsStringAsync();
        using var finished = await PostForm(client, host, "Save", new()
        {
            ["id"] = newId,
            ["expectedRevision"] = HiddenValue(edit, "expectedRevision"),
            ["expectedClaimGeneration"] = HiddenValue(edit, "expectedClaimGeneration"),
            ["title"] = "All done",
            ["body"] = "Body",
            ["status"] = "Todo",
            ["priority"] = "P2",
            ["action"] = "finish"
        });
        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        var card = CardMarkup(board, newId);
        Assert.Contains("handler=ArchiveItem", card);
        Assert.Contains("data-confirm-title=\"Archive this item?\"", card);

        using var archived = await PostForm(client, host, "ArchiveItem", new Dictionary<string, string>
        {
            ["id"] = newId
        });

        Assert.Equal(HttpStatusCode.NoContent, archived.StatusCode);
        Assert.Equal("wrighty:refresh", Assert.Single(archived.Headers.GetValues("HX-Trigger")));
        // The card leaving the active board is the feedback; the item is still there to restore.
        using var afterRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var after = await (await client.SendAsync(afterRequest)).Content.ReadAsStringAsync();
        Assert.DoesNotContain($"data-drag-item=\"{newId}\"", after);
        using var archivedViewRequest = AuthenticatedGet(
            host, $"{host.Origin}/?handler=Board&scope=archived");
        var archivedView = await (await client.SendAsync(archivedViewRequest)).Content
            .ReadAsStringAsync();
        Assert.Contains("All done", archivedView);
        await host.Stop();
    }

    [Fact]
    public async Task Dragging_a_paused_item_back_to_the_backlog_is_refused()
    {
        // Queueing a paused session requires the in-progress status, so moving it to a backlog or
        // queue column would leave it looking available while its resume path was broken.
        var host = await StartServer(openBrowser: false, releaseSeededClaim: true);
        using var client = new HttpClient();

        using var refused = await PostForm(client, host, "MoveItem", new Dictionary<string, string>
        {
            ["id"] = "local:1",
            ["status"] = "Todo"
        });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("recorded agent session", await refused.Content.ReadAsStringAsync());
        await host.Stop();
    }

    [Fact]
    public async Task Dragging_a_paused_item_to_done_finishes_it()
    {
        // The operator judging that the agent did enough is a legitimate one-gesture decision:
        // finishing is not stranding the session, it is the work ending.
        var host = await StartServer(openBrowser: false, releaseSeededClaim: true);
        using var client = new HttpClient();
        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        var card = CardMarkup(board, "local:1");
        Assert.Contains("draggable=\"true\"", card);
        // Only the finish column is offered — not the backlog or the queue.
        Assert.Contains("data-drag-targets=\"Done\"", card);

        using var finished = await PostForm(client, host, "MoveItem", new Dictionary<string, string>
        {
            ["id"] = "local:1",
            ["status"] = "Done"
        });

        Assert.Equal(HttpStatusCode.NoContent, finished.StatusCode);
        using var detailRequest = AuthenticatedGet(
            host, $"{host.Origin}/?handler=Item&id=local:1");
        var detail = await (await client.SendAsync(detailRequest)).Content.ReadAsStringAsync();
        Assert.Contains("Done", detail);
        // Finishing ends the waiting-for-a-person state; nothing is left queued for a worker.
        Assert.DoesNotContain("Needs attention", detail);
        await host.Stop();
    }

    [Fact]
    public async Task Dropping_a_card_on_an_unconfigured_status_is_refused()
    {
        // The browser supplies the target column, so it is validated rather than trusted: an
        // unconfigured status would strand the item outside every column on the board.
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();

        using var refused = await PostForm(client, host, "MoveItem", new Dictionary<string, string>
        {
            ["id"] = "local:1",
            ["status"] = "Somewhere else"
        });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var body = await refused.Content.ReadAsStringAsync();
        Assert.Contains("not a configured status", body);
        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Somewhere else", board);
        await host.Stop();
    }

    [Fact]
    public async Task Board_marks_cards_as_drag_sources_and_columns_as_drop_targets()
    {
        var host = await StartServer(openBrowser: false, pickFrom: "Worker queue");
        using var client = new HttpClient();

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();

        // The whole column is the drop zone, not its card list: an empty column's list has no
        // height, so a board could never receive its first card by drag.
        Assert.Contains("<section class=\"column\" data-drop-status=\"Worker queue\"", board);
        Assert.Contains("data-drag-item=\"local:1\"", board);
        // The seeded item holds a recorded session, so it is not the operator's to drag: moving
        // it out of the in-progress status would silently make it unresumable.
        Assert.DoesNotContain("draggable", CardMarkup(board, "local:1"));
        // The drag post needs a token of its own: it is issued by script, not by a card form.
        Assert.Contains("id=\"board-drag-token\"", board);
        await host.Stop();
    }

    /// <summary>One card's markup, so an assertion about its actions cannot be satisfied — or
    /// broken — by another item's card on the same board.</summary>
    private static string CardMarkup(string board, string id)
    {
        var start = board.IndexOf($"data-item-id=\"{id}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"the board must contain a card for '{id}'");
        // Matched without the closing bracket: the wrapper carries drag attributes, and a
        // literal that included the bracket silently stopped matching when they were added,
        // making every "card" slice run to the end of the board.
        var wrapStart = board.LastIndexOf("<div class=\"card-wrap\"", start, StringComparison.Ordinal);
        var next = board.IndexOf("<div class=\"card-wrap\"", start, StringComparison.Ordinal);
        var from = wrapStart >= 0 ? wrapStart : start;
        return next > from ? board[from..next] : board[from..];
    }

    /// <summary>One Operations row, isolating its actions from the other operational items.</summary>
    private static string OperationsRowMarkup(string operations, string id)
    {
        var start = operations.IndexOf($"<tr data-item-id=\"{id}\">", StringComparison.Ordinal);
        Assert.True(start >= 0, $"Operations must contain a row for '{id}'.");
        var end = operations.IndexOf("</tr>", start, StringComparison.Ordinal);
        Assert.True(end > start, $"The Operations row for '{id}' must be complete.");
        return operations[start..(end + "</tr>".Length)];
    }

    [Fact]
    public async Task A_backlog_card_offers_editing_beside_queueing()
    {
        // A backlog item's two next moves: hand it to a worker, or say more about it first.
        // Editing took opening the panel and then claiming; the panel's claim already lands on the
        // edit form, so the ceremony was only in getting there.
        var host = await StartServer(openBrowser: false, pickFrom: "Worker queue");
        using var client = new HttpClient();
        using var formRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Create");
        var form = await (await client.SendAsync(formRequest)).Content.ReadAsStringAsync();
        using var created = await PostForm(client, host, "Create", new Dictionary<string, string>
        {
            ["title"] = "Say more about me",
            ["body"] = "Body",
            ["status"] = "Todo",
            ["priority"] = "P2",
            ["creationAttemptId"] = HiddenValue(form, "creationAttemptId")
        });
        var newId = await StoredItemId("Say more about me");

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        var card = CardMarkup(board, newId);
        Assert.Contains("handler=QueueItem", card);
        Assert.Contains("handler=Claim", card);
        // Queue stays the primary move; editing is the considered one.
        Assert.Contains("card-action-primary", card);

        // fromCard is what the card's Edit action posts; it is what makes the edit a bounded
        // gesture rather than an ordinary panel claim.
        using var edit = await PostForm(
            client, host, "Claim",
            new Dictionary<string, string> { ["id"] = newId, ["fromCard"] = "true" });
        var html = await edit.Content.ReadAsStringAsync();

        // Lands on the edit form itself, not the detail panel — one gesture, not one and a half.
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Contains("name=\"body\"", html);
        Assert.Contains("expectedClaimGeneration", html);
        // Opened as a card gesture, the form offers only the two ways out that release, and both
        // say so. A plain Save would keep the claim and take the card's own Edit action away.
        Assert.Contains("value=\"save-release\"", html);
        Assert.DoesNotContain("value=\"save\"", html);
        Assert.DoesNotContain("value=\"finish\"", html);
        Assert.Contains("name=\"fromCard\"", html);
        // Claiming to edit is not queueing: the worker-queue rule keys off status moves, so
        // automatic execution must be exactly as it was. Read back from the store, because the
        // edit form does not render it and a claim that flipped it would look identical here.
        var (config, backend, id) = await StoredBackend(newId);
        var stored = await backend.GetOperationalAsync(config, id, CancellationToken.None);
        Assert.False(stored!.Item.AutomaticExecutionAllowed);
        Assert.Equal("Todo", stored.Item.Status);
        await host.Stop();
    }

    [Fact]
    public async Task A_claimed_or_dispatch_pending_card_does_not_offer_editing()
    {
        // The action belongs to the untouched backlog state only. Once someone holds the item, or
        // a worker decision is pending on it, claiming it out from under that is not a one-gesture
        // decision.
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();

        // local:2 is claimed by another installation; local:1 carries a dispatch state.
        Assert.DoesNotContain("handler=QueueItem", CardMarkup(board, "local:2"));
        var needsAttention = CardMarkup(board, "local:1");
        Assert.DoesNotContain("handler=QueueItem", needsAttention);
        await host.Stop();
    }

    [Fact]
    public async Task Saving_a_card_opened_edit_releases_and_returns_to_the_board()
    {
        // The trap this closes: an edit opened from a card used to end in the item viewer with the
        // claim still held, so the operator had a panel to dismiss and the card had lost the Edit
        // action that got them there.
        var host = await StartServer(openBrowser: false, pickFrom: "Worker queue");
        using var client = new HttpClient();
        using var formRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Create");
        var form = await (await client.SendAsync(formRequest)).Content.ReadAsStringAsync();
        using var created = await PostForm(client, host, "Create", new Dictionary<string, string>
        {
            ["title"] = "Edit and return",
            ["body"] = "Before",
            ["status"] = "Todo",
            ["priority"] = "P2",
            ["creationAttemptId"] = HiddenValue(form, "creationAttemptId")
        });
        var newId = await StoredItemId("Edit and return");
        using var opened = await PostForm(client, host, "Claim", new Dictionary<string, string>
        {
            ["id"] = newId,
            ["fromCard"] = "true"
        });
        var editHtml = await opened.Content.ReadAsStringAsync();

        using var saved = await PostForm(client, host, "Save", new Dictionary<string, string>
        {
            ["id"] = newId,
            ["expectedRevision"] = HiddenValue(editHtml, "expectedRevision"),
            ["expectedClaimGeneration"] = HiddenValue(editHtml, "expectedClaimGeneration"),
            ["title"] = "Edit and return",
            ["body"] = "After",
            ["status"] = "Todo",
            ["priority"] = "P2",
            ["action"] = "save-release",
            ["fromCard"] = "true"
        });

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        var triggers = Assert.Single(saved.Headers.GetValues("HX-Trigger"));
        Assert.Contains("wrighty:refresh", triggers);
        Assert.Contains("wrighty:close-panel", triggers);
        var (config, backend, id) = await StoredBackend(newId);
        var stored = await backend.GetOperationalAsync(config, id, CancellationToken.None);
        // Saved, and given back — so the card offers Edit again.
        Assert.Equal("After", (await backend.GetAsync(config, id, CancellationToken.None))!.Body);
        Assert.Equal(ClaimOwnershipState.Unclaimed, stored!.Claim.State);
        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        Assert.Contains("handler=Claim", CardMarkup(board, newId));
        await host.Stop();
    }

    [Fact]
    public async Task Cancelling_a_card_opened_edit_releases_without_saving()
    {
        var host = await StartServer(openBrowser: false, pickFrom: "Worker queue");
        using var client = new HttpClient();
        using var formRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Create");
        var form = await (await client.SendAsync(formRequest)).Content.ReadAsStringAsync();
        using var created = await PostForm(client, host, "Create", new Dictionary<string, string>
        {
            ["title"] = "Leave me alone",
            ["body"] = "Untouched",
            ["status"] = "Todo",
            ["priority"] = "P2",
            ["creationAttemptId"] = HiddenValue(form, "creationAttemptId")
        });
        var newId = await StoredItemId("Leave me alone");
        using var opened = await PostForm(client, host, "Claim", new Dictionary<string, string>
        {
            ["id"] = newId,
            ["fromCard"] = "true"
        });
        var editHtml = await opened.Content.ReadAsStringAsync();

        using var cancelled = await PostForm(client, host, "Save", new Dictionary<string, string>
        {
            ["id"] = newId,
            ["expectedRevision"] = HiddenValue(editHtml, "expectedRevision"),
            ["expectedClaimGeneration"] = HiddenValue(editHtml, "expectedClaimGeneration"),
            ["title"] = "Discarded title",
            ["body"] = "Discarded body",
            ["status"] = "Todo",
            ["priority"] = "P2",
            ["action"] = "release",
            ["fromCard"] = "true"
        });

        Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
        Assert.Contains(
            "wrighty:close-panel", Assert.Single(cancelled.Headers.GetValues("HX-Trigger")));
        var (config, backend, id) = await StoredBackend(newId);
        var item = await backend.GetAsync(config, id, CancellationToken.None);
        Assert.Equal("Untouched", item!.Body);
        Assert.Equal("Leave me alone", item.Title);
        var stored = await backend.GetOperationalAsync(config, id, CancellationToken.None);
        Assert.Equal(ClaimOwnershipState.Unclaimed, stored!.Claim.State);
        await host.Stop();
    }

    [Fact]
    public async Task Clarifying_a_paused_session_saves_releases_and_leaves_Resume_on_the_card()
    {
        // Clarify is the same gesture as Edit: save and release, deciding nothing else. Resuming is
        // the card's own action, which means the release must keep the needs-attention marker —
        // clearing it would take Resume off the card the operator is about to press it on.
        var host = await StartServer(openBrowser: false, releaseSeededClaim: true);
        using var client = new HttpClient();
        using var opened = await PostForm(client, host, "Claim", new Dictionary<string, string>
        {
            ["id"] = "local:1",
            ["fromCard"] = "true"
        });
        var editHtml = await opened.Content.ReadAsStringAsync();
        Assert.Contains("value=\"save-release\"", editHtml);
        Assert.DoesNotContain("value=\"save-queue\"", editHtml);
        Assert.Equal(DispatchStates.NeedsAttention, (await StoredState()).Item.DispatchState);

        using var saved = await PostForm(client, host, "Save", new Dictionary<string, string>
        {
            ["id"] = "local:1",
            ["expectedRevision"] = HiddenValue(editHtml, "expectedRevision"),
            ["expectedClaimGeneration"] = HiddenValue(editHtml, "expectedClaimGeneration"),
            ["title"] = "Hostile item",
            ["body"] = "Answered: keep the playful tone.",
            ["status"] = "In Progress",
            ["priority"] = "P1",
            // The form posts the checkbox as it stands. Omitting it binds false, which turns
            // automatic execution off — and that legitimately clears the dispatch state, because a
            // pending decision to run an item is void once workers must ignore it.
            ["automaticExecutionAllowed"] = "true",
            ["action"] = "save-release",
            ["fromCard"] = "true"
        });

        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        var after = await StoredState();
        Assert.Equal("Answered: keep the playful tone.", after.Item.Body);
        Assert.Equal(ClaimOwnershipState.Unclaimed, after.Claim.State);
        // The marker survives, so the item is still waiting for a person's next move...
        Assert.Equal(DispatchStates.NeedsAttention, after.Item.DispatchState);
        // ...and Resume is there to be that move.
        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        Assert.Contains("handler=ResumeSession", CardMarkup(board, "local:1"));
        await host.Stop();
    }

    [Fact]
    public async Task Clarify_picks_up_an_item_the_worker_handed_off_without_a_takeover()
    {
        // needs-attention is the worker saying it stopped and a person is needed. A claim left
        // behind by a previous dashboard process — claimant identity is per-process — is residue,
        // not an occupant, so answering the hand-off must not require a ceremony. An *agent's*
        // claim is a different matter and stays behind web.protectNonHumanClaims; here the holder
        // is a human web claimant, which is the case that used to refuse for no good reason.
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();
        var (config, backend, id) = await StoredBackend();
        var stale = new AgentExecutionContext(
            "codex", "web-test-session", AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Human, ClaimantId: "web:a-previous-dashboard");
        await backend.TakeoverAsync(config, id, stale, null, CancellationToken.None);
        var before = await StoredState();
        Assert.Equal(ClaimOwnershipState.OwnedByCurrent, before.Claim.State);
        Assert.Equal("human", before.Claim.ClaimantKind);

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        Assert.Contains("handler=Claim", CardMarkup(board, "local:1"));

        using var clarified = await PostForm(client, host, "Claim", new Dictionary<string, string>
        {
            ["id"] = "local:1",
            ["fromCard"] = "true"
        });
        var html = await clarified.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, clarified.StatusCode);
        Assert.Contains("name=\"body\"", html);
        var after = await StoredState();
        // Taken over by the dashboard's human claimant, with the recorded address carried through.
        Assert.Equal("human", after.Claim.ClaimantKind);
        Assert.Equal("codex", after.Claim.Agent);
        Assert.Equal("web-test-session", after.Claim.SessionId);
        await host.Stop();
    }

    [Fact]
    public async Task Clarify_routes_a_retained_agent_claim_through_confirmed_takeover()
    {
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        var card = CardMarkup(board, "local:1");
        Assert.Contains("handler=Takeover", card);
        Assert.DoesNotContain("handler=Claim", card);
        Assert.Contains(">Clarify", card);
        Assert.Contains(
            "data-confirm-title=\"Clarify this paused item?\"",
            card);
        Assert.Contains("The agent has stopped and is waiting for input", card);
        Assert.Contains("The saved agent session will remain available to resume afterward", card);
        Assert.Contains("data-confirm-action=\"Open for clarification\"", card);
        Assert.Contains("name=\"fromCard\" value=\"true\"", card);

        using var clarified = await PostForm(client, host, "Takeover", new()
        {
            ["id"] = "local:1",
            ["fromCard"] = "true"
        });
        var html = await clarified.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, clarified.StatusCode);
        Assert.Contains("name=\"body\"", html);
        Assert.Contains("Ready for clarification", html);
        Assert.DoesNotContain("previous claimant is fenced", html);
        Assert.DoesNotContain("Save and resume automatically</strong> queues", html);
        Assert.DoesNotContain("More actions…", html);
        Assert.Contains("value=\"save-release\"", html);
        Assert.DoesNotContain("value=\"save-queue\"", html);
        Assert.Equal("human", (await StoredState()).Claim.ClaimantKind);
        await host.Stop();
    }

    [Fact]
    public async Task Clarify_refuses_an_item_another_installation_holds()
    {
        // The party worth refusing. local:2 is claimed by a different installation.
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        Assert.DoesNotContain("handler=Claim", CardMarkup(board, "local:2"));

        using var refused = await PostForm(
            client, host, "Claim", new Dictionary<string, string> { ["id"] = "local:2" });

        Assert.NotEqual(HttpStatusCode.OK, refused.StatusCode);
        await host.Stop();
    }

    [Fact]
    public async Task Board_send_back_button_returns_a_queued_item_to_the_backlog()
    {
        // The symmetric revocation: the same one-gesture bundling as the queue button, the other
        // way, with the queue rule clearing automatic execution as part of the move.
        var host = await StartServer(openBrowser: false, pickFrom: "Worker queue");
        using var client = new HttpClient();
        using var formRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Create");
        var form = await (await client.SendAsync(formRequest)).Content.ReadAsStringAsync();
        using var created = await PostForm(client, host, "Create", new Dictionary<string, string>
        {
            ["title"] = "Send me back",
            ["body"] = "Body",
            ["status"] = "Todo",
            ["priority"] = "P2",
            ["creationAttemptId"] = HiddenValue(form, "creationAttemptId")
        });
        var newId = await StoredItemId("Send me back");
        using var queued = await PostForm(client, host, "QueueItem", new Dictionary<string, string>
        {
            ["id"] = newId
        });
        Assert.Equal(HttpStatusCode.NoContent, queued.StatusCode);

        using var queuedBoardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var queuedBoard = await (await client.SendAsync(queuedBoardRequest)).Content
            .ReadAsStringAsync();
        Assert.Contains("handler=DequeueItem", CardMarkup(queuedBoard, newId));
        Assert.DoesNotContain("handler=QueueItem", CardMarkup(queuedBoard, newId));

        using var sentBack = await PostForm(client, host, "DequeueItem", new Dictionary<string, string>
        {
            ["id"] = newId
        });

        Assert.Equal(HttpStatusCode.NoContent, sentBack.StatusCode);
        Assert.Equal("wrighty:refresh", Assert.Single(sentBack.Headers.GetValues("HX-Trigger")));
        using var detailRequest = AuthenticatedGet(
            host, $"{host.Origin}/?handler=Item&id={Uri.EscapeDataString(newId)}");
        var detail = await (await client.SendAsync(detailRequest)).Content.ReadAsStringAsync();
        Assert.Contains("Todo", detail);
        Assert.Contains("Manual only", detail);

        using var afterRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var after = await (await client.SendAsync(afterRequest)).Content.ReadAsStringAsync();
        Assert.Contains("handler=QueueItem", CardMarkup(after, newId));
        Assert.DoesNotContain("handler=DequeueItem", CardMarkup(after, newId));
        await host.Stop();
    }

    [Fact]
    public async Task Board_offers_no_card_action_for_an_item_a_human_holds()
    {
        // Eligibility is state-aware: a claimed item is not a one-gesture decision either way.
        var host = await StartServer(openBrowser: false, pickFrom: "Worker queue");
        using var client = new HttpClient();
        using var formRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Create");
        var form = await (await client.SendAsync(formRequest)).Content.ReadAsStringAsync();
        using var created = await PostForm(client, host, "Create", new Dictionary<string, string>
        {
            ["title"] = "Claimed item",
            ["body"] = "Body",
            ["status"] = "Todo",
            ["priority"] = "P2",
            ["creationAttemptId"] = HiddenValue(form, "creationAttemptId")
        });
        var newId = await StoredItemId("Claimed item");
        using var claimed = await PostForm(client, host, "Claim", new Dictionary<string, string>
        {
            ["id"] = newId
        });
        Assert.Equal(HttpStatusCode.OK, claimed.StatusCode);

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();

        var card = CardMarkup(board, newId);
        Assert.DoesNotContain("handler=QueueItem", card);
        Assert.DoesNotContain("handler=DequeueItem", card);
        await host.Stop();
    }

    [Fact]
    public async Task Board_cancel_resume_returns_a_queued_session_to_needs_attention()
    {
        // The inverse of Resume. Asserting the resulting state matters more than the status code:
        // an ordinary release clears dispatch state, so a handler using one would report success
        // and silently leave the item merely paused.
        var host = await StartServer(openBrowser: false, releaseSeededClaim: true);
        using var client = new HttpClient();
        const string paused = "local:1";

        using var queued = await PostForm(client, host, "ResumeSession", new Dictionary<string, string>
        {
            ["id"] = paused
        });
        Assert.Equal(HttpStatusCode.NoContent, queued.StatusCode);
        using var queuedBoardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var queuedBoard = await (await client.SendAsync(queuedBoardRequest)).Content
            .ReadAsStringAsync();
        Assert.Contains("handler=HoldSession", CardMarkup(queuedBoard, paused));

        using var held = await PostForm(client, host, "HoldSession", new Dictionary<string, string>
        {
            ["id"] = paused
        });

        Assert.Equal(HttpStatusCode.NoContent, held.StatusCode);
        using var afterRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var after = await (await client.SendAsync(afterRequest)).Content.ReadAsStringAsync();
        var card = CardMarkup(after, paused);
        Assert.Contains("Needs attention", card);
        Assert.Contains("handler=ResumeSession", card);
        Assert.DoesNotContain("handler=HoldSession", card);
        await host.Stop();
    }

    [Fact]
    public async Task A_paused_session_with_no_dispatch_state_can_be_reopened_from_its_card()
    {
        // The dead end: a retained session whose dispatch state was cleared. It is resumable —
        // the recorded address is intact — but queueing refuses it, because queueing a recorded
        // session requires the needs-attention marker that is missing. Before this action the way
        // out was a no-op `wrighty edit --takeover --requeue` followed by Cancel resume.
        var host = await StartServer(openBrowser: false, releaseSeededClaim: true);
        using var client = new HttpClient();
        // Reached the way it is reached in practice: a claim, then a release that clears.
        var (config, backend, id) = await StoredBackend();
        var context = new AgentExecutionContext(
            null, null, AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Human, ClaimantId: "human:test");
        var claim = await backend.TryClaimAsync(config, id, context, CancellationToken.None);
        await backend.ReleaseAsync(
            config,
            id,
            new ClaimHandle(context, claim.ClaimToken),
            false,
            DispatchStateOnRelease.Clear,
            CancellationToken.None);
        Assert.Null((await StoredState()).Item.DispatchState);

        // Queueing refuses while the marker is missing — the dead end, asserted rather than
        // assumed.
        using var refused = await PostForm(
            client, host, "ResumeSession", new() { ["id"] = "local:1" });
        Assert.NotEqual(HttpStatusCode.NoContent, refused.StatusCode);

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        Assert.Contains("handler=HoldSession", CardMarkup(board, "local:1"));

        using var reopened = await PostForm(
            client, host, "HoldSession", new() { ["id"] = "local:1" });

        Assert.Equal(HttpStatusCode.NoContent, reopened.StatusCode);
        var after = await StoredState();
        Assert.Equal(DispatchStates.NeedsAttention, after.Item.DispatchState);
        // The recorded address survives the round trip, and the item's ordinary actions come back.
        Assert.Equal("codex", after.Session?.Agent);
        Assert.Equal("web-test-session", after.Session?.SessionId);
        using var afterBoard = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var reopenedCard = CardMarkup(
            await (await client.SendAsync(afterBoard)).Content.ReadAsStringAsync(), "local:1");
        Assert.Contains("handler=ResumeSession", reopenedCard);
        await host.Stop();
    }

    [Fact]
    public async Task Needs_attention_card_offers_clarify_and_resume()
    {
        // The state where the next step branches: answer the question, or hand it back. Both
        // belong on the card rather than behind the panel.
        var host = await StartServer(openBrowser: false, releaseSeededClaim: true);
        using var client = new HttpClient();

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        var card = CardMarkup(board, "local:1");

        Assert.Contains("handler=Claim", card);
        Assert.Contains("handler=ResumeSession", card);
        // The interactive ways back in, no longer buried in the panel: each acquires the claim its
        // own mode needs rather than demanding the operator already hold it. Both modes are
        // available here, so they fold into one named button that opens a chooser — two buttons
        // for one intent crowded the card.
        Assert.Contains("Open Codex<span aria-hidden=\"true\"> ⏵</span>", card);
        Assert.Contains("data-open-dialog=\"launch-local:1-open-session\"", card);
        Assert.Contains("handler=OpenSessionCli", card);
        Assert.Contains("handler=OpenSessionDesktop", card);
        // The modes differ in who holds the item afterwards, which the labels alone do not say.
        Assert.Contains("passes the claim into the Codex CLI", card);
        Assert.Contains("You supervise this one", card);
        await host.Stop();
    }

    [Fact]
    public async Task Done_unclaimed_session_opens_unmanaged_from_board_operations_and_panel()
    {
        var launcher = new RecordingAgentSessionLauncher();
        var host = await StartServer(
            openBrowser: false,
            sessionLauncher: launcher,
            finishSeededSession: true,
            releaseSeededClaim: true);
        using var client = new HttpClient();

        using var operationsRequest = AuthenticatedGet(
            host, $"{host.Origin}/?handler=Operations");
        var operations = await (
            await client.SendAsync(operationsRequest)).Content.ReadAsStringAsync();
        Assert.Contains("Open Codex", operations);
        Assert.Contains("handler=OpenSessionCli", operations);
        Assert.Contains("handler=OpenSessionDesktop", operations);
        Assert.Contains("stays outside Wrighty&#x27;s management", operations);

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        var card = CardMarkup(board, "local:1");
        Assert.Contains("Open Codex", card);
        Assert.Contains("handler=ArchiveItem", card);

        using var itemRequest = AuthenticatedGet(
            host, $"{host.Origin}/?handler=Item&id=local%3A1");
        var item = await (await client.SendAsync(itemRequest)).Content.ReadAsStringAsync();
        Assert.Contains("id=\"session-open-cli-codex\"", item);
        Assert.Contains("id=\"session-open-desktop-codex\"", item);
        Assert.Contains("without a Wrighty claim or claimant credentials", item);

        using var cliLaunch = await PostFormWithToken(
            client,
            host,
            "OpenSessionCli",
            new Dictionary<string, string>
            {
                ["id"] = "local:1",
                ["expectedSessionId"] = HiddenValue(operations, "expectedSessionId"),
                ["expectedSessionGeneration"] =
                    HiddenValue(operations, "expectedSessionGeneration")
            },
            operations);
        Assert.Equal(HttpStatusCode.NoContent, cliLaunch.StatusCode);
        Assert.Equal("codex", launcher.CliInvocation?.Executable);
        Assert.False(launcher.CliInvocation?.Environment.ContainsKey(
            "WRIGHTY_CLAIMANT_ID"));
        Assert.False(launcher.CliInvocation?.Environment.ContainsKey(
            "WRIGHTY_CLAIM_TOKEN"));
        Assert.Equal(ClaimOwnershipState.Unclaimed, (await StoredState()).Claim.State);

        using var desktopLaunch = await PostFormWithToken(
            client,
            host,
            "OpenSessionDesktop",
            new Dictionary<string, string>
            {
                ["id"] = "local:1",
                ["expectedSessionId"] = HiddenValue(operations, "expectedSessionId"),
                ["expectedSessionGeneration"] =
                    HiddenValue(operations, "expectedSessionGeneration")
            },
            operations);
        Assert.Equal(HttpStatusCode.NoContent, desktopLaunch.StatusCode);
        Assert.NotNull(launcher.DesktopAddress);
        Assert.Equal(ClaimOwnershipState.Unclaimed, (await StoredState()).Claim.State);
        await host.Stop();
    }

    [Fact]
    public async Task Done_session_with_an_active_claim_is_not_offered_as_unmanaged()
    {
        var host = await StartServer(
            openBrowser: false,
            finishSeededSession: true);
        using var client = new HttpClient();

        Assert.Equal(ClaimOwnershipState.OwnedByCurrent, (await StoredState()).Claim.State);
        using var operationsRequest = AuthenticatedGet(
            host, $"{host.Origin}/?handler=Operations");
        var operations = await (
            await client.SendAsync(operationsRequest)).Content.ReadAsStringAsync();
        Assert.Contains("Codex session retained here", operations);
        Assert.DoesNotContain("handler=OpenSessionCli", operations);
        Assert.DoesNotContain("handler=OpenSessionDesktop", operations);

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        var card = CardMarkup(board, "local:1");
        Assert.DoesNotContain("handler=OpenSessionCli", card);
        Assert.DoesNotContain("handler=OpenSessionDesktop", card);
        await host.Stop();
    }

    [Fact]
    public async Task A_single_available_launch_mode_is_named_and_launches_without_a_chooser()
    {
        // A chooser holding one option is a wasted click, and "Open Codex" would say less than
        // naming the one route that exists. Desktop is unavailable on this host.
        var launcher = new CliOnlyAgentSessionLauncher();
        var host = await StartServer(
            openBrowser: false, sessionLauncher: launcher, releaseSeededClaim: true);
        using var client = new HttpClient();

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        var card = CardMarkup(board, "local:1");

        Assert.Contains("Open Codex CLI", card);
        Assert.Contains("handler=OpenSessionCli", card);
        Assert.DoesNotContain("data-open-dialog", card);
        Assert.DoesNotContain("handler=OpenSessionDesktop", card);
        await host.Stop();
    }

    [Fact]
    public async Task Launch_card_actions_are_absent_when_another_installation_holds_the_item()
    {
        // Acquire, never displace: another installation's claim is not this operator's to take in
        // one gesture, so the board does not offer a gesture that would have to refuse.
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        var card = CardMarkup(board, "local:2");

        Assert.DoesNotContain("handler=OpenSessionCli", card);
        Assert.DoesNotContain("handler=OpenSessionDesktop", card);
        await host.Stop();
    }

    [Fact]
    public async Task Card_launch_reclaims_this_installations_own_ended_session()
    {
        // The ordinary paused state: needs-attention while still holding the agent claim of the
        // run that just stopped, on this installation. Refusing it would put the launch actions
        // out of reach in exactly the state an operator wants them.
        var launcher = new RecordingAgentSessionLauncher();
        var host = await StartServer(openBrowser: false, sessionLauncher: launcher);
        using var client = new HttpClient();
        var before = await StoredState();
        Assert.Equal(ClaimOwnershipState.OwnedByCurrent, before.Claim.State);
        Assert.Equal("agent:web-test-session", before.Claim.ClaimantId);

        using var launch = await PostCardLaunch(client, host, "OpenSessionCli");

        Assert.Equal(HttpStatusCode.NoContent, launch.StatusCode);
        Assert.Equal("wrighty:refresh", launch.Headers.GetValues("HX-Trigger").Single());
        Assert.Equal("codex", launcher.CliInvocation?.Executable);
        // The defect this whole path exists to avoid: the recorded address must survive being
        // reclaimed. Asserted on what was stored, not on the response, because the first attempt
        // returned success while writing agent: null underneath.
        var after = await StoredState();
        Assert.Equal("codex", after.Session?.Agent);
        Assert.Equal("web-test-session", after.Session?.SessionId);
        Assert.Equal("codex", after.Claim.Agent);
        Assert.Equal("web-test-session", after.Claim.SessionId);
        // ...and the claim is now the launching operator's, not the ended run's.
        Assert.StartsWith("agent:web-launch:", after.Claim.ClaimantId);
        await host.Stop();
    }

    [Fact]
    public async Task Reclaiming_a_session_fences_the_previous_claimant()
    {
        // Wrighty cannot see whether the vendor client stopped, so safety is fencing, not
        // detection: if that client is still alive, its next cooperating mutation is rejected
        // rather than silently overwriting the operator who reclaimed.
        var launcher = new RecordingAgentSessionLauncher();
        var host = await StartServer(openBrowser: false, sessionLauncher: launcher);
        using var client = new HttpClient();
        using var launch = await PostCardLaunch(client, host, "OpenSessionCli");
        Assert.Equal(HttpStatusCode.NoContent, launch.StatusCode);

        var (config, backend, id) = await StoredBackend();
        var superseded = new ClaimHandle(
            new AgentExecutionContext(
                "codex",
                "web-test-session",
                AgentContextSource.ExplicitOption,
                ClaimantKind: ClaimantKind.Agent,
                ClaimantId: "agent:web-test-session"),
            "superseded-token");
        var stale = await Assert.ThrowsAsync<TrackerException>(() =>
            backend.RenewClaimAsync(
                config, id, superseded, null, "web-test-session", CancellationToken.None));

        Assert.Equal("CLAIM_STALE", stale.Code);
        await host.Stop();
    }

    [Fact]
    public async Task Card_launch_refuses_while_a_worker_decision_is_pending()
    {
        // The race the guard exists for: the operator's board still shows a paused card, the item
        // gets queued underneath them, and they then click Open CLI. Opening the session now would
        // race the worker that is about to claim it. The session itself has not changed, so the
        // generation check passes and only this rule stands between the two.
        var launcher = new RecordingAgentSessionLauncher();
        var host = await StartServer(
            openBrowser: false, sessionLauncher: launcher, releaseSeededClaim: true);
        using var client = new HttpClient();
        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        var card = CardMarkup(board, "local:1");
        using var queued = await PostForm(
            client, host, "ResumeSession", new() { ["id"] = "local:1" });
        Assert.Equal(HttpStatusCode.NoContent, queued.StatusCode);

        using var launch = await PostForm(client, host, "OpenSessionCli", new()
        {
            ["id"] = "local:1",
            ["expectedSessionId"] = HiddenValue(card, "expectedSessionId"),
            ["expectedSessionGeneration"] = HiddenValue(card, "expectedSessionGeneration")
        });
        var html = await launch.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, launch.StatusCode);
        Assert.Equal("wrighty:refresh", launch.Headers.GetValues("HX-Trigger").Single());
        Assert.Contains("worker decision is pending", html);
        Assert.Null(launcher.CliInvocation);
        var after = await StoredState();
        Assert.Equal(ClaimOwnershipState.Unclaimed, after.Claim.State);
        await host.Stop();
    }

    [Fact]
    public async Task A_launch_that_fails_after_acquiring_leaves_no_claim_behind()
    {
        // Getting in is one gesture and getting out is not, so a successful launch keeps its
        // claim. A launch that never happened must leave no residue.
        var launcher = new FailingAgentSessionLauncher();
        var host = await StartServer(
            openBrowser: false, sessionLauncher: launcher, releaseSeededClaim: true);
        using var client = new HttpClient();
        Assert.Equal(ClaimOwnershipState.Unclaimed, (await StoredState()).Claim.State);

        using var launch = await PostCardLaunch(client, host, "OpenSessionCli");

        Assert.NotEqual(HttpStatusCode.NoContent, launch.StatusCode);
        var after = await StoredState();
        Assert.Equal(ClaimOwnershipState.Unclaimed, after.Claim.State);
        // The address the failed launch acquired under must not be collateral damage either.
        Assert.Equal("codex", after.Session?.Agent);
        Assert.Equal("web-test-session", after.Session?.SessionId);
        // Nor the dispatch state. An ordinary release clears it, which would demote the item from
        // needs-attention to a plain paused session — no marker, no card actions, no way back in.
        // This assertion exists because the first live run did exactly that.
        Assert.Equal(DispatchStates.NeedsAttention, after.Item.DispatchState);
        await host.Stop();
    }

    [Fact]
    public async Task Desktop_card_launch_keeps_the_recorded_agent_when_it_takes_a_human_claim()
    {
        // Finding 1, as a standing guard. Desktop's mode needs a *human* claim, and a human
        // claimant carries no agent — so a plain acquisition on an unclaimed item wrote
        // agent: null over the durable address, and the item could no longer be resumed by
        // worker, terminal, or Desktop. Asserted on the stored address: the first attempt
        // returned success while destroying it.
        var launcher = new RecordingAgentSessionLauncher();
        var host = await StartServer(
            openBrowser: false, sessionLauncher: launcher, releaseSeededClaim: true);
        using var client = new HttpClient();
        Assert.Equal(ClaimOwnershipState.Unclaimed, (await StoredState()).Claim.State);

        using var launch = await PostCardLaunch(client, host, "OpenSessionDesktop");

        Assert.Equal(HttpStatusCode.NoContent, launch.StatusCode);
        Assert.NotNull(launcher.DesktopAddress);
        var after = await StoredState();
        Assert.Equal(ClaimOwnershipState.OwnedByCurrent, after.Claim.State);
        Assert.Equal("human", after.Claim.ClaimantKind);
        Assert.Equal("codex", after.Claim.Agent);
        Assert.Equal("web-test-session", after.Claim.SessionId);
        Assert.Equal("codex", after.Session?.Agent);
        Assert.Equal("web-test-session", after.Session?.SessionId);
        await host.Stop();
    }

    [Fact]
    public async Task A_desktop_only_card_names_the_route_and_keeps_its_warning()
    {
        // The mirror of the CLI-only card, and the case that carries Claude's experimental
        // warning: with no chooser to state it, the confirmation has to.
        var launcher = new DesktopOnlyAgentSessionLauncher();
        var host = await StartServer(
            openBrowser: false,
            sessionLauncher: launcher,
            sessionAgent: "claude",
            sessionId: "940cd4c6-bb95-84d8-a78a-73af49c898a0",
            releaseSeededClaim: true);
        using var client = new HttpClient();

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        var card = CardMarkup(board, "local:1");

        Assert.Contains("Open Claude Desktop", card);
        Assert.Contains("handler=OpenSessionDesktop", card);
        Assert.DoesNotContain("data-open-dialog", card);
        Assert.DoesNotContain("handler=OpenSessionCli", card);
        // Enabled by default, so this warning is the only thing telling the operator the route is
        // unproven. It must reach the card.
        Assert.Contains("passed qualification on one release", card);
        Assert.Contains("You supervise this one", card);
        await host.Stop();
    }

    [Fact]
    public async Task A_card_launch_reuses_a_claim_the_dashboard_already_holds()
    {
        // Nothing to acquire: the dashboard handed the item back to an agent and kept the handle,
        // so the launch validates and uses it rather than rotating to a second claimant.
        var launcher = new RecordingAgentSessionLauncher();
        var host = await StartServer(openBrowser: false, sessionLauncher: launcher);
        using var client = new HttpClient();
        using var takeover = await PostForm(
            client, host, "Takeover", new() { ["id"] = "local:1" });
        var takeoverHtml = await takeover.Content.ReadAsStringAsync();
        using var handback = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:1",
            ["expectedRevision"] = HiddenValue(takeoverHtml, "expectedRevision"),
            ["expectedClaimGeneration"] = HiddenValue(takeoverHtml, "expectedClaimGeneration"),
            ["title"] = "Clarified item",
            ["body"] = "Actionable body",
            ["status"] = "In Progress",
            ["priority"] = "P1",
            ["action"] = "save-handback"
        });
        var handbackHtml = await handback.Content.ReadAsStringAsync();
        var claimantBefore = (await StoredState()).Claim.ClaimantId;

        using var launch = await PostForm(client, host, "OpenSessionCli", new()
        {
            ["id"] = "local:1",
            ["expectedSessionId"] = HiddenValue(handbackHtml, "expectedSessionId"),
            ["expectedSessionGeneration"] =
                HiddenValue(handbackHtml, "expectedSessionGeneration")
        });

        Assert.Equal(HttpStatusCode.NoContent, launch.StatusCode);
        Assert.Equal("wrighty:refresh", launch.Headers.GetValues("HX-Trigger").Single());
        Assert.StartsWith(
            "agent:web-handback:",
            launcher.CliInvocation?.Environment["WRIGHTY_CLAIMANT_ID"]);
        Assert.Equal(claimantBefore, (await StoredState()).Claim.ClaimantId);
        await host.Stop();
    }

    private async Task<HttpResponseMessage> PostCardLaunch(
        HttpClient client,
        RunningServer host,
        string handler)
    {
        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var board = await (await client.SendAsync(boardRequest)).Content.ReadAsStringAsync();
        var card = CardMarkup(board, "local:1");
        return await PostForm(client, host, handler, new()
        {
            ["id"] = "local:1",
            ["expectedSessionId"] = HiddenValue(card, "expectedSessionId"),
            ["expectedSessionGeneration"] = HiddenValue(card, "expectedSessionGeneration")
        });
    }

    private async Task<WorkItemOperationalSnapshot> StoredState()
    {
        var (config, backend, id) = await StoredBackend();
        return await backend.GetOperationalAsync(config, id, CancellationToken.None)
            ?? throw new InvalidOperationException("The seeded item is missing.");
    }

    private async Task<string> StoredItemId(string title)
    {
        var (config, backend, _) = await StoredBackend();
        var items = await backend.ListAsync(
            config,
            new ListWorkItemsRequest(null, null, ArchiveScope.All),
            CancellationToken.None);
        return Assert.Single(items, item => item.Title == title).Id.Value;
    }

    private async Task<(TrackerConfig Config, LocalMarkdownTrackerBackend Backend, WorkItemId Id)>
        StoredBackend(string id = "local:1")
    {
        var config = await new TrackerConfigLoader()
            .LoadAsync(directory, CancellationToken.None);
        var backend = new LocalMarkdownTrackerBackend(
            new FixedIdentity("web-test-worker"), new SystemClock());
        return (config, backend, new WorkItemId(id));
    }

    private sealed class DesktopOnlyAgentSessionLauncher : ILocalAgentSessionLauncher
    {
        public LocalSessionLaunchCapabilities GetCapabilities(string agentType) =>
            new(false, true, "No terminal is available on this platform.");

        public Task<int> ExecuteAsync(
            LocalAgentInvocation invocation,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<SessionLaunchResult> LaunchCliAsync(
            LocalAgentInvocation invocation,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Unsupported));

        public Task<SessionLaunchResult> LaunchDesktopAsync(
            DesktopLaunchAddress address,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Launched));
    }

    private sealed class CliOnlyAgentSessionLauncher : ILocalAgentSessionLauncher
    {
        public LocalSessionLaunchCapabilities GetCapabilities(string agentType) =>
            new(true, false, null, "Desktop is unavailable on this platform.");

        public Task<int> ExecuteAsync(
            LocalAgentInvocation invocation,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<SessionLaunchResult> LaunchCliAsync(
            LocalAgentInvocation invocation,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Launched));

        public Task<SessionLaunchResult> LaunchDesktopAsync(
            DesktopLaunchAddress address,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Unsupported));
    }

    private sealed class FailingAgentSessionLauncher : ILocalAgentSessionLauncher
    {
        public LocalSessionLaunchCapabilities GetCapabilities(string agentType) => new(true, true);

        public Task<int> ExecuteAsync(
            LocalAgentInvocation invocation,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<SessionLaunchResult> LaunchCliAsync(
            LocalAgentInvocation invocation,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Failed, "no terminal"));

        public Task<SessionLaunchResult> LaunchDesktopAsync(
            DesktopLaunchAddress address,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Failed, "no handler"));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("localhost")]
    [InlineData("LOCALHOST")]
    [InlineData("[::1]")]
    public async Task Requests_accept_every_loopback_authority(string hostName)
    {
        var host = await StartServer();
        using var client = new HttpClient();
        var port = new Uri(host.Origin).Port;
        using var request = new HttpRequestMessage(HttpMethod.Get, host.Origin);
        request.Headers.Host = $"{hostName}:{port}";

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await host.Stop();
    }

    [Fact]
    public async Task Requests_reject_invalid_hosts_before_checking_the_token()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        var port = new Uri(host.Origin).Port;
        var invalidAuthorities = new[]
        {
            $"evil.example:{port}",
            $"127.0.0.1:{port + 1}",
            "localhost"
        };

        foreach (var authority in invalidAuthorities)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"{host.Origin}/?handler=Board");
            request.Headers.Host = authority;
            request.Headers.Add(WrightyWebServer.TokenHeader, "invalid-token");
            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("HOST_INVALID", await ProblemTitle(response));
        }

        await host.Stop();
    }

    [Fact]
    public async Task Requests_reject_non_form_mutations()
    {
        var host = await StartServer();
        using var client = new HttpClient();

        using var nonForm = new HttpRequestMessage(HttpMethod.Post, $"{host.Origin}/?handler=Claim");
        nonForm.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        nonForm.Headers.Add("Origin", host.Origin);
        nonForm.Content = new StringContent("id=local%3A3", Encoding.UTF8, "text/plain");
        var nonFormResponse = await client.SendAsync(nonForm);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, nonFormResponse.StatusCode);

        await host.Stop();
    }

    [Fact]
    public void Unconfigured_endpoint_rejects_an_authority_instead_of_bypassing_validation()
    {
        var config = new TrackerConfig
        {
            SourcePath = Path.Combine(Path.GetTempPath(), TrackerConfigLoader.FileName)
        };
        var state = new WebApplicationState(config, "token", Path.GetTempPath());

        Assert.Equal(0, state.Port);
        Assert.False(state.AllowsAuthority(new HostString("127.0.0.1", 5000)));
    }

    [Fact]
    public void Configured_endpoint_rejects_an_empty_authority()
    {
        var config = new TrackerConfig
        {
            SourcePath = Path.Combine(Path.GetTempPath(), TrackerConfigLoader.FileName)
        };
        var state = new WebApplicationState(config, "token", Path.GetTempPath());
        state.ConfigureEndpoint(IPAddress.Loopback, 5000, []);

        Assert.False(state.AllowsAuthority(new HostString()));
    }

    [Fact]
    public void Non_loopback_endpoint_accepts_only_the_bound_address_and_explicit_hosts()
    {
        var config = new TrackerConfig
        {
            SourcePath = Path.Combine(Path.GetTempPath(), TrackerConfigLoader.FileName)
        };
        var state = new WebApplicationState(config, "token", Path.GetTempPath());
        var bindAddress = IPAddress.Parse("192.0.2.10");
        state.ConfigureEndpoint(bindAddress, 5000, ["wrighty.tailnet.example"]);

        Assert.True(state.AllowsAuthority(new HostString("192.0.2.10", 5000)));
        Assert.True(state.AllowsAuthority(new HostString("WRIGHTY.TAILNET.EXAMPLE", 5000)));
        Assert.False(state.AllowsAuthority(new HostString("localhost", 5000)));
        Assert.Contains(
            state.AllowedOrigins,
            origin => origin == "http://wrighty.tailnet.example:5000");
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    public void Endpoint_options_reject_wildcard_bind_addresses(string bindAddress)
    {
        var exception = Assert.Throws<TrackerException>(() =>
            WebEndpointOptionsResolver.Resolve(
                new WebServerOptions(BindAddress: bindAddress),
                []));

        Assert.Equal("WEB_BIND_WILDCARD_FORBIDDEN", exception.Code);
    }

    [Fact]
    public void Endpoint_options_reject_an_unassigned_bind_address()
    {
        var exception = Assert.Throws<TrackerException>(() =>
            WebEndpointOptionsResolver.Resolve(
                new WebServerOptions(BindAddress: "192.0.2.10"),
                [IPAddress.Parse("192.0.2.11")]));

        Assert.Equal("WEB_BIND_ADDRESS_UNAVAILABLE", exception.Code);
    }

    [Fact]
    public void Endpoint_options_accept_an_assigned_address_without_inferring_transport_security()
    {
        var address = IPAddress.Parse("192.0.2.10");

        var endpoint = WebEndpointOptionsResolver.Resolve(
            new WebServerOptions(
                Port: 8123,
                BindAddress: address.ToString(),
                AllowedHosts:
                [
                    "WRIGHTY.TAILNET.EXAMPLE",
                    "wrighty.tailnet.example"
                ]),
            [address]);

        Assert.Equal(address, endpoint.BindAddress);
        Assert.Equal(8123, endpoint.Port);
        Assert.False(endpoint.IsLoopback);
        Assert.Equal(["wrighty.tailnet.example"], endpoint.AllowedHosts);
    }

    [Fact]
    public void Non_loopback_warning_names_plaintext_transport_and_active_authentication()
    {
        var address = IPAddress.Parse("192.0.2.10");
        var warning = WrightyWebServer.AccessWarning(
            new WebEndpointOptions(address, 0, false, [], null),
            new WebAuthenticationSession(
                WebAuthenticationMode.EphemeralToken,
                "token"));

        Assert.NotNull(warning);
        Assert.Contains("192.0.2.10", warning);
        Assert.Contains("plaintext HTTP", warning);
        Assert.Contains("Token authentication is enabled", warning);
        Assert.Contains("Tailscale", warning);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("*.example")]
    [InlineData("https://wrighty.example")]
    [InlineData("wrighty.example:8123")]
    [InlineData("192.0.2.11")]
    public void Endpoint_options_reject_wildcard_malformed_or_unbound_allowed_hosts(
        string allowedHost)
    {
        var address = IPAddress.Parse("192.0.2.10");

        var exception = Assert.Throws<TrackerException>(() =>
            WebEndpointOptionsResolver.Resolve(
                new WebServerOptions(
                    BindAddress: address.ToString(),
                    AllowedHosts: [allowedHost]),
                [address]));

        Assert.Equal("WEB_ALLOWED_HOST_INVALID", exception.Code);
    }

    [Fact]
    public async Task Default_endpoint_listens_on_ipv6_loopback_when_available()
    {
        if (!System.Net.Sockets.Socket.OSSupportsIPv6)
        {
            return;
        }

        var host = await StartServer();
        using var client = new HttpClient();
        var port = new Uri(host.Origin).Port;
        using var response = await client.GetAsync($"http://[::1]:{port}/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await host.Stop();
    }

    [Fact]
    public async Task Assigned_non_loopback_endpoint_starts_with_token_authentication_and_warning()
    {
        var address = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .SelectMany(network => network.GetIPProperties().UnicastAddresses)
            .Select(entry => entry.Address)
            .FirstOrDefault(candidate =>
                candidate.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                !IPAddress.IsLoopback(candidate));
        if (address is null)
        {
            return;
        }

        var warnings = new StringWriter();
        var host = await StartServer(
            openBrowser: false,
            serverOptions: new WebServerOptions(
                OpenBrowser: false,
                BindAddress: address.ToString()),
            errorOutput: warnings);
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false });
        using var unauthorized = await client.GetAsync($"{host.Origin}/?handler=Board");

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Contains(address.ToString(), warnings.ToString());
        Assert.Contains("Token authentication is enabled", warnings.ToString());
        await host.Stop();
    }

    [Fact]
    public void Plain_web_authentication_uses_a_new_ephemeral_token_each_time()
    {
        var options = WebAuthenticationOptionsResolver.Resolve(new WebServerOptions());
        var provider = new WebTokenProvider(Path.Combine(directory, "managed"));
        var config = new TrackerConfig
        {
            SourcePath = Path.Combine(directory, "tracker", TrackerConfigLoader.FileName)
        };

        var first = provider.Resolve(options, config, directory);
        var second = provider.Resolve(options, config, directory);

        Assert.Equal(WebAuthenticationMode.EphemeralToken, first.Mode);
        Assert.True(first.TokenRequired);
        Assert.NotNull(first.Token);
        Assert.NotEqual(first.Token, second.Token);
    }

    [Fact]
    public void Managed_persistent_token_is_securely_created_and_reused()
    {
        var trackerRoot = Path.Combine(directory, "tracker");
        var managedRoot = Path.Combine(directory, "managed");
        var provider = new WebTokenProvider(managedRoot);
        var options = WebAuthenticationOptionsResolver.Resolve(
            new WebServerOptions(PersistToken: true));
        var config = new TrackerConfig
        {
            SourcePath = Path.Combine(trackerRoot, TrackerConfigLoader.FileName)
        };

        var first = provider.Resolve(options, config, directory);
        var second = provider.Resolve(options, config, directory);
        var path = provider.ManagedTokenPath(trackerRoot);

        Assert.Equal(WebAuthenticationMode.PersistentToken, first.Mode);
        Assert.Equal(first.Token, second.Token);
        Assert.Equal(first.Token, File.ReadAllText(path).Trim());
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(Path.GetDirectoryName(path)!));
        }
    }

    [Fact]
    public void Same_named_trackers_at_different_roots_get_different_managed_paths()
    {
        var provider = new WebTokenProvider(Path.Combine(directory, "managed"));
        var first = provider.ManagedTokenPath(
            Path.Combine(directory, "first", "project"));
        var second = provider.ManagedTokenPath(
            Path.Combine(directory, "second", "project"));

        Assert.NotEqual(first, second);
        Assert.StartsWith(
            "project-",
            Path.GetFileName(Path.GetDirectoryName(first)));
        Assert.StartsWith(
            "project-",
            Path.GetFileName(Path.GetDirectoryName(second)));
    }

    [Fact]
    public void Explicit_token_file_is_reused_and_can_be_rotated()
    {
        var trackerRoot = Path.Combine(directory, "tracker");
        var tokenPath = Path.Combine(directory, "credentials", "web-token");
        var provider = new WebTokenProvider(Path.Combine(directory, "managed"));
        var config = new TrackerConfig
        {
            SourcePath = Path.Combine(trackerRoot, TrackerConfigLoader.FileName)
        };
        var persistent = WebAuthenticationOptionsResolver.Resolve(
            new WebServerOptions(TokenFile: tokenPath));

        var first = provider.Resolve(persistent, config, directory);
        var reused = provider.Resolve(persistent, config, directory);
        var rotated = provider.Resolve(
            persistent with { RotateToken = true },
            config,
            directory);

        Assert.Equal(first.Token, reused.Token);
        Assert.NotEqual(first.Token, rotated.Token);
        Assert.Equal(rotated.Token, File.ReadAllText(tokenPath).Trim());
    }

    [Fact]
    public async Task Concurrent_first_persistent_starts_converge_on_one_token()
    {
        var trackerRoot = Path.Combine(directory, "tracker");
        var provider = new WebTokenProvider(Path.Combine(directory, "managed"));
        var options = WebAuthenticationOptionsResolver.Resolve(
            new WebServerOptions(PersistToken: true));
        var config = new TrackerConfig
        {
            SourcePath = Path.Combine(trackerRoot, TrackerConfigLoader.FileName)
        };

        var sessions = await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() =>
                    provider.Resolve(options, config, directory))));

        Assert.Single(sessions.Select(session => session.Token).Distinct());
        Assert.Equal(
            sessions[0].Token,
            File.ReadAllText(provider.ManagedTokenPath(trackerRoot)).Trim());
    }

    [Fact]
    public void Persistent_token_refuses_unsafe_existing_permissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var trackerRoot = Path.Combine(directory, "tracker");
        var provider = new WebTokenProvider(Path.Combine(directory, "managed"));
        var path = provider.ManagedTokenPath(trackerRoot);
        Directory.CreateDirectory(
            Path.GetDirectoryName(path)!,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(path, $"{WebTokenProvider.GenerateToken()}\n");
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        var options = WebAuthenticationOptionsResolver.Resolve(
            new WebServerOptions(PersistToken: true));
        var config = new TrackerConfig
        {
            SourcePath = Path.Combine(trackerRoot, TrackerConfigLoader.FileName)
        };
        var exception = Assert.Throws<TrackerException>(() =>
            provider.Resolve(options, config, directory));

        Assert.Equal("WEB_TOKEN_FILE_UNSAFE", exception.Code);
    }

    [Fact]
    public void Explicit_token_file_cannot_be_created_inside_the_tracker()
    {
        var trackerRoot = Path.Combine(directory, "tracker");
        var provider = new WebTokenProvider(Path.Combine(directory, "managed"));
        var options = WebAuthenticationOptionsResolver.Resolve(
            new WebServerOptions(
                TokenFile: Path.Combine(trackerRoot, ".tokens", "web-token")));
        var config = new TrackerConfig
        {
            SourcePath = Path.Combine(trackerRoot, TrackerConfigLoader.FileName)
        };
        var exception = Assert.Throws<TrackerException>(() =>
            provider.Resolve(options, config, directory));

        Assert.Equal("WEB_TOKEN_FILE_IN_REPOSITORY", exception.Code);
    }

    [Fact]
    public void Explicit_token_file_cannot_reach_the_tracker_through_a_directory_link()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var trackerRoot = Path.Combine(directory, "tracker");
        Directory.CreateDirectory(trackerRoot);
        var linkedDirectory = Path.Combine(directory, "linked-tracker");
        Directory.CreateSymbolicLink(linkedDirectory, trackerRoot);
        var provider = new WebTokenProvider(Path.Combine(directory, "managed"));
        var options = WebAuthenticationOptionsResolver.Resolve(
            new WebServerOptions(
                TokenFile: Path.Combine(linkedDirectory, "web-token")));
        var config = new TrackerConfig
        {
            SourcePath = Path.Combine(trackerRoot, TrackerConfigLoader.FileName)
        };
        var exception = Assert.Throws<TrackerException>(() =>
            provider.Resolve(options, config, directory));

        Assert.Equal("WEB_TOKEN_FILE_IN_REPOSITORY", exception.Code);
    }

    [Fact]
    public void Persistent_token_refuses_invalid_existing_token_material()
    {
        var trackerRoot = Path.Combine(directory, "tracker");
        var tokenPath = Path.Combine(directory, "credentials", "web-token");
        var provider = new WebTokenProvider(Path.Combine(directory, "managed"));
        var options = WebAuthenticationOptionsResolver.Resolve(
            new WebServerOptions(TokenFile: tokenPath));
        var config = new TrackerConfig
        {
            SourcePath = Path.Combine(trackerRoot, TrackerConfigLoader.FileName)
        };
        provider.Resolve(options, config, directory);
        File.WriteAllText(tokenPath, "not-a-wrighty-token\n");

        var exception = Assert.Throws<TrackerException>(() =>
            provider.Resolve(options, config, directory));

        Assert.Equal("WEB_TOKEN_FILE_INVALID", exception.Code);
    }

    [Theory]
    [InlineData("none", true, null, false)]
    [InlineData("none", false, "/tmp/token", false)]
    [InlineData("none", false, null, true)]
    [InlineData("token", false, null, true)]
    public void Authentication_options_reject_incompatible_token_modes(
        string authMode,
        bool persistToken,
        string? tokenFile,
        bool rotateToken)
    {
        var exception = Assert.Throws<TrackerException>(() =>
            WebAuthenticationOptionsResolver.Resolve(new WebServerOptions(
                AuthMode: authMode,
                PersistToken: persistToken,
                TokenFile: tokenFile,
                RotateToken: rotateToken)));

        Assert.Equal("WEB_AUTH_OPTIONS_CONFLICT", exception.Code);
    }

    [Fact]
    public void Authentication_options_reject_an_unknown_mode()
    {
        var exception = Assert.Throws<TrackerException>(() =>
            WebAuthenticationOptionsResolver.Resolve(
                new WebServerOptions(AuthMode: "password")));

        Assert.Equal("WEB_AUTH_INVALID", exception.Code);
    }

    [Fact]
    public async Task No_token_mode_omits_the_fragment_but_retains_host_and_origin_validation()
    {
        var warnings = new StringWriter();
        var host = await StartServer(
            openBrowser: false,
            serverOptions: new WebServerOptions(
                OpenBrowser: false,
                AuthMode: "none"),
            errorOutput: warnings);
        using var client = new HttpClient();

        using var board = await client.GetAsync($"{host.Origin}/?handler=Board");
        Assert.Equal(HttpStatusCode.OK, board.StatusCode);
        Assert.DoesNotContain("#token=", host.LaunchUrl);
        Assert.Equal(string.Empty, host.Token);
        Assert.Contains("token authentication is disabled", warnings.ToString());
        Assert.Contains(
            "<meta name=\"wrighty-auth\" content=\"none\">",
            await client.GetStringAsync(host.Origin));

        using var badHost = new HttpRequestMessage(HttpMethod.Get, host.Origin);
        badHost.Headers.Host = "evil.example";
        using var badHostResponse = await client.SendAsync(badHost);
        Assert.Equal(HttpStatusCode.BadRequest, badHostResponse.StatusCode);

        using var badOrigin = new HttpRequestMessage(
            HttpMethod.Post,
            $"{host.Origin}/?handler=Claim");
        badOrigin.Headers.Add("Origin", "http://evil.example");
        badOrigin.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = "local:3"
        });
        using var badOriginResponse = await client.SendAsync(badOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, badOriginResponse.StatusCode);
        await host.Stop();
    }

    [Fact]
    public async Task Public_url_controls_launch_and_adds_exact_proxy_host_and_origin()
    {
        var host = await StartServer(
            openBrowser: false,
            serverOptions: new WebServerOptions(
                OpenBrowser: false,
                PublicUrl: "https://wrighty.example"));
        using var client = new HttpClient();

        Assert.StartsWith("https://wrighty.example/#token=", host.LaunchUrl);

        using var boardRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"{host.Origin}/?handler=Board");
        boardRequest.Headers.Host = "wrighty.example";
        boardRequest.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        using var board = await client.SendAsync(boardRequest);
        Assert.Equal(HttpStatusCode.OK, board.StatusCode);

        using var mutation = new HttpRequestMessage(
            HttpMethod.Post,
            $"{host.Origin}/?handler=Claim");
        mutation.Headers.Host = "wrighty.example";
        mutation.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        mutation.Headers.Add("Origin", "https://wrighty.example");
        mutation.Content = new StringContent(
            "id=local%3A3",
            Encoding.UTF8,
            "text/plain");
        using var mutationResponse = await client.SendAsync(mutation);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, mutationResponse.StatusCode);

        using var forwarded = new HttpRequestMessage(HttpMethod.Get, host.Origin);
        forwarded.Headers.Host = "evil.example";
        forwarded.Headers.Add("X-Forwarded-Host", "wrighty.example");
        using var forwardedResponse = await client.SendAsync(forwarded);
        Assert.Equal(HttpStatusCode.BadRequest, forwardedResponse.StatusCode);

        using var nearbyHost = new HttpRequestMessage(
            HttpMethod.Get,
            $"{host.Origin}/?handler=Board");
        nearbyHost.Headers.Host = "other.wrighty.example";
        nearbyHost.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        using var nearbyHostResponse = await client.SendAsync(nearbyHost);
        Assert.Equal(HttpStatusCode.BadRequest, nearbyHostResponse.StatusCode);
        Assert.Equal("HOST_INVALID", await ProblemTitle(nearbyHostResponse));

        using var nearbyOrigin = new HttpRequestMessage(
            HttpMethod.Post,
            $"{host.Origin}/?handler=Claim");
        nearbyOrigin.Headers.Host = "wrighty.example";
        nearbyOrigin.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        nearbyOrigin.Headers.Add("Origin", "https://other.wrighty.example");
        nearbyOrigin.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = "local:3"
        });
        using var nearbyOriginResponse = await client.SendAsync(nearbyOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, nearbyOriginResponse.StatusCode);
        Assert.Equal("ORIGIN_INVALID", await ProblemTitle(nearbyOriginResponse));
        await host.Stop();
    }

    [Theory]
    [InlineData("ftp://wrighty.example")]
    [InlineData("https://user@wrighty.example")]
    [InlineData("https://wrighty.example/path")]
    [InlineData("https://wrighty.example?query=1")]
    [InlineData("https://wrighty.example/#fragment")]
    public void Endpoint_options_reject_invalid_public_urls(string publicUrl)
    {
        var exception = Assert.Throws<TrackerException>(() =>
            WebEndpointOptionsResolver.Resolve(
                new WebServerOptions(PublicUrl: publicUrl),
                []));

        Assert.Equal("WEB_PUBLIC_URL_INVALID", exception.Code);
    }

    [Theory]
    [InlineData("http://localhost:5000", "http://127.0.0.1:5000")]
    [InlineData(
        "http://localhost:5000/a/localhost?next=localhost#localhost",
        "http://127.0.0.1:5000/a/localhost?next=localhost#localhost")]
    [InlineData(
        "http://127.0.0.1:5000/a/localhost?next=localhost",
        "http://127.0.0.1:5000/a/localhost?next=localhost")]
    public void Listening_url_normalization_changes_only_the_localhost_authority(
        string address,
        string expected)
    {
        Assert.Equal(expected, WrightyWebServer.NormalizeListeningUrl(address));
    }

    [Fact]
    public async Task Unauthorized_htmx_request_returns_an_html_problem()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{host.Origin}/?handler=Board");
        request.Headers.Add("HX-Request", "true");

        var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<strong>AUTH_REQUIRED</strong>", html);
        await host.Stop();
    }

    [Fact]
    public async Task Mutation_requires_antiforgery_token()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{host.Origin}/?handler=Claim");
        request.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        request.Headers.Add("Origin", host.Origin);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["id"] = "local:3"
        });

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await host.Stop();
    }

    [Fact]
    public async Task Claim_from_web_is_attributed_to_a_human()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var claimResponse = await PostForm(client, host, "Claim", new()
        {
            ["id"] = "local:3"
        });
        Assert.Equal(HttpStatusCode.OK, claimResponse.StatusCode);

        using var boardRequest = new HttpRequestMessage(HttpMethod.Get, $"{host.Origin}/?handler=Board");
        boardRequest.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        var boardResponse = await client.SendAsync(boardRequest);
        var html = await boardResponse.Content.ReadAsStringAsync();
        Assert.Contains("Web claim item", html);
        Assert.Contains(">Human<", html);

        using var itemRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"{host.Origin}/?handler=Item&id=local%3A3");
        itemRequest.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        var itemResponse = await client.SendAsync(itemRequest);
        var itemHtml = await itemResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, itemResponse.StatusCode);
        Assert.Contains("<dt>Claimant type</dt><dd>Human</dd>", itemHtml);
        Assert.DoesNotContain("<dt>Agent</dt><dd>Codex</dd>", itemHtml);

        await host.Stop();
    }

    [Fact]
    public async Task Claim_from_web_after_expiry_preserves_local_agent_session()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        var runtimeStatePath = Path.Combine(directory, ".wrighty", ".wrighty-runtime-v1.json");
        var runtimeState = await File.ReadAllTextAsync(runtimeStatePath);
        var expired = System.Text.RegularExpressions.Regex.Replace(
            runtimeState,
            "\"expiresAt\": \"[^\"]+\"",
            "\"expiresAt\": \"2000-01-01T00:00:00+00:00\"");
        Assert.NotEqual(runtimeState, expired);
        await File.WriteAllTextAsync(runtimeStatePath, expired);

        using var claimResponse = await PostForm(client, host, "Claim", new()
        {
            ["id"] = "local:1"
        });
        var html = await claimResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, claimResponse.StatusCode);
        Assert.Contains("Claimed for editing. The recorded agent session was preserved.", html);
        Assert.Contains("Save and show manual Codex resume command", html);

        using var itemRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Item&id=local%3A1");
        using var itemResponse = await client.SendAsync(itemRequest);
        var itemHtml = await itemResponse.Content.ReadAsStringAsync();
        Assert.Contains("<dt>Claimant type</dt><dd>Human</dd>", itemHtml);
        Assert.Contains("Continue agent session", itemHtml);
        Assert.Contains("wrighty worker --item", itemHtml);
        // The durable session records a workspace, so the viewer shows the workspace tiles. The
        // recorded path is the store directory (not a linked worktree), so the git state is
        // reported as unavailable rather than crashing the view.
        Assert.Contains("Workspace path", itemHtml);
        Assert.Contains("Worktree status", itemHtml);
        var preservedState = await File.ReadAllTextAsync(runtimeStatePath);
        Assert.Contains("web-test-session", preservedState);

        await host.Stop();
    }

    [Fact]
    public async Task Claim_and_archive_archives_an_unclaimed_item_in_one_step()
    {
        var host = await StartServer();
        using var client = new HttpClient();

        using var response = await PostForm(client, host, "ClaimAndArchive", new()
        {
            ["id"] = "local:3"
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(
            "wrighty:refresh, wrighty:close-panel",
            Assert.Single(response.Headers.GetValues("HX-Trigger")));
        using var activeBoardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var activeBoard = await (await client.SendAsync(activeBoardRequest)).Content
            .ReadAsStringAsync();
        Assert.DoesNotContain("Web claim item", activeBoard);
        await host.Stop();
    }

    [Fact]
    public async Task Delete_is_offered_only_for_unprocessed_items_and_returns_to_the_board()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var eligibleRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Item&id=local%3A3");
        using var eligibleResponse = await client.SendAsync(eligibleRequest);
        var eligibleHtml = await eligibleResponse.Content.ReadAsStringAsync();
        using var processedRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Item&id=local%3A1");
        using var processedResponse = await client.SendAsync(processedRequest);
        var processedHtml = await processedResponse.Content.ReadAsStringAsync();

        Assert.Contains("handler=Delete", eligibleHtml);
        Assert.Contains("Permanently delete this item?", eligibleHtml);
        Assert.DoesNotContain("handler=Delete", processedHtml);

        using var deleteResponse = await PostForm(client, host, "Delete", new()
        {
            ["id"] = "local:3"
        });

        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.Equal(
            "wrighty:refresh, wrighty:close-panel",
            Assert.Single(deleteResponse.Headers.GetValues("HX-Trigger")));
        var (config, backend, id) = await StoredBackend("local:3");
        Assert.Null(await backend.GetAsync(config, id, CancellationToken.None));
        await host.Stop();
    }

    [Theory]
    [InlineData("save", "Saved. The claim remains active.")]
    [InlineData("save-release", "Saved and released.")]
    [InlineData("finish", "Saved and finished.")]
    [InlineData("release", "Draft discarded and claim released.")]
    public async Task Save_actions_apply_the_expected_claim_lifecycle(string action, string notice)
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var claimResponse = await PostForm(client, host, "Claim", new() { ["id"] = "local:3" });
        var claimHtml = await claimResponse.Content.ReadAsStringAsync();
        var revision = HiddenValue(claimHtml, "expectedRevision");
        var generation = HiddenValue(claimHtml, "expectedClaimGeneration");

        using var saveResponse = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:3",
            ["expectedRevision"] = revision,
            ["expectedClaimGeneration"] = generation,
            ["title"] = "Updated from web",
            ["body"] = "Updated body",
            ["status"] = "In Progress",
            ["priority"] = "P2",
            ["action"] = action
        });
        var html = await saveResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        Assert.Contains(notice, html);
        if (action != "release")
        {
            Assert.Contains("Updated from web", html);
        }

        await host.Stop();
    }

    [Fact]
    public async Task Save_accepts_an_empty_markdown_body()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var claimResponse = await PostForm(client, host, "Claim", new() { ["id"] = "local:3" });
        var claimHtml = await claimResponse.Content.ReadAsStringAsync();

        using var saveResponse = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:3",
            ["expectedRevision"] = HiddenValue(claimHtml, "expectedRevision"),
            ["expectedClaimGeneration"] = HiddenValue(claimHtml, "expectedClaimGeneration"),
            ["title"] = "Saved without a body",
            ["body"] = string.Empty,
            ["status"] = "Todo",
            ["priority"] = "P3",
            ["action"] = "save"
        });
        var html = await saveResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        Assert.Contains("Saved without a body", html);
        Assert.Contains("Saved. The claim remains active.", html);
        var itemPath = Assert.Single(Directory.GetFiles(
            Path.Combine(directory, ".wrighty", "items"),
            "*-saved-without-a-body.md"));
        var document = await File.ReadAllTextAsync(itemPath);
        Assert.DoesNotContain("\nBody", document);
        await host.Stop();
    }

    [Fact]
    public async Task Save_rejects_stale_revisions_and_preserves_the_submitted_draft()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var claimResponse = await PostForm(client, host, "Claim", new() { ["id"] = "local:3" });
        var claimHtml = await claimResponse.Content.ReadAsStringAsync();
        var revision = HiddenValue(claimHtml, "expectedRevision");
        var generation = HiddenValue(claimHtml, "expectedClaimGeneration");
        var values = new Dictionary<string, string>
        {
            ["id"] = "local:3",
            ["expectedRevision"] = revision,
            ["expectedClaimGeneration"] = generation,
            ["title"] = "First update",
            ["body"] = "First body",
            ["status"] = "In Progress",
            ["priority"] = "P2",
            ["action"] = "save"
        };
        using var firstSave = await PostForm(client, host, "Save", values);
        Assert.Equal(HttpStatusCode.OK, firstSave.StatusCode);

        values["title"] = "Conflicting draft";
        values["body"] = "Unsaved conflict body";
        using var conflict = await PostForm(client, host, "Save", values);
        var conflictHtml = await conflict.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Contains("Conflicting draft", conflictHtml);
        Assert.Contains("Unsaved conflict body", conflictHtml);
        await host.Stop();
    }

    [Fact]
    public async Task Edit_form_offers_the_execution_profile_only_when_the_repository_configures_one()
    {
        var host = await StartServer(workerConfig: new WorkerConfig
        {
            ExecutionProfiles = ["economy", "balanced", "deep"],
            DefaultExecutionProfile = "balanced"
        });
        using var client = new HttpClient();
        using var claimResponse = await PostForm(client, host, "Claim", new() { ["id"] = "local:3" });
        var claimHtml = await claimResponse.Content.ReadAsStringAsync();
        Assert.Contains("name=\"executionProfile\"", claimHtml);
        Assert.Contains(">deep</option>", claimHtml);
        Assert.Contains("Repository default", claimHtml);

        using var saveResponse = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:3",
            ["expectedRevision"] = HiddenValue(claimHtml, "expectedRevision"),
            ["expectedClaimGeneration"] = HiddenValue(claimHtml, "expectedClaimGeneration"),
            ["title"] = "Web claim item",
            ["body"] = "Body",
            ["status"] = "Todo",
            ["priority"] = "P3",
            ["executionProfile"] = "deep",
            ["action"] = "save"
        });
        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);

        var document = await File.ReadAllTextAsync(
            Path.Combine(directory, ".wrighty", "items", "003-web-claim-item.md"));
        Assert.Contains("profile: deep", document);
        await host.Stop();
    }

    [Fact]
    public async Task Edit_form_hides_the_execution_profile_when_no_profiles_are_configured()
    {
        // A repository that does not use profiles must see no new control at all.
        var host = await StartServer();
        using var client = new HttpClient();
        using var claimResponse = await PostForm(client, host, "Claim", new() { ["id"] = "local:3" });
        var claimHtml = await claimResponse.Content.ReadAsStringAsync();

        Assert.DoesNotContain("name=\"executionProfile\"", claimHtml);
        await host.Stop();
    }

    [Fact]
    public async Task Edit_form_sets_and_displays_managed_worker_eligibility_fields()
    {
        var host = await StartServer(
            workerConfig: new WorkerConfig { UseWorkerQueue = false });
        using var client = new HttpClient();
        using var claimResponse = await PostForm(client, host, "Claim", new() { ["id"] = "local:3" });
        var claimHtml = await claimResponse.Content.ReadAsStringAsync();
        Assert.Contains("name=\"automaticExecutionAllowed\"", claimHtml);
        Assert.Contains("name=\"agentPolicy\"", claimHtml);
        Assert.Contains("Workers ignore the item when this is off.", claimHtml);
        Assert.Contains("A worker-level", claimHtml);
        var revision = HiddenValue(claimHtml, "expectedRevision");
        var generation = HiddenValue(claimHtml, "expectedClaimGeneration");

        using var saveResponse = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:3",
            ["expectedRevision"] = revision,
            ["expectedClaimGeneration"] = generation,
            ["title"] = "Web claim item",
            ["body"] = "Body",
            ["status"] = "Todo",
            ["priority"] = "P3",
            ["automaticExecutionAllowed"] = "true",
            ["agentPolicy"] = "claude",
            ["action"] = "save"
        });
        var savedHtml = await saveResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        Assert.Contains("<dt>Automatic execution</dt><dd>Allowed</dd>", savedHtml);
        Assert.Contains("<dt>Agent</dt><dd>Claude</dd>", savedHtml);

        using var editRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Edit&id=local%3A3");
        var editResponse = await client.SendAsync(editRequest);
        var editHtml = await editResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, editResponse.StatusCode);
        Assert.Contains("name=\"automaticExecutionAllowed\"", editHtml);
        Assert.Contains("checked", editHtml);
        Assert.Contains("value=\"claude\" selected", editHtml);

        var itemPath = Path.Combine(directory, ".wrighty", "items", "003-web-claim-item.md");
        var document = await File.ReadAllTextAsync(itemPath);
        Assert.Contains("execution: automatic", document);
        Assert.Contains("agent: claude", document);
        await host.Stop();
    }

    [Fact]
    public async Task Edit_form_uses_status_instead_of_a_checkbox_when_queue_authorizes_execution()
    {
        var host = await StartServer(pickFrom: "Worker queue");
        using var client = new HttpClient();
        using var claimResponse = await PostForm(
            client,
            host,
            "Claim",
            new() { ["id"] = "local:3" });
        var claimHtml = await claimResponse.Content.ReadAsStringAsync();

        Assert.DoesNotContain(
            "type=\"checkbox\" name=\"automaticExecutionAllowed\"",
            claimHtml);
        Assert.Contains("Controlled by status.", claimHtml);
        Assert.Contains("Worker queue", claimHtml);

        using var saveResponse = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:3",
            ["expectedRevision"] = HiddenValue(claimHtml, "expectedRevision"),
            ["expectedClaimGeneration"] = HiddenValue(claimHtml, "expectedClaimGeneration"),
            ["title"] = "Web claim item",
            ["body"] = "Body",
            ["status"] = "Worker queue",
            ["priority"] = "P3",
            ["action"] = "save"
        });
        var savedHtml = await saveResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        Assert.Contains("<dt>Automatic execution</dt><dd>Allowed</dd>", savedHtml);
        await host.Stop();
    }

    [Fact]
    public async Task Save_rejects_oversized_markdown_and_preserves_the_draft()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var claimResponse = await PostForm(client, host, "Claim", new() { ["id"] = "local:3" });
        var claimHtml = await claimResponse.Content.ReadAsStringAsync();
        var revision = HiddenValue(claimHtml, "expectedRevision");
        var generation = HiddenValue(claimHtml, "expectedClaimGeneration");
        var oversizedBody = new string('x', 1_000_001);

        using var response = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:3",
            ["expectedRevision"] = revision,
            ["expectedClaimGeneration"] = generation,
            ["title"] = "Oversized draft",
            ["body"] = oversizedBody,
            ["status"] = "Todo",
            ["action"] = "save"
        });
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Markdown body must not exceed 1,000,000 characters.", html);
        Assert.Contains("Oversized draft", html);
        await host.Stop();
    }

    [Fact]
    public async Task Archive_scopes_and_unarchive_round_trip_an_item()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var claim = await PostForm(client, host, "Claim", new() { ["id"] = "local:3" });
        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);

        using var archive = await PostForm(client, host, "Archive", new() { ["id"] = "local:3" });
        Assert.Equal(HttpStatusCode.NoContent, archive.StatusCode);
        Assert.Equal(
            "wrighty:refresh, wrighty:close-panel",
            Assert.Single(archive.Headers.GetValues("HX-Trigger")));

        foreach (var scope in new[] { "archived", "all" })
        {
            using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board&scope={scope}");
            var board = await client.SendAsync(boardRequest);
            Assert.Contains("Web claim item", await board.Content.ReadAsStringAsync());
        }

        using var unarchive = await PostForm(client, host, "Unarchive", new() { ["id"] = "local:3" });
        Assert.Equal(HttpStatusCode.OK, unarchive.StatusCode);
        Assert.Contains("Restored to the active board.", await unarchive.Content.ReadAsStringAsync());
        await host.Stop();
    }

    [Fact]
    public async Task Missing_items_return_a_not_found_web_error()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Item&id=local%3A999");

        var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("WORK_ITEM_NOT_FOUND", html);
        await host.Stop();
    }

    [Fact]
    public async Task Missing_items_are_mapped_consistently_across_handler_fallbacks()
    {
        var host = await StartServer(protectNonHumanClaims: false);
        using var client = new HttpClient();

        using var editRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Edit&id=local%3A999");
        var edit = await client.SendAsync(editRequest);
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        Assert.Contains("WORK_ITEM_NOT_FOUND", await edit.Content.ReadAsStringAsync());

        foreach (var handler in new[] { "Claim", "Release" })
        {
            using var response = await PostForm(client, host, handler, new() { ["id"] = "local:999" });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("WORK_ITEM_NOT_FOUND", await response.Content.ReadAsStringAsync());
        }

        await host.Stop();
    }

    [Fact]
    public async Task Invalid_updates_return_the_edit_form_with_the_submitted_values()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var claimResponse = await PostForm(client, host, "Claim", new() { ["id"] = "local:3" });
        var claimHtml = await claimResponse.Content.ReadAsStringAsync();
        var revision = HiddenValue(claimHtml, "expectedRevision");
        var generation = HiddenValue(claimHtml, "expectedClaimGeneration");

        using var response = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:3",
            ["expectedRevision"] = revision,
            ["expectedClaimGeneration"] = generation,
            ["title"] = "Invalid priority draft",
            ["body"] = "Keep this body",
            ["status"] = "Todo",
            ["priority"] = "not-configured",
            ["action"] = "save"
        });
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ARGUMENT_INVALID", html);
        Assert.Contains("Invalid priority draft", html);
        Assert.Contains("Keep this body", html);
        await host.Stop();
    }

    [Fact]
    public async Task Dashboard_reports_invalid_documents_without_exposing_the_store_path()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        Assert.Equal(
            "Press Ctrl+C to stop.",
            await host.Output.ReadLineAsync(host.Cancellation.Token));
        var itemPath = Directory.EnumerateFiles(
            Path.Combine(directory, ".wrighty"),
            "*.md",
            SearchOption.AllDirectories).First();
        await File.WriteAllTextAsync(itemPath, "---\ninvalid: [\n---\ncorrupt");
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");

        var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("WORK_ITEM_DOCUMENT_INVALID", html);
        Assert.Contains("&lt;tracker&gt;", html);
        Assert.DoesNotContain(directory, html);
        var log = await host.Output.ReadLineAsync(host.Cancellation.Token);
        Assert.Contains("GET /?handler=Board -> 500 WORK_ITEM_DOCUMENT_INVALID", log);
        Assert.Contains("TrackerException", log);
        Assert.Contains(itemPath, log);
        await host.Stop();
    }

    [Fact]
    public async Task Dashboard_rejects_legacy_claim_frontmatter_without_migration()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        var itemPath = Path.Combine(
            directory,
            ".wrighty",
            "items",
            "008-unassigned-status.md");
        var document = await File.ReadAllTextAsync(itemPath);
        await File.WriteAllTextAsync(itemPath, document.Replace(
            "updatedAt:",
            """
            claimEpoch: 1
            claim:
              workerIdentity: legacy-worker
              claimantKind: human
              claimAttemptId: legacy-attempt
              claimedAt: 2000-01-01T00:00:00.0000000Z
              expiresAt: 2000-01-01T01:00:00.0000000Z
            updatedAt:
            """.ReplaceLineEndings("\n").TrimEnd('\n'),
            StringComparison.Ordinal));
        using var request = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Board");

        var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("STORE_SCHEMA_UNSUPPORTED", html);
        Assert.Contains("Remove or rename the listed file", html);
        await host.Stop();
    }

    [Theory]
    [InlineData("4", "Agent", "Copilot")]
    [InlineData("5", "Agent", "Other")]
    [InlineData("6", "Automation", null)]
    [InlineData("7", "Unknown", null)]
    public async Task Item_details_label_supported_claimant_metadata(
        string id,
        string claimantKind,
        string? agentType)
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Item&id=local%3A{id}");

        var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"<dt>Claimant type</dt><dd>{claimantKind}</dd>", html);
        if (agentType is not null)
        {
            Assert.Contains($"<dt>Agent</dt><dd>{agentType}</dd>", html);
        }
        await host.Stop();
    }

    [Fact]
    public async Task Non_human_claim_protection_is_visible_and_enforced_by_handlers()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var itemRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Item&id=local%3A1");
        var itemResponse = await client.SendAsync(itemRequest);
        var itemHtml = await itemResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, itemResponse.StatusCode);
        Assert.Contains("Take over for editing…", itemHtml);
        Assert.Contains("Queue for worker", itemHtml);
        Assert.Contains("Release existing claim…", itemHtml);
        Assert.Contains("recorded agent session remains available", itemHtml);
        Assert.Contains("headless process has exited", itemHtml);
        Assert.DoesNotContain("Takeover does not stop that process", itemHtml);
        Assert.DoesNotContain(">Edit</button>", itemHtml);
        Assert.DoesNotContain(">Release</button>", itemHtml);
        Assert.DoesNotContain(">Archive</button>", itemHtml);

        using var editRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Edit&id=local%3A1");
        var editResponse = await client.SendAsync(editRequest);
        Assert.Equal(HttpStatusCode.Conflict, editResponse.StatusCode);
        Assert.Contains("CLAIM_STALE", await editResponse.Content.ReadAsStringAsync());

        foreach (var handler in new[] { "Claim", "Release", "Archive" })
        {
            using var response = await PostForm(client, host, handler, new() { ["id"] = "local:1" });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains(handler == "Claim" ? "CLAIM_HELD_BY_LOCAL_CLAIMANT" : "CLAIM_STALE", await response.Content.ReadAsStringAsync());
        }

        using var saveResponse = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:1",
            ["expectedRevision"] = "stale",
            ["title"] = "Blocked",
            ["body"] = "Blocked",
            ["status"] = "Todo",
            ["action"] = "save"
        });
        Assert.Equal(HttpStatusCode.Conflict, saveResponse.StatusCode);
        Assert.Contains("CLAIM_STALE", await saveResponse.Content.ReadAsStringAsync());

        await host.Stop();
    }

    [Fact]
    public async Task Paused_agent_item_can_be_queued_directly_without_opening_the_editor()
    {
        var host = await StartServer();
        using var client = new HttpClient();

        using var queued = await PostForm(client, host, "QueueForWorker", new()
        {
            ["id"] = "local:1"
        });
        var html = await queued.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        Assert.Contains("Queued. A continuous worker can now resume the recorded session.", html);
        Assert.Contains("Resume queued", html);
        Assert.Contains("<dt>Operational status</dt><dd>queued</dd>", html);
        Assert.Contains("Claim for editing", html);
        Assert.DoesNotContain("Take over for editing", html);
        Assert.DoesNotContain("Queue for worker", html);

        using var stale = await PostForm(client, host, "QueueForWorker", new()
        {
            ["id"] = "local:1"
        });
        var staleHtml = await stale.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Contains("WORKER_ITEM_NOT_PAUSED", staleHtml);

        await host.Stop();
    }

    [Fact]
    public async Task Expired_paused_agent_item_can_be_queued_directly()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        var runtimeStatePath = Path.Combine(directory, ".wrighty", ".wrighty-runtime-v1.json");
        var runtimeState = await File.ReadAllTextAsync(runtimeStatePath);
        var expired = System.Text.RegularExpressions.Regex.Replace(
            runtimeState,
            "\"expiresAt\": \"[^\"]+\"",
            "\"expiresAt\": \"2000-01-01T00:00:00+00:00\"");
        await File.WriteAllTextAsync(runtimeStatePath, expired);

        using var itemRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Item&id=local%3A1");
        using var itemResponse = await client.SendAsync(itemRequest);
        var itemHtml = await itemResponse.Content.ReadAsStringAsync();
        Assert.Contains("Queue for worker", itemHtml);
        Assert.Contains("Claim for editing", itemHtml);

        using var queued = await PostForm(client, host, "QueueForWorker", new()
        {
            ["id"] = "local:1"
        });
        var html = await queued.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        Assert.Contains("Resume queued", html);
        Assert.Contains("<dt>Operational status</dt><dd>queued</dd>", html);

        await host.Stop();
    }

    [Fact]
    public async Task Claim_fencing_cannot_be_disabled_by_the_legacy_display_setting()
    {
        var host = await StartServer(protectNonHumanClaims: false);
        using var client = new HttpClient();
        using var itemRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Item&id=local%3A1");
        var itemResponse = await client.SendAsync(itemRequest);
        var itemHtml = await itemResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, itemResponse.StatusCode);
        Assert.Contains("Take over for editing…", itemHtml);
        Assert.DoesNotContain(">Edit</button>", itemHtml);

        using var editRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Edit&id=local%3A1");
        var editResponse = await client.SendAsync(editRequest);
        Assert.Equal(HttpStatusCode.Conflict, editResponse.StatusCode);
        Assert.Contains("CLAIM_STALE", await editResponse.Content.ReadAsStringAsync());

        await host.Stop();
    }

    [Fact]
    public async Task Agent_claim_requires_confirmed_takeover_before_editor_opens()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var beforeRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Item&id=local%3A1");
        using var before = await client.SendAsync(beforeRequest);
        var beforeHtml = await before.Content.ReadAsStringAsync();
        Assert.Contains("Claimant type</dt><dd>Agent", beforeHtml);
        Assert.Contains("Agent</dt><dd>Codex", beforeHtml);
        Assert.DoesNotContain(">Edit</button>", beforeHtml);
        Assert.Contains(
            "data-confirm-title=\"Take over the paused session for editing?\"",
            beforeHtml);
        Assert.Contains("data-confirm-action=\"Take over\"", beforeHtml);

        using var takeover = await PostForm(client, host, "Takeover", new() { ["id"] = "local:1" });
        var html = await takeover.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, takeover.StatusCode);
        Assert.Contains("Takeover complete", html);
        Assert.Contains("Edit work item", html);
        Assert.Contains("expectedClaimGeneration", html);
        Assert.DoesNotContain("Resume agent session", html);
        Assert.DoesNotContain("WRIGHTY_CLAIM_TOKEN=", html);
        Assert.Contains("Save and show manual Codex resume command", html);
        Assert.Contains("Save and resume automatically", html);
        Assert.Contains("actions edit-actions", html);
        Assert.Contains("More actions…", html);
        Assert.Contains("Save and release", html);
        Assert.Contains("Release without saving", html);
        Assert.Contains(
            "data-tooltip=\"Save these changes and queue this session. A running continuous " +
            "worker will resume it. To continue it yourself, use “Save and show manual Codex " +
            "resume command” under More actions.\"",
            html);
        Assert.Contains(
            "data-tooltip=\"Save these changes and show a command; this does not start Codex. " +
            "For automatic continuation by a running worker, use “Save and resume " +
            "automatically.”\"",
            html);
        var manualResume = html.IndexOf("value=\"save-handback\"", StringComparison.Ordinal);
        var actionsMenuEnd = html.IndexOf("</details>", manualResume, StringComparison.Ordinal);
        var automaticResume = html.IndexOf("value=\"save-queue\"", StringComparison.Ordinal);
        Assert.True(manualResume >= 0);
        Assert.True(actionsMenuEnd > manualResume);
        Assert.True(automaticResume > actionsMenuEnd);
        Assert.DoesNotContain(
            "class=\"primary has-tooltip\" name=\"action\" value=\"save-handback\"",
            html);
        Assert.True(
            html.IndexOf("actions-secondary", StringComparison.Ordinal) <
            html.IndexOf("actions-primary", StringComparison.Ordinal));
        Assert.Contains(
            "data-confirm-title=\"Save changes and release this claim?\"",
            html);
        Assert.Contains("data-confirm-action=\"Save and release\"", html);
        Assert.DoesNotContain("onclick=", html);
        Assert.DoesNotContain("onsubmit=", html);
        await host.Stop();
    }

    [Fact]
    public async Task Web_takeover_plain_save_stays_human_and_preserves_address_for_handback()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var takeover = await PostForm(client, host, "Takeover", new() { ["id"] = "local:1" });
        var takeoverHtml = await takeover.Content.ReadAsStringAsync();
        var revision = HiddenValue(takeoverHtml, "expectedRevision");
        var generation = HiddenValue(takeoverHtml, "expectedClaimGeneration");

        using var save = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:1",
            ["expectedRevision"] = revision,
            ["expectedClaimGeneration"] = generation,
            ["title"] = "Clarified item",
            ["body"] = "Actionable body",
            ["status"] = "In Progress",
            ["priority"] = "P1",
            ["automaticExecutionAllowed"] = "true",
            ["action"] = "save"
        });
        var savedHtml = await save.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.Contains("Saved. The claim remains active.", savedHtml);
        Assert.Contains("Claimant type</dt><dd>Human", savedHtml);
        Assert.Contains("Continue agent session", savedHtml);
        Assert.Contains("<details class=\"resume-address\" data-copy-scope>", savedHtml);
        Assert.Contains("2 options", savedHtml);
        Assert.Contains("id=\"session-open-desktop-codex\"", savedHtml);
        Assert.Contains("Wrighty will keep your human claim while you work.", savedHtml);
        Assert.Contains("Headless worker", savedHtml);
        Assert.Contains("wrighty worker --item", savedHtml);
        Assert.Contains("--resume --yes", savedHtml);
        Assert.Contains("WRIGHTY_CONFIG_PATH=", savedHtml);
        Assert.Contains("WRIGHTY_CLAIM_TOKEN=", savedHtml);
        Assert.Contains("data-copy-target=\"headless-resume-command\"", savedHtml);
        Assert.DoesNotContain("codex resume", savedHtml);
        Assert.Contains("Select", savedHtml);
        Assert.Contains("then open", savedHtml);
        Assert.Contains("Save and show manual Codex resume command", savedHtml);
        Assert.Contains("Release claim", savedHtml);
        Assert.Contains(">Queue for worker</button>", savedHtml);

        using var editRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Edit&id=local%3A1");
        using var edit = await client.SendAsync(editRequest);
        var editHtml = await edit.Content.ReadAsStringAsync();
        using var release = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:1",
            ["expectedRevision"] = HiddenValue(editHtml, "expectedRevision"),
            ["expectedClaimGeneration"] = HiddenValue(editHtml, "expectedClaimGeneration"),
            ["title"] = "Clarified item",
            ["body"] = "Actionable body",
            ["status"] = "In Progress",
            ["priority"] = "P1",
            ["automaticExecutionAllowed"] = "true",
            ["action"] = "save-release"
        });
        var releasedHtml = await release.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        Assert.Contains("Saved and released.", releasedHtml);
        Assert.DoesNotContain("Resume agent session", releasedHtml);
        Assert.DoesNotContain("WRIGHTY_CLAIM_TOKEN=", releasedHtml);
        Assert.Contains("Claim for editing", releasedHtml);
        await host.Stop();
    }

    [Fact]
    public async Task Web_save_and_queue_ends_human_claim_and_preserves_session_for_worker()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var takeover = await PostForm(client, host, "Takeover", new() { ["id"] = "local:1" });
        var takeoverHtml = await takeover.Content.ReadAsStringAsync();

        using var queued = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:1",
            ["expectedRevision"] = HiddenValue(takeoverHtml, "expectedRevision"),
            ["expectedClaimGeneration"] = HiddenValue(takeoverHtml, "expectedClaimGeneration"),
            ["title"] = "Clarified item",
            ["body"] = "Actionable body",
            ["status"] = "In Progress",
            ["priority"] = "P1",
            ["automaticExecutionAllowed"] = "true",
            ["agentPolicy"] = "codex",
            ["action"] = "save-queue"
        });
        var html = await queued.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        Assert.Contains("Saved and queued", html);
        Assert.Contains("Resume queued", html);
        Assert.Contains("Claim for editing", html);
        Assert.Contains("<dt>Operational status</dt><dd>queued</dd>", html);
        Assert.DoesNotContain("WRIGHTY_CLAIM_TOKEN=", html);

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        using var board = await client.SendAsync(boardRequest);
        var boardHtml = await board.Content.ReadAsStringAsync();
        Assert.Contains("activity-queued", boardHtml);
        Assert.Contains("Resume queued", boardHtml);
        await host.Stop();
    }

    [Fact]
    public async Task Retry_scheduled_editor_locks_agent_and_rejects_forged_agent_change()
    {
        var host = await StartServer(scheduleRetry: true);
        using var client = new HttpClient();
        using var claim = await PostForm(client, host, "Claim", new() { ["id"] = "local:1" });
        var claimHtml = await claim.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, claim.StatusCode);
        Assert.Contains("agent-policy-locked-help", claimHtml);
        Assert.Contains("<select aria-describedby=\"agent-policy-locked-help\" disabled>", claimHtml);
        Assert.Contains("name=\"agentPolicy\" value=\"codex\"", claimHtml);
        Assert.Contains("wrighty worker --item local:1 --handoff --agent AGENT --yes", claimHtml);

        using var changedAgent = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:1",
            ["expectedRevision"] = HiddenValue(claimHtml, "expectedRevision"),
            ["expectedClaimGeneration"] = HiddenValue(claimHtml, "expectedClaimGeneration"),
            ["title"] = "Hostile item",
            ["body"] = "Body",
            ["status"] = "In Progress",
            ["priority"] = "P1",
            ["automaticExecutionAllowed"] = "true",
            ["agentPolicy"] = "claude",
            ["action"] = "save"
        });
        var changedHtml = await changedAgent.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, changedAgent.StatusCode);
        Assert.Contains("AGENT_HANDOFF_REQUIRED", changedHtml);
        Assert.Contains("scheduled retry belongs to codex", changedHtml);

        using var itemRequest = AuthenticatedGet(
            host, $"{host.Origin}/?handler=Item&id=local%3A1");
        using var item = await client.SendAsync(itemRequest);
        var itemHtml = await item.Content.ReadAsStringAsync();
        Assert.Contains("<dt>Agent</dt><dd>Codex</dd>", itemHtml);
        Assert.Contains("Retry scheduled", itemHtml);

        await host.Stop();
    }

    [Fact]
    public async Task Retry_scheduled_release_without_saving_preserves_timer_and_dispatch()
    {
        var host = await StartServer(scheduleRetry: true);
        using var client = new HttpClient();
        using var claim = await PostForm(client, host, "Claim", new() { ["id"] = "local:1" });
        var claimHtml = await claim.Content.ReadAsStringAsync();

        using var release = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:1",
            ["expectedRevision"] = HiddenValue(claimHtml, "expectedRevision"),
            ["expectedClaimGeneration"] = HiddenValue(claimHtml, "expectedClaimGeneration"),
            ["title"] = "Ignored draft",
            ["body"] = "Ignored body",
            ["status"] = "In Progress",
            ["priority"] = "P1",
            ["agentPolicy"] = "codex",
            ["action"] = "release"
        });
        var html = await release.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, release.StatusCode);
        Assert.Contains("scheduled retry preserved", html);
        Assert.Contains("Retry scheduled", html);
        Assert.Contains("<dt>State</dt><dd>Unclaimed</dd>", html);
        Assert.Equal(
            "retry-scheduled",
            await RuntimeDispatchState());

        await host.Stop();
    }

    [Fact]
    public async Task Retry_scheduled_save_and_release_preserves_updated_item_and_timer()
    {
        var host = await StartServer(scheduleRetry: true);
        using var client = new HttpClient();
        using var claim = await PostForm(client, host, "Claim", new() { ["id"] = "local:1" });
        var claimHtml = await claim.Content.ReadAsStringAsync();

        using var save = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:1",
            ["expectedRevision"] = HiddenValue(claimHtml, "expectedRevision"),
            ["expectedClaimGeneration"] = HiddenValue(claimHtml, "expectedClaimGeneration"),
            ["title"] = "Clarified while waiting",
            ["body"] = "Updated instructions",
            ["status"] = "In Progress",
            ["priority"] = "P2",
            ["automaticExecutionAllowed"] = "true",
            ["agentPolicy"] = "codex",
            ["action"] = "save-release"
        });
        var html = await save.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.Contains("Saved and released. The scheduled retry was preserved.", html);
        Assert.Contains("Clarified while waiting", html);
        Assert.Contains("Retry scheduled", html);
        Assert.Equal(
            "retry-scheduled",
            await RuntimeDispatchState());

        await host.Stop();
    }

    [Fact]
    public async Task Retry_scheduled_disabling_worker_cancels_timer_and_clears_dispatch()
    {
        var host = await StartServer(
            scheduleRetry: true,
            workerConfig: new WorkerConfig { UseWorkerQueue = false });
        using var client = new HttpClient();
        using var claim = await PostForm(client, host, "Claim", new() { ["id"] = "local:1" });
        var claimHtml = await claim.Content.ReadAsStringAsync();

        using var save = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:1",
            ["expectedRevision"] = HiddenValue(claimHtml, "expectedRevision"),
            ["expectedClaimGeneration"] = HiddenValue(claimHtml, "expectedClaimGeneration"),
            ["title"] = "Manual continuation",
            ["body"] = "Updated instructions",
            ["status"] = "In Progress",
            ["priority"] = "P1",
            ["agentPolicy"] = "codex",
            ["action"] = "save-release"
        });
        var html = await save.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.Contains("Saved and released.", html);
        Assert.DoesNotContain("Retry scheduled", html);
        Assert.Contains("<dt>Automatic execution</dt><dd>Manual only</dd>", html);
        Assert.Null(await RuntimeDispatchState());

        await host.Stop();
    }

    [Fact]
    public async Task Retry_scheduled_save_and_queue_overrides_timer_and_clears_dispatch()
    {
        var host = await StartServer(scheduleRetry: true);
        using var client = new HttpClient();
        using var claim = await PostForm(client, host, "Claim", new() { ["id"] = "local:1" });
        var claimHtml = await claim.Content.ReadAsStringAsync();

        using var queue = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:1",
            ["expectedRevision"] = HiddenValue(claimHtml, "expectedRevision"),
            ["expectedClaimGeneration"] = HiddenValue(claimHtml, "expectedClaimGeneration"),
            ["title"] = "Retry now",
            ["body"] = "Updated instructions",
            ["status"] = "In Progress",
            ["priority"] = "P1",
            ["automaticExecutionAllowed"] = "true",
            ["agentPolicy"] = "codex",
            ["action"] = "save-queue"
        });
        var html = await queue.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, queue.StatusCode);
        Assert.Contains("Saved and queued", html);
        Assert.Contains("Resume queued", html);
        Assert.DoesNotContain("Retry scheduled", html);
        Assert.Null(await RuntimeDispatchState());

        await host.Stop();
    }

    [Fact]
    public async Task Web_save_and_handback_rotates_to_agent_before_showing_resume_command()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var takeover = await PostForm(client, host, "Takeover", new() { ["id"] = "local:1" });
        var takeoverHtml = await takeover.Content.ReadAsStringAsync();

        using var handback = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:1",
            ["expectedRevision"] = HiddenValue(takeoverHtml, "expectedRevision"),
            ["expectedClaimGeneration"] = HiddenValue(takeoverHtml, "expectedClaimGeneration"),
            ["title"] = "Clarified item",
            ["body"] = "Actionable body",
            ["status"] = "In Progress",
            ["priority"] = "P1",
            ["action"] = "save-handback"
        });
        var html = await handback.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, handback.StatusCode);
        Assert.Contains("Saved. Use the command below to resume Codex manually.", html);
        Assert.Contains("Claimant type</dt><dd>Agent", html);
        Assert.Contains("Agent</dt><dd>Codex", html);
        Assert.Contains("agent:web-handback:", html);
        Assert.Contains("Continue agent session", html);
        Assert.Contains("<details class=\"resume-address\" data-copy-scope>", html);
        Assert.Contains("3 options", html);
        Assert.Contains("id=\"session-open-cli-codex\"", html);
        Assert.Contains("Open Codex Desktop:", html);
        Assert.Contains("Take over as human before opening Desktop", html);
        Assert.Contains("Interactive", html);
        Assert.Contains("codex resume", html);
        Assert.Contains("web-test-session", html);
        Assert.Contains("WRIGHTY_CONFIG_PATH=", html);
        Assert.Contains("WRIGHTY_CLAIMANT_ID=", html);
        Assert.Contains("WRIGHTY_CLAIM_TOKEN=", html);
        Assert.Contains("Headless worker", html);
        Assert.Contains("wrighty worker --item", html);
        Assert.Contains("--resume --yes", html);
        Assert.Contains("data-copy-target=\"interactive-resume-command\"", html);
        Assert.Contains("data-copy-target=\"interactive-resume-prompt\"", html);
        Assert.Contains("data-copy-target=\"headless-resume-command\"", html);
        Assert.Contains("$wrighty Item local:1 has been clarified.", html);
        Assert.DoesNotContain(">Edit</button>", html);

        await host.Stop();
    }

    [Fact]
    public async Task Agent_claim_without_a_web_handle_names_the_visible_cli_handoff_controls()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var request = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Item&id=local%3A1");
        using var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Take over for editing…", html);
        Assert.Contains("then open", html);
        Assert.Contains("Save and show manual Codex resume command", html);
        Assert.DoesNotContain("Hand the item back to the agent", html);

        await host.Stop();
    }

    [Fact]
    public async Task Human_owned_session_can_be_opened_in_desktop_from_a_fresh_page_generation()
    {
        var launcher = new RecordingAgentSessionLauncher();
        var host = await StartServer(sessionLauncher: launcher);
        using var client = new HttpClient();
        using var takeover = await PostForm(
            client, host, "Takeover", new() { ["id"] = "local:1" });
        var takeoverHtml = await takeover.Content.ReadAsStringAsync();
        using var save = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:1",
            ["expectedRevision"] = HiddenValue(takeoverHtml, "expectedRevision"),
            ["expectedClaimGeneration"] =
                HiddenValue(takeoverHtml, "expectedClaimGeneration"),
            ["title"] = "Clarified item",
            ["body"] = "Actionable body",
            ["status"] = "In Progress",
            ["priority"] = "P1",
            ["automaticExecutionAllowed"] = "true",
            ["action"] = "save"
        });
        var savedHtml = await save.Content.ReadAsStringAsync();

        using var launch = await PostForm(client, host, "LaunchAgentDesktop", new()
        {
            ["id"] = "local:1",
            ["expectedSessionId"] = HiddenValue(savedHtml, "expectedSessionId"),
            ["expectedSessionGeneration"] =
                HiddenValue(savedHtml, "expectedSessionGeneration")
        });
        var html = await launch.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, launch.StatusCode);
        Assert.Contains(
            "Your human claim remains active; stop or idle Desktop before handing back",
            html);
        Assert.Equal("codex://threads/web-test-session", launcher.DesktopAddress?.Uri?.AbsoluteUri);
        Assert.Equal("codex", launcher.DesktopAddress?.Vendor);
        await host.Stop();
    }

    [Fact]
    public async Task Claude_desktop_stays_disabled_when_a_repository_turns_it_off()
    {
        // Enabled by default now, so the case worth holding is the withdrawal: a repository that
        // sets the key to "off" gets the route refused, with the reason still stated.
        const string sessionId = "940cd4c6-bb95-84d8-a78a-73af49c898a0";
        var host = await StartServer(
            sessionAgent: "claude",
            sessionId: sessionId,
            workerConfig: new WorkerConfig
            {
                DesktopSessions = new WorkerDesktopSessionsConfig { Claude = "off" }
            });
        using var client = new HttpClient();
        using var takeover = await PostForm(
            client, host, "Takeover", new() { ["id"] = "local:1" });
        var takeoverHtml = await takeover.Content.ReadAsStringAsync();
        using var save = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:1",
            ["expectedRevision"] = HiddenValue(takeoverHtml, "expectedRevision"),
            ["expectedClaimGeneration"] =
                HiddenValue(takeoverHtml, "expectedClaimGeneration"),
            ["title"] = "Clarified item",
            ["body"] = "Actionable body",
            ["status"] = "In Progress",
            ["priority"] = "P1",
            ["action"] = "save"
        });
        var html = await save.Content.ReadAsStringAsync();

        Assert.DoesNotContain("id=\"session-open-desktop-claude\"", html);
        Assert.Contains(
            "Opening this recorded session in Claude Desktop is experimental and is not enabled.",
            html);
        await host.Stop();
    }

    [Fact]
    public async Task Copilot_desktop_is_available_with_its_settings_and_compatibility_note()
    {
        const string sessionId = "fd889d8b-70b8-4803-a480-8bd638a59778";
        var launcher = new RecordingAgentSessionLauncher();
        var host = await StartServer(
            sessionLauncher: launcher,
            sessionAgent: "copilot",
            sessionId: sessionId);
        using var client = new HttpClient();
        using var takeover = await PostForm(
            client, host, "Takeover", new() { ["id"] = "local:1" });
        var takeoverHtml = await takeover.Content.ReadAsStringAsync();
        using var save = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:1",
            ["expectedRevision"] = HiddenValue(takeoverHtml, "expectedRevision"),
            ["expectedClaimGeneration"] =
                HiddenValue(takeoverHtml, "expectedClaimGeneration"),
            ["title"] = "Clarified item",
            ["body"] = "Actionable body",
            ["status"] = "In Progress",
            ["priority"] = "P1",
            ["action"] = "save"
        });
        var html = await save.Content.ReadAsStringAsync();

        Assert.Contains("id=\"session-open-desktop-copilot\"", html);
        Assert.Contains("id=\"session-desktop-note-copilot\"", html);
        Assert.Contains("Show Copilot CLI Session", html);
        Assert.Contains("change Off to a retention period", html);
        Assert.Contains("may open Home instead of the recorded CLI session", html);

        using var launch = await PostForm(client, host, "LaunchAgentDesktop", new()
        {
            ["id"] = "local:1",
            ["expectedSessionId"] = HiddenValue(html, "expectedSessionId"),
            ["expectedSessionGeneration"] = HiddenValue(html, "expectedSessionGeneration")
        });

        Assert.Equal(HttpStatusCode.OK, launch.StatusCode);
        Assert.Equal(
            $"ghapp://sessions/{sessionId}",
            launcher.DesktopAddress?.Uri?.AbsoluteUri);
        Assert.Equal(DesktopSessionSupport.Supported, launcher.DesktopAddress?.Support);
        Assert.True(launcher.DesktopAddress?.Enabled);
        await host.Stop();
    }

    [Fact]
    public async Task Claude_desktop_can_be_opened_after_the_explicit_experimental_opt_in()
    {
        const string sessionId = "940cd4c6-bb95-84d8-a78a-73af49c898a0";
        var launcher = new RecordingAgentSessionLauncher();
        var host = await StartServer(
            sessionLauncher: launcher,
            workerConfig: new WorkerConfig
            {
                DesktopSessions = new WorkerDesktopSessionsConfig
                {
                    Claude = "experimental"
                }
            },
            sessionAgent: "claude",
            sessionId: sessionId);
        using var client = new HttpClient();
        using var takeover = await PostForm(
            client, host, "Takeover", new() { ["id"] = "local:1" });
        var takeoverHtml = await takeover.Content.ReadAsStringAsync();
        using var save = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:1",
            ["expectedRevision"] = HiddenValue(takeoverHtml, "expectedRevision"),
            ["expectedClaimGeneration"] =
                HiddenValue(takeoverHtml, "expectedClaimGeneration"),
            ["title"] = "Clarified item",
            ["body"] = "Actionable body",
            ["status"] = "In Progress",
            ["priority"] = "P1",
            ["action"] = "save"
        });
        var savedHtml = await save.Content.ReadAsStringAsync();

        Assert.Contains("id=\"session-open-desktop-claude\"", savedHtml);
        Assert.Contains("Open Claude Desktop", savedHtml);
        Assert.Contains("(experimental)", savedHtml);

        using var launch = await PostForm(client, host, "LaunchAgentDesktop", new()
        {
            ["id"] = "local:1",
            ["expectedSessionId"] = HiddenValue(savedHtml, "expectedSessionId"),
            ["expectedSessionGeneration"] =
                HiddenValue(savedHtml, "expectedSessionGeneration")
        });

        Assert.Equal(HttpStatusCode.OK, launch.StatusCode);
        Assert.Equal(
            $"claude://resume?session={sessionId}",
            launcher.DesktopAddress?.Uri?.OriginalString);
        Assert.Equal(DesktopSessionSupport.Experimental, launcher.DesktopAddress?.Support);
        Assert.True(launcher.DesktopAddress?.Enabled);
        await host.Stop();
    }

    [Fact]
    public async Task Agent_owned_session_can_be_opened_in_a_new_cli_terminal_with_exact_claim()
    {
        var launcher = new RecordingAgentSessionLauncher();
        var host = await StartServer(sessionLauncher: launcher);
        using var client = new HttpClient();
        using var takeover = await PostForm(
            client, host, "Takeover", new() { ["id"] = "local:1" });
        var takeoverHtml = await takeover.Content.ReadAsStringAsync();
        using var handback = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:1",
            ["expectedRevision"] = HiddenValue(takeoverHtml, "expectedRevision"),
            ["expectedClaimGeneration"] =
                HiddenValue(takeoverHtml, "expectedClaimGeneration"),
            ["title"] = "Clarified item",
            ["body"] = "Actionable body",
            ["status"] = "In Progress",
            ["priority"] = "P1",
            ["action"] = "save-handback"
        });
        var handbackHtml = await handback.Content.ReadAsStringAsync();

        using var launch = await PostForm(client, host, "LaunchAgentCli", new()
        {
            ["id"] = "local:1",
            ["expectedSessionId"] = HiddenValue(handbackHtml, "expectedSessionId"),
            ["expectedSessionGeneration"] =
                HiddenValue(handbackHtml, "expectedSessionGeneration")
        });
        var html = await launch.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, launch.StatusCode);
        Assert.Contains("Opened Codex CLI in a new terminal.", html);
        Assert.Equal("codex", launcher.CliInvocation?.Executable);
        Assert.Equal(["resume", "web-test-session"], launcher.CliInvocation?.Arguments);
        Assert.StartsWith(
            "agent:web-handback:",
            launcher.CliInvocation?.Environment["WRIGHTY_CLAIMANT_ID"]);
        Assert.False(string.IsNullOrWhiteSpace(
            launcher.CliInvocation?.Environment["WRIGHTY_CLAIM_TOKEN"]));
        await host.Stop();
    }

    [Fact]
    public async Task Session_launch_rejects_a_stale_session_generation_without_invoking_the_os()
    {
        var launcher = new RecordingAgentSessionLauncher();
        var host = await StartServer(sessionLauncher: launcher);
        using var client = new HttpClient();
        using var launch = await PostForm(client, host, "LaunchAgentDesktop", new()
        {
            ["id"] = "local:1",
            ["expectedSessionId"] = "web-test-session",
            ["expectedSessionGeneration"] = "stale"
        });
        var html = await launch.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Conflict, launch.StatusCode);
        Assert.Contains("RESUME_SESSION_CHANGED", html);
        Assert.Null(launcher.DesktopAddress);
        Assert.Null(launcher.CliInvocation);
        await host.Stop();
    }

    [Fact]
    public async Task Every_module_the_entry_script_imports_is_served()
    {
        // Embedding an asset and serving it are separate steps: the project embeds Assets/** by a
        // wildcard, but the server only serves names on an explicit list. Adding a module and
        // importing it therefore compiles, passes every test, and 404s in a browser — which takes
        // the whole entry script down with it, because a module that fails to load stops the
        // script that imported it. Whatever app.js imports must be reachable.
        var host = await StartServer();
        using var client = new HttpClient();
        var entryScript = await client.GetStringAsync($"{host.Origin}/assets/app.js");
        var modules = Regex
            .Matches(entryScript, """from\s+"\./(?<module>[^"]+)";""")
            .Select(match => match.Groups["module"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(modules);
        foreach (var module in modules)
        {
            var response = await client.GetAsync($"{host.Origin}/assets/{module}");
            Assert.True(
                response.IsSuccessStatusCode,
                $"{module} is imported by app.js but the server answered {(int)response.StatusCode}.");
            Assert.Equal("text/javascript", response.Content.Headers.ContentType?.MediaType);
        }

        await host.Stop();
    }

    [Fact]
    public async Task Embedded_htmx_is_the_complete_pinned_distribution()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        var bytes = await client.GetByteArrayAsync($"{host.Origin}/assets/htmx.js");

        Assert.True(bytes.Length > 50_000);
        Assert.Equal("6eaa5e1530c14966ae4e2add137c8104a0edcd55a9311550e361d097c0e488fe", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        await host.Stop();
    }

    [Fact]
    public async Task Embedded_highlight_js_is_the_pinned_yaml_only_build()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        var bytes = await client.GetByteArrayAsync($"{host.Origin}/assets/highlight-yaml.js");
        var script = Encoding.UTF8.GetString(bytes);

        Assert.InRange(bytes.Length, 20_000, 25_000);
        Assert.Equal(
            "99775fe31908c6aac992fb04b03ba48fdca58c46af066413d80b4c6043a2ba99",
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Contains("yaml", script, StringComparison.OrdinalIgnoreCase);
        await host.Stop();
    }

    [Fact]
    public void Workspace_path_inside_home_is_abbreviated()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrWhiteSpace(home));
        var workspace = Path.Combine(home, "source", "wrighty");
        var config = new TrackerConfig
        {
            SourcePath = Path.Combine(workspace, ".wrighty.json")
        };

        var state = new WebApplicationState(config, "token", Path.GetTempPath());

        Assert.Equal(Path.GetFullPath(workspace), state.WorkspacePath);
        Assert.Equal(
            $"~{Path.DirectorySeparatorChar}source{Path.DirectorySeparatorChar}wrighty",
            state.WorkspaceDisplayPath);
    }

    [Fact]
    public void Workspace_path_outside_home_remains_absolute()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrWhiteSpace(home));
        var root = Path.GetPathRoot(Path.GetFullPath(home));
        Assert.False(string.IsNullOrWhiteSpace(root));
        var workspace = Path.Combine(root, "wrighty-outside-home", "workspace");
        var config = new TrackerConfig
        {
            SourcePath = Path.Combine(workspace, ".wrighty.json")
        };

        var state = new WebApplicationState(config, "token", home);

        Assert.Equal(Path.GetFullPath(workspace), state.WorkspacePath);
        Assert.Equal(Path.GetFullPath(workspace), state.WorkspaceDisplayPath);
    }

    [Fact]
    public void Local_host_name_is_sanitized_bounded_and_has_a_safe_fallback()
    {
        var config = new TrackerConfig();
        var unsafeName = $"  office\u0000-mac-{new string('x', 120)}  ";

        var state = new WebApplicationState(
            config,
            "token",
            Path.GetTempPath(),
            localHostName: unsafeName);
        var fallback = new WebApplicationState(
            config,
            "token",
            Path.GetTempPath(),
            localHostName: " \u0000 ");

        Assert.StartsWith("office-mac-", state.LocalHostName, StringComparison.Ordinal);
        Assert.Equal(100, state.LocalHostName.Length);
        Assert.Equal("Unknown host", fallback.LocalHostName);
    }

    [Fact]
    public void Header_and_item_panel_layout_contracts_are_embedded()
    {
        using var stream = typeof(WrightyWebServer).Assembly.GetManifestResourceStream(
            "Highbyte.Wrighty.Web.Assets.wrighty.css");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var stylesheet = reader.ReadToEnd();

        Assert.Contains("[hidden] { display: none !important; }", stylesheet);
        Assert.Contains("button:disabled { opacity: .55; cursor: not-allowed; }", stylesheet);
        Assert.Contains("button:disabled:hover { border-color: var(--line); }", stylesheet);
        Assert.Contains(".app-header { position: relative; z-index: 20;", stylesheet);
        Assert.Contains(".app-identity { flex: 1 1 auto; min-width: 0;", stylesheet);
        Assert.Contains(".local-host-name { flex: none;", stylesheet);
        Assert.Contains(".workspace-path { display: block; max-width: 100%; overflow: hidden;", stylesheet);
        Assert.Contains(".connection-tools { display: grid; justify-items: end;", stylesheet);
        Assert.Contains(".access-link-button { min-height: 0; padding: 0; border: 0;", stylesheet);
        Assert.Contains(".provider-capacity-menu.has-available > summary", stylesheet);
        Assert.Contains(".board-filter-menu { position: relative; align-self: end; width: 7.25rem; }", stylesheet);
        Assert.Contains(".board-filter-menu > summary { display: flex; align-items: center; justify-content: space-between;", stylesheet);
        Assert.Contains(".board-filter-heading-actions { display: flex; align-items: center; gap: .25rem; }", stylesheet);
        Assert.Contains(".board-filter-fields .board-filter-clear { min-height: 1.8rem;", stylesheet);
        Assert.Contains(".app-header { align-items: start; flex-wrap: wrap;", stylesheet);
        Assert.Contains(
            ".operations-grid { display: grid; grid-template-columns: minmax(0, 3fr) minmax(16rem, 1fr);",
            stylesheet);
        Assert.Contains("align-items: start; gap: 1rem; }", stylesheet);
        Assert.Contains(
            ".operations-card-heading-actions { display: flex; align-items: center; gap: .55rem; min-height: 1.8rem; }",
            stylesheet);
        Assert.Contains(
            ".operations-card-heading-actions .operations-item-count { min-width: 3ch; font-variant-numeric: tabular-nums; text-align: right; }",
            stylesheet);
        Assert.Contains(
            ".worker-facts { grid-column: 1 / -1; display: grid; grid-template-columns: repeat(auto-fit, minmax(6rem, 1fr));",
            stylesheet);
        Assert.Contains(".operations-grid { grid-template-columns: 1fr; }", stylesheet);
        Assert.Contains(
            ".item-panel { position: fixed; inset: 0 0 0 auto; width: min(44rem, 94vw); z-index: 30;",
            stylesheet);
        Assert.Contains(
            "#operational-items table { min-width: 62rem; table-layout: fixed; }",
            stylesheet);
        Assert.Contains(
            "#operational-items .operations-col-actions { width: 7.5rem; }",
            stylesheet);
        Assert.Contains(
            "#operational-items td.operations-action-cell { text-align: right; }",
            stylesheet);
        Assert.Contains(
            "#operational-items td.operations-action-cell > button { width: 100%; min-width: 0; }",
            stylesheet);
        Assert.Contains(
            "#operational-items td.operations-action-cell > .card-actions > form,",
            stylesheet);
        Assert.Contains(
            ".context-approval-summary { display: flex; align-items: start; justify-content: space-between;",
            stylesheet);
        Assert.Contains(".panel-loading-status { display: flex; align-items: center;", stylesheet);
        Assert.Contains("@keyframes panel-loading-spin", stylesheet);
        Assert.Contains(".request-button { display: inline-grid; grid-template-areas: \"content\";", stylesheet);
        Assert.Contains(".request-button-idle, .request-button-progress { grid-area: content;", stylesheet);
        Assert.Contains(".request-button-progress { display: inline-flex; align-items: center;", stylesheet);
        Assert.Contains(".request-button.htmx-request .request-button-idle { visibility: hidden; }", stylesheet);
        Assert.Contains(".request-button.htmx-request .request-button-progress { visibility: visible; }", stylesheet);
        Assert.Contains(".request-button:disabled { opacity: .72; cursor: wait; }", stylesheet);
        Assert.Contains("@keyframes request-button-spin", stylesheet);
        Assert.Contains(
            ".worker-row.worker-stale { border: 1px solid var(--line); background: transparent; }",
            stylesheet);
        Assert.Contains(
            ".hosted-worker-log li { display: flex; flex-wrap: wrap; align-items: baseline;",
            stylesheet);
        Assert.Contains(
            ".user-configuration-summary { display: grid; grid-template-columns: minmax(0, 1fr) minmax(20rem, 28rem);",
            stylesheet);
        Assert.Contains(
            ".operations-card > header span, .operations-card > header code, .settings-section-subtitle { color: var(--muted); font-size: .72rem; }",
            stylesheet);
        Assert.Contains(
            ".user-configuration-source { display: flex; flex-wrap: wrap; align-items: baseline;",
            stylesheet);
        Assert.Contains(
            ".user-configuration .user-profile-mappings-heading { margin: .9rem 0 .25rem; }",
            stylesheet);
        Assert.Contains(".workspace-mode-popover { position: absolute;", stylesheet);
        Assert.Contains(
            ".user-host-label-controls { display: grid; grid-template-columns: minmax(0, 1fr) auto;",
            stylesheet);
        Assert.Contains("@media (max-width: 800px)", stylesheet);
    }

    [Fact]
    public async Task Embedded_first_party_assets_are_served_and_unknown_assets_are_not()
    {
        var host = await StartServer();
        using var client = new HttpClient();

        var css = await client.GetAsync($"{host.Origin}/assets/wrighty.css");
        var script = await client.GetAsync($"{host.Origin}/assets/app.js");
        var confirmationScriptResponse =
            await client.GetAsync($"{host.Origin}/assets/confirmation-dialog.mjs");
        var launchTokenScriptResponse =
            await client.GetAsync($"{host.Origin}/assets/launch-token.mjs");
        var stylesheet = await css.Content.ReadAsStringAsync();
        var applicationScript = await script.Content.ReadAsStringAsync();
        var confirmationScript = await confirmationScriptResponse.Content.ReadAsStringAsync();
        var launchTokenScript = await launchTokenScriptResponse.Content.ReadAsStringAsync();
        var missing = await client.GetAsync($"{host.Origin}/assets/missing.js");

        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
        Assert.Contains(".item-panel:has(.edit-form) { width: min(64rem, 94vw);", stylesheet);
        Assert.Contains(".edit-actions { display: grid; grid-template-columns: max-content minmax(0, 1fr);", stylesheet);
        Assert.Contains(".edit-actions .actions-secondary { justify-content: flex-start; flex-wrap: nowrap;", stylesheet);
        Assert.Contains(".edit-actions .actions-primary { min-width: 0; justify-content: flex-end;", stylesheet);
        Assert.Contains(".action-menu-popover { position: absolute;", stylesheet);
        Assert.Contains(".resume-address > summary { display: flex;", stylesheet);
        Assert.Contains(".copy-button { min-height: auto;", stylesheet);
        Assert.Contains(".metadata > div { min-width: 0; overflow: hidden;", stylesheet);
        Assert.Contains(".inspectable-value-text { display: block; min-width: 0; overflow: hidden;", stylesheet);
        Assert.Contains(".inspectable-value-text.expanded { overflow: visible;", stylesheet);
        Assert.Contains(".custom-field-value { display: grid; grid-template-columns: minmax(0, 1fr) max-content;", stylesheet);
        Assert.Contains(".column-count { display: inline-flex;", stylesheet);
        Assert.Contains(".column-count.has-tooltip::after { top:", stylesheet);
        Assert.Contains(".provider-capacity-popover { position: absolute;", stylesheet);
        Assert.Contains(".button-compact,", stylesheet);
        Assert.Contains("#settings-content button[data-settings-save]", stylesheet);
        Assert.Contains(".settings-field-grid { --settings-field-width: 19rem; display: flex;", stylesheet);
        Assert.Contains(".settings-field-grid--dense { --settings-field-width: 15rem; }", stylesheet);
        Assert.Contains(".settings-field-grid--wide { --settings-field-width: 32rem; }", stylesheet);
        Assert.Contains(".settings-field-grid--fluid > * { flex-grow: 1; }", stylesheet);
        Assert.Contains(".confirmation-dialog { width: min(30rem, calc(100vw - 2rem));", stylesheet);
        Assert.Contains(".confirmation-dialog::backdrop { background:", stylesheet);
        Assert.Contains(
            ".confirmation-dialog[data-tone=danger] #confirmation-dialog-accept",
            stylesheet);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "text/javascript",
            confirmationScriptResponse.Content.Headers.ContentType?.MediaType);
        Assert.Equal(
            "text/javascript",
            launchTokenScriptResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains(
            "import { installConfirmationDialog } from \"./confirmation-dialog.mjs\";",
            applicationScript);
        Assert.Contains(
            "buildLaunchUrl,",
            applicationScript);
        Assert.Contains(
            "let token = tokenAuthenticationRequired ? loadLaunchToken() : null;",
            applicationScript);
        Assert.Contains("meta[name=\"wrighty-auth\"]", applicationScript);
        Assert.Contains("clearLaunchToken();", applicationScript);
        Assert.Contains("writeClipboard(buildLaunchUrl(token))", applicationScript);
        Assert.Contains("copyAccessLink(accessLinkButton)", applicationScript);
        Assert.Contains("export function buildLaunchUrl", launchTokenScript);
        Assert.Contains("wrighty.web.launch-token.v1", launchTokenScript);
        Assert.Contains("sessionStorage", launchTokenScript);
        Assert.DoesNotContain("localStorage", launchTokenScript);
        Assert.DoesNotContain("cookie", launchTokenScript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("highlightElement", applicationScript);
        Assert.Contains("htmx:afterSwap", applicationScript);
        Assert.Contains("confirmationUi.handleKeydown(event)", applicationScript);
        Assert.Contains("dataset.confirmMessage", confirmationScript);
        Assert.Contains("dialog.showModal()", confirmationScript);
        Assert.Contains(
            "dialog.returnValue === \"confirm\"",
            confirmationScript);
        Assert.Contains("cancel.focus()", confirmationScript);
        Assert.Contains("dialog.close(\"cancel\")", confirmationScript);
        Assert.DoesNotContain("confirm(", applicationScript);
        Assert.DoesNotContain("confirm(", confirmationScript);
        Assert.Contains("navigator.clipboard?.writeText", applicationScript);
        Assert.Contains("document.execCommand(\"copy\")", applicationScript);
        Assert.Contains("copyValue(copyButton)", applicationScript);
        Assert.Contains("refreshExpandableValues(event.detail.target)", applicationScript);
        Assert.Contains("toggleExpandableValue(expandButton)", applicationScript);
        Assert.Contains("target.scrollWidth <= target.clientWidth", applicationScript);
        Assert.Contains("`${count} of ${total}`", applicationScript);
        Assert.Contains("countElement.dataset.tooltip = description", applicationScript);
        Assert.Contains("countElement.setAttribute(\"aria-label\", description)", applicationScript);
        Assert.Contains("function refreshProviderCapacity()", applicationScript);
        Assert.Contains("function refreshWorkerSummary()", applicationScript);
        Assert.Contains("function openWorkerProcesses()", applicationScript);
        Assert.Contains("handler=WorkerSummary", applicationScript);
        Assert.Contains("refreshVisibleOperations(document);", applicationScript);
        Assert.Contains("setInterval(refreshDashboard, 2000)", applicationScript);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await host.Stop();
    }

    [Fact]
    public async Task Browser_launch_failure_is_reported_without_stopping_the_server()
    {
        var host = await StartServer(browserLauncher: new ThrowingBrowserLauncher());
        using var client = new HttpClient();

        Assert.Equal("Press Ctrl+C to stop.", await host.Output.ReadLineAsync(host.Cancellation.Token));
        Assert.Equal(
            "warning: Could not open the default browser: Browser unavailable",
            await host.Output.ReadLineAsync(host.Cancellation.Token));
        Assert.Equal("ok", (await client.GetStringAsync($"{host.Origin}/web/health")).Split('"')[3]);
        await host.Stop();
    }

    [Fact]
    public async Task Server_can_start_without_opening_a_browser()
    {
        var host = await StartServer(openBrowser: false);
        Assert.Equal("Press Ctrl+C to stop.", await host.Output.ReadLineAsync(host.Cancellation.Token));
        await host.Stop();
    }

    [Fact]
    public void GitHub_backend_uses_operations_and_context_approval_capabilities()
    {
        var config = new TrackerConfig
        {
            Backend = "github",
            Repository = "owner/repository",
            ProjectNumber = 1,
            SourcePath = Path.Combine(directory, TrackerConfigLoader.FileName)
        };
        var capabilities = WebSurfaceCapabilities.Resolve(config);

        Assert.True(capabilities.ConfigurationRead);
        Assert.True(capabilities.ConfigurationWrite);
        Assert.True(capabilities.WorkerInstances);
        Assert.True(capabilities.OperationalItems);
        Assert.True(capabilities.GitHubTarget);
        Assert.True(capabilities.ContextApproval);
        Assert.False(capabilities.LocalBoard);
        Assert.False(capabilities.LocalItemMutation);
    }

    [Fact]
    public void Context_badges_distinguish_project_projection_from_inspected_state()
    {
        var projected = new OperationsItemView(
            "github:owner/repository#1",
            "Approval test",
            "Todo",
            "P1",
            null,
            "none",
            null,
            null,
            ContextApprovalFieldApproved: true);
        var inspected = new ContextApprovalView(
            projected.Id,
            projected.Title,
            Url: null,
            ProjectedApproved: true,
            Approved: false,
            Code: ExecutionContextResult.Codes.BaseNeedsReview,
            Message: "The body changed.",
            ApprovalSource: "project-field",
            BaseApprovedAt: DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
            BatchCommentCutoff: DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
            Revision: null,
            IncludedCount: null,
            ExcludedCount: null,
            PendingCount: null,
            PendingUrls: []);

        Assert.Equal("Approved (*)", projected.ContextApprovalLabel);
        Assert.Equal("approved", projected.ContextApprovalAppearance);
        Assert.Contains("Inspect to verify", projected.ContextApprovalTitle);
        Assert.Equal("Needs review", inspected.InspectedLabel);
        Assert.Equal("needs-review", inspected.InspectedAppearance);
        Assert.Contains("needs review", inspected.InspectedTitle, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("Reapprove current context", inspected.ActionLabel);
        Assert.True(inspected.CanApprove);

        var needsReviewProjection = projected with { ContextApprovalFieldApproved = false };
        Assert.Equal("Needs review", needsReviewProjection.ContextApprovalLabel);
        Assert.Equal("needs-review", needsReviewProjection.ContextApprovalAppearance);
        Assert.Contains("Needs review", needsReviewProjection.ContextApprovalTitle);

        var unknownProjection = projected with { ContextApprovalFieldApproved = null };
        Assert.Equal("Unknown", unknownProjection.ContextApprovalLabel);
        Assert.Equal("unknown", unknownProjection.ContextApprovalAppearance);
        Assert.Contains("could not be resolved", unknownProjection.ContextApprovalTitle);

        var approved = inspected with { Approved = true, Code = null };
        Assert.Equal("Approved", approved.InspectedLabel);
        Assert.Equal("approved", approved.InspectedAppearance);
        Assert.Contains("verified", approved.InspectedTitle);
        Assert.True(approved.CanApprove);

        var unavailable = inspected with
        {
            ProjectedApproved = false,
            Code = ExecutionContextResult.Codes.ApprovalUnavailable
        };
        Assert.Equal("Approve current context", unavailable.ActionLabel);
        Assert.True(unavailable.CanApprove);
        Assert.True((inspected with
        {
            Code = ExecutionContextResult.Codes.CommentPending
        }).CanApprove);

        var unreadable = inspected with { Code = ExecutionContextResult.Codes.ReadFailed };
        Assert.Equal("Unknown", unreadable.InspectedLabel);
        Assert.Equal("unknown", unreadable.InspectedAppearance);
        Assert.Contains("could not verify", unreadable.InspectedTitle);
        Assert.False(unreadable.CanApprove);

        var unsupported = inspected with { Code = ExecutionContextResult.Codes.Unsupported };
        Assert.Equal("Unknown", unsupported.InspectedLabel);
    }

    [Fact]
    public async Task GitHub_shell_hides_local_board_and_rejects_direct_item_mutation()
    {
        Directory.CreateDirectory(directory);
        var config = new TrackerConfig
        {
            Backend = "github",
            Repository = "highbyte/wrighty-github-test",
            ProjectNumber = 38,
            SourcePath = Path.Combine(directory, TrackerConfigLoader.FileName),
            LocalMarkdown = new LocalMarkdownBackendConfig { Path = ".wrighty" }
        };
        var local = new LocalMarkdownTrackerBackend(
            new FixedIdentity("github-shell-test"),
            new SystemClock());
        await local.InitializeAsync(config, checkOnly: false, CancellationToken.None);
        var created = await local.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest(
                    "Approval test item",
                    "Review this body.",
                    "Todo",
                    "P1",
                    AgentPolicy: "copilot"),
                false),
            CancellationToken.None);
        const string copilotSessionId = "fd889d8b-70b8-4803-a480-8bd638a59778";
        var agentContext = new AgentExecutionContext(
            "copilot",
            copilotSessionId,
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:github-operations-test");
        var claim = await local.TryClaimAsync(
            config, created.Id, agentContext, CancellationToken.None);
        var claimHandle = new ClaimHandle(agentContext, claim.ClaimToken);
        await local.RenewClaimAsync(
            config,
            created.Id,
            claimHandle,
            directory,
            copilotSessionId,
            CancellationToken.None);
        await local.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string?>.Unspecified,
                    DispatchState: OptionalValue<string?>.From(
                        DispatchStates.NeedsAttention)),
                false,
                ClaimHandle: claimHandle),
            CancellationToken.None);
        const string doneCopilotSessionId = "996112fa-67de-45ae-8099-8d8248c00c3b";
        var done = await local.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest(
                    "Done session test item",
                    "Open this retained session without putting it back under management.",
                    "In Progress",
                    "P2",
                    AgentPolicy: "copilot"),
                false),
            CancellationToken.None);
        var doneContext = new AgentExecutionContext(
            "copilot",
            doneCopilotSessionId,
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:github-done-session-test");
        var doneClaim = await local.TryClaimAsync(
            config, done.Id, doneContext, CancellationToken.None);
        var doneHandle = new ClaimHandle(doneContext, doneClaim.ClaimToken);
        await local.RenewClaimAsync(
            config,
            done.Id,
            doneHandle,
            directory,
            doneCopilotSessionId,
            CancellationToken.None);
        await local.UpdateAsync(
            config,
            done.Id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.From(config.DefaultFinishTo),
                    OptionalValue<string?>.Unspecified,
                    DispatchState: OptionalValue<string?>.From(null)),
                false,
                ClaimHandle: doneHandle),
            CancellationToken.None);
        await local.ReleaseAsync(
            config,
            done.Id,
            doneHandle,
            false,
            DispatchStateOnRelease.Preserve,
            CancellationToken.None);
        // The real GitHub backend intentionally has no Local Markdown dashboard interface. This
        // wrapper prevents the test double from accidentally letting a launch depend on one.
        var tracker = new TrackerService(
            new FixedBackendRegistry(new NonDashboardBackend(local)));
        var contextApproval = new RecordingWebContextApprovalService();
        var sessionLauncher = new RecordingAgentSessionLauncher();
        var output = new LineChannelWriter();
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new WrightyWebServer(
            new FixedConfigLoader(config),
            tracker,
            new RecordingBrowserLauncher(),
            directory,
            new WrightyWebServerDependencies(
                new GitWorkspaceInventory(new PathExecutableResolver()),
                AgentAdapters:
                [new ClaudeAgentAdapter(), new CodexAgentAdapter(), new CopilotAgentAdapter()],
                AgentRuntimeCatalog: new InstalledAgentRuntimeCatalog(),
                LocalAgentSessionLauncher: sessionLauncher,
                ContextApproval: contextApproval,
                GitHubProjectUrls: new GitHubProjectUrlResolver(
                    (_, _, _, _) => Task.FromResult<GitHubProjectInfo?>(new(
                        "project-node-id",
                        "highbyte",
                        38,
                        "Wrighty GitHub test",
                        "https://github.com/users/highbyte/projects/38",
                        ["highbyte/wrighty-github-test"])))));
        var run = server.RunAsync(
            new WebServerOptions(0, false),
            output,
            TextWriter.Null,
            cancellation.Token);
        var startupLine = output.ReadLineAsync(cancellation.Token).AsTask();
        if (await Task.WhenAny(startupLine, run) == run)
            await run;
        var origin = (await startupLine)[
            "Wrighty web server listening on ".Length..];
        var launch = (await output.ReadLineAsync(cancellation.Token))["Open ".Length..];
        var token = new URL(launch).Fragment.GetValueOrDefault("token") ?? string.Empty;
        var host = new RunningServer(origin, launch, token, cancellation, run, output);
        using var client = new HttpClient();

        var shell = await client.GetStringAsync(origin);
        Assert.Contains("GITHUB PROJECT", shell);
        Assert.Contains("id=\"operations-content\"", shell);
        Assert.DoesNotContain("id=\"board-search\"", shell);
        Assert.DoesNotContain(">New item</button>", shell);
        // No board tab without a board; Operations is the default tab and Settings sits beside it.
        Assert.DoesNotContain("id=\"tab-board\"", shell);
        Assert.Contains("id=\"tab-operations\"", shell);
        Assert.Contains("id=\"tab-settings\"", shell);

        using var settingsRequest = AuthenticatedGet(
            host,
            $"{origin}/?handler=Settings");
        var settingsHtml = await (await client.SendAsync(settingsRequest)).Content.ReadAsStringAsync();
        Assert.Contains("Repository settings", settingsHtml);
        Assert.Contains("id=\"refresh-settings\"", settingsHtml);
        Assert.Contains("class=\"request-button\"", settingsHtml);
        Assert.Contains("hx-indicator=\"this\"", settingsHtml);
        Assert.Contains("hx-disabled-elt=\"this\"", settingsHtml);
        Assert.Contains("Refresh settings", settingsHtml);
        Assert.Contains("Refreshing…", settingsHtml);
        Assert.Contains("id=\"storage-locations\"", settingsHtml);
        Assert.Contains("Wrighty GitHub Issue Form", settingsHtml);
        Assert.Contains(
            Path.Combine(directory, ".github", "ISSUE_TEMPLATE", "wrighty-task.yml"),
            settingsHtml);
        Assert.DoesNotContain("Local Markdown runtime state", settingsHtml);

        using var operationsRequest = AuthenticatedGet(
            host,
            $"{origin}/?handler=Operations");
        var operationsResponse = await client.SendAsync(operationsRequest);
        var operationsHtml = await operationsResponse.Content.ReadAsStringAsync();
        Assert.Contains("Local worker processes", operationsHtml);
        Assert.DoesNotContain("Repository settings", operationsHtml);
        Assert.Contains("id=\"github-repository-link\"", operationsHtml);
        var repositoryLink = GitHubRepositoryLinkRegex().Match(operationsHtml);
        Assert.Equal("https://github.com/highbyte/wrighty-github-test", repositoryLink.Groups[1].Value);
        Assert.Contains(">highbyte/wrighty-github-test</a>", operationsHtml);
        Assert.Contains("id=\"github-project-link\"", operationsHtml);
        Assert.Contains("href=\"https://github.com/users/highbyte/projects/38\"", operationsHtml);
        Assert.Contains(">Project highbyte/#38</a>", operationsHtml);
        Assert.DoesNotContain("Open GitHub repository", operationsHtml);
        Assert.Contains("id=\"refresh-operations\"", operationsHtml);
        Assert.Contains("hx-request='{\"timeout\":130000}'", operationsHtml);
        Assert.Contains("hx-indicator=\"this\"", operationsHtml);
        Assert.Contains("hx-disabled-elt=\"this\"", operationsHtml);
        Assert.Contains("Refreshing…", operationsHtml);
        Assert.Contains("id=\"validate-github-target\"", operationsHtml);
        Assert.Contains("hx-indicator=\"#validate-github-target\"", operationsHtml);
        Assert.Contains("hx-disabled-elt=\"#validate-github-target\"", operationsHtml);
        Assert.Contains("Validate GitHub target", operationsHtml);
        Assert.Contains("Validating…", operationsHtml);
        Assert.Contains("<th>Context</th>", operationsHtml);
        Assert.Contains(">Actions</th>", operationsHtml);
        Assert.Contains("Copilot session retained here", operationsHtml);
        Assert.Contains("Open Copilot", operationsHtml);
        Assert.Contains("handler=OpenSessionCli", operationsHtml);
        Assert.Contains("handler=OpenSessionDesktop", operationsHtml);
        Assert.Contains("In a terminal", operationsHtml);
        Assert.Contains("In the Desktop app", operationsHtml);
        Assert.Contains("Show Copilot CLI Session", operationsHtml);
        Assert.Contains("may open Home", operationsHtml);
        Assert.Contains("id=\"context-approval-inspect-local-1\"", operationsHtml);
        Assert.Contains("id=\"context-approval-state-local-1\"", operationsHtml);
        Assert.Contains("hx-target=\"#item-panel\"", operationsHtml);
        Assert.Contains("data-panel-loading-label=\"Loading context approval…\"", operationsHtml);
        Assert.Contains("hx-request='{\"timeout\":130000}'", operationsHtml);
        Assert.DoesNotContain("id=\"context-approval-details\"", operationsHtml);
        var doneRow = OperationsRowMarkup(operationsHtml, "local:2");
        Assert.Contains("Done session test item", doneRow);
        Assert.Contains("Copilot session retained here", doneRow);
        Assert.Contains("handler=OpenSessionCli", doneRow);
        Assert.Contains("handler=OpenSessionDesktop", doneRow);
        Assert.Contains("stays outside Wrighty&#x27;s management", doneRow);

        using var cliLaunch = await PostFormWithToken(
            client,
            host,
            "OpenSessionCli",
            new Dictionary<string, string>
            {
                ["id"] = "local:1",
                ["expectedSessionId"] = HiddenValue(operationsHtml, "expectedSessionId"),
                ["expectedSessionGeneration"] =
                    HiddenValue(operationsHtml, "expectedSessionGeneration")
            },
            operationsHtml);
        Assert.Equal(HttpStatusCode.NoContent, cliLaunch.StatusCode);
        Assert.Equal("copilot", sessionLauncher.CliInvocation?.Executable);
        Assert.Contains(
            $"--resume={copilotSessionId}",
            sessionLauncher.CliInvocation?.Arguments ?? []);

        using var refreshedOperationsRequest = AuthenticatedGet(
            host,
            $"{origin}/?handler=Operations");
        var refreshedOperationsHtml = await (
            await client.SendAsync(refreshedOperationsRequest)).Content.ReadAsStringAsync();
        using var desktopLaunch = await PostFormWithToken(
            client,
            host,
            "OpenSessionDesktop",
            new Dictionary<string, string>
            {
                ["id"] = "local:1",
                ["expectedSessionId"] =
                    HiddenValue(refreshedOperationsHtml, "expectedSessionId"),
                ["expectedSessionGeneration"] =
                    HiddenValue(refreshedOperationsHtml, "expectedSessionGeneration")
            },
            refreshedOperationsHtml);
        Assert.Equal(HttpStatusCode.NoContent, desktopLaunch.StatusCode);
        Assert.Equal(
            $"ghapp://sessions/{copilotSessionId}",
            sessionLauncher.DesktopAddress?.Uri?.AbsoluteUri);

        using var doneCliLaunch = await PostFormWithToken(
            client,
            host,
            "OpenSessionCli",
            new Dictionary<string, string>
            {
                ["id"] = "local:2",
                ["expectedSessionId"] = HiddenValue(doneRow, "expectedSessionId"),
                ["expectedSessionGeneration"] =
                    HiddenValue(doneRow, "expectedSessionGeneration")
            },
            doneRow);
        Assert.Equal(HttpStatusCode.NoContent, doneCliLaunch.StatusCode);
        Assert.Equal("copilot", sessionLauncher.CliInvocation?.Executable);
        Assert.Contains(
            $"--resume={doneCopilotSessionId}",
            sessionLauncher.CliInvocation?.Arguments ?? []);
        Assert.DoesNotContain(
            "WRIGHTY_CLAIMANT_ID",
            sessionLauncher.CliInvocation?.Environment.Keys ?? []);
        Assert.DoesNotContain(
            "WRIGHTY_CLAIM_TOKEN",
            sessionLauncher.CliInvocation?.Environment.Keys ?? []);
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await tracker.GetOperationalAsync(config, done.Id, CancellationToken.None))
                .Claim.State);

        using var doneDesktopLaunch = await PostFormWithToken(
            client,
            host,
            "OpenSessionDesktop",
            new Dictionary<string, string>
            {
                ["id"] = "local:2",
                ["expectedSessionId"] = HiddenValue(doneRow, "expectedSessionId"),
                ["expectedSessionGeneration"] =
                    HiddenValue(doneRow, "expectedSessionGeneration")
            },
            doneRow);
        Assert.Equal(HttpStatusCode.NoContent, doneDesktopLaunch.StatusCode);
        Assert.Equal(
            $"ghapp://sessions/{doneCopilotSessionId}",
            sessionLauncher.DesktopAddress?.Uri?.AbsoluteUri);
        Assert.Equal(
            ClaimOwnershipState.Unclaimed,
            (await tracker.GetOperationalAsync(config, done.Id, CancellationToken.None))
                .Claim.State);

        using var contextRequest = AuthenticatedGet(
            host,
            $"{origin}/?handler=ContextApproval&id=local%3A1");
        var contextResponse = await client.SendAsync(contextRequest);
        var contextHtml = await contextResponse.Content.ReadAsStringAsync();
        Assert.Contains("id=\"context-approval-details\"", contextHtml);
        Assert.Contains("class=\"close-panel\"", contextHtml);
        Assert.Contains("hx-target=\"#item-panel\"", contextHtml);
        Assert.DoesNotContain("id=\"operations-content\"", contextHtml);
        Assert.Contains("2 included · 1 excluded · 0 pending", contextHtml);
        Assert.Contains("id=\"context-approval-approve\"", contextHtml);
        Assert.Contains("data-panel-loading-label=\"Approving current context…\"", contextHtml);
        Assert.Contains("hx-request='{\"timeout\":130000}'", contextHtml);
        Assert.Equal("local:1", contextApproval.Inspected?.Value);
        Assert.Equal(
            "{\"wrighty:context-state\":{\"automationKey\":\"local-1\",\"label\":\"Approved\",\"appearance\":\"approved\",\"title\":\"Inspect verified that the current issue content and discussion are approved.\"}}",
            Assert.Single(contextResponse.Headers.GetValues("HX-Trigger-After-Swap")));

        using var approve = new HttpRequestMessage(
            HttpMethod.Post,
            $"{origin}/?handler=ApproveContext");
        approve.Headers.Add(WrightyWebServer.TokenHeader, token);
        approve.Headers.Add("Origin", origin);
        approve.Content = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["id"] = "local:1",
                ["__RequestVerificationToken"] =
                    HiddenValue(contextHtml, "__RequestVerificationToken")
            });
        var approveResponse = await client.SendAsync(approve);
        var approveHtml = await approveResponse.Content.ReadAsStringAsync();
        Assert.Contains("Context approval renewed", approveHtml);
        Assert.DoesNotContain("id=\"operations-content\"", approveHtml);
        Assert.Contains("Project field</dt><dd>Approved", approveHtml);
        Assert.False(approveResponse.Headers.Contains("HX-Trigger"));
        Assert.Equal(
            "{\"wrighty:context-state\":{\"automationKey\":\"local-1\",\"label\":\"Approved\",\"appearance\":\"approved\",\"title\":\"Inspect verified that the current issue content and discussion are approved.\"}}",
            Assert.Single(approveResponse.Headers.GetValues("HX-Trigger-After-Swap")));
        Assert.Equal("local:1", contextApproval.Approved?.Value);

        using var validation = new HttpRequestMessage(
            HttpMethod.Post,
            $"{origin}/?handler=ValidateTarget");
        validation.Headers.Add(WrightyWebServer.TokenHeader, token);
        validation.Headers.Add("Origin", origin);
        validation.Content = new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] =
                    HiddenValue(operationsHtml, "__RequestVerificationToken")
            });
        var validationResponse = await client.SendAsync(validation);
        var validationHtml = await validationResponse.Content.ReadAsStringAsync();
        Assert.Contains("id=\"github-target-health\"", validationHtml);
        Assert.Contains("Local worker processes", validationHtml);
        Assert.DoesNotContain("Repository settings", validationHtml);

        using var boardRequest = AuthenticatedGet(
            host,
            $"{origin}/?handler=Board");
        var boardResponse = await client.SendAsync(boardRequest);
        Assert.Equal(HttpStatusCode.NotFound, boardResponse.StatusCode);
        Assert.Equal("WEB_SURFACE_UNAVAILABLE", await ProblemTitle(boardResponse));

        using var mutation = new HttpRequestMessage(
            HttpMethod.Post,
            $"{origin}/?handler=Create");
        mutation.Headers.Add(WrightyWebServer.TokenHeader, token);
        mutation.Headers.Add("Origin", origin);
        mutation.Content = new FormUrlEncodedContent(
            new Dictionary<string, string> { ["title"] = "Must not be created" });
        var response = await client.SendAsync(mutation);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("WEB_MUTATION_UNSUPPORTED", await ProblemTitle(response));

        await cancellation.CancelAsync();
        await run;
        cancellation.Dispose();
    }

    private async Task<RunningServer> StartServer(
        bool protectNonHumanClaims = true,
        bool openBrowser = true,
        IBrowserLauncher? browserLauncher = null,
        bool scheduleRetry = false,
        bool providerUnavailable = false,
        bool providerProbeInProgress = false,
        bool providerProbeSucceeds = false,
        WebServerOptions? serverOptions = null,
        TextWriter? errorOutput = null,
        ILocalAgentSessionLauncher? sessionLauncher = null,
        WorkerConfig? workerConfig = null,
        string sessionAgent = "codex",
        string sessionId = "web-test-session",
        string pickFrom = "Todo",
        string? defaultCreateStatus = null,
        bool finishSeededSession = false,
        // Releases the seeded session's claim, leaving an unclaimed needs-attention item — the
        // state a paused run leaves behind once its lease ends, and the one the board's launch
        // actions target.
        bool releaseSeededClaim = false,
        bool hostedWorkerAvailable = false)
    {
        Directory.CreateDirectory(directory);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = pickFrom,
            DefaultCreateStatus = defaultCreateStatus,
            SourcePath = Path.Combine(directory, TrackerConfigLoader.FileName),
            LocalMarkdown = new LocalMarkdownBackendConfig
            {
                Path = ".wrighty",
                // Includes the alternate workflow statuses the operations-surface tests save,
                // which are validated against this list.
                Statuses = ["Todo", "Worker queue", "In Progress", "Done", "Ready", "Doing", "Complete"]
            },
            Web = new WebConfig { ProtectNonHumanClaims = protectNonHumanClaims },
            Worker = workerConfig
        };
        var configStore = new TrackerConfigLoader();
        await configStore.SaveAsync(config.SourcePath, config, CancellationToken.None);
        var backend = new LocalMarkdownTrackerBackend(new FixedIdentity("web-test-worker"), new SystemClock());
        await backend.InitializeAsync(config, checkOnly: false, CancellationToken.None);
        var created = await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest(
                    "Hostile item",
                    "# Safe heading\n<script>alert(1)</script>\n<img src=\"https://evil.example/pixel\">\n<div hx-get=\"https://evil.example\">bad</div>\n[bad](javascript:alert(1))\n![remote](https://evil.example/pixel.png)",
                    "In Progress",
                    "P1",
                    new Dictionary<string, string?> { ["unsafe"] = "<script>&" },
                    AutomaticExecutionAllowed: true,
                    AgentPolicy: sessionAgent),
                false),
            CancellationToken.None);
        var createdPath = Path.Combine(directory, ".wrighty", "items", "001-hostile-item.md");
        var createdContent = await File.ReadAllTextAsync(createdPath);
        await File.WriteAllTextAsync(createdPath, createdContent.Replace(
            "status: In Progress",
            "status: In Progress\ntestNode:\n  nodefield1: a long hierarchical value that must wrap inside the disclosure rather than clip\n  nodefield2: 42"));
        var initialContext = new AgentExecutionContext(
            sessionAgent,
            sessionId,
            AgentContextSource.ExplicitOption,
            ClaimantKind: ClaimantKind.Agent,
            ClaimantId: "agent:web-test-session");
        var initialClaim = await backend.TryClaimAsync(
            config,
            created.Id,
            initialContext,
            CancellationToken.None);
        await backend.RenewClaimAsync(
            config,
            created.Id,
            new ClaimHandle(initialContext, initialClaim.ClaimToken),
            directory,
            sessionId,
            CancellationToken.None);
        await backend.UpdateAsync(
            config,
            created.Id,
            new UpdateWorkItemOperation(
                new WorkItemPatch(
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string>.Unspecified,
                    OptionalValue<string?>.Unspecified,
                    DispatchState: OptionalValue<string?>.From(
                        scheduleRetry
                            ? DispatchStates.RetryScheduled
                            : DispatchStates.NeedsAttention)),
                false,
                ClaimHandle: new ClaimHandle(initialContext, initialClaim.ClaimToken)),
            CancellationToken.None);
        // A run outcome and the agent's own report on it, so the dashboard has both kinds of
        // statement to render: one Wrighty observed, one the agent claimed.
        var reportedAt = DateTimeOffset.UtcNow;
        await backend.RecordRunOutcomeAsync(
            config, created.Id, RunOutcome.Succeeded,
            "Paused for a decision.\n\n```wrighty-report\n{\"summary\":\"x\"}\n```",
            reportedAt, null, CancellationToken.None);
        await backend.RecordRunReportAsync(
            config,
            created.Id,
            Highbyte.Wrighty.ApprovedContext.RunReportRenderer.Build(
                new Highbyte.Wrighty.ApprovedContext.RunIdentity(
                    created.Id, sessionId, sessionAgent),
                Highbyte.Wrighty.ApprovedContext.RunReportDisposition.NeedsAttention,
                AgentOutcome.Succeeded, reportedAt,
                new Highbyte.Wrighty.ApprovedContext.AgentReportContent(
                    "Wired the setting through.",
                    Changes: ["WorkerConfig.cs"],
                    Verification: ["dotnet test — all green"],
                    RequestedInput: ["Per item or per worker?"])),
            CancellationToken.None);
        if (finishSeededSession)
        {
            await backend.UpdateAsync(
                config,
                created.Id,
                new UpdateWorkItemOperation(
                    new WorkItemPatch(
                        OptionalValue<string>.Unspecified,
                        OptionalValue<string>.Unspecified,
                        OptionalValue<string>.From(config.DefaultFinishTo),
                        OptionalValue<string?>.Unspecified,
                        DispatchState: OptionalValue<string?>.From(null)),
                    false,
                    ClaimHandle: new ClaimHandle(initialContext, initialClaim.ClaimToken)),
                CancellationToken.None);
        }
        if (scheduleRetry)
        {
            var endedAt = DateTimeOffset.UtcNow;
            var failure = new AgentFailure(
                AgentFailureKind.UsageExhausted,
                "quota_exceeded",
                endedAt.AddHours(2),
                null,
                true,
                AgentFailureConfidence.Authoritative,
                "Usage limit reached.");
            await backend.RecordRunOutcomeAsync(
                config,
                created.Id,
                RunOutcome.Failed,
                "Usage limit reached.",
                endedAt,
                failure,
                CancellationToken.None);
            await backend.RecordPendingDispatchAsync(
                config,
                created.Id,
                new PendingDispatch(
                    created.Id.Value,
                    DispatchStates.RetryScheduled,
                    "Usage limit reached.",
                    sessionAgent,
                    sessionId,
                    null,
                    endedAt.AddHours(2),
                    1,
                    5,
                    AgentFailureConfidence.Authoritative,
                    endedAt),
                CancellationToken.None);
            await backend.ReleaseAsync(config,
                created.Id,
                new ClaimHandle(initialContext, initialClaim.ClaimToken),
                false,
                DispatchStateOnRelease.Preserve,
                CancellationToken.None);
        }
        else if (releaseSeededClaim)
        {
            await backend.ReleaseAsync(config,
                created.Id,
                new ClaimHandle(initialContext, initialClaim.ClaimToken),
                false,
                DispatchStateOnRelease.Preserve,
                CancellationToken.None);
        }
        var otherBackend = new LocalMarkdownTrackerBackend(
            new FixedIdentity("another-worker"),
            new SystemClock());
        var other = await otherBackend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest("Claimed elsewhere", "Body", "In Progress", "P2"),
                false),
            CancellationToken.None);
        await otherBackend.TryClaimAsync(
            config,
            other.Id,
            new AgentExecutionContext(
                "claude",
                "other-session",
                AgentContextSource.ExplicitOption,
                ClaimantKind: ClaimantKind.Agent),
            CancellationToken.None);
        await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest("Web claim item", "Body", "Todo", "P3"),
                false),
            CancellationToken.None);
        foreach (var (title, context) in new[]
        {
            ("Copilot claim", new AgentExecutionContext(
                "copilot", "copilot-session", AgentContextSource.ExplicitOption,
                ClaimantKind: ClaimantKind.Agent)),
            ("Other agent claim", new AgentExecutionContext(
                "other", "other-agent-session", AgentContextSource.ExplicitOption,
                ClaimantKind: ClaimantKind.Agent)),
            ("Automation claim", new AgentExecutionContext(null, null, AgentContextSource.ExplicitOption, ClaimantKind: ClaimantKind.Automation, ClaimantId: "automation:web-tests")),
            ("Unknown claim", new AgentExecutionContext(null, null, AgentContextSource.ExplicitOption))
        })
        {
            var item = await backend.CreateAsync(
                config,
                new CreateWorkItemOperation(
                    new CreateWorkItemRequest(title, "Body", "Todo", null),
                    false),
                CancellationToken.None);
            await backend.TryClaimAsync(config, item.Id, context, CancellationToken.None);
        }
        await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest("Unassigned status", "Body", null, null),
                false),
            CancellationToken.None);
        await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest(
                    "Provider blocked ready item",
                    "Body",
                    "Todo",
                    null,
                    AutomaticExecutionAllowed: true,
                    AgentPolicy: "codex"),
                false),
            CancellationToken.None);
        var providerStore = ProviderStore();
        if (providerUnavailable || providerProbeInProgress)
        {
            var observedAt = DateTimeOffset.UtcNow;
            await providerStore.RecordUnavailableAsync(
                "codex",
                "Synthetic Codex capacity failure.",
                providerProbeInProgress ? observedAt : observedAt.AddHours(2),
                AgentFailureConfidence.Authoritative,
                observedAt,
                CancellationToken.None);
            if (providerProbeInProgress)
            {
                Assert.NotNull(await providerStore.TryAcquireProbeAsync(
                    "codex",
                    observedAt,
                    TimeSpan.FromMinutes(2),
                    CancellationToken.None));
            }
        }
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var workerInstances = new JsonWorkerInstanceRegistry(
            new CachePaths(Path.Combine(directory, ".worker-cache")));
        var hostedWorker = hostedWorkerAvailable
            ? new WorkerService(
                tracker,
                new RejectingAgentProcessRunner(),
                new WebTestWorkspaceManager(),
                [new CodexAgentAdapter()],
                (_, cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken))
            : null;
        var output = new LineChannelWriter();
        var browser = browserLauncher ?? new RecordingBrowserLauncher();
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new WrightyWebServer(
            new FixedConfigLoader(config),
            tracker,
            browser,
            directory,
            new WrightyWebServerDependencies(
                new GitWorkspaceInventory(new PathExecutableResolver()),
                providerStore,
                providerProbeSucceeds
                    ? new SuccessfulProviderProbe(providerStore)
                    : new SupportedProviderProbe(),
                [new ClaudeAgentAdapter(), new CodexAgentAdapter(), new CopilotAgentAdapter()],
                new TestingAgentRuntimeCatalog(
                    new InstalledAgentRuntimeCatalog(), configStore, directory),
                sessionLauncher ?? new RecordingAgentSessionLauncher(),
                new RepositoryConfigurationService(configStore),
                workerInstances,
                ContextApproval: null,
                UserConfiguration: new Highbyte.Wrighty.Settings.UserConfigurationService(
                    new Highbyte.Wrighty.Settings.UserSettingsStore(
                        new Highbyte.Wrighty.Settings.UserConfigPaths(
                            Path.Combine(directory, ".user-config")))),
                // Only codex answers. claude and copilot resolve to no adapter and so report
                // NotInstalled, which is how the free-text fallback gets exercised on the same page
                // as the picker.
                ModelDiscoveries: new Highbyte.Wrighty.Workers.AgentModelDiscoveries(
                    new[] { new StubModelDiscovery() }),
                StorageLocations: new StorageLocationCatalog(
                    new CachePaths(Path.Combine(directory, ".worker-cache"))),
                WorkerService: hostedWorker));
        var effectiveOptions = serverOptions ?? new WebServerOptions(0, openBrowser);
        var run = server.RunAsync(
            effectiveOptions,
            output,
            errorOutput ?? TextWriter.Null,
            cancellation.Token);
        var prefix = "Wrighty web server listening on ";
        var startupLine = output.ReadLineAsync(cancellation.Token).AsTask();
        if (await Task.WhenAny(startupLine, run) == run)
        {
            await run;
            throw new InvalidOperationException("The web server stopped before reporting its listening address.");
        }
        var origin = (await startupLine)[prefix.Length..];
        var launch = (await output.ReadLineAsync(cancellation.Token))["Open ".Length..];
        var token = new URL(launch).Fragment.GetValueOrDefault("token") ?? string.Empty;
        if (browser is RecordingBrowserLauncher recordingBrowser && effectiveOptions.OpenBrowser)
        {
            Assert.Equal(launch, await recordingBrowser.WaitForUrlAsync(cancellation.Token));
        }
        return new RunningServer(origin, launch, token, cancellation, run, output);
    }

    private JsonProviderCapacityStore ProviderStore() =>
        new(new CachePaths(Path.Combine(directory, ".provider-cache")));

    private sealed class SuccessfulProviderProbe(
        IProviderCapacityStore providerStore) : IProviderCapacityProbeService
    {
        public IReadOnlyList<string> SupportedAgents => ["claude", "codex", "copilot"];

        public async Task<ProviderCapacity> ProbeProviderAsync(
            TrackerConfig config,
            string agentType,
            string repositoryPath,
            Func<WorkerEvent, Task> emit,
            CancellationToken cancellationToken)
        {
            await providerStore.RecordAvailableAsync(
                agentType,
                DateTimeOffset.UtcNow,
                cancellationToken);
            return (await providerStore.GetAsync(agentType, cancellationToken))!;
        }
    }

    private sealed class SupportedProviderProbe : IProviderCapacityProbeService
    {
        public IReadOnlyList<string> SupportedAgents => ["claude", "codex", "copilot"];

        public Task<ProviderCapacity> ProbeProviderAsync(
            TrackerConfig config,
            string agentType,
            string repositoryPath,
            Func<WorkerEvent, Task> emit,
            CancellationToken cancellationToken) =>
            Task.FromException<ProviderCapacity>(new TrackerException(
                "PROVIDER_PROBE_UNAVAILABLE",
                "Synthetic provider execution is not enabled for this web test.",
                7));
    }

    private sealed class WebTestWorkspaceManager : IWorkspaceManager
    {
        public Task<Workspace> PrepareAsync(
            WorkspaceRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new Workspace(Path.GetFullPath(request.RepositoryPath)));
    }

    private sealed class RejectingAgentProcessRunner : IAgentProcessRunner
    {
        public Task<AgentRunResult> RunAsync(
            AgentInvocation invocation,
            IAgentAdapter adapter,
            TimeSpan timeout,
            IReadOnlyDictionary<string, string> grantEnvironment,
            Func<string, CancellationToken, Task>? sessionStarted,
            bool killOnCancellation,
            CancellationToken cancellationToken) =>
            throw new Xunit.Sdk.XunitException("No agent should run for an empty worker queue.");
    }

    private async Task<string?> RuntimeDispatchState()
    {
        var path = Path.Combine(directory, ".wrighty", ".wrighty-runtime-v1.json");
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        var dispatch = document.RootElement
            .GetProperty("items")
            .GetProperty("1")
            .GetProperty("pendingDispatch");
        return dispatch.ValueKind == JsonValueKind.Null
            ? null
            : dispatch.GetProperty("state").GetString();
    }

    private static HttpRequestMessage AuthenticatedGet(RunningServer host, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        return request;
    }

    private static async Task<HttpResponseMessage> PostForm(
        HttpClient client,
        RunningServer host,
        string handler,
        Dictionary<string, string> values)
    {
        values["__RequestVerificationToken"] = await GetAntiforgeryToken(client, host);
        return await PostFormRequest(client, host, handler, values);
    }

    private static Task<HttpResponseMessage> PostFormWithToken(
        HttpClient client,
        RunningServer host,
        string handler,
        Dictionary<string, string> values,
        string tokenSource)
    {
        values["__RequestVerificationToken"] =
            HiddenValue(tokenSource, "__RequestVerificationToken");
        return PostFormRequest(client, host, handler, values);
    }

    private static async Task<HttpResponseMessage> PostFormRequest(
        HttpClient client,
        RunningServer host,
        string handler,
        IReadOnlyDictionary<string, string> values)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{host.Origin}/?handler={handler}");
        request.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        request.Headers.Add("Origin", host.Origin);
        request.Content = new FormUrlEncodedContent(values);
        return await client.SendAsync(request);
    }

    private static async Task<string> GetAntiforgeryToken(HttpClient client, RunningServer host)
    {
        using var request = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Item&id=local%3A3");
        var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();
        return HiddenValue(html, "__RequestVerificationToken");
    }

    /// <summary>Reads an input's value by id, for a page carrying more than one of a field name.</summary>
    private static string ValueOfInput(string html, string id)
    {
        var marker = $"id=\"{id}\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The response did not contain an input with id '{id}'.");
        start = html.IndexOf("value=\"", start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The input '{id}' did not contain a value.");
        start += "value=\"".Length;
        return html[start..html.IndexOf('"', start)];
    }

    /// <summary>One agent that answers, so the console can be tested without a vendor installed.</summary>
    private sealed class StubModelDiscovery : Highbyte.Wrighty.Workers.IAgentModelDiscovery
    {
        public string Agent => "codex";

        public Task<Highbyte.Wrighty.Workers.AgentModelCatalog> DiscoverAsync(
            CancellationToken cancellationToken) =>
            Task.FromResult(new Highbyte.Wrighty.Workers.AgentModelCatalog(
                "codex",
                [
                    new Highbyte.Wrighty.Workers.AgentModel(
                        "gpt-5.6-sol",
                        Effort: Highbyte.Wrighty.Workers.EffortSupport.Yes,
                        SupportedEfforts: ["low", "high", "ultra"])
                ],
                CurrentModelId: "gpt-5.6-sol"));
    }

    private static string HiddenValue(string html, string name)
    {
        var marker = $"name=\"{name}\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The response did not contain the hidden field '{name}'.");
        start += marker.Length;
        start = html.IndexOf("value=\"", start, StringComparison.Ordinal);
        Assert.True(start >= 0, $"The hidden field '{name}' did not contain a value.");
        start += "value=\"".Length;
        var end = html.IndexOf('"', start);
        return html[start..end];
    }

    [GeneratedRegex("id=\\\"github-repository-link\\\"[\\s\\S]*?href=\\\"([^\\\"]+)\\\"")]
    private static partial Regex GitHubRepositoryLinkRegex();

    private static async Task<string?> ProblemTitle(HttpResponseMessage response)
    {
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return problem.RootElement.GetProperty("title").GetString();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed record RunningServer(
        string Origin,
        string LaunchUrl,
        string Token,
        CancellationTokenSource Cancellation,
        Task Run,
        LineChannelWriter Output)
    {
        public async Task Stop()
        {
            await Cancellation.CancelAsync();
            await Run;
            Cancellation.Dispose();
        }
    }

    private sealed class FixedConfigLoader(TrackerConfig config) : ITrackerConfigLoader
    {
        public Task<TrackerConfig> LoadAsync(string startDirectory, CancellationToken cancellationToken) => Task.FromResult(config);
    }

    private sealed class FixedBackendRegistry(ITrackerBackend backend)
        : ITrackerBackendRegistry
    {
        public ITrackerBackend Get(string backendName) => backend;
    }

    /// <summary>
    /// Delegates the backend contract without implementing <see cref="ITrackerDashboardBackend"/>,
    /// matching GitHub's web capabilities. A test using the Local Markdown backend directly could
    /// otherwise keep passing while session launch accidentally called a dashboard-only method.
    /// </summary>
    private sealed class NonDashboardBackend(ITrackerBackend inner) : ITrackerBackend
    {
        public string Name => inner.Name;

        public Highbyte.Wrighty.Addressing.IWorkItemAddressResolver AddressResolver =>
            inner.AddressResolver;

        public Task<BackendInitializationResult> InitializeAsync(
            TrackerConfig config, bool checkOnly, CancellationToken cancellationToken) =>
            inner.InitializeAsync(config, checkOnly, cancellationToken);

        public Task<IReadOnlyList<WorkItemSummary>> ListAsync(
            TrackerConfig config,
            ListWorkItemsRequest request,
            CancellationToken cancellationToken) =>
            inner.ListAsync(config, request, cancellationToken);

        public Task<WorkItemDetail?> GetAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            inner.GetAsync(config, id, cancellationToken);

        public Task<CreateWorkItemResult> CreateAsync(
            TrackerConfig config,
            CreateWorkItemOperation operation,
            CancellationToken cancellationToken) =>
            inner.CreateAsync(config, operation, cancellationToken);

        public Task<UpdateWorkItemResult> UpdateAsync(
            TrackerConfig config,
            WorkItemId id,
            UpdateWorkItemOperation operation,
            CancellationToken cancellationToken) =>
            inner.UpdateAsync(config, id, operation, cancellationToken);

        public Task<ClaimResult> TryClaimAsync(
            TrackerConfig config,
            WorkItemId id,
            AgentExecutionContext agentContext,
            CancellationToken cancellationToken) =>
            inner.TryClaimAsync(config, id, agentContext, cancellationToken);

        public Task<ClaimResult> TryClaimAsync(
            TrackerConfig config,
            WorkItemId id,
            AgentExecutionContext agentContext,
            CancellationToken cancellationToken,
            string? expectedClaimToken) =>
            inner.TryClaimAsync(
                config, id, agentContext, cancellationToken, expectedClaimToken);

        public Task<ClaimResult> TakeoverAsync(
            TrackerConfig config,
            WorkItemId id,
            AgentExecutionContext claimantContext,
            string? currentClaimToken,
            CancellationToken cancellationToken) =>
            inner.TakeoverAsync(
                config, id, claimantContext, currentClaimToken, cancellationToken);

        public Task<ClaimResult> RenewClaimAsync(
            TrackerConfig config,
            WorkItemId id,
            ClaimHandle claimHandle,
            string? workspacePath,
            string? sessionId,
            string? branch,
            CancellationToken cancellationToken) =>
            inner.RenewClaimAsync(
                config, id, claimHandle, workspacePath, sessionId, branch, cancellationToken);

        public Task<ClaimOwnershipResult> GetClaimOwnershipAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            inner.GetClaimOwnershipAsync(config, id, cancellationToken);

        public Task<AgentSessionRecord?> GetAgentSessionAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            inner.GetAgentSessionAsync(config, id, cancellationToken);

        public Task<WorkItemOperationalSnapshot?> GetOperationalAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            inner.GetOperationalAsync(config, id, cancellationToken);

        public Task ReleaseAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            inner.ReleaseAsync(config, id, cancellationToken);

        public Task ReleaseAsync(
            TrackerConfig config,
            WorkItemId id,
            ClaimHandle claimHandle,
            bool overrideClaimant,
            DispatchStateOnRelease dispatchState,
            CancellationToken cancellationToken) =>
            inner.ReleaseAsync(
                config,
                id,
                claimHandle,
                overrideClaimant,
                dispatchState,
                cancellationToken);

        public Task<ArchiveWorkItemResult> ArchiveAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            inner.ArchiveAsync(config, id, cancellationToken);

        public Task<ArchiveWorkItemResult> ArchiveAsync(
            TrackerConfig config,
            WorkItemId id,
            ClaimHandle claimHandle,
            CancellationToken cancellationToken) =>
            inner.ArchiveAsync(config, id, claimHandle, cancellationToken);

        public Task<ArchiveWorkItemResult> UnarchiveAsync(
            TrackerConfig config, WorkItemId id, CancellationToken cancellationToken) =>
            inner.UnarchiveAsync(config, id, cancellationToken);
    }

    private sealed class FixedIdentity(string identity) : IInstallationIdentityProvider
    {
        public Task<string> GetInstallationIdAsync(CancellationToken cancellationToken) => Task.FromResult(identity);
    }

    private sealed class RecordingWebContextApprovalService :
        Highbyte.Wrighty.ApprovedContext.IContextApprovalService
    {
        private static readonly DateTimeOffset Now =
            new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

        public WorkItemId? Inspected { get; private set; }
        public WorkItemId? Approved { get; private set; }

        public Task<Highbyte.Wrighty.ApprovedContext.ExecutionContextResult> InspectAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken)
        {
            Inspected = id;
            return Task.FromResult(Result(id));
        }

        public Task<Highbyte.Wrighty.ApprovedContext.ExecutionContextResult> ApproveAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken)
        {
            Approved = id;
            return Task.FromResult(Result(id));
        }

        public Task<Highbyte.Wrighty.ApprovedContext.ContextApprovalInvalidationDisposition>
            InvalidateAsync(
            TrackerConfig config,
            WorkItemId id,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        private static Highbyte.Wrighty.ApprovedContext.ExecutionContextResult Result(
            WorkItemId id)
        {
            var decisions = new[]
            {
                new Highbyte.Wrighty.ApprovedContext.DiscussionDecision(
                    "one",
                    Highbyte.Wrighty.ApprovedContext.DiscussionDecisionKind.Include,
                    Highbyte.Wrighty.ApprovedContext.DiscussionDecisionSource.Batch),
                new Highbyte.Wrighty.ApprovedContext.DiscussionDecision(
                    "two",
                    Highbyte.Wrighty.ApprovedContext.DiscussionDecisionKind.Include,
                    Highbyte.Wrighty.ApprovedContext.DiscussionDecisionSource.Batch),
                new Highbyte.Wrighty.ApprovedContext.DiscussionDecision(
                    "three",
                    Highbyte.Wrighty.ApprovedContext.DiscussionDecisionKind.Exclude,
                    Highbyte.Wrighty.ApprovedContext.DiscussionDecisionSource.Reaction)
            };
            var snapshot = new Highbyte.Wrighty.ApprovedContext.ExecutionContextSnapshot(
                id,
                "Approval test item",
                "Review this body.",
                new Highbyte.Wrighty.ApprovedContext.ContextApproval(
                    Highbyte.Wrighty.ApprovedContext.ContextApprovalSource.ProjectField,
                    Now,
                    Now),
                new Highbyte.Wrighty.ApprovedContext.BaseContentRevision("title", "body"),
                new Highbyte.Wrighty.ApprovedContext.ContextRevision(
                    2,
                    "sha256:1234567890abcdef1234567890abcdef",
                    Now),
                [],
                decisions);
            return Highbyte.Wrighty.ApprovedContext.ExecutionContextResult.Approved(snapshot);
        }
    }

    private sealed class RecordingBrowserLauncher : IBrowserLauncher
    {
        private readonly TaskCompletionSource<string> opened = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open(string url) => opened.TrySetResult(url);

        public Task<string> WaitForUrlAsync(CancellationToken cancellationToken) =>
            opened.Task.WaitAsync(cancellationToken);
    }

    private sealed class ThrowingBrowserLauncher : IBrowserLauncher
    {
        public void Open(string url) => throw new InvalidOperationException("Browser unavailable");
    }

    private sealed class InstalledAgentRuntimeCatalog : IAgentRuntimeCatalog
    {
        public AgentRuntimeSnapshot Snapshot() => new(
        [
            new AgentRuntime(
                "claude", "claude", true, AgentInstallationState.Installed, "/usr/bin/claude"),
            new AgentRuntime(
                "codex", "codex", true, AgentInstallationState.Installed, "/usr/bin/codex"),
            new AgentRuntime(
                "copilot", "copilot", true, AgentInstallationState.Installed, "/usr/bin/copilot")
        ]);
    }

    private sealed class RecordingAgentSessionLauncher : ILocalAgentSessionLauncher
    {
        public LocalAgentInvocation? CliInvocation { get; private set; }
        public DesktopLaunchAddress? DesktopAddress { get; private set; }

        public LocalSessionLaunchCapabilities GetCapabilities(string agentType) =>
            new(true, true);

        public Task<int> ExecuteAsync(
            LocalAgentInvocation invocation,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        public Task<SessionLaunchResult> LaunchCliAsync(
            LocalAgentInvocation invocation,
            CancellationToken cancellationToken)
        {
            CliInvocation = invocation;
            return Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Launched));
        }

        public Task<SessionLaunchResult> LaunchDesktopAsync(
            DesktopLaunchAddress address,
            CancellationToken cancellationToken)
        {
            DesktopAddress = address;
            return Task.FromResult(new SessionLaunchResult(SessionLaunchStatus.Launched));
        }
    }

    private sealed class LineChannelWriter : TextWriter
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();
        public override Encoding Encoding => Encoding.UTF8;
        public override Task WriteLineAsync(string? value) { lines.Writer.TryWrite(value ?? string.Empty); return Task.CompletedTask; }
        public ValueTask<string> ReadLineAsync(CancellationToken cancellationToken) => lines.Reader.ReadAsync(cancellationToken);
    }

    private sealed class URL
    {
        public URL(string value)
        {
            var uri = new Uri(value);
            Fragment = uri.Fragment
                .TrimStart('#')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .Where(part => part.Length == 2)
                .ToDictionary(
                    part => part[0],
                    part => Uri.UnescapeDataString(part[1]));
        }
        public IReadOnlyDictionary<string, string> Fragment { get; }
    }
}

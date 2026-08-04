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
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;
using Highbyte.Wrighty.Processes;
using Highbyte.Wrighty.Web;
using Highbyte.Wrighty.Workers;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;

namespace Highbyte.Wrighty.UnitTests.Web;

public sealed class WrightyWebServerTests : IDisposable
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
        Assert.Contains("id=\"operations-content\"", shell);
        Assert.Contains("id=\"provider-capacity-region\"", shell);
        Assert.Contains("<dialog id=\"confirmation-dialog\"", shell);
        Assert.Contains("id=\"confirmation-dialog-title\"", shell);
        Assert.Contains("id=\"confirmation-dialog-message\"", shell);
        Assert.Contains("id=\"confirmation-dialog-cancel\"", shell);
        Assert.Contains("id=\"confirmation-dialog-accept\"", shell);
        Assert.Contains("<meta name=\"wrighty-auth\" content=\"token\">", shell);
        Assert.Contains("id=\"copy-access-link\"", shell);
        Assert.Contains("<output id=\"copy-access-link-feedback\"", shell);
        Assert.True(
            shell.IndexOf("id=\"copy-access-link\"", StringComparison.Ordinal) <
            shell.IndexOf("id=\"provider-capacity-region\"", StringComparison.Ordinal));
        Assert.True(
            shell.IndexOf("id=\"provider-capacity-region\"", StringComparison.Ordinal) <
            shell.IndexOf("id=\"connection-status\"", StringComparison.Ordinal));
        Assert.True(
            shell.IndexOf("id=\"item-panel\"", StringComparison.Ordinal) <
            shell.IndexOf("id=\"confirmation-dialog\"", StringComparison.Ordinal));
        Assert.DoesNotContain("name=\"q\"", shell);
        Assert.DoesNotContain(">Load scope<", shell);

        var unauthorized = await client.GetAsync($"{host.Origin}/?handler=Board");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

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
        Assert.NotNull(board.Headers.ETag);

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
        Assert.Contains("Provider capacity", providerHtml);
        Assert.Contains("Available", providerHtml);
        Assert.NotNull(provider.Headers.ETag);

        await host.Stop();
    }

    [Fact]
    public async Task Operations_surface_reads_and_updates_typed_repository_configuration()
    {
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Operations");
        var response = await client.SendAsync(request);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Repository configuration", html);
        Assert.Contains("Configuration catalogue", html);
        Assert.Contains("Local worker processes", html);
        Assert.DoesNotContain("<th>Context</th>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Actions</th>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"context-approval-details\"", html, StringComparison.Ordinal);
        var revision = HiddenValue(html, "revision");

        var result = await PostForm(
            client,
            host,
            "Configuration",
            new Dictionary<string, string>
            {
                ["operation"] = "workflow",
                ["revision"] = revision,
                ["defaultPickFrom"] = "Ready",
                ["defaultPickTo"] = "Doing",
                ["defaultFinishTo"] = "Complete",
                ["configPath"] = Path.Combine(directory, "must-not-be-used.json")
            });
        var resultHtml = await result.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Contains("Configuration saved", resultHtml);
        var stored = await new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None);
        Assert.Equal("Ready", stored.DefaultPickFrom);
        Assert.Equal("Doing", stored.DefaultPickTo);
        Assert.Equal("Complete", stored.DefaultFinishTo);
        Assert.False(File.Exists(Path.Combine(directory, "must-not-be-used.json")));
        await host.Stop();
    }

    [Fact]
    public async Task Operations_surface_updates_worker_completion_archive_and_web_policies()
    {
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Operations");
        var html = await (await client.SendAsync(request)).Content.ReadAsStringAsync();

        html = await SaveAsync(
            html,
            new Dictionary<string, string>
            {
                ["operation"] = "worker",
                ["defaultAgent"] = "codex",
                ["workspaceMode"] = "worktree"
            });
        html = await SaveAsync(
            html,
            new Dictionary<string, string>
            {
                ["operation"] = "completion",
                ["completionCommit"] = "agent",
                ["completionIntegration"] = "merge-local"
            });
        html = await SaveAsync(
            html,
            new Dictionary<string, string>
            {
                ["operation"] = "archive",
                ["archiveStatuses"] = "Done, Todo"
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
        Assert.Equal("agent", stored.EffectiveWorker.Completion?.Commit);
        Assert.Equal("merge-local", stored.EffectiveWorker.Completion?.Integration);
        Assert.Equal(["Done", "Todo"], stored.Archive.OnStatuses);
        Assert.False(stored.EffectiveWeb.ProtectNonHumanClaims);
        Assert.Contains("<output id=\"configuration-save-notice\"", html);
        await host.Stop();

        async Task<string> SaveAsync(
            string currentHtml,
            Dictionary<string, string> values)
        {
            values["revision"] = HiddenValue(currentHtml, "revision");
            var response = await PostForm(client, host, "Configuration", values);
            var responseHtml = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("Configuration saved", responseHtml);
            return responseHtml;
        }
    }

    [Fact]
    public async Task Operations_surface_rejects_unknown_or_incomplete_configuration_updates()
    {
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Operations");
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
        await host.Stop();
    }

    [Fact]
    public async Task Configuration_update_reports_revision_conflict_without_overwriting()
    {
        var host = await StartServer(openBrowser: false);
        using var client = new HttpClient();
        using var request = AuthenticatedGet(host, $"{host.Origin}/?handler=Operations");
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
                ["defaultPickFrom"] = "Web edit",
                ["defaultPickTo"] = "In Progress",
                ["defaultFinishTo"] = "Done"
            });
        var resultHtml = await result.Content.ReadAsStringAsync();

        Assert.Contains("CONFIG_CONFLICT", resultHtml);
        Assert.Contains("value=\"Web edit\"", resultHtml);
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
            "<button type=\"button\" disabled>Probe in progress</button>",
            html);
        Assert.DoesNotContain("Probe Codex</button>", html);
        Assert.Contains("Probe Claude</button>", html);
        Assert.Contains("Probe Copilot</button>", html);
        Assert.Contains(
            "title=\"Wait for the active provider probe to finish.\"",
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

        Assert.Contains("Provider capacity", providerHtml);
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
            "Checked 3 providers: 3 available, 0 unavailable.",
            html);
        Assert.Contains("Probe Claude</button>", html);
        Assert.Contains("Probe Codex</button>", html);
        Assert.Contains("Probe Copilot</button>", html);
        Assert.Contains("Probe all</button>", html);
        Assert.Equal("wrighty:refresh", response.Headers.GetValues("HX-Trigger").Single());
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
        var host = await StartServer();
        using var client = new HttpClient();
        using var formRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Create");
        using var formResponse = await client.SendAsync(formRequest);
        var form = await formResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, formResponse.StatusCode);
        Assert.Contains("NEW ITEM", form);
        Assert.Contains("value=\"Todo\" selected", form);
        Assert.DoesNotContain("name=\"automaticExecutionAllowed\" value=\"true\" checked", form);
        Assert.Contains(
            "The agent policy only selects a provider when automatic execution is allowed.",
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
        var firstHtml = await first.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("Item created. Worker processing was not started.", firstHtml);
        Assert.Contains("Created from web", firstHtml);

        using var second = await PostForm(client, host, "Create", new(values));
        var secondHtml = await second.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("resumed without allocating a duplicate", secondHtml);
        Assert.Equal(
            before + 1,
            Directory.GetFiles(
                Path.Combine(directory, ".wrighty", "items"),
                "*.md").Length);

        await host.Stop();
    }

    [Fact]
    public async Task Board_queue_button_moves_a_backlog_item_into_the_worker_queue()
    {
        // The board's one-click queue action bundles claim, status move, and release; with the
        // worker queue on (the default) the move is also the automatic-execution authorization.
        var host = await StartServer(openBrowser: false, pickFrom: "Agent queue");
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
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var newId = HiddenValue(await created.Content.ReadAsStringAsync(), "id");

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
        Assert.Contains("Agent queue", detail);
        Assert.Contains("Allowed", detail);
        Assert.Contains("Unclaimed", detail);

        // The queued item left the backlog, so exactly its queue action disappeared.
        using var afterRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        var after = await (await client.SendAsync(afterRequest)).Content.ReadAsStringAsync();
        Assert.Equal(queueActionsBefore - 1, after.Split("handler=QueueItem").Length - 1);
        await host.Stop();
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
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Archived.", html);
        // The re-rendered detail is now the archived view, which offers Unarchive.
        Assert.Contains("Unarchive", html);
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
    public async Task Edit_form_sets_and_displays_managed_worker_eligibility_fields()
    {
        var host = await StartServer();
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
        var archiveHtml = await archive.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode);
        Assert.Contains("Archived.", archiveHtml);
        Assert.Contains(">Unarchive</button>", archiveHtml);

        foreach (var scope in new[] { "archived", "all" })
        {
            using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board&scope={scope}");
            var board = await client.SendAsync(boardRequest);
            Assert.Contains("Web claim item", await board.Content.ReadAsStringAsync());
        }

        using var unarchive = await PostForm(client, host, "Unarchive", new() { ["id"] = "local:3" });
        Assert.Equal(HttpStatusCode.OK, unarchive.StatusCode);
        Assert.Contains("Restored to the active dashboard.", await unarchive.Content.ReadAsStringAsync());
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
        Assert.Contains("Changing vendors requires an explicit cross-agent handoff", claimHtml);

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
        var host = await StartServer(scheduleRetry: true);
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
    public async Task Claude_desktop_stays_disabled_without_the_experimental_opt_in()
    {
        const string sessionId = "940cd4c6-bb95-84d8-a78a-73af49c898a0";
        var host = await StartServer(
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
    public void Header_and_item_panel_layout_contracts_are_embedded()
    {
        using var stream = typeof(WrightyWebServer).Assembly.GetManifestResourceStream(
            "Highbyte.Wrighty.Web.Assets.wrighty.css");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var stylesheet = reader.ReadToEnd();

        Assert.Contains(".app-header { position: relative; z-index: 20;", stylesheet);
        Assert.Contains(".app-identity { flex: 1 1 auto; min-width: 0;", stylesheet);
        Assert.Contains(".workspace-path { display: block; max-width: 100%; overflow: hidden;", stylesheet);
        Assert.Contains(".access-link-button { min-height: 2.15rem;", stylesheet);
        Assert.Contains(".app-header { align-items: start; flex-wrap: wrap;", stylesheet);
        Assert.Contains(
            ".item-panel { position: fixed; inset: 0 0 0 auto; width: min(44rem, 94vw); z-index: 30;",
            stylesheet);
        Assert.Contains(
            ".operations th.operations-action-cell, .operations td.operations-action-cell { text-align: right; }",
            stylesheet);
        Assert.Contains(
            ".context-approval-summary { display: flex; align-items: start; justify-content: space-between;",
            stylesheet);
        Assert.Contains(".panel-loading-status { display: flex; align-items: center;", stylesheet);
        Assert.Contains("@keyframes panel-loading-spin", stylesheet);
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
            Repository = "owner/repository",
            ProjectNumber = 1,
            SourcePath = Path.Combine(directory, TrackerConfigLoader.FileName),
            LocalMarkdown = new LocalMarkdownBackendConfig { Path = ".wrighty" }
        };
        var local = new LocalMarkdownTrackerBackend(
            new FixedIdentity("github-shell-test"),
            new SystemClock());
        await local.InitializeAsync(config, checkOnly: false, CancellationToken.None);
        await local.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest(
                    "Approval test item",
                    "Review this body.",
                    "Todo",
                    "P1"),
                false),
            CancellationToken.None);
        var tracker = new TrackerService(new FixedBackendRegistry(local));
        var contextApproval = new RecordingWebContextApprovalService();
        var output = new LineChannelWriter();
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new WrightyWebServer(
            new FixedConfigLoader(config),
            tracker,
            new RecordingBrowserLauncher(),
            directory,
            new WrightyWebServerDependencies(
                new GitWorkspaceInventory(new PathExecutableResolver()),
                ContextApproval: contextApproval));
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
        using var client = new HttpClient();

        var shell = await client.GetStringAsync(origin);
        Assert.Contains("GITHUB PROJECT", shell);
        Assert.Contains("id=\"operations-content\"", shell);
        Assert.DoesNotContain("id=\"board-search\"", shell);
        Assert.DoesNotContain(">New item</button>", shell);

        using var operationsRequest = AuthenticatedGet(
            new RunningServer(origin, launch, token, cancellation, run, output),
            $"{origin}/?handler=Operations");
        var operationsResponse = await client.SendAsync(operationsRequest);
        var operationsHtml = await operationsResponse.Content.ReadAsStringAsync();
        Assert.Contains("Local worker processes", operationsHtml);
        Assert.Contains("Repository configuration", operationsHtml);
        Assert.Contains("Open GitHub repository", operationsHtml);
        Assert.Contains("Validate GitHub target", operationsHtml);
        Assert.Contains("<th>Context</th>", operationsHtml);
        Assert.Contains(">Actions</th>", operationsHtml);
        Assert.Contains("id=\"context-approval-inspect-local-1\"", operationsHtml);
        Assert.Contains("id=\"context-approval-state-local-1\"", operationsHtml);
        Assert.Contains("hx-target=\"#item-panel\"", operationsHtml);
        Assert.Contains("data-panel-loading-label=\"Loading context approval…\"", operationsHtml);
        Assert.DoesNotContain("id=\"context-approval-details\"", operationsHtml);

        using var contextRequest = AuthenticatedGet(
            new RunningServer(origin, launch, token, cancellation, run, output),
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
        Assert.Contains("Repository configuration", validationHtml);

        using var boardRequest = AuthenticatedGet(
            new RunningServer(origin, launch, token, cancellation, run, output),
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
        string pickFrom = "Todo")
    {
        Directory.CreateDirectory(directory);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            DefaultPickFrom = pickFrom,
            SourcePath = Path.Combine(directory, TrackerConfigLoader.FileName),
            LocalMarkdown = new LocalMarkdownBackendConfig
            {
                Path = ".wrighty",
                // Includes the alternate workflow statuses the operations-surface tests save,
                // which are validated against this list.
                Statuses = ["Todo", "Agent queue", "In Progress", "Done", "Ready", "Doing", "Complete"]
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
            await backend.ReleasePreservingDispatchStateAsync(
                config,
                created.Id,
                new ClaimHandle(initialContext, initialClaim.ClaimToken),
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
                new InstalledAgentRuntimeCatalog(),
                sessionLauncher ?? new RecordingAgentSessionLauncher(),
                new RepositoryConfigurationService(configStore),
                new JsonWorkerInstanceRegistry(
                    new CachePaths(Path.Combine(directory, ".worker-cache")))));
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

using System.Net;
using System.Text;
using System.Threading.Channels;
using Highbyte.Wrighty;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Identity;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Time;
using Highbyte.Wrighty.Web;
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
        Assert.DoesNotContain("Hostile item", shell);
        Assert.Contains("allowEval\":false", shell);
        Assert.Contains("includeIndicatorStyles\":false", shell);
        Assert.Contains("timeout\":3000", shell);
        Assert.Contains("/assets/highlight-yaml.js", shell);
        Assert.Contains("id=\"board-search\"", shell);
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
        Assert.Contains("<dt>Claimant</dt><dd>Agent</dd>", html);
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
        Assert.DoesNotContain("name=\"automationEligible\" value=\"true\" checked", form);
        Assert.Contains("Choosing an agent does not enable eligibility", form);
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
            ["preferredAgent"] = "codex",
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
    public async Task Requests_reject_invalid_hosts_and_non_form_mutations()
    {
        var host = await StartServer();
        using var client = new HttpClient();

        using var invalidHost = new HttpRequestMessage(HttpMethod.Get, host.Origin);
        invalidHost.Headers.Host = "evil.example";
        var invalidHostResponse = await client.SendAsync(invalidHost);
        Assert.Equal(HttpStatusCode.BadRequest, invalidHostResponse.StatusCode);

        using var nonForm = new HttpRequestMessage(HttpMethod.Post, $"{host.Origin}/?handler=Claim");
        nonForm.Headers.Add(WrightyWebServer.TokenHeader, host.Token);
        nonForm.Headers.Add("Origin", host.Origin);
        nonForm.Content = new StringContent("id=local%3A3", Encoding.UTF8, "text/plain");
        var nonFormResponse = await client.SendAsync(nonForm);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, nonFormResponse.StatusCode);

        await host.Stop();
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
        Assert.Contains("<dt>Claimant</dt><dd>Human</dd>", itemHtml);
        Assert.DoesNotContain("<dt>Agent</dt>", itemHtml);

        await host.Stop();
    }

    [Fact]
    public async Task Claim_from_web_after_expiry_preserves_local_agent_session()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        var runtimeStatePath = Path.Combine(directory, ".wrighty", ".runtime-state.json");
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
        Assert.Contains("Save and hand back to Codex", html);

        using var itemRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Item&id=local%3A1");
        using var itemResponse = await client.SendAsync(itemRequest);
        var itemHtml = await itemResponse.Content.ReadAsStringAsync();
        Assert.Contains("<dt>Claimant</dt><dd>Human</dd>", itemHtml);
        Assert.Contains("Continue agent session", itemHtml);
        Assert.Contains("wrighty worker --item", itemHtml);
        var preservedState = await File.ReadAllTextAsync(runtimeStatePath);
        Assert.Contains("web-test-session", preservedState);

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
        Assert.Contains("name=\"automationEligible\"", claimHtml);
        Assert.Contains("name=\"preferredAgent\"", claimHtml);
        Assert.Contains("<code>wrighty-auto: true</code>", claimHtml);
        Assert.Contains("<code>wrighty-agent</code>", claimHtml);
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
            ["automationEligible"] = "true",
            ["preferredAgent"] = "claude",
            ["action"] = "save"
        });
        var savedHtml = await saveResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        Assert.Contains("<dt>Worker eligible</dt><dd>Yes</dd>", savedHtml);
        Assert.Contains("<dt>Preferred agent</dt><dd>Claude</dd>", savedHtml);

        using var editRequest = AuthenticatedGet(
            host,
            $"{host.Origin}/?handler=Edit&id=local%3A3");
        var editResponse = await client.SendAsync(editRequest);
        var editHtml = await editResponse.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, editResponse.StatusCode);
        Assert.Contains("name=\"automationEligible\"", editHtml);
        Assert.Contains("checked", editHtml);
        Assert.Contains("value=\"claude\" selected", editHtml);

        var itemPath = Path.Combine(directory, ".wrighty", "items", "003-web-claim-item.md");
        var document = await File.ReadAllTextAsync(itemPath);
        Assert.Contains("wrighty-auto: true", document);
        Assert.Contains("wrighty-agent: claude", document);
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
    public async Task Dashboard_reports_migration_required_for_legacy_claim_frontmatter()
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
        Assert.Contains("STORE_MIGRATION_REQUIRED", html);
        Assert.Contains("wrighty init", html);
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
        Assert.Contains($"<dt>Claimant</dt><dd>{claimantKind}</dd>", html);
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
        Assert.Contains("Queued to resume", html);
        Assert.Contains("<dt>Worker activity</dt><dd>queued</dd>", html);
        Assert.Contains("Claim for editing", html);
        Assert.DoesNotContain("Take over for editing", html);
        Assert.DoesNotContain("Queue for worker", html);

        await host.Stop();
    }

    [Fact]
    public async Task Expired_paused_agent_item_can_be_queued_directly()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        var runtimeStatePath = Path.Combine(directory, ".wrighty", ".runtime-state.json");
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
        Assert.Contains("Queued to resume", html);
        Assert.Contains("<dt>Worker activity</dt><dd>queued</dd>", html);

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
        Assert.Contains("Claimant</dt><dd>Agent", beforeHtml);
        Assert.Contains("Agent</dt><dd>Codex", beforeHtml);
        Assert.DoesNotContain(">Edit</button>", beforeHtml);

        using var takeover = await PostForm(client, host, "Takeover", new() { ["id"] = "local:1" });
        var html = await takeover.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, takeover.StatusCode);
        Assert.Contains("Takeover complete", html);
        Assert.Contains("Edit work item", html);
        Assert.Contains("expectedClaimGeneration", html);
        Assert.DoesNotContain("Resume agent session", html);
        Assert.DoesNotContain("WRIGHTY_CLAIM_TOKEN=", html);
        Assert.Contains("Save and hand back to Codex", html);
        Assert.Contains("Save and queue for worker", html);
        Assert.Contains("actions edit-actions", html);
        Assert.Contains("More actions…", html);
        Assert.Contains("Save and release", html);
        Assert.Contains("Release without saving", html);
        Assert.True(
            html.IndexOf("actions-secondary", StringComparison.Ordinal) <
            html.IndexOf("actions-primary", StringComparison.Ordinal));
        Assert.Contains("data-confirm-message=", html);
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
            ["automationEligible"] = "true",
            ["action"] = "save"
        });
        var savedHtml = await save.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
        Assert.Contains("Saved. The claim remains active.", savedHtml);
        Assert.Contains("Claimant</dt><dd>Human", savedHtml);
        Assert.Contains("Continue agent session", savedHtml);
        Assert.Contains("<details class=\"resume-address\" data-copy-scope>", savedHtml);
        Assert.Contains("1 option", savedHtml);
        Assert.Contains("Headless worker", savedHtml);
        Assert.Contains("wrighty worker --item", savedHtml);
        Assert.Contains("--resume --yes", savedHtml);
        Assert.Contains("WRIGHTY_CONFIG_PATH=", savedHtml);
        Assert.Contains("WRIGHTY_CLAIM_TOKEN=", savedHtml);
        Assert.Contains("data-copy-target=\"headless-resume-command\"", savedHtml);
        Assert.DoesNotContain("codex resume", savedHtml);
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
            ["automationEligible"] = "true",
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
            ["automationEligible"] = "true",
            ["preferredAgent"] = "codex",
            ["action"] = "save-queue"
        });
        var html = await queued.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, queued.StatusCode);
        Assert.Contains("Saved and queued", html);
        Assert.Contains("Queued to resume", html);
        Assert.Contains("Claim for editing", html);
        Assert.Contains("<dt>Worker activity</dt><dd>queued</dd>", html);
        Assert.DoesNotContain("WRIGHTY_CLAIM_TOKEN=", html);

        using var boardRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Board");
        using var board = await client.SendAsync(boardRequest);
        var boardHtml = await board.Content.ReadAsStringAsync();
        Assert.Contains("activity-queued", boardHtml);
        Assert.Contains("Queued to resume", boardHtml);
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
        Assert.Contains("Saved and handed back to Codex.", html);
        Assert.Contains("Claimant</dt><dd>Agent", html);
        Assert.Contains("Agent</dt><dd>Codex", html);
        Assert.Contains("agent:web-handback:", html);
        Assert.Contains("Continue agent session", html);
        Assert.Contains("<details class=\"resume-address\" data-copy-scope>", html);
        Assert.Contains("2 options", html);
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
    public async Task Embedded_first_party_assets_are_served_and_unknown_assets_are_not()
    {
        var host = await StartServer();
        using var client = new HttpClient();

        var css = await client.GetAsync($"{host.Origin}/assets/wrighty.css");
        var script = await client.GetAsync($"{host.Origin}/assets/app.js");
        var stylesheet = await css.Content.ReadAsStringAsync();
        var applicationScript = await script.Content.ReadAsStringAsync();
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
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Contains("highlightElement", applicationScript);
        Assert.Contains("htmx:afterSwap", applicationScript);
        Assert.Contains("dataset.confirmMessage", applicationScript);
        Assert.Contains("navigator.clipboard?.writeText", applicationScript);
        Assert.Contains("document.execCommand(\"copy\")", applicationScript);
        Assert.Contains("copyValue(copyButton)", applicationScript);
        Assert.Contains("refreshExpandableValues(event.detail.target)", applicationScript);
        Assert.Contains("toggleExpandableValue(expandButton)", applicationScript);
        Assert.Contains("target.scrollWidth <= target.clientWidth", applicationScript);
        Assert.Contains("`${count} of ${total}`", applicationScript);
        Assert.Contains("countElement.dataset.tooltip = description", applicationScript);
        Assert.Contains("countElement.setAttribute(\"aria-label\", description)", applicationScript);
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
    public async Task GitHub_backend_is_rejected_before_host_startup()
    {
        var config = new TrackerConfig
        {
            Backend = "github",
            Repository = "owner/repository",
            ProjectNumber = 1,
            SourcePath = Path.Combine(directory, TrackerConfigLoader.FileName)
        };
        var local = new LocalMarkdownTrackerBackend(new FixedIdentity("worker"), new SystemClock());
        var tracker = new TrackerService(new TrackerBackendRegistry([local]));
        var server = new WrightyWebServer(new FixedConfigLoader(config), tracker, new RecordingBrowserLauncher(), directory);

        var exception = await Assert.ThrowsAsync<Highbyte.Wrighty.Errors.TrackerException>(() =>
            server.RunAsync(new WebServerOptions(0, false), TextWriter.Null, CancellationToken.None));
        Assert.Equal("WEB_BACKEND_UNSUPPORTED", exception.Code);
    }

    private async Task<RunningServer> StartServer(
        bool protectNonHumanClaims = true,
        bool openBrowser = true,
        IBrowserLauncher? browserLauncher = null)
    {
        Directory.CreateDirectory(directory);
        var config = new TrackerConfig
        {
            Backend = "local-markdown",
            SourcePath = Path.Combine(directory, TrackerConfigLoader.FileName),
            LocalMarkdown = new LocalMarkdownBackendConfig { Path = ".wrighty" },
            Web = new WebConfig { ProtectNonHumanClaims = protectNonHumanClaims }
        };
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
                    AutomationEligible: true,
                    PreferredAgent: "codex"),
                false),
            CancellationToken.None);
        var createdPath = Path.Combine(directory, ".wrighty", "items", "001-hostile-item.md");
        var createdContent = await File.ReadAllTextAsync(createdPath);
        await File.WriteAllTextAsync(createdPath, createdContent.Replace(
            "status: In Progress",
            "status: In Progress\ntestNode:\n  nodefield1: a long hierarchical value that must wrap inside the disclosure rather than clip\n  nodefield2: 42"));
        var initialContext = new AgentExecutionContext(
            "codex",
            "web-test-session",
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
            "web-test-session",
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
                    WorkerState: OptionalValue<string?>.From(
                        WorkerDispatchStates.NeedsAttention)),
                false,
                ClaimHandle: new ClaimHandle(initialContext, initialClaim.ClaimToken)),
            CancellationToken.None);
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
            new AgentExecutionContext("claude", "other-session", AgentContextSource.ExplicitOption),
            CancellationToken.None);
        await backend.CreateAsync(
            config,
            new CreateWorkItemOperation(
                new CreateWorkItemRequest("Web claim item", "Body", "Todo", "P3"),
                false),
            CancellationToken.None);
        foreach (var (title, context) in new[]
        {
            ("Copilot claim", new AgentExecutionContext("copilot", "copilot-session", AgentContextSource.ExplicitOption)),
            ("Other agent claim", new AgentExecutionContext("other", "other-agent-session", AgentContextSource.ExplicitOption)),
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
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var output = new LineChannelWriter();
        var browser = browserLauncher ?? new RecordingBrowserLauncher();
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new WrightyWebServer(new FixedConfigLoader(config), tracker, browser, directory);
        var run = server.RunAsync(new WebServerOptions(0, openBrowser), output, cancellation.Token);
        var prefix = "Wrighty web server listening on ";
        var origin = (await output.ReadLineAsync(cancellation.Token))[prefix.Length..];
        var launch = (await output.ReadLineAsync(cancellation.Token))["Open ".Length..];
        var token = new URL(launch).Fragment["token"];
        if (browser is RecordingBrowserLauncher recordingBrowser && openBrowser)
        {
            Assert.Equal(launch, await recordingBrowser.WaitForUrlAsync(cancellation.Token));
        }
        return new RunningServer(origin, token, cancellation, run, output);
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

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed record RunningServer(
        string Origin,
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

    private sealed class FixedIdentity(string identity) : IWorkerIdentityProvider
    {
        public Task<string> GetIdentityAsync(CancellationToken cancellationToken) => Task.FromResult(identity);
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
            Fragment = uri.Fragment.TrimStart('#').Split('&').Select(part => part.Split('=', 2)).ToDictionary(part => part[0], part => Uri.UnescapeDataString(part[1]));
        }
        public IReadOnlyDictionary<string, string> Fragment { get; }
    }
}

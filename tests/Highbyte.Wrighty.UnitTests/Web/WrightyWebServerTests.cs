using System.Net;
using System.Text;
using System.Threading.Channels;
using Highbyte.Wrighty;
using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Backends;
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
        Assert.Contains("<dt>Claimant</dt><dd>Agent</dd>", html);
        Assert.Contains("<dt>Agent</dt><dd>Codex</dd>", html);
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

        using var saveResponse = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:3",
            ["expectedRevision"] = revision,
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
        var revision = HiddenValue(await claimResponse.Content.ReadAsStringAsync(), "expectedRevision");
        var values = new Dictionary<string, string>
        {
            ["id"] = "local:3",
            ["expectedRevision"] = revision,
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
    public async Task Save_rejects_oversized_markdown_and_preserves_the_draft()
    {
        var host = await StartServer();
        using var client = new HttpClient();
        using var claimResponse = await PostForm(client, host, "Claim", new() { ["id"] = "local:3" });
        var revision = HiddenValue(await claimResponse.Content.ReadAsStringAsync(), "expectedRevision");
        var oversizedBody = new string('x', 1_000_001);

        using var response = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:3",
            ["expectedRevision"] = revision,
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
        var revision = HiddenValue(await claimResponse.Content.ReadAsStringAsync(), "expectedRevision");

        using var response = await PostForm(client, host, "Save", new()
        {
            ["id"] = "local:3",
            ["expectedRevision"] = revision,
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
        Assert.Contains("Web changes are disabled while non-human claim protection is enabled", itemHtml);
        Assert.Contains("Explicit takeover is planned for a future release", itemHtml);
        Assert.DoesNotContain(">Edit</button>", itemHtml);
        Assert.DoesNotContain(">Release</button>", itemHtml);
        Assert.DoesNotContain(">Archive</button>", itemHtml);

        using var editRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Edit&id=local%3A1");
        var editResponse = await client.SendAsync(editRequest);
        Assert.Equal(HttpStatusCode.Conflict, editResponse.StatusCode);
        Assert.Contains("WEB_CLAIM_PROTECTED", await editResponse.Content.ReadAsStringAsync());

        foreach (var handler in new[] { "Claim", "Release", "Archive" })
        {
            using var response = await PostForm(client, host, handler, new() { ["id"] = "local:1" });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("WEB_CLAIM_PROTECTED", await response.Content.ReadAsStringAsync());
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
        Assert.Contains("WEB_CLAIM_PROTECTED", await saveResponse.Content.ReadAsStringAsync());

        await host.Stop();
    }

    [Fact]
    public async Task Non_human_claim_protection_can_be_disabled()
    {
        var host = await StartServer(protectNonHumanClaims: false);
        using var client = new HttpClient();
        using var itemRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Item&id=local%3A1");
        var itemResponse = await client.SendAsync(itemRequest);
        var itemHtml = await itemResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, itemResponse.StatusCode);
        Assert.Contains(">Edit</button>", itemHtml);
        Assert.DoesNotContain("non-human claim protection", itemHtml);

        using var editRequest = AuthenticatedGet(host, $"{host.Origin}/?handler=Edit&id=local%3A1");
        var editResponse = await client.SendAsync(editRequest);
        Assert.Equal(HttpStatusCode.OK, editResponse.StatusCode);
        Assert.Contains("Edit work item", await editResponse.Content.ReadAsStringAsync());

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
    public async Task Embedded_first_party_assets_are_served_and_unknown_assets_are_not()
    {
        var host = await StartServer();
        using var client = new HttpClient();

        var css = await client.GetAsync($"{host.Origin}/assets/wrighty.css");
        var script = await client.GetAsync($"{host.Origin}/assets/app.js");
        var missing = await client.GetAsync($"{host.Origin}/assets/missing.js");

        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
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
                    "Todo",
                    "P1"),
                false),
            CancellationToken.None);
        await backend.TryClaimAsync(
            config,
            created.Id,
            new AgentExecutionContext("codex", "web-test-session", AgentContextSource.ExplicitOption),
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
            ("Automation claim", new AgentExecutionContext(null, null, AgentContextSource.ExplicitOption, ClaimantKind: ClaimantKind.Automation)),
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

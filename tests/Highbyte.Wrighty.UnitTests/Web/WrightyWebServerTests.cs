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

    private async Task<RunningServer> StartServer(bool protectNonHumanClaims = true)
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
        var tracker = new TrackerService(new TrackerBackendRegistry([backend]));
        var output = new LineChannelWriter();
        var browser = new RecordingBrowserLauncher();
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var server = new WrightyWebServer(new FixedConfigLoader(config), tracker, browser, directory);
        var run = server.RunAsync(new WebServerOptions(0, true), output, cancellation.Token);
        var prefix = "Wrighty web server listening on ";
        var origin = (await output.ReadLineAsync(cancellation.Token))[prefix.Length..];
        var launch = (await output.ReadLineAsync(cancellation.Token))["Open ".Length..];
        var token = new URL(launch).Fragment["token"];
        Assert.Equal(launch, await browser.WaitForUrlAsync(cancellation.Token));
        return new RunningServer(origin, token, cancellation, run);
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
        const string marker = "name=\"__RequestVerificationToken\" type=\"hidden\" value=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "The mutation form did not contain an antiforgery token.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        return html[start..end];
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed record RunningServer(string Origin, string Token, CancellationTokenSource Cancellation, Task Run)
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

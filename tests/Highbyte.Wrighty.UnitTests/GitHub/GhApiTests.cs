using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;

namespace Highbyte.Wrighty.UnitTests.GitHub;

public sealed class GhApiTests
{
    [Fact]
    public async Task Conditional_get_returns_body_and_response_metadata()
    {
        var process = new RecordingProcess(new GhProcessResult(
            0,
            "HTTP/2.0 200 OK\r\n" +
            "Etag: W/\"comment-1\"\r\n" +
            "Link: <https://api.github.com/items?page=2>; rel=\"next\"\r\n" +
            "\r\n{\"id\":900}",
            string.Empty));

        var response = await new GhApi(process).GetVersionedConditionalAsync(
            "github.com", "repos/owner/repo/issues/comments/900", "2022-11-28",
            "W/\"comment-0\"", CancellationToken.None);

        Assert.False(response.NotModified);
        Assert.Equal("W/\"comment-1\"", response.ETag);
        Assert.Contains("rel=\"next\"", response.Link, StringComparison.Ordinal);
        Assert.Equal(900, response.Body!.Value.GetProperty("id").GetInt32());
        Assert.Contains(
            process.Arguments!, value => value == "If-None-Match: W/\"comment-0\"");
    }

    [Fact]
    public async Task Conditional_get_accepts_ghs_nonzero_exit_for_not_modified()
    {
        var process = new RecordingProcess(new GhProcessResult(
            1,
            "HTTP/2.0 304 Not Modified\nEtag: \"comment-1\"\n\n",
            "gh: HTTP 304"));

        var response = await new GhApi(process).GetVersionedConditionalAsync(
            "github.com", "repos/owner/repo/issues/comments/900", "2022-11-28",
            "\"comment-1\"", CancellationToken.None);

        Assert.True(response.NotModified);
        Assert.Equal("\"comment-1\"", response.ETag);
        Assert.Null(response.Body);
    }

    [Fact]
    public async Task Conditional_get_preserves_authentication_errors_without_http_output()
    {
        var process = new RecordingProcess(new GhProcessResult(
            1, string.Empty, "gh: authentication required; run gh auth login"));

        var error = await Assert.ThrowsAsync<TrackerException>(() =>
            new GhApi(process).GetVersionedConditionalAsync(
                "github.com", "repos/owner/repo/issues/comments/900", "2022-11-28",
                null, CancellationToken.None));

        Assert.Equal("GH_AUTH_REQUIRED", error.Code);
    }

    private sealed class RecordingProcess(GhProcessResult result) : IGhProcess
    {
        public IReadOnlyList<string>? Arguments { get; private set; }

        public Task<GhProcessResult> RunAsync(
            IReadOnlyList<string> arguments,
            string? standardInput,
            CancellationToken cancellationToken)
        {
            Arguments = arguments;
            return Task.FromResult(result);
        }
    }
}

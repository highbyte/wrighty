using Highbyte.Wrighty.GitHub;

namespace Highbyte.Wrighty.UnitTests.GitHub;

/// <summary>
/// The login Wrighty posts as, which is how it recognises its own comments.
///
/// The failure direction is the point. This answer decides what gets removed from the content a
/// maintainer reviews, so an identity resolved wrongly in the permissive direction would hide
/// requirements from an agent. Every way of not knowing has to come back null.
/// </summary>
public sealed class GitHubViewerIdentityTests
{
    private sealed class ScriptedGh(params GhProcessResult[] results) : IGhProcess
    {
        private readonly Queue<GhProcessResult> queue = new(results);

        public int Calls { get; private set; }

        public Task<GhProcessResult> RunAsync(
            IReadOnlyList<string> arguments, string? standardInput, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(queue.Count > 0
                ? queue.Dequeue()
                : new GhProcessResult(1, string.Empty, "no scripted response"));
        }
    }

    private static GitHubViewerIdentity Identity(IGhProcess process) => new(new GhApi(process));

    [Fact]
    public async Task TheAuthenticatedLoginIsReturned()
    {
        var identity = Identity(new ScriptedGh(
            new GhProcessResult(0, """{"login":"wrighty-bot"}""", string.Empty)));

        Assert.Equal(
            "wrighty-bot", await identity.GetLoginAsync("github.com", CancellationToken.None));
    }

    [Fact]
    public async Task TheLookupHappensOncePerHost()
    {
        // A conversation read asks this question for every comment, and the worker polls. The token
        // cannot change under a running process, so re-asking would be a request per iteration.
        var process = new ScriptedGh(
            new GhProcessResult(0, """{"login":"wrighty-bot"}""", string.Empty));
        var identity = Identity(process);

        await identity.GetLoginAsync("github.com", CancellationToken.None);
        await identity.GetLoginAsync("github.com", CancellationToken.None);
        await identity.GetLoginAsync("github.com", CancellationToken.None);

        Assert.Equal(1, process.Calls);
    }

    [Fact]
    public async Task AFailedLookupIsNullRatherThanAGuess()
    {
        // Unauthenticated, rate limited, offline: all the same answer. The caller excludes nothing,
        // every marker comment stays ordinary discussion, and the launch gate decides it as usual.
        var identity = Identity(new ScriptedGh(
            new GhProcessResult(1, string.Empty, "gh: not authenticated")));

        Assert.Null(await identity.GetLoginAsync("github.com", CancellationToken.None));
    }

    [Fact]
    public async Task AResponseWithoutALoginIsNull()
    {
        var identity = Identity(new ScriptedGh(
            new GhProcessResult(0, """{"id":42}""", string.Empty)));

        Assert.Null(await identity.GetLoginAsync("github.com", CancellationToken.None));
    }

    [Fact]
    public async Task ABlankLoginIsNull()
    {
        // An empty string would compare equal to nothing useful, but it is not null — and a caller
        // checking only for null would treat it as an identity it could match against.
        var identity = Identity(new ScriptedGh(
            new GhProcessResult(0, """{"login":"   "}""", string.Empty)));

        Assert.Null(await identity.GetLoginAsync("github.com", CancellationToken.None));
    }

    [Fact]
    public async Task EachHostIsResolvedSeparately()
    {
        var identity = Identity(new ScriptedGh(
            new GhProcessResult(0, """{"login":"on-dotcom"}""", string.Empty),
            new GhProcessResult(0, """{"login":"on-enterprise"}""", string.Empty)));

        Assert.Equal("on-dotcom", await identity.GetLoginAsync("github.com", CancellationToken.None));
        Assert.Equal(
            "on-enterprise", await identity.GetLoginAsync("ghe.example", CancellationToken.None));
    }
}

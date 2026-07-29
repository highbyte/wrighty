using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.UnitTests.Configuration;

/// <summary>
/// The logins whose comments count as decided without moving the approval field.
///
/// A setting that names an author who does not exist is worse than one that errors: it reads as a
/// configured trust that silently never applies, and the operator finds out by wondering why they
/// are still being asked to approve their own comments.
/// </summary>
public sealed class TrustedCommentAuthorsTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"wrighty-trusted-authors-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private async Task<TrackerConfig> LoadAsync(string authorsJson)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, TrackerConfigLoader.FileName),
            $$"""
            {
              "backend": "github",
              "github": {
                "repository": "owner/repo",
                "projectNumber": 1,
                "trustedCommentAuthors": {{authorsJson}}
              }
            }
            """);
        return await new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None);
    }

    [Fact]
    public async Task TheDocumentedKeyBindsFromConfiguration()
    {
        var config = await LoadAsync("""["first-login", "second-login"]""");

        Assert.Equal(["first-login", "second-login"], config.TrustedCommentAuthors);
    }

    [Fact]
    public void NobodyIsTrustedWhenNothingIsConfigured()
    {
        // The default has to be inert. Trusting an author is a decision about who can put content
        // in front of an unattended agent, and an upgrade must not make it for anyone.
        Assert.Empty(new TrackerConfig().TrustedCommentAuthors);
    }

    [Fact]
    public async Task AnEmptyListTrustsNobody()
    {
        Assert.Empty((await LoadAsync("[]")).TrustedCommentAuthors);
    }

    [Fact]
    public async Task ABlankEntryIsRejected()
    {
        // It would match no author at all, so the file would claim a trust that never applies.
        var failure = await Assert.ThrowsAsync<TrackerException>(() => LoadAsync("""["  "]"""));

        Assert.Equal("CONFIG_INVALID", failure.Code);
        Assert.Contains("must not contain an empty entry", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASurroundingSpaceIsRejectedRatherThanTrimmed()
    {
        // A GitHub login never has one, so this is a typo rather than an intent to be guessed at —
        // and silently trimming would make the file and the behaviour disagree.
        var failure =
            await Assert.ThrowsAsync<TrackerException>(() => LoadAsync("""[" some-login"]"""));

        Assert.Equal("CONFIG_INVALID", failure.Code);
        Assert.Contains("whitespace", failure.Message, StringComparison.Ordinal);
    }
}

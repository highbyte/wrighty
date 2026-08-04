using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.UnitTests.Configuration;

/// <summary>
/// The logins whose +1/-1 reactions decide pending comments.
///
/// The same rules as trusted comment authors, for the same reason: a configured authority that
/// silently never applies is worse than an error, because the operator finds out by wondering why
/// their reactions decide nothing.
/// </summary>
public sealed class ContextApproversTests : IDisposable
{
    private readonly string directory =
        Path.Combine(Path.GetTempPath(), $"wrighty-context-approvers-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private async Task<TrackerConfig> LoadAsync(string approversJson)
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
                "contextApprovers": {{approversJson}}
              }
            }
            """);
        return await new TrackerConfigLoader().LoadAsync(directory, CancellationToken.None);
    }

    [Fact]
    public async Task TheDocumentedKeyBindsFromConfiguration()
    {
        var config = await LoadAsync("""["first-login", "second-login"]""");

        Assert.Equal(["first-login", "second-login"], config.ContextApprovers);
    }

    [Fact]
    public void NobodyApprovesWhenNothingIsConfigured()
    {
        // The default has to be inert: naming an approver is a decision about who controls what an
        // unattended agent reads, and an upgrade must not make it for anyone.
        Assert.Empty(new TrackerConfig().ContextApprovers);
    }

    [Fact]
    public async Task AnEmptyListAuthorisesNobody()
    {
        Assert.Empty((await LoadAsync("[]")).ContextApprovers);
    }

    [Fact]
    public async Task ABlankEntryIsRejected()
    {
        var failure = await Assert.ThrowsAsync<TrackerException>(() => LoadAsync("""["  "]"""));

        Assert.Equal("CONFIG_INVALID", failure.Code);
        Assert.Contains("must not contain an empty entry", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASurroundingSpaceIsRejectedRatherThanTrimmed()
    {
        var failure =
            await Assert.ThrowsAsync<TrackerException>(() => LoadAsync("""[" some-login"]"""));

        Assert.Equal("CONFIG_INVALID", failure.Code);
        Assert.Contains("whitespace", failure.Message, StringComparison.Ordinal);
        Assert.Contains("contextApprovers", failure.Message, StringComparison.Ordinal);
    }
}

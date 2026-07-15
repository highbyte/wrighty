using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.GitHub;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.Addressing;

public sealed class GitHubWorkItemAddressResolverTests
{
    private static readonly TrackerConfig Config = new()
    {
        Repository = "Owner/Repo",
        ProjectNumber = 1
    };

    [Theory]
    [InlineData("42")]
    [InlineData("#42")]
    [InlineData("owner/repo#42")]
    [InlineData("github:owner/repo#42")]
    [InlineData("https://github.com/owner/repo/issues/42")]
    public void Resolve_accepts_every_github_input_form(string input)
    {
        var resolver = new GitHubWorkItemAddressResolver();

        var id = resolver.Resolve(input, Config);

        Assert.Equal("github:Owner/Repo#42", id.Value);
        Assert.Equal("#42", resolver.FormatShort(id, Config));
    }

    [Fact]
    public void Resolve_accepts_the_configured_enterprise_host()
    {
        var config = Config with { GitHubHost = "github.example.test" };

        var id = new GitHubWorkItemAddressResolver().Resolve(
            "https://github.example.test/owner/repo/issues/42",
            config);

        Assert.Equal("github:Owner/Repo#42", id.Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("999999999999999999999")]
    [InlineData("owner/repo/pull/42")]
    [InlineData("https://github.com/owner/repo/pull/42")]
    [InlineData("https://example.test/owner/repo/issues/42")]
    public void Resolve_rejects_malformed_or_non_issue_ids(string input)
    {
        var exception = Assert.Throws<TrackerException>(
            () => new GitHubWorkItemAddressResolver().Resolve(input, Config));

        Assert.Equal("WORK_ITEM_ID_INVALID", exception.Code);
        Assert.Equal(2, exception.ExitCode);
    }

    [Fact]
    public void Resolve_rejects_a_different_repository()
    {
        var exception = Assert.Throws<TrackerException>(
            () => new GitHubWorkItemAddressResolver().Resolve("owner/other#42", Config));

        Assert.Equal("WORK_ITEM_REPOSITORY_MISMATCH", exception.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" value")]
    [InlineData("value ")]
    [InlineData("value\n")]
    public void WorkItemId_rejects_transport_unsafe_values(string value)
    {
        var exception = Assert.Throws<TrackerException>(() => new WorkItemId(value));

        Assert.Equal("WORK_ITEM_ID_INVALID", exception.Code);
    }
}

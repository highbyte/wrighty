using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.LocalMarkdown;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.LocalMarkdown;

public sealed class LocalMarkdownWorkItemAddressResolverTests
{
    private static readonly TrackerConfig Config = new() { Backend = "local-markdown" };

    [Theory]
    [InlineData("42")]
    [InlineData("#42")]
    [InlineData("local:42")]
    [InlineData("LOCAL:42")]
    [InlineData("42-fix-timer")]
    [InlineData("42-fix-timer.md")]
    [InlineData("42-FIX-TIMER.MD")]
    [InlineData("/tmp/items/42-fix-timer.md")]
    public void Resolve_accepts_supported_local_references(string input)
    {
        var result = new LocalMarkdownWorkItemAddressResolver().Resolve(input, Config);

        Assert.Equal("local:42", result.Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("local:0")]
    [InlineData("-1")]
    [InlineData("item-42.md")]
    [InlineData("42-.md")]
    [InlineData("42_bad.md")]
    [InlineData("42-title.txt")]
    [InlineData("999999999999999999999999")]
    public void Resolve_rejects_invalid_local_references(string input)
    {
        var exception = Assert.Throws<TrackerException>(() =>
            new LocalMarkdownWorkItemAddressResolver().Resolve(input, Config));

        Assert.Equal("WORK_ITEM_ID_INVALID", exception.Code);
        Assert.Equal(2, exception.ExitCode);
    }

    [Fact]
    public void Resolve_preserves_the_shared_work_item_input_validation()
    {
        var exception = Assert.Throws<TrackerException>(() =>
            new LocalMarkdownWorkItemAddressResolver().Resolve(" 42 ", Config));

        Assert.Equal("WORK_ITEM_ID_INVALID", exception.Code);
        Assert.Contains("surrounding whitespace", exception.Message);
    }

    [Theory]
    [InlineData("local:1", 1)]
    [InlineData("LOCAL:42", 42)]
    [InlineData("local:2147483647", int.MaxValue)]
    public void Decode_returns_positive_canonical_number(string input, int expected)
    {
        Assert.Equal(expected, LocalMarkdownWorkItemAddressResolver.Decode(new WorkItemId(input)));
    }

    [Theory]
    [InlineData("local:0")]
    [InlineData("local:-1")]
    [InlineData("local:42-title")]
    [InlineData("github:owner/repo#42")]
    [InlineData("local:999999999999999999999999")]
    public void Decode_rejects_noncanonical_or_invalid_ids(string input)
    {
        var exception = Assert.Throws<TrackerException>(() =>
            LocalMarkdownWorkItemAddressResolver.Decode(new WorkItemId(input)));

        Assert.Equal("WORK_ITEM_ID_INVALID", exception.Code);
    }

    [Fact]
    public void FromNumber_and_FormatShort_produce_canonical_forms()
    {
        var resolver = new LocalMarkdownWorkItemAddressResolver();
        var id = LocalMarkdownWorkItemAddressResolver.FromNumber(42);

        Assert.Equal("local:42", id.Value);
        Assert.Equal("#42", resolver.FormatShort(id, Config));
    }
}

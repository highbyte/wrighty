using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.Models;

/// <summary>
/// The one ranking every ordered surface shares (plan 037). These pin the contract: rank is
/// position in the backend-owned scale, an item with no priority comes after everything, and a
/// set-but-unknown value comes after the scale but before nothing at all — whoever set it
/// expressed more intent than whoever set none.
/// </summary>
public sealed class PriorityScaleTests
{
    private static readonly string[] Scale = ["High", "Medium", "Low"];

    [Fact]
    public void Rank_is_position_in_the_scale_not_anything_parsed_from_the_name()
    {
        Assert.Equal(0, PriorityScale.Rank(Scale, "High"));
        Assert.Equal(1, PriorityScale.Rank(Scale, "Medium"));
        Assert.Equal(2, PriorityScale.Rank(Scale, "Low"));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        Assert.Equal(0, PriorityScale.Rank(Scale, "high"));
        Assert.Equal(2, PriorityScale.Rank(Scale, "LOW"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_priority_ranks_after_everything(string? priority) =>
        Assert.Equal(PriorityScale.None, PriorityScale.Rank(Scale, priority));

    [Fact]
    public void An_unknown_value_ranks_after_the_scale_but_before_none()
    {
        var unknown = PriorityScale.Rank(Scale, "Legacy");

        Assert.Equal(PriorityScale.Unknown, unknown);
        Assert.True(unknown > PriorityScale.Rank(Scale, "Low"));
        Assert.True(unknown < PriorityScale.Rank(Scale, priority: null));
    }

    [Fact]
    public void A_missing_scale_degrades_order_never_availability()
    {
        // A cache written before the scale existed, or a field with no options: every set value
        // ties as unknown and unprioritized items still come last. Order degrades to item number;
        // nothing throws and nothing is filtered out.
        Assert.Equal(PriorityScale.Unknown, PriorityScale.Rank(null, "P1"));
        Assert.Equal(PriorityScale.None, PriorityScale.Rank(null, null));
        Assert.Equal(PriorityScale.Unknown, PriorityScale.Rank([], "P1"));
    }
}

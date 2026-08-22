using Highbyte.Wrighty.Web;

namespace Highbyte.Wrighty.UnitTests.Web;

public sealed class RelativeTimeDisplayTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(-10, "just now")]
    [InlineData(-3600, "1h ago")]
    [InlineData(-259200, "3d ago")]
    [InlineData(120, "in 2m")]
    public void Label_matches_browser_relative_time_units(int offsetSeconds, string expected)
    {
        Assert.Equal(expected, RelativeTimeDisplay.Label(Now.AddSeconds(offsetSeconds), Now));
    }
}

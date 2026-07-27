using System.Text.Json;
using Highbyte.Wrighty.ApprovedContext;

namespace Highbyte.Wrighty.UnitTests.ApprovedContext;

public class ContextApprovalSourceTests
{
    /// <summary>
    /// The operator-facing name and the JSON contract are produced by different code — a switch and
    /// a serializer attribute — and a value added to one but not the other would print a C#
    /// identifier into a documented contract without anything failing.
    /// </summary>
    [Theory]
    [InlineData(ContextApprovalSource.None)]
    [InlineData(ContextApprovalSource.ProjectField)]
    [InlineData(ContextApprovalSource.BackendLocal)]
    public void TheDisplayedNameIsTheSerializedName(ContextApprovalSource source)
    {
        var serialized = JsonSerializer.Serialize(source).Trim('"');

        Assert.Equal(serialized, source.WireName());
    }

    [Fact]
    public void EveryValueIsCovered()
    {
        // Guards the switch's catch-all: a new member falling into it would silently read as "none",
        // which is the value that means nothing was approved.
        foreach (var source in Enum.GetValues<ContextApprovalSource>())
            Assert.Equal(JsonSerializer.Serialize(source).Trim('"'), source.WireName());
    }
}

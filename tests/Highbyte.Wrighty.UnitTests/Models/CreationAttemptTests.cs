using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.UnitTests.Models;

public sealed class CreationAttemptTests
{
    [Theory]
    [InlineData("019f5c485c2b7862aeac80eb638a7b5c")]
    [InlineData("019f5c48-5c2b-7862-aeac-80eb638a7b5c")]
    public void NormalizeOrCreate_accepts_uuid_n_and_d_forms(string value)
    {
        Assert.Equal(
            "019f5c485c2b7862aeac80eb638a7b5c",
            CreationAttempt.NormalizeOrCreate(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("{019f5c48-5c2b-7862-aeac-80eb638a7b5c}")]
    public void NormalizeOrCreate_rejects_non_contract_forms(string value)
    {
        var exception = Assert.Throws<TrackerException>(() =>
            CreationAttempt.NormalizeOrCreate(value));

        Assert.Equal("ARGUMENT_INVALID", exception.Code);
        Assert.Equal(2, exception.ExitCode);
    }

    [Fact]
    public void ComputeIntentHash_has_fixed_canonical_property_order()
    {
        var hash = CreationAttempt.ComputeIntentHash(
            new CreateWorkItemRequest("Example", "Body", "Todo", "P1"),
            false);

        Assert.Equal(
            "0473b08bb5c75b4d85b06f4c81720339f1f6f6adc80a7e9f09153c05ed8525ca",
            hash);
    }

    [Fact]
    public void ComputeIntentHash_includes_fields_in_name_order()
    {
        var first = CreationAttempt.ComputeIntentHash(
            new CreateWorkItemRequest("Example", "Body", "Todo", "P1",
                new Dictionary<string, string?> { ["owner"] = "ana", ["epic"] = "PLAT-3" }),
            false);
        var reordered = CreationAttempt.ComputeIntentHash(
            new CreateWorkItemRequest("Example", "Body", "Todo", "P1",
                new Dictionary<string, string?> { ["epic"] = "PLAT-3", ["owner"] = "ana" }),
            false);
        var changed = CreationAttempt.ComputeIntentHash(
            new CreateWorkItemRequest("Example", "Body", "Todo", "P1",
                new Dictionary<string, string?> { ["epic"] = "PLAT-4", ["owner"] = "ana" }),
            false);

        Assert.Equal(first, reordered);
        Assert.NotEqual(first, changed);
    }
}

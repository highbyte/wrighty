using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.Models;

public readonly record struct WorkItemId
{
    public WorkItemId(string value)
    {
        if (string.IsNullOrEmpty(value) ||
            value.Length > 500 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            throw new TrackerException(
                "WORK_ITEM_ID_INVALID",
                "The work-item ID is empty, too long, contains control characters, or has surrounding whitespace.",
                2);
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

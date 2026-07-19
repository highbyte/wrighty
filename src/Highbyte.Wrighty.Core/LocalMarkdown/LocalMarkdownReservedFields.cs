using Highbyte.Wrighty.Errors;

namespace Highbyte.Wrighty.LocalMarkdown;

public static class LocalMarkdownReservedFields
{
    public static readonly IReadOnlyList<string> ManagedKeys =
    [
        "title", "status", "priority", "createdAt", "updatedAt", "claimEpoch", "claim", "creation",
        "wrighty-auto", "wrighty-agent", "wrighty-worker-state"
    ];

    public static bool IsReserved(string name) =>
        ManagedKeys.Contains(name, StringComparer.Ordinal) ||
        string.Equals(name, "wrighty", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("wrighty-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("x-wrighty-", StringComparison.OrdinalIgnoreCase);

    public static void ValidateCustomFieldName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new TrackerException("ARGUMENT_INVALID", "Custom field names cannot be empty.", 2);
        }

        if (IsReserved(name))
        {
            throw new TrackerException(
                "RESERVED_FIELD_COLLISION",
                $"Custom field '{name}' collides with a Wrighty-reserved frontmatter name.",
                2,
                new Dictionary<string, object?> { ["field"] = name });
        }
    }
}

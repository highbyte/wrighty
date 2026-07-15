using System.Globalization;
using System.Text.RegularExpressions;
using Highbyte.Wrighty.Addressing;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.LocalMarkdown;

public sealed partial class LocalMarkdownWorkItemAddressResolver : IWorkItemAddressResolver
{
    public WorkItemId Resolve(string input, TrackerConfig config)
    {
        _ = new WorkItemId(input);
        var candidate = input.StartsWith("local:", StringComparison.OrdinalIgnoreCase)
            ? input["local:".Length..]
            : Path.GetFileName(input);
        candidate = candidate.TrimStart('#');
        var match = LocalReference().Match(candidate);
        if (!match.Success ||
            !int.TryParse(match.Groups["number"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            throw new TrackerException(
                "WORK_ITEM_ID_INVALID",
                $"'{input}' is not a valid work-item ID for the local-markdown backend.",
                2);
        }

        return FromNumber(number);
    }

    public string FormatShort(WorkItemId id, TrackerConfig config) => $"#{Decode(id)}";

    public static WorkItemId FromNumber(int number) => new($"local:{number}");

    public static int Decode(WorkItemId id)
    {
        var match = CanonicalReference().Match(id.Value);
        if (!match.Success ||
            !int.TryParse(match.Groups["number"].Value, NumberStyles.None,
                CultureInfo.InvariantCulture, out var number) || number <= 0)
        {
            throw new TrackerException(
                "WORK_ITEM_ID_INVALID",
                $"'{id}' is not a valid work-item ID for the local-markdown backend.",
                2);
        }

        return number;
    }

    [GeneratedRegex(@"^(?<number>[0-9]+)(?:-[a-z0-9]+(?:-[a-z0-9]+)*)?(?:\.md)?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocalReference();

    [GeneratedRegex(@"^local:(?<number>[0-9]+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalReference();
}

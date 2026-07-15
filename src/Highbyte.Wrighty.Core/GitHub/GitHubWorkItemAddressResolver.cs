using System.Globalization;
using System.Text.RegularExpressions;
using Highbyte.Wrighty.Addressing;
using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.GitHub;

public sealed partial class GitHubWorkItemAddressResolver : IWorkItemAddressResolver
{
    public WorkItemId Resolve(string input, TrackerConfig config)
    {
        _ = new WorkItemId(input);

        if (TryParsePositiveNumber(input.TrimStart('#'), out var shorthand))
        {
            return Canonical(config, shorthand);
        }

        var candidate = input.StartsWith("github:", StringComparison.OrdinalIgnoreCase)
            ? input["github:".Length..]
            : input;
        var match = RepositoryReference().Match(candidate);
        if (match.Success)
        {
            EnsureConfiguredRepository(config, match.Groups["repository"].Value);
            return Canonical(config, ParseNumber(match.Groups["number"].Value));
        }

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(uri.Host, config.GitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            if (segments.Length == 4 &&
                string.Equals(segments[2], "issues", StringComparison.OrdinalIgnoreCase))
            {
                EnsureConfiguredRepository(config, $"{segments[0]}/{segments[1]}");
                return Canonical(config, ParseNumber(segments[3]));
            }
        }

        throw Invalid(input);
    }

    public string FormatShort(WorkItemId id, TrackerConfig config) =>
        $"#{Decode(id, config).IssueNumber}";

    public GitHubWorkItemAddress Decode(WorkItemId id, TrackerConfig config)
    {
        var match = CanonicalReference().Match(id.Value);
        if (!match.Success)
        {
            throw Invalid(id.Value);
        }

        EnsureConfiguredRepository(config, match.Groups["repository"].Value);
        return new GitHubWorkItemAddress(
            config.GitHubHost,
            config.RepositoryOwner,
            config.RepositoryName,
            ParseNumber(match.Groups["number"].Value));
    }

    public WorkItemId FromIssueNumber(TrackerConfig config, int issueNumber)
    {
        if (issueNumber <= 0)
        {
            throw Invalid(issueNumber.ToString(CultureInfo.InvariantCulture));
        }

        return Canonical(config, issueNumber);
    }

    private static WorkItemId Canonical(TrackerConfig config, int issueNumber) =>
        new($"github:{config.Repository}#{issueNumber.ToString(CultureInfo.InvariantCulture)}");

    private static void EnsureConfiguredRepository(TrackerConfig config, string repository)
    {
        if (!string.Equals(repository, config.Repository, StringComparison.OrdinalIgnoreCase))
        {
            throw new TrackerException(
                "WORK_ITEM_REPOSITORY_MISMATCH",
                $"Work item repository '{repository}' does not match configured repository '{config.Repository}'.",
                2);
        }
    }

    private static int ParseNumber(string value)
    {
        if (!TryParsePositiveNumber(value, out var number))
        {
            throw Invalid(value);
        }

        return number;
    }

    private static bool TryParsePositiveNumber(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;

    private static TrackerException Invalid(string input) => new(
        "WORK_ITEM_ID_INVALID",
        $"'{input}' is not a valid work-item ID for the GitHub backend.",
        2);

    [GeneratedRegex(@"^(?<repository>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)#(?<number>[0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex RepositoryReference();

    [GeneratedRegex(@"^github:(?<repository>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)#(?<number>[0-9]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalReference();
}

public sealed record GitHubWorkItemAddress(
    string Host,
    string Owner,
    string Repository,
    int IssueNumber);

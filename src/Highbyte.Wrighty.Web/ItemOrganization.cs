using System.Globalization;
using System.Text;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Workers;

namespace Highbyte.Wrighty.Web;

public enum ItemSortField
{
    Default,
    Number,
    Created,
    Updated,
    Priority,
    Title,
    Agent,
    Status,
    Operational
}

public readonly record struct ItemSort(ItemSortField Field, bool Descending)
{
    public static ItemSort Default => new(ItemSortField.Default, false);

    public string Key => $"{Field.ToString().ToLowerInvariant()}:{(Descending ? "desc" : "asc")}";
}

public sealed class BoardListInput
{
    public string? Q { get; set; }
    public string? Scope { get; set; }
    public string? Sort { get; set; }
    public string[]? ColumnSort { get; set; }
    public string[]? ClaimKind { get; set; }
    public string[]? Agent { get; set; }
    public string[]? Priority { get; set; }
    public string[]? ClaimState { get; set; }
    public string? UpdatedWithin { get; set; }
}

public sealed record BoardListQuery(
    string? Search,
    ItemSort Sort,
    IReadOnlyDictionary<int, ItemSort> ColumnSorts,
    IReadOnlySet<string> ClaimKinds,
    IReadOnlySet<string> Agents,
    IReadOnlySet<string> Priorities,
    IReadOnlySet<string> ClaimStates,
    string? UpdatedWithin)
{
    private const int MaximumValues = 50;

    public static BoardListQuery Parse(BoardListInput input)
    {
        var columns = new Dictionary<int, ItemSort>();
        foreach (var value in Limited(input.ColumnSort))
        {
            var parts = value.Split(':', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                index < 0 || index > 100 ||
                !TryParseSort(parts[1], out var parsed))
            {
                continue;
            }

            columns[index] = parsed;
        }

        var updated = Normalize(input.UpdatedWithin);
        if (updated is not ("today" or "7d" or "30d")) updated = null;

        return new BoardListQuery(
            ParseSearch(input.Q),
            ParseSort(input.Sort),
            columns,
            Known(input.ClaimKind, ["unclaimed", "human", "agent", "automation", "unknown"]),
            Values(input.Agent, 64),
            Values(input.Priority, 100),
            Known(input.ClaimState, ["unclaimed", "current", "other"]),
            updated);
    }

    public ItemSort SortForColumn(int index) =>
        ColumnSorts.TryGetValue(index, out var value) ? value : Sort;

    public bool HasStructuredFilters => ClaimKinds.Count > 0 || Agents.Count > 0 || Priorities.Count > 0 ||
        ClaimStates.Count > 0 || UpdatedWithin is not null;

    public bool HasFilters => Search is not null || HasStructuredFilters;

    public bool Matches(BoardCardModel card, DateTimeOffset now)
    {
        if (Search is { } search && !SearchText(card).Contains(search, StringComparison.OrdinalIgnoreCase))
            return false;
        if (ClaimKinds.Count > 0 && !ClaimKinds.Contains(ClaimKind(card))) return false;
        if (Agents.Count > 0 && (card.AgentKey is null || !Agents.Contains(card.AgentKey))) return false;
        if (Priorities.Count > 0 &&
            (Normalize(card.Priority) is not { } priority || !Priorities.Contains(priority))) return false;
        if (ClaimStates.Count > 0 && !ClaimStates.Contains(ClaimState(card.ClaimState))) return false;
        if (UpdatedWithin is not null && !UpdatedRecently(card.UpdatedAt, now, UpdatedWithin)) return false;
        return true;
    }

    public string RevisionKey
    {
        get
        {
            var builder = new StringBuilder();
            builder.Append("search:").Append(Search ?? string.Empty).Append('\n').Append(Sort.Key);
            Append(builder, "columns", ColumnSorts.OrderBy(value => value.Key)
                .Select(value => $"{value.Key}:{value.Value.Key}"));
            Append(builder, "claim-kind", ClaimKinds);
            Append(builder, "agent", Agents);
            Append(builder, "priority", Priorities);
            Append(builder, "claim-state", ClaimStates);
            builder.Append("\nupdated:").Append(UpdatedWithin ?? string.Empty);
            return builder.ToString();
        }
    }

    public static ItemSort ParseSort(string? value) =>
        TryParseSort(value, out var parsed) ? parsed : ItemSort.Default;

    private static bool TryParseSort(string? value, out ItemSort result)
    {
        result = ItemSort.Default;
        var parts = value?.Split(':', 2, StringSplitOptions.TrimEntries) ?? [];
        if (parts.Length == 0 || !Enum.TryParse<ItemSortField>(parts[0], true, out var field) ||
            field is ItemSortField.Agent or ItemSortField.Status or ItemSortField.Operational)
        {
            return false;
        }

        var descending = field is ItemSortField.Created or ItemSortField.Updated;
        if (parts.Length == 2)
        {
            var direction = parts[1].ToLowerInvariant();
            if (direction is not ("asc" or "desc")) return false;
            descending = direction == "desc";
        }

        result = new ItemSort(field, descending);
        return true;
    }

    private static IEnumerable<string> Limited(IReadOnlyList<string>? values) =>
        (values ?? []).Take(MaximumValues).Where(value => !string.IsNullOrWhiteSpace(value));

    private static HashSet<string> Known(IReadOnlyList<string>? values, IReadOnlyCollection<string> known) =>
        Values(values, 100).Where(known.Contains).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Values(IReadOnlyList<string>? values, int maximumLength) =>
        Limited(values)
            .Select(Normalize)
            .Where(value => value is not null && value.Length <= maximumLength && !value.Any(char.IsControl))
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);

    internal static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string? ParseSearch(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is { Length: <= 200 } && !normalized.Any(char.IsControl)
            ? normalized
            : null;
    }

    private static string SearchText(BoardCardModel card) => string.Join(' ',
        card.DisplayId,
        card.Title,
        card.Status,
        card.Priority,
        card.ClaimantKindLabel,
        card.AgentLabel,
        OperationalStatusDisplay.Label(card.OperationalStatus, card.AgentLabel),
        card.ProviderBlock?.Reason);

    private static string ClaimKind(BoardCardModel card) => card.ClaimState switch
    {
        ClaimOwnershipState.Unclaimed => "unclaimed",
        _ => Normalize(card.ClaimantKindLabel) ?? "unknown"
    };

    private static string ClaimState(ClaimOwnershipState value) => value switch
    {
        ClaimOwnershipState.OwnedByCurrent => "current",
        ClaimOwnershipState.HeldByOther => "other",
        _ => "unclaimed"
    };

    private static bool UpdatedRecently(DateTimeOffset? updatedAt, DateTimeOffset now, string range)
    {
        if (updatedAt is null) return false;
        var cutoff = range switch
        {
            "today" => new DateTimeOffset(now.Date, now.Offset),
            "7d" => now.AddDays(-7),
            _ => now.AddDays(-30)
        };
        return updatedAt >= cutoff;
    }

    private static void Append(StringBuilder builder, string name, IEnumerable<string> values)
    {
        builder.Append('\n').Append(name).Append(':')
            .AppendJoin(',', values.Order(StringComparer.Ordinal));
    }
}

public sealed class BoardCardComparer(
    ItemSort sort,
    IReadOnlyList<string> priorities) : IComparer<BoardCardModel>
{
    public int Compare(BoardCardModel? left, BoardCardModel? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return 1;
        if (right is null) return -1;

        var compared = sort.Field switch
        {
            ItemSortField.Default => CompareDefault(left, right),
            ItemSortField.Number => Direction(Number(left).CompareTo(Number(right))),
            ItemSortField.Created => Optional(left.CreatedAt, right.CreatedAt),
            ItemSortField.Updated => Optional(left.UpdatedAt, right.UpdatedAt),
            ItemSortField.Priority => Optional(Priority(left.Priority), Priority(right.Priority)),
            ItemSortField.Title => Optional(left.Title, right.Title, StringComparer.OrdinalIgnoreCase),
            _ => 0
        };
        return compared != 0 ? compared : Number(left).CompareTo(Number(right));
    }

    private int CompareDefault(BoardCardModel left, BoardCardModel right)
    {
        var compared = OperationalRank(left.OperationalStatus).CompareTo(
            OperationalRank(right.OperationalStatus));
        if (compared != 0) return compared;
        compared = Optional(Priority(left.Priority), Priority(right.Priority), descending: false);
        return compared != 0 ? compared : Number(left).CompareTo(Number(right));
    }

    private int Optional<T>(T? left, T? right, IComparer<T>? comparer = null, bool? descending = null)
        where T : class
    {
        if (left is null) return right is null ? 0 : 1;
        if (right is null) return -1;
        return Direction((comparer ?? Comparer<T>.Default).Compare(left, right), descending);
    }

    private int Optional<T>(T? left, T? right, bool? descending = null)
        where T : struct, IComparable<T>
    {
        if (left is null) return right is null ? 0 : 1;
        if (right is null) return -1;
        return Direction(left.Value.CompareTo(right.Value), descending);
    }

    private int Direction(int value, bool? descending = null) =>
        (descending ?? sort.Descending) ? -value : value;

    private int? Priority(string? value)
    {
        if (value is null) return null;
        for (var index = 0; index < priorities.Count; index++)
        {
            if (string.Equals(priorities[index], value, StringComparison.OrdinalIgnoreCase)) return index;
        }
        return priorities.Count;
    }

    internal static int Number(BoardCardModel card)
    {
        var value = card.DisplayId;
        var start = value.Length;
        while (start > 0 && char.IsAsciiDigit(value[start - 1])) start--;
        return start < value.Length &&
            int.TryParse(value.AsSpan(start), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : int.MaxValue;
    }

    private static int OperationalRank(string activity) => activity switch
    {
        OperationalStatuses.NeedsAttention => 0,
        OperationalStatuses.AgentActive => 1,
        OperationalStatuses.RetryScheduled => 2,
        OperationalStatuses.HandoffQueued => 3,
        OperationalStatuses.Queued => 4,
        _ => 5
    };
}

public sealed class OperationsListInput
{
    public string? Sort { get; set; }
    public string? Search { get; set; }
    public string? Agent { get; set; }
    public string? Priority { get; set; }
    public string? WorkflowStatus { get; set; }
    public string? OperationalStatus { get; set; }
    public string? Recovery { get; set; }
    public string? ContextState { get; set; }
    public string? UpdatedWithin { get; set; }
    public string? ClaimKind { get; set; }
    public string? ClaimState { get; set; }
}

public sealed record OperationsListQuery(
    ItemSort Sort,
    string? Search,
    string? Agent,
    string? Priority,
    string? WorkflowStatus,
    string? OperationalStatus,
    string? Recovery,
    string? ContextState,
    string? UpdatedWithin,
    string? ClaimKind,
    string? ClaimState)
{
    public bool HasFilters => Search is not null || Agent is not null || Priority is not null ||
        WorkflowStatus is not null || OperationalStatus is not null || Recovery is not null ||
        ContextState is not null || UpdatedWithin is not null || ClaimKind is not null ||
        ClaimState is not null;

    public static OperationsListQuery Parse(OperationsListInput input)
    {
        var parsedSort = ParseSort(input.Sort);
        var updated = BoardListQuery.Normalize(input.UpdatedWithin);
        if (updated is not ("today" or "7d" or "30d")) updated = null;
        var claimKindValue = Known(input.ClaimKind, "unclaimed", "human", "agent", "automation", "unknown");
        var claimStateValue = Known(input.ClaimState, "unclaimed", "current", "other");
        return new OperationsListQuery(
            parsedSort,
            Limited(input.Search, 200),
            LimitedNormalized(input.Agent, 64),
            LimitedNormalized(input.Priority, 100),
            LimitedNormalized(input.WorkflowStatus, 100),
            LimitedNormalized(input.OperationalStatus, 64),
            Known(input.Recovery, "present", "absent"),
            Known(input.ContextState, "approved", "needs-review", "unknown"),
            updated,
            claimKindValue,
            claimStateValue);
    }

    public bool Matches(OperationsItemView item, DateTimeOffset now, bool localClaimsAvailable)
    {
        return MatchesIdentity(item) && MatchesWorkflow(item, now) &&
            MatchesRecovery(item) && MatchesClaims(item, localClaimsAvailable);
    }

    private bool MatchesIdentity(OperationsItemView item)
    {
        if (Search is not null)
        {
            var haystack = string.Join(' ',
                item.Id, item.Title, item.Status, item.Priority, item.RequestedAgent,
                OperationalStatusDisplay.Label(item.OperationalStatus));
            if (!haystack.Contains(Search, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (Agent is not null && BoardListQuery.Normalize(item.RequestedAgent) != Agent) return false;
        if (Priority is not null && BoardListQuery.Normalize(item.Priority) != Priority) return false;
        return true;
    }

    private bool MatchesWorkflow(OperationsItemView item, DateTimeOffset now)
    {
        if (WorkflowStatus is not null && BoardListQuery.Normalize(item.Status) != WorkflowStatus)
            return false;
        if (OperationalStatus is not null &&
            BoardListQuery.Normalize(item.OperationalStatus) != OperationalStatus) return false;
        if (UpdatedWithin is not null && !UpdatedRecently(item.UpdatedAt, now, UpdatedWithin)) return false;
        return true;
    }

    private bool MatchesRecovery(OperationsItemView item)
    {
        if (Recovery == "present" && item.Recovery is null) return false;
        if (Recovery == "absent" && item.Recovery is not null) return false;
        if (ContextState == "approved" && item.ContextApprovalFieldApproved is not true) return false;
        if (ContextState == "needs-review" && item.ContextApprovalFieldApproved is not false) return false;
        if (ContextState == "unknown" && item.ContextApprovalFieldApproved is not null) return false;
        return true;
    }

    private bool MatchesClaims(OperationsItemView item, bool localClaimsAvailable)
    {
        if (localClaimsAvailable && ClaimKind is not null &&
            BoardListQuery.Normalize(item.ClaimantKind) != ClaimKind) return false;
        if (localClaimsAvailable && ClaimState is not null && ClaimStateKey(item.ClaimState) != ClaimState)
            return false;
        return true;
    }

    private static string? Known(string? value, params string[] known)
    {
        var normalized = LimitedNormalized(value, 64);
        return normalized is not null && known.Contains(normalized, StringComparer.Ordinal)
            ? normalized
            : null;
    }

    private static string? Limited(string? value, int length) =>
        string.IsNullOrWhiteSpace(value) || value.Length > length || value.Any(char.IsControl)
            ? null
            : value.Trim();

    private static string? LimitedNormalized(string? value, int length) =>
        BoardListQuery.Normalize(Limited(value, length));

    private static ItemSort ParseSort(string? value)
    {
        var parts = value?.Split(':', 2, StringSplitOptions.TrimEntries) ?? [];
        if (parts.Length == 0 || !Enum.TryParse<ItemSortField>(parts[0], true, out var field))
            return ItemSort.Default;
        var descending = field is ItemSortField.Created or ItemSortField.Updated;
        if (parts.Length == 2)
        {
            if (parts[1].Equals("desc", StringComparison.OrdinalIgnoreCase)) descending = true;
            else if (parts[1].Equals("asc", StringComparison.OrdinalIgnoreCase)) descending = false;
            else return ItemSort.Default;
        }
        return new ItemSort(field, descending);
    }

    private static string ClaimStateKey(ClaimOwnershipState? value) => value switch
    {
        ClaimOwnershipState.OwnedByCurrent => "current",
        ClaimOwnershipState.HeldByOther => "other",
        _ => "unclaimed"
    };

    private static bool UpdatedRecently(DateTimeOffset? value, DateTimeOffset now, string range)
    {
        if (value is null) return false;
        var cutoff = range switch
        {
            "today" => new DateTimeOffset(now.Date, now.Offset),
            "7d" => now.AddDays(-7),
            _ => now.AddDays(-30)
        };
        return value >= cutoff;
    }
}

public sealed class OperationsItemComparer(
    ItemSort sort,
    IReadOnlyList<string> priorities) : IComparer<OperationsItemView>
{
    public int Compare(OperationsItemView? left, OperationsItemView? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return 1;
        if (right is null) return -1;
        var compared = sort.Field switch
        {
            ItemSortField.Default => CompareDefault(left, right),
            ItemSortField.Number => Direction(Number(left.Id).CompareTo(Number(right.Id))),
            ItemSortField.Created => Optional(left.CreatedAt, right.CreatedAt),
            ItemSortField.Updated => Optional(left.UpdatedAt, right.UpdatedAt),
            ItemSortField.Priority => Optional(Priority(left.Priority), Priority(right.Priority)),
            ItemSortField.Title => Optional(left.Title, right.Title, StringComparer.OrdinalIgnoreCase),
            ItemSortField.Agent => Optional(left.RequestedAgent, right.RequestedAgent,
                StringComparer.OrdinalIgnoreCase),
            ItemSortField.Status => Optional(left.Status, right.Status, StringComparer.OrdinalIgnoreCase),
            ItemSortField.Operational => Optional(left.OperationalStatus, right.OperationalStatus,
                StringComparer.OrdinalIgnoreCase),
            _ => 0
        };
        return compared != 0 ? compared : Number(left.Id).CompareTo(Number(right.Id));
    }

    private int CompareDefault(OperationsItemView left, OperationsItemView right)
    {
        var compared = OperationalRank(left.OperationalStatus).CompareTo(
            OperationalRank(right.OperationalStatus));
        if (compared != 0) return compared;
        compared = Optional(Priority(left.Priority), Priority(right.Priority), descending: false);
        return compared != 0 ? compared : Number(left.Id).CompareTo(Number(right.Id));
    }

    private int Optional<T>(T? left, T? right, IComparer<T>? comparer = null, bool? descending = null)
        where T : class
    {
        if (left is null) return right is null ? 0 : 1;
        if (right is null) return -1;
        return Direction((comparer ?? Comparer<T>.Default).Compare(left, right), descending);
    }

    private int Optional<T>(T? left, T? right, bool? descending = null)
        where T : struct, IComparable<T>
    {
        if (left is null) return right is null ? 0 : 1;
        if (right is null) return -1;
        return Direction(left.Value.CompareTo(right.Value), descending);
    }

    private int Direction(int value, bool? descending = null) =>
        (descending ?? sort.Descending) ? -value : value;

    private int? Priority(string? value)
    {
        if (value is null) return null;
        for (var index = 0; index < priorities.Count; index++)
            if (string.Equals(priorities[index], value, StringComparison.OrdinalIgnoreCase)) return index;
        return priorities.Count;
    }

    private static int Number(string id)
    {
        var start = id.Length;
        while (start > 0 && char.IsAsciiDigit(id[start - 1])) start--;
        return start < id.Length &&
            int.TryParse(id.AsSpan(start), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                ? number
                : int.MaxValue;
    }

    private static int OperationalRank(string activity) => activity switch
    {
        OperationalStatuses.NeedsAttention => 0,
        OperationalStatuses.AgentActive => 1,
        OperationalStatuses.RetryScheduled => 2,
        OperationalStatuses.HandoffQueued => 3,
        OperationalStatuses.Queued => 4,
        _ => 5
    };
}

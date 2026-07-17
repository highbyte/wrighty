using Highbyte.Wrighty.AgentContext;
using Highbyte.Wrighty.Claims;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;
using Highbyte.Wrighty.Web.Markdown;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Highbyte.Wrighty.Web.Pages;

public sealed class IndexModel(
    TrackerService tracker,
    WebApplicationState state,
    MarkdownRenderer markdown) : PageModel
{
    private const int MaximumBodyLength = 1_000_000;
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    public async Task<IActionResult> OnGetBoardAsync(string? scope, CancellationToken cancellationToken)
    {
        var archiveScope = ParseScope(scope);
        try
        {
            var snapshot = await tracker.GetDashboardAsync(state.Config, archiveScope, cancellationToken);
            var responseRevision = ResponseRevision(snapshot.Revision, archiveScope);
            var etag = $"\"{responseRevision}\"";
            if (Request.Headers.IfNoneMatch.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
            {
                // htmx treats 204 as an explicit no-swap response. Some browser/htmx
                // combinations process an empty 304 as replaceable content.
                return StatusCode(StatusCodes.Status204NoContent);
            }

            Response.Headers.ETag = etag;
            return Partial("Shared/_Board", Board(snapshot, archiveScope, responseRevision));
        }
        catch (TrackerException exception)
        {
            Response.StatusCode = Status(exception);
            return Partial("Shared/_Board", new BoardPageModel([], [], [], [], scope ?? "active", "error", exception.Code, SafeMessage(exception)));
        }
    }

    public async Task<IActionResult> OnGetItemAsync(string id, CancellationToken cancellationToken)
    {
        try { return Partial("Shared/_ItemDetail", await Item(id, cancellationToken: cancellationToken)); }
        catch (TrackerException exception) { return KnownError(exception); }
    }

    public async Task<IActionResult> OnGetEditAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureWebMutationAllowed(id, cancellationToken);
            return Partial("Shared/_EditForm", (await Item(id, editing: true, cancellationToken: cancellationToken)) with { Editing = true });
        }
        catch (TrackerException exception) { return await ItemError(id, exception, cancellationToken); }
    }

    public async Task<IActionResult> OnPostClaimAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var result = await tracker.ClaimAsync(state.Config, resolved, state.ClaimantContext, cancellationToken);
            state.Retain(resolved.Value, result);
            return Partial("Shared/_EditForm", await Item(id, "Claimed by this Wrighty installation.", editing: true, cancellationToken: cancellationToken));
        }
        catch (TrackerException exception)
        {
            try
            {
                Response.StatusCode = Status(exception);
                return Partial("Shared/_ItemDetail", await Item(id, error: exception, cancellationToken: cancellationToken));
            }
            catch (TrackerException) { return KnownError(exception); }
        }
    }

    public async Task<IActionResult> OnPostSaveAsync(
        string id,
        string expectedRevision,
        string expectedClaimGeneration,
        string title,
        string body,
        string status,
        string? priority,
        string action,
        CancellationToken cancellationToken)
    {
        ClaimHandle handle;
        try
        {
            await EnsureWebMutationAllowed(id, cancellationToken);
            handle = RequiredWebHandle(id);
            if (!string.Equals(state.Generation(tracker.ResolveId(state.Config, id).Value), expectedClaimGeneration, StringComparison.Ordinal))
                throw new TrackerException("WEB_CLAIM_GENERATION_STALE", "This editor was opened under an older claim generation.", 6);
        }
        catch (TrackerException exception) { return await ItemError(id, exception, cancellationToken); }

        if (string.Equals(action, "release", StringComparison.Ordinal))
        {
            try
            {
                var resolved = tracker.ResolveId(state.Config, id);
                await tracker.ReleaseAsync(state.Config, resolved, handle, false, cancellationToken);
                state.Forget(resolved.Value);
                return Partial("Shared/_ItemDetail", await Item(id, "Draft discarded and claim released.", cancellationToken: cancellationToken));
            }
            catch (TrackerException exception) { return KnownError(exception); }
        }

        if (body.Length > MaximumBodyLength)
        {
            var tooLarge = new TrackerException("ARGUMENT_INVALID", "Markdown body must not exceed 1,000,000 characters.", 2);
            Response.StatusCode = 400;
            return Partial("Shared/_EditForm", await Draft(id, title, body, status, priority, tooLarge, cancellationToken));
        }

        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var patch = new WorkItemPatch(
                OptionalValue<string>.From(title),
                OptionalValue<string>.From(body),
                OptionalValue<string>.From(status),
                OptionalValue<string?>.From(string.IsNullOrWhiteSpace(priority) ? null : priority));
            await tracker.UpdateAsync(state.Config, resolved, patch, expectedRevision, handle, cancellationToken);

            var notice = "Saved. The claim remains active.";
            if (string.Equals(action, "save-release", StringComparison.Ordinal))
            {
                await tracker.ReleaseAsync(state.Config, resolved, handle, false, cancellationToken);
                state.Forget(resolved.Value);
                notice = "Saved and released.";
            }
            else if (string.Equals(action, "finish", StringComparison.Ordinal))
            {
                await tracker.FinishAsync(state.Config, resolved, null, handle, cancellationToken);
                state.Forget(resolved.Value);
                notice = "Saved and finished.";
            }

            return Partial("Shared/_ItemDetail", await Item(id, notice, cancellationToken: cancellationToken));
        }
        catch (TrackerException exception) when (exception.Code == "UPDATE_CONFLICT")
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            var current = await Item(id, cancellationToken: cancellationToken);
            return Partial("Shared/_Conflict", new ConflictPageModel(current, title, body, status, priority));
        }
        catch (TrackerException exception)
        {
            Response.StatusCode = Status(exception);
            try { return Partial("Shared/_EditForm", await Draft(id, title, body, status, priority, exception, cancellationToken)); }
            catch (TrackerException) { return KnownError(exception); }
        }
    }

    public Task<IActionResult> OnPostReleaseAsync(string id, CancellationToken cancellationToken) =>
        Mutate(id, async resolved => { await tracker.ReleaseAsync(state.Config, resolved, RequiredWebHandle(id), false, cancellationToken); state.Forget(resolved.Value); }, "Released.", cancellationToken, protectNonHumanClaim: true);

    public Task<IActionResult> OnPostArchiveAsync(string id, CancellationToken cancellationToken) =>
        Mutate(id, async resolved => { await tracker.ArchiveAsync(state.Config, resolved, RequiredWebHandle(id), cancellationToken); state.Forget(resolved.Value); }, "Archived.", cancellationToken, protectNonHumanClaim: true);

    public Task<IActionResult> OnPostUnarchiveAsync(string id, CancellationToken cancellationToken) =>
        Mutate(id, async resolved => await tracker.UnarchiveAsync(state.Config, resolved, cancellationToken), "Restored to the active dashboard.", cancellationToken);

    public async Task<IActionResult> OnPostTakeoverAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            var result = await tracker.TakeoverAsync(state.Config, resolved, state.ClaimantContext, null, cancellationToken);
            state.Retain(resolved.Value, result);
            return Partial("Shared/_EditForm", await Item(id,
                "Takeover complete. The previous claimant is fenced from later Wrighty mutations; an operation already holding the store lock may have finished first.",
                editing: true, cancellationToken: cancellationToken));
        }
        catch (TrackerException exception) { return await ItemError(id, exception, cancellationToken); }
    }

    public async Task<IActionResult> OnPostOverrideReleaseAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = tracker.ResolveId(state.Config, id);
            await tracker.ReleaseAsync(state.Config, resolved,
                new ClaimHandle(state.ClaimantContext, null), true, cancellationToken);
            state.Forget(resolved.Value);
            return Partial("Shared/_ItemDetail", await Item(id, "Existing claim released.", cancellationToken: cancellationToken));
        }
        catch (TrackerException exception) { return await ItemError(id, exception, cancellationToken); }
    }

    private async Task<IActionResult> Mutate(
        string id,
        Func<WorkItemId, Task> operation,
        string notice,
        CancellationToken cancellationToken,
        bool protectNonHumanClaim = false)
    {
        try
        {
            if (protectNonHumanClaim)
            {
                await EnsureWebMutationAllowed(id, cancellationToken);
            }
            await operation(tracker.ResolveId(state.Config, id));
            return Partial("Shared/_ItemDetail", await Item(id, notice, cancellationToken: cancellationToken));
        }
        catch (TrackerException exception)
        {
            try
            {
                Response.StatusCode = Status(exception);
                return Partial("Shared/_ItemDetail", await Item(id, error: exception, cancellationToken: cancellationToken));
            }
            catch (TrackerException) { return KnownError(exception); }
        }
    }

    private async Task EnsureWebMutationAllowed(string id, CancellationToken cancellationToken)
    {
        var editable = await tracker.GetEditableAsync(
            state.Config,
            tracker.ResolveId(state.Config, id),
            cancellationToken);
        if (IsExactWebClaim(editable.Claim) && state.TryHandle(editable.Item.Id.Value, out _))
        {
            return;
        }

        var claimant = AgentTypeLabel(editable.Claim) ?? ClaimantKindLabel(editable.Claim) ?? "another claimant";
        throw new TrackerException(
            "CLAIM_STALE",
            $"This item is claimed by {claimant}. Take over explicitly before editing.",
            7);
    }

    private async Task<IActionResult> ItemError(
        string id,
        TrackerException exception,
        CancellationToken cancellationToken)
    {
        try
        {
            Response.StatusCode = Status(exception);
            return Partial("Shared/_ItemDetail", await Item(id, error: exception, cancellationToken: cancellationToken));
        }
        catch (TrackerException)
        {
            return KnownError(exception);
        }
    }

    private IActionResult KnownError(TrackerException exception)
    {
        Response.StatusCode = Status(exception);
        return Partial("Shared/_Error", new WebErrorModel(exception.Code, SafeMessage(exception)));
    }

    private string SafeMessage(TrackerException exception)
    {
        var message = exception.Message;
        if (state.Config.SourcePath is { } sourcePath)
        {
            var root = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            if (!string.IsNullOrEmpty(root))
            {
                message = message.Replace(root, "<tracker>", StringComparison.Ordinal);
            }
        }

        return message;
    }

    private async Task<ItemPageModel> Draft(string id, string title, string body, string status, string? priority, TrackerException error, CancellationToken cancellationToken)
    {
        var current = await Item(id, editing: true, cancellationToken: cancellationToken);
        return current with { Title = title, Body = body, Status = status, Priority = priority, ErrorCode = error.Code, ErrorMessage = SafeMessage(error), Editing = true };
    }

    private async Task<ItemPageModel> Item(
        string id,
        string? notice = null,
        TrackerException? error = null,
        bool editing = false,
        CancellationToken cancellationToken = default)
    {
        var editable = await tracker.GetEditableAsync(state.Config, tracker.ResolveId(state.Config, id), cancellationToken);
        var item = editable.Item;
        var claimantKindLabel = ClaimantKindLabel(editable.Claim);
        var agentTypeLabel = AgentTypeLabel(editable.Claim);
        var webMutationProtected = IsWebMutationProtected(editable.Claim);
        return new ItemPageModel(
            item.Id.Value,
            tracker.FormatShort(state.Config, item.Id),
            item.Title,
            item.Body,
            item.Status,
            item.Priority,
            item.Archived,
            editable.Revision,
            editable.Claim.State,
            ClaimLabel(editable.Claim),
            claimantKindLabel,
            agentTypeLabel,
            webMutationProtected,
            webMutationProtected
                ? $"This item is claimed by {agentTypeLabel ?? claimantKindLabel ?? "another claimant"}. Takeover does not stop that process; it fences later cooperating Wrighty mutations. An operation already executing may finish first."
                : null,
            editable.Claim.TakeoverAvailable && editable.Claim.State == ClaimOwnershipState.OwnedByCurrent,
            editable.Claim.ClaimantId,
            state.Generation(item.Id.Value),
            state.Config.LocalMarkdown?.Statuses ?? [],
            state.Config.LocalMarkdown?.Priorities ?? [],
            markdown.Render(item.Body),
            notice,
            error?.Code,
            error is null ? null : SafeMessage(error),
            editing,
            item.EffectiveFields.ToDictionary(
                pair => pair.Key,
                pair => FormatFieldValue(pair.Value),
                StringComparer.Ordinal),
            item.RawFrontmatter);
    }

    private static string FormatFieldValue(JsonElement value) =>
        value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
            ? JsonSerializer.Serialize(value, IndentedJson)
            : value.ToString();

    private BoardPageModel Board(DashboardSnapshot snapshot, ArchiveScope scope, string responseRevision)
    {
        var cards = snapshot.Items.Select(value => new BoardCardModel(
                value.Item.Id.Value,
                tracker.FormatShort(state.Config, value.Item.Id),
                value.Item.Title,
                value.Item.Status,
                value.Item.Priority,
                value.Item.Archived,
                value.Claim.State,
                ClaimLabel(value.Claim),
                ClaimantKindLabel(value.Claim),
                AgentTypeLabel(value.Claim)))
            .ToArray();
        var active = cards.Where(card => !card.Archived).ToArray();
        var columns = snapshot.Statuses
            .Select(status => new BoardColumnModel(status, active.Where(card => string.Equals(card.Status, status, StringComparison.OrdinalIgnoreCase)).ToArray()))
            .ToList();
        var unassigned = active.Where(card => card.Status is null || !snapshot.Statuses.Contains(card.Status, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (unassigned.Length > 0) columns.Add(new BoardColumnModel("No configured status", unassigned));
        return new BoardPageModel(snapshot.Statuses, snapshot.Priorities, columns, cards.Where(card => card.Archived).ToArray(), scope.ToString().ToLowerInvariant(), responseRevision);
    }

    private static string ResponseRevision(string snapshotRevision, ArchiveScope scope)
    {
        var value = $"{snapshotRevision}\n{scope}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    private static string ClaimLabel(WorkItemClaimSummary claim) => claim.State switch
    {
        ClaimOwnershipState.Unclaimed => "Unclaimed",
        ClaimOwnershipState.OwnedByCurrent => "Claimed by this Wrighty installation",
        _ => "Claimed by another Wrighty installation"
    };

    private static string? AgentTypeLabel(WorkItemClaimSummary claim)
    {
        if (claim.State == ClaimOwnershipState.Unclaimed ||
            ClaimantKinds.FromStorageValue(claim.ClaimantKind, claim.AgentType) != ClaimantKind.Agent)
        {
            return null;
        }

        return claim.AgentType?.Trim().ToLowerInvariant() switch
        {
            "codex" => "Codex",
            "claude" => "Claude",
            "copilot" => "Copilot",
            _ => "Other"
        };
    }

    private static string? ClaimantKindLabel(WorkItemClaimSummary claim)
    {
        if (claim.State == ClaimOwnershipState.Unclaimed) return null;
        return ClaimantKinds.FromStorageValue(claim.ClaimantKind, claim.AgentType) switch
        {
            ClaimantKind.Agent => "Agent",
            ClaimantKind.Human => "Human",
            ClaimantKind.Automation => "Automation",
            _ => "Unknown"
        };
    }

    private bool IsWebMutationProtected(WorkItemClaimSummary claim) =>
        claim.State != ClaimOwnershipState.Unclaimed && !IsExactWebClaim(claim);

    private bool IsExactWebClaim(WorkItemClaimSummary claim) =>
        claim.State == ClaimOwnershipState.OwnedByCurrent &&
        string.Equals(claim.ClaimantId, state.ClaimantId, StringComparison.Ordinal);

    private ClaimHandle RequiredWebHandle(string id)
    {
        var resolved = tracker.ResolveId(state.Config, id);
        if (state.TryHandle(resolved.Value, out var handle)) return handle;
        throw new TrackerException("CLAIM_TOKEN_REQUIRED",
            "This web session does not hold the claim token. Use explicit takeover to recover the claim.", 6);
    }

    private static ArchiveScope ParseScope(string? scope) => scope?.ToLowerInvariant() switch
    {
        "archived" => ArchiveScope.Archived,
        "all" => ArchiveScope.All,
        _ => ArchiveScope.Active
    };

    private static int Status(TrackerException exception) => exception.Code switch
    {
        "WORK_ITEM_NOT_FOUND" => 404,
        "CLAIM_REQUIRED" or "CLAIM_HELD" or "CLAIM_HELD_BY_LOCAL_CLAIMANT" or "CLAIM_STALE" or "CLAIM_TOKEN_REQUIRED" or
            "UPDATE_CONFLICT" or "WEB_CLAIM_GENERATION_STALE" => 409,
        "LOCAL_STORE_INVALID" or "CONFIG_INVALID" => 422,
        _ when exception.ExitCode == 2 => 400,
        _ => 500
    };
}

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

namespace Highbyte.Wrighty.Web.Pages;

public sealed class IndexModel(
    TrackerService tracker,
    WebApplicationState state,
    MarkdownRenderer markdown) : PageModel
{
    private const int MaximumBodyLength = 1_000_000;

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
            await EnsureWebMutationAllowed(id, cancellationToken);
            var resolved = tracker.ResolveId(state.Config, id);
            await tracker.ClaimAsync(state.Config, resolved, AgentExecutionContext.Human, cancellationToken);
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
        string title,
        string body,
        string status,
        string? priority,
        string action,
        CancellationToken cancellationToken)
    {
        try { await EnsureWebMutationAllowed(id, cancellationToken); }
        catch (TrackerException exception) { return await ItemError(id, exception, cancellationToken); }

        if (string.Equals(action, "release", StringComparison.Ordinal))
        {
            try
            {
                var resolved = tracker.ResolveId(state.Config, id);
                await tracker.ReleaseAsync(state.Config, resolved, cancellationToken);
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
            await tracker.UpdateAsync(state.Config, resolved, patch, expectedRevision, cancellationToken);

            var notice = "Saved. The claim remains active.";
            if (string.Equals(action, "save-release", StringComparison.Ordinal))
            {
                await tracker.ReleaseAsync(state.Config, resolved, cancellationToken);
                notice = "Saved and released.";
            }
            else if (string.Equals(action, "finish", StringComparison.Ordinal))
            {
                await tracker.FinishAsync(state.Config, resolved, null, cancellationToken);
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
        Mutate(id, async resolved => await tracker.ReleaseAsync(state.Config, resolved, cancellationToken), "Released.", cancellationToken, protectNonHumanClaim: true);

    public Task<IActionResult> OnPostArchiveAsync(string id, CancellationToken cancellationToken) =>
        Mutate(id, async resolved => await tracker.ArchiveAsync(state.Config, resolved, cancellationToken), "Archived.", cancellationToken, protectNonHumanClaim: true);

    public Task<IActionResult> OnPostUnarchiveAsync(string id, CancellationToken cancellationToken) =>
        Mutate(id, async resolved => await tracker.UnarchiveAsync(state.Config, resolved, cancellationToken), "Restored to the active dashboard.", cancellationToken);

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
        if (!state.Config.EffectiveWeb.ProtectNonHumanClaims)
        {
            return;
        }

        var editable = await tracker.GetEditableAsync(
            state.Config,
            tracker.ResolveId(state.Config, id),
            cancellationToken);
        if (!IsWebMutationProtected(editable.Claim))
        {
            return;
        }

        var claimant = AgentTypeLabel(editable.Claim) ?? ClaimantKindLabel(editable.Claim) ?? "non-human claimant";
        throw new TrackerException(
            "WEB_CLAIM_PROTECTED",
            $"This item is claimed by {claimant}. Web changes are disabled while non-human claim protection is enabled. Explicit takeover is planned for a future release.",
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
                ? $"This item is claimed by {agentTypeLabel ?? claimantKindLabel ?? "a non-human claimant"}. Web changes are disabled while non-human claim protection is enabled. Explicit takeover is planned for a future release."
                : null,
            state.Config.LocalMarkdown?.Statuses ?? [],
            state.Config.LocalMarkdown?.Priorities ?? [],
            markdown.Render(item.Body),
            notice,
            error?.Code,
            error is null ? null : SafeMessage(error),
            editing,
            item.EffectiveFields.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToString(),
                StringComparer.Ordinal),
            item.RawFrontmatter);
    }

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
        state.Config.EffectiveWeb.ProtectNonHumanClaims &&
        claim.State == ClaimOwnershipState.OwnedByCurrent &&
        ClaimantKinds.FromStorageValue(claim.ClaimantKind, claim.AgentType) != ClaimantKind.Human;

    private static ArchiveScope ParseScope(string? scope) => scope?.ToLowerInvariant() switch
    {
        "archived" => ArchiveScope.Archived,
        "all" => ArchiveScope.All,
        _ => ArchiveScope.Active
    };

    private static int Status(TrackerException exception) => exception.Code switch
    {
        "WORK_ITEM_NOT_FOUND" => 404,
        "CLAIM_REQUIRED" or "CLAIM_HELD" or "UPDATE_CONFLICT" or "WEB_CLAIM_PROTECTED" => 409,
        "LOCAL_STORE_INVALID" or "CONFIG_INVALID" => 422,
        _ when exception.ExitCode == 2 => 400,
        _ => 500
    };
}

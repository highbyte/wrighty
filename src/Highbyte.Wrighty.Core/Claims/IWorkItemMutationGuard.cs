using Highbyte.Wrighty.Configuration;
using Highbyte.Wrighty.Errors;
using Highbyte.Wrighty.Models;

namespace Highbyte.Wrighty.Claims;

public interface IWorkItemMutationGuard
{
    Task EnsureOwnedAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken);
}

public sealed class ClaimMutationGuard(IClaimService claims) : IWorkItemMutationGuard
{
    public async Task EnsureOwnedAsync(
        TrackerConfig config,
        WorkItemId id,
        CancellationToken cancellationToken)
    {
        var ownership = await claims.GetOwnershipAsync(config, id, cancellationToken);
        if (ownership.State == ClaimOwnershipState.OwnedByCurrent)
        {
            return;
        }

        throw new TrackerException(
            "CLAIM_LOST",
            $"Claim ownership for work item '{id}' was lost before an update.",
            6,
            OwnershipDetails(ownership));
    }

    internal static IReadOnlyDictionary<string, object?> OwnershipDetails(
        ClaimOwnershipResult ownership)
    {
        var details = new Dictionary<string, object?>();
        if (ownership.WorkerIdentity is not null)
        {
            details["workerIdentity"] = ownership.WorkerIdentity;
        }

        if (ownership.ExpiresAt.HasValue)
        {
            details["expiresAt"] = ownership.ExpiresAt;
        }

        return details;
    }
}

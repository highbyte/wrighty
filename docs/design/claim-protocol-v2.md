# Claim protocol v2

Claim protocol v2 makes the authoritative owner `(workerIdentity, claimantId, claimToken)`.
Claimant kind, agent type, and session ID are descriptive only. A caller must retain and present
its claimant ID and token; authoritative storage is never a token-discovery mechanism.

## Local Markdown

Managed frontmatter uses this breaking shape:

```yaml
claim:
  version: 2
  workerIdentity: 8a31c0be11af
  claimantKind: agent
  claimantId: codex:019f...
  agentType: codex
  sessionId: 019f...
  claimToken: 7e5c...
  claimedAt: 2026-07-16T10:00:00Z
  expiresAt: 2026-07-16T11:00:00Z
```

Acquisition, takeover, exact-token validation, mutation, archive-release, and release execute under
the store-wide lock. `claimEpoch` remains a dashboard revision input; it is not authorization.
Active pre-v2 frontmatter fails with `CLAIM_FORMAT_UNSUPPORTED`.

## GitHub event chain

Each transition is a new issue comment containing `<!-- wrighty-claim:v2` and a JSON `ClaimRecord`.
Event types are `acquired`, `takenOver`, `released`, `overrideReleased`, and reserved `renewed`.
Transfer and ending events carry `previousClaimToken`.
Best-effort cleanup runs only when no claim resolves and may remove old inactive events; it never
deletes any event while an active chain exists.

Resolution sorts valid events by GitHub `created_at`, then comment ID. An acquisition starts a chain
only when no claim is active at that event time. A later transition applies only if its previous
token equals the resolved token and its worker identity is authorized. Thus, when two takeovers
reference one generation, the first server-ordered event wins. Stale release, renewal, and takeover
events are ignored. The successful takeover caller re-reads the chain and reports success only if
its new token resolves.

Project fields are display projections and are never authorization inputs. Claim tokens are not
projected. Active v1 claim comments block v2 acquisition; inactive v1 history is ignored. Writers
must not mix protocol versions.

Before a GitHub work-item or Project mutation Wrighty resolves and validates the exact handle. It
resolves again after the write. A changed generation produces `CLAIM_LOST_DURING_UPDATE` with
applied and pending stages. GitHub offers no transaction conditioned on this token, so this detects
but cannot undo an in-flight race.

## Bounded renewal

Worker mode implements `renewed` events and exact-token Local Markdown renewal. Renewal preserves
the current fencing token, may update descriptive session/workspace metadata, and extends expiry
only for the exact `(workerIdentity, claimantId, claimToken)` generation. A stale generation gets
`CLAIM_STALE`; an expired generation gets `CLAIM_EXPIRED` and is never resurrected.

Any feature that extends a lease must bound its total extension. Plan 014 opens one fixed renewal
budget when the child starts, equal to `--item-timeout`, and never moves that deadline. Consequently
another installation can always recover by waiting no longer than
`--item-timeout + leaseMinutes`; process liveness alone is never permission to renew forever.
When a vendor turn succeeds but leaves its exact claim active, worker mode performs one final fenced
metadata renewal, reports `needs-attention`, and stops renewing. This preserves the resume address
for one finite lease without converting process success into work-item completion.

The web handback path uses two distinct generations. Taking over for editing creates a human
claimant and fences the prior agent. Plain Save retains that human generation; its displayed
`wrighty worker --resume` command carries the human handle only to Wrighty, which atomically rotates
to a fresh agent claimant before spawning the vendor. **Save and hand back to _Agent_** performs
that rotation immediately for interactive continuation. In both paths, the vendor process receives
only the new agent generation's handle.

## Upgrade prerequisite

Before installing a v2 binary, finish or release every active claim with the v1 binary. Never run
v1 and v2 Wrighty binaries concurrently against the same store or repository. Obsolete alpha
Project display fields may be deleted only after active claims are cleared.

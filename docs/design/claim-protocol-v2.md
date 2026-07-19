# Claim protocol v2

Claim protocol v2 makes the authoritative owner `(workerIdentity, claimantId, claimToken)`.
Claimant kind, agent type, and session ID are descriptive only. A caller must retain and present
its claimant ID and token; authoritative storage is never a token-discovery mechanism.

## Local Markdown

The authoritative claim lives in the machine-local `.runtime-state.json` sidecar next to the store
lock, keyed by item number; item documents never contain claim state:

```json
"claims": {
  "3": {
    "workerIdentity": "8a31c0be11af",
    "claimantKind": "agent",
    "claimantId": "codex:019f...",
    "agentType": "codex",
    "sessionId": "019f...",
    "claimToken": "7e5c...",
    "claimedAt": "2026-07-16T10:00:00Z",
    "expiresAt": "2026-07-16T11:00:00Z"
  }
}
```

Acquisition, takeover, exact-token validation, mutation, archive-release, and release execute under
the store-wide lock. A sidecar `sessions` map holds durable recorded session addresses that survive
release and expiry; it is recovery metadata, not authorization.
Documents still containing pre-sidecar `claim:`/`claimEpoch:` frontmatter fail with
`STORE_MIGRATION_REQUIRED` until `wrighty init` migrates them; an active pre-v2 claim fails
migration with `CLAIM_FORMAT_UNSUPPORTED`.

## GitHub event chain

Each transition is a new issue comment containing `<!-- wrighty-claim:v2` and a JSON `ClaimRecord`.
Event types are `acquired`, `takenOver`, `released`, `overrideReleased`, `renewed`, and `requeued`.
Transfer and ending events carry `previousClaimToken`.
Best-effort cleanup runs only when no claim resolves and may remove old inactive events; it never
deletes any event while an active chain exists.

Resolution sorts valid events by GitHub `created_at`, then comment ID. An acquisition starts a chain
only when no claim is active at that event time. A later transition applies only if its previous
token equals the resolved token and its worker identity is authorized. Thus, when two takeovers
reference one generation, the first server-ordered event wins. Stale release, renewal, and takeover
events are ignored. The successful takeover caller re-reads the chain and reports success only if
its new token resolves.

`requeued` is an inactive terminal generation used by worker dispatch. It rotates the token, ends
the referenced active claim, and retains descriptive agent/session/workspace metadata so a later
`acquired` event can resume that address. The rotation ensures a concurrently arriving transition
for the old token cannot erase or replace the queue decision. Protocol-v2 readers that predate this
extension ignore the unknown event and conservatively retain the previous generation until its
finite expiry; the protocol number remains v2 because authorization and fencing semantics are
unchanged.

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
for one finite lease without converting process success into work-item completion. The takeover
instruction is valid only until that lease expires and is printed with its deadline. After expiry
there is no live generation to rotate, so takeover explicitly reports that it is unavailable and
the primary continuation remains `wrighty worker --item <id> --yes`. That command infers active
takeover or expired-session recovery from current claim state. For recovery, the worker reads only
the latest unreleased agent/session/workspace address, acquires a new claim generation, and resumes
the durable vendor session on the originating installation. The expired claimant and token remain
invalid and are never adopted. A different installation must explicitly choose `--fresh`, because
the recorded workspace and vendor-local session state are not a portable resume address.

The managed worker-dispatch state separates operator intent from claim ownership:
`needs-attention` prevents continuous retry, while `queued` marks the recorded session for a
continuous worker to acquire under a new agent generation and resume. On GitHub the queue decision
is a terminal `requeued` event; on Local Markdown it is the durable session record with no claim.
Existing eligible `Todo` items with no dispatch state remain ordinary fresh candidates.

The web handback path uses two distinct generations. Taking over for editing creates a human
claimant and fences the prior agent. Plain Save retains that human generation; its displayed
`wrighty worker --item <id> --resume` command carries the human handle only to Wrighty, which
atomically rotates to a fresh agent claimant before spawning the vendor. **Save and hand back to
_Agent_** performs
that rotation immediately for interactive continuation. In both paths, the vendor process receives
only the new agent generation's handle.

The CLI `edit --takeover` path keeps the same invariant without requiring shell exports. For an
active same-installation claim, the takeover result and its new token remain inside one Wrighty
process and authorize the immediately following edit. For an unclaimed or expired item, it acquires
a new human editing claim; a complete local agent/session/workspace address is carried through a
temporary agent acquisition before rotation to the human. Wrighty does not reread and adopt an
authoritative token. The combined command retains the human claim and prints the worker continuation
after the mutation succeeds.

## Upgrade prerequisite

Before installing a v2 binary, finish or release every active claim with the v1 binary. Never run
v1 and v2 Wrighty binaries concurrently against the same store or repository. Obsolete alpha
Project display fields may be deleted only after active claims are cleared.

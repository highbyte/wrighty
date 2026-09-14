# Worker discovery

`wrighty workers` lists registered worker runs in the current local configuration scope. Plain
listing reads configuration and registry files and probes only registered PIDs and their start
identities. It does not contact the tracker, inspect retained worktrees, start an agent, or clean
up expired registry records.

```sh
wrighty workers --json
wrighty workers --item local:42 --json
```

`--item` resolves the canonical item ID and adds advisory pickup assessment using tracker state
and the same selection/admission primitives as the worker. It can read local runtime, context,
provider-cache, and workspace evidence, but does not claim work, authorize the queue, prepare a
workspace, or run a paid provider probe. Execution still revalidates and atomically claims work.

## JSON contract

The versioned envelope has `schemaVersion: 1` and a `result` with:

| Field | Meaning |
| --- | --- |
| `observedAt` | Start of this observation; the snapshot is not a reservation. |
| `scope`, `configurationPathHash` | Registered workers in this installation/configuration. |
| `coverage`, `detail` | `complete`, `incomplete`, or `unavailable` registry coverage and any explanation. |
| `configurationRevision` | Current repository configuration revision, when readable. |
| `itemId` | Canonical assessed item, when requested. |
| `localWorkers` | Individual run entries; an empty array does not prove global absence. |

Each entry reuses the `instance` and `detail` projection from `status --json` and adds named
`liveness` (`Running`, `Stale`, `Unknown`), `origin` (`cli-process`, `web-hosted`, `unknown`),
`reportedState`, `intake`, `remainingItemAllowance`, `idleExpiresAt`, `configurationDrift`, and
optional `pickup`. The nested instance retains its existing enum representation; `status --json`
is unchanged apart from the additive scheduling/progress fields.

`instance` includes run ID, PID/start identity, CLI/web-host origin, last heartbeat/state/item/agent,
startup configuration revision, and cooperative control capabilities. Several web-hosted runs may
share a PID; address a run by its run ID, never by PID alone.

New registrations include `scheduling`: continuous/bounded/targeted mode, canonical target and
intent, effective source/active statuses, explicit/default agent, filters, workspace mode/repository,
item limit, idle/item timeout, profile, and dry-run mode. These values are captured at startup;
`invocationSummary` remains display text. No claim token is included. `progress` reports completed
item accounting and the current idle-period start from the worker loop. Missing legacy fields mean
unknown, not unrestricted eligibility. Remaining allowance excludes an active item; no configured
limit means unlimited, while missing progress means unknown.

A `pickup` includes outcome (`could-pick-up`, `cannot-pick-up`, `unknown`), a stable reason code,
explanation, resolved agent when known, and flags for already processing or eligibility after the
current item. It considers intake/lifetime, target/limits, filters, workflow and execution policy,
claims, recorded sessions/dispatch, runtime/agent enablement, approved context, cached provider
state, workspace evidence, and configuration drift. Inconclusive checks yield unknown.

A due retry remains subject to retained-session rules; discovery never bypasses its timer.
Already-processing reports the worker's last-reported activity and does not authorize competing
work. A possible pickup is not a promise about queue order or timing. Workspace and provider state
can change immediately afterwards. Reassess after queueing or resuming; this command does not
predict a proposed mutation's consequences.

## Liveness and coverage

Records use cached heartbeat metadata plus targeted OS process/start-identity checks. A missing
process, reused PID, or expired heartbeat is stale; denied identity inspection is unknown. Older
workers, failed registration, other users/configurations, and remote machines can be outside the
scope. An unreadable record makes coverage incomplete. Discovery does not broaden permissions or
scan process command lines/environments. Cooperative stop still requires fresh verified identity.

`workers` is read-only. For launching see [worker.md](worker.md); for item action discovery see
[actions.md](actions.md). OS service installation and startup management are operator-managed.

# Local Markdown backend metadata

Wrighty's Local Markdown backend stores each work item as a UTF-8 Markdown file with one YAML
frontmatter mapping. The numeric filename prefix is the stable local identity:
`001-example-item.md` is `local:1`. The remainder of the filename follows the title and may change
when the title changes.

```markdown
---
title: Example item
status: Todo
priority: P1
createdAt: 2026-07-17T10:00:00.0000000+00:00
updatedAt: 2026-07-17T10:00:00.0000000+00:00
creation:
  version: 1
  attemptId: 11111111111111111111111111111111
  requestHash: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
---
The Markdown body begins here.
```

The configured `items` directory contains active work items. The configured `archive` directory
contains archived work items. Archive state is represented by file location; there is no
`archived` frontmatter field.

Documents contain only portable work-item content. Live claims and recorded agent sessions are
machine-local runtime state stored in the `.runtime-state.json` sidecar next to the store lock;
claim and release cycles therefore never rewrite or dirty the committed Markdown documents.

## Top-level fields

| Field | Required | Type / format | Meaning and behavior |
| --- | --- | --- | --- |
| `title` | Yes | Non-empty scalar | Display title. Wrighty also derives the filename slug from it. Editing the title may rename the file without changing its numeric identity. |
| `status` | Yes | Non-empty scalar | Workflow status. It must match one of the statuses configured in `.wrighty.json`; Wrighty writes the configured canonical spelling. |
| `priority` | No | Scalar | Optional configured priority such as `P1`. Clearing priority removes this key. |
| `createdAt` | Yes | Timestamp | Creation time. Wrighty writes UTC using the round-trip ISO 8601 format. |
| `updatedAt` | Yes | Timestamp | Time of the latest Wrighty-managed item change. Claim acquisition, renewal, takeover, and release live in the runtime-state sidecar and do not modify this value. |
| `wrighty-auto` | No | Boolean | Managed opt-in permission for unattended worker execution. |
| `wrighty-agent` | No | Scalar | Optional managed preferred vendor: `claude`, `codex`, or `copilot`. |
| `wrighty-worker-state` | No | Scalar | Managed dispatch state: `needs-attention` or `queued`. Absence is the normal state. |
| `creation` | No | Mapping | Retry-safe creation metadata. Wrighty-created items contain it; the parser permits it to be absent for compatible imported or manually managed documents. |

These names are Wrighty-managed and reserved. The historical `claim` and `claimEpoch` names, the
case-insensitive name `wrighty`, and every case-insensitive `x-wrighty-` prefix are also reserved.
Other top-level keys are custom fields.

A document that still contains pre-sidecar `claim:` or `claimEpoch:` frontmatter fails strict
loading with `STORE_MIGRATION_REQUIRED`. Run `wrighty init` once to migrate the store; see
[Migration from pre-sidecar stores](#migration-from-pre-sidecar-stores).

## Runtime-state sidecar

`.runtime-state.json` in the tracker root holds the machine-local runtime state for the store: the
authoritative live claims and the durable per-item agent session records. It is read and written
only under the store lock, is covered by the generated `.gitignore`, and must not be committed:
Git does not arbitrate local claims, and recorded workspaces and vendor sessions are only
meaningful on the filesystem that recorded them.

```json
{
  "version": 1,
  "claims": { "3": { "workerIdentity": "…", "claimantId": "…", "claimToken": "…" } },
  "sessions": { "3": { "agentType": "codex", "sessionId": "…", "workspacePath": "…" } }
}
```

Deleting the file releases every live local claim and forgets every recorded session; a corrupt
file fails closed with `LOCAL_STORE_INVALID`.

### `claims` entries

The map key is the numeric item identity. The authoritative current owner is the tuple
`(workerIdentity, claimantId, claimToken)`. Claim attribution fields are descriptive and are not
sufficient authorization on their own.

| Field | Required | Type / format | Meaning and behavior |
| --- | --- | --- | --- |
| `workerIdentity` | Yes | Non-empty scalar | Stable identity of the Wrighty installation that owns the lease. It is separate from the human, agent, or automation claimant. |
| `claimantId` | Yes | Non-empty opaque scalar | Identity of the particular human surface, agent session, or automation run. Direct human CLI commands default to the installation-local `human-cli` identity. Automation requires an explicit ID. |
| `claimToken` | Yes | Non-empty opaque scalar | Current fencing generation. It changes on acquisition and takeover and must be presented unchanged by later mutations. It is operational metadata, not a password, but callers must never discover and adopt it from storage. |
| `workspacePath` | Worker claims only | Absolute path | Directory in which the vendor session was started; used to resume after takeover. |
| `agentType` | Agent claims only when known | Scalar | Descriptive agent family, normally `codex`, `claude`, `copilot`, or `other`. It is null for ordinary human and automation claims. |
| `sessionId` | No | Opaque scalar | Optional vendor or caller session identifier used for attribution and correlation. It is not authorization. |
| `claimantKind` | Written by Wrighty | Scalar enum | Descriptive claimant category: `agent`, `human`, `automation`, or `unknown`. |
| `claimedAt` | Yes | Timestamp | Acquisition or takeover time for the current generation. A takeover replaces the previous value. |
| `expiresAt` | Yes | Timestamp | Lease expiry for the current generation. Expired claims do not authorize mutation; normal acquisition is used after expiry. |

`claimToken` is visible in the local sidecar by design. Fencing works because a mutation must
present the token it already retained and Wrighty compares that same token at the locked mutation
boundary. Reading the current token from the sidecar and adopting it would defeat that contract.

### `sessions` entries

Session records are durable, overwrite-only recovery metadata. Wrighty writes them whenever a
claim records a session address and preserves them when a claim is released, taken over, requeued,
archived, or expires. A record is replaced only when a newer address is recorded for the same
item. Releasing a claim therefore no longer discards the recorded resume address.

| Field | Meaning |
| --- | --- |
| `workerIdentity` | Installation that recorded the session; recovery is only offered to it. |
| `agentType`, `sessionId`, `workspacePath` | The recorded vendor session address. |
| `updatedAt` | When the record was last written. |
| `lastClaimExpiresAt` | Lease expiry of the claim that most recently carried this address. |

A queued item (`wrighty-worker-state: queued`) is unclaimed and holds no claim entry; the recorded
session entry alone carries the resume address a continuous worker uses.

## `creation` fields

Creation metadata makes retries of one logical `wrighty create` request deterministic.

| Field | Required when `creation` exists | Type / format | Meaning and behavior |
| --- | --- | --- | --- |
| `creation.version` | Yes | Integer | Creation-metadata format. Must be `1`. |
| `creation.attemptId` | Yes | Non-empty scalar | Client-generated identifier for one logical creation attempt. Reusing it allows Wrighty to reconcile a retry with the original item. |
| `creation.requestHash` | Yes | Non-empty scalar | Hash of the normalized creation request. Wrighty uses it to reject reuse of an attempt ID for different content. |

Creation metadata is independent of claim ownership. It remains after claim, takeover, release,
archive, and unarchive operations.

## Custom fields and YAML behavior

Every non-reserved top-level key is a user custom field. Values may be scalars, sequences, or nested
mappings. Wrighty preserves custom values and their relative order across application updates.
Newly introduced managed keys are inserted in this canonical order:

```text
title, status, priority, createdAt, updatedAt, creation
```

Duplicate or non-scalar top-level keys make a document invalid. YAML comments and scalar style are
not guaranteed to round-trip because the YAML representation model does not preserve comments and
may normalize quoting.

## Lifecycle representation

| Scenario | Document | Sidecar `claims` entry | Sidecar `sessions` entry |
| --- | --- | --- | --- |
| Never claimed | `items/`, unchanged | Absent | Absent |
| Active acquisition | Unchanged | Current claimant and token | Written once an address is recorded |
| Takeover | Unchanged | Atomically replaced with the new claimant and token | Preserved |
| Normal or override release | Unchanged (`wrighty-worker-state` cleared when set) | Removed | Preserved |
| Queued for worker resume | `wrighty-worker-state: queued` | Removed | Preserved; carries the resume address |
| Archive | Moved to `archive/` | Removed | Preserved |
| Unarchive | Moved to `items/` | Absent; a new claim is required before mutation | Preserved |

## Migration from pre-sidecar stores

Stores written before the runtime-state sidecar kept the live claim and a `claimEpoch` revision in
each document's frontmatter. `wrighty init` migrates such a store in one idempotent pass under the
store lock:

- an active v2 claim moves to the sidecar `claims` map unchanged;
- an expired or `state: requeued` v2 claim becomes a durable `sessions` record when it carries a
  session address, so recorded sessions survive the upgrade;
- an expired v1 claim is dropped; an **active** v1 claim fails the migration with
  `CLAIM_FORMAT_UNSUPPORTED` and must be finished or released with the previous Wrighty version
  first;
- `claim:` and `claimEpoch:` frontmatter is then stripped without changing `updatedAt`.

Until migration runs, ordinary commands fail with `STORE_MIGRATION_REQUIRED`. Do not run
pre-sidecar and current Wrighty binaries concurrently against one store.

## Examples

The deterministic examples are documentation fixtures:

| File | Scenario |
| --- | --- |
| [`examples/local-markdown/001-unclaimed.md`](examples/local-markdown/001-unclaimed.md) | Newly created and never claimed |
| [`examples/local-markdown/runtime-state.example.json`](examples/local-markdown/runtime-state.example.json) | Sidecar with an active human CLI claim (item 2), an active Codex claim with its session record (item 3), an automation claim (item 4), a web takeover retaining the agent session (item 5), and a released item whose session record survives (item 6) |
| [`examples/local-markdown/archive/007-archived.md`](examples/local-markdown/archive/007-archived.md) | Previously claimed item after archive |
| [`examples/local-markdown/unsupported/008-active-v1.md`](examples/local-markdown/unsupported/008-active-v1.md) | Historical pre-sidecar document whose active v1 claim must fail migration with `CLAIM_FORMAT_UNSUPPORTED` |

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
wrighty:
  policy:
    execution: automatic
    agent: codex
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
machine-local runtime state stored in the `.wrighty-runtime-v1.json` sidecar next to the store lock;
claim and release cycles therefore never rewrite or dirty the committed Markdown documents.

## Top-level fields

| Field | Required | Type / format | Meaning and behavior |
| --- | --- | --- | --- |
| `title` | Yes | Non-empty scalar | Display title. Wrighty also derives the filename slug from it. Editing the title may rename the file without changing its numeric identity. |
| `status` | Yes | Non-empty scalar | Workflow status. It must match one of the statuses configured in `.wrighty.json`; Wrighty writes the configured canonical spelling. |
| `priority` | No | Scalar | Optional configured priority such as `P1`. Clearing priority removes this key. |
| `createdAt` | Yes | Timestamp | Creation time. Wrighty writes UTC using the round-trip ISO 8601 format. |
| `updatedAt` | Yes | Timestamp | Time of the latest Wrighty-managed item change. Claim acquisition, renewal, takeover, and release live in the runtime sidecar and do not modify this value. |
| `wrighty` | No | Mapping | Root for Wrighty-managed portable metadata. |
| `wrighty.policy.execution` | No | Scalar | `automatic` permits unattended execution; absence means manual-only. |
| `wrighty.policy.agent` | No | Scalar | Optional agent policy: `claude`, `codex`, or `copilot`; absence uses the repository default. |
| `wrighty.dispatch.state` | No | Scalar | Managed dispatch state: `needs-attention`, `queued`, `retry-scheduled`, or `handoff-queued`. Absence is the normal state. Exact scheduling/handoff data remains in the machine-local sidecar. |
| `wrighty.creation` | No | Mapping | Retry-safe creation metadata. Wrighty-created items contain it; imported or manually managed documents may omit it. |

These names are Wrighty-managed and reserved. The historical `claim` and `claimEpoch` names, the
case-insensitive name `wrighty`, and every case-insensitive `x-wrighty-` prefix are also reserved.
Other top-level keys are custom fields.

A document containing the pre-overhaul flat metadata schema fails closed with
`STORE_SCHEMA_UNSUPPORTED`. This pre-release intentionally provides no migration; the exception
names each unsupported file that must be removed or renamed outside the active store.

## Runtime sidecar

`.wrighty-runtime-v1.json` in the tracker root holds the machine-local runtime state for the store: the
authoritative live claims and the durable per-item agent session records. It is read and written
only under the store lock, is covered by the generated `.gitignore`, and must not be committed:
Git does not arbitrate local claims, and recorded workspaces and vendor sessions are only
meaningful on the filesystem that recorded them.

```json
{
  "version": 1,
  "claims": { "3": { "installationId": "…", "claimantId": "…", "claimToken": "…" } },
  "items": {
    "3": {
      "installationId": "…",
      "session": { "agent": "codex", "id": "…", "workspacePath": "…" },
      "lastRun": null,
      "pendingDispatch": null
    }
  }
}
```

Deleting the file releases every live local claim and forgets every recorded session; a corrupt
file fails closed with `LOCAL_STORE_INVALID`.

### `claims` entries

The map key is the numeric item identity. The authoritative current owner is the tuple
`(installationId, claimantId, claimToken)`. Claim attribution fields are descriptive and are not
sufficient authorization on their own.

| Field | Required | Type / format | Meaning and behavior |
| --- | --- | --- | --- |
| `installationId` | Yes | Non-empty scalar | Stable identity of the Wrighty installation that owns the lease. It is separate from the human, agent, or automation claimant. |
| `claimantId` | Yes | Non-empty opaque scalar | Identity of the particular human surface, agent session, or automation run. Direct human CLI commands default to the installation-local `human-cli` identity. Automation requires an explicit ID. |
| `claimToken` | Yes | Non-empty opaque scalar | Current fencing generation. It changes on acquisition and takeover and must be presented unchanged by later mutations. It is operational metadata, not a password, but callers must never discover and adopt it from storage. |
| `workspacePath` | Worker claims only | Absolute path | Directory in which the vendor session was started; used to resume after takeover. |
| `agent` | Agent claims only when known | Scalar | Descriptive agent family, normally `codex`, `claude`, `copilot`, or `other`. It is null for ordinary human and automation claims. |
| `sessionId` | No | Opaque scalar | Optional vendor or caller session identifier used for attribution and correlation. It is not authorization. |
| `claimantKind` | Written by Wrighty | Scalar enum | Descriptive claimant category: `agent`, `human`, `automation`, or `unknown`. |
| `claimedAt` | Yes | Timestamp | Acquisition or takeover time for the current generation. A takeover replaces the previous value. |
| `expiresAt` | Yes | Timestamp | Lease expiry for the current generation. Expired claims do not authorize mutation; normal acquisition is used after expiry. |

`claimToken` is visible in the local sidecar by design. Fencing works because a mutation must
present the token it already retained and Wrighty compares that same token at the locked mutation
boundary. Reading the current token from the sidecar and adopting it would defeat that contract.

### `items` entries

Session records are durable, overwrite-only recovery metadata. Wrighty writes them whenever a
claim records a session address and preserves them when a claim is released, taken over, requeued,
archived, or expires. A record is replaced only when a newer address is recorded for the same
item. Releasing a claim therefore no longer discards the recorded resume address.

| Field | Meaning |
| --- | --- |
| `installationId` | Installation that recorded the session; recovery is only offered to it. |
| `session` | Recorded address with `agent`, `id`, `workspacePath`, and optional `branch`. |
| `updatedAt` | When the record was last written. |
| `lastClaimExpiresAt` | Lease expiry of the claim that most recently carried this address. |
| `lastRun` | Bounded outcome, end time, final message, and optional normalized provider failure. |
| `pendingDispatch` | Optional retry/handoff decision: state, reason, session agent, dispatch agent, `notBefore`, and attempt bounds. Invalid data fails closed. |

A queued item (`wrighty.dispatch.state: queued`) is unclaimed and holds no claim entry; the recorded
session entry alone carries the resume address a continuous worker uses.
A retry-scheduled item is likewise unclaimed, but its session entry also carries the exact
`notBefore` and bounded attempt state.

## `wrighty.creation` fields

Creation metadata makes retries of one logical `wrighty create` request deterministic.

| Field | Required when `creation` exists | Type / format | Meaning and behavior |
| --- | --- | --- | --- |
| `wrighty.creation.version` | Yes | Integer | Creation-metadata format. Must be `1`. |
| `wrighty.creation.attemptId` | Yes | Non-empty scalar | Client-generated identifier for one logical creation attempt. Reusing it allows Wrighty to reconcile a retry with the original item. |
| `wrighty.creation.requestHash` | Yes | Non-empty scalar | Hash of the normalized creation request. Wrighty uses it to reject reuse of an attempt ID for different content. |

Creation metadata is independent of claim ownership. It remains after claim, takeover, release,
archive, and unarchive operations.

## Custom fields and YAML behavior

Every non-reserved top-level key is a user custom field. Values may be scalars, sequences, or nested
mappings. Wrighty preserves custom values and their relative order across application updates.
Newly introduced managed keys are inserted in this canonical order:

```text
title, status, priority, createdAt, updatedAt, wrighty
```

Duplicate or non-scalar top-level keys make a document invalid. YAML comments and scalar style are
not guaranteed to round-trip because the YAML representation model does not preserve comments and
may normalize quoting.

## Lifecycle representation

| Scenario | Document | Sidecar `claims` entry | Sidecar `items` entry |
| --- | --- | --- | --- |
| Never claimed | `items/`, unchanged | Absent | Absent |
| Active acquisition | Unchanged | Wrighty claim - claimant and token | Written once an address is recorded |
| Takeover | Unchanged | Atomically replaced with the new claimant and token | Preserved |
| Normal or override release | Unchanged (`wrighty.dispatch.state` cleared when set) | Removed | Preserved |
| Queued for worker resume | `wrighty.dispatch.state: queued` | Removed | Preserved; carries the resume address |
| Archive | Moved to `archive/` | Removed | Preserved |
| Unarchive | Moved to `items/` | Absent; a new claim is required before mutation | Preserved |

## Pre-overhaul stores

Wrighty detects the former `.runtime-state.json` file and flat frontmatter keys only to produce a
specific `STORE_SCHEMA_UNSUPPORTED` error. It never copies, rewrites, or removes that data. The
exception lists the unsupported file to remove or rename before retrying.

## Examples

The deterministic examples are documentation fixtures:

| File | Scenario |
| --- | --- |
| [`examples/local-markdown/001-unclaimed.md`](examples/local-markdown/001-unclaimed.md) | Newly created and never claimed |
| [`examples/local-markdown/wrighty-runtime-v1.example.json`](examples/local-markdown/wrighty-runtime-v1.example.json) | Current runtime sidecar shape with claims and nested per-item runtime records. |
| [`examples/local-markdown/archive/007-archived.md`](examples/local-markdown/archive/007-archived.md) | Previously claimed item after archive |
| [`examples/local-markdown/unsupported/008-active-v1.md`](examples/local-markdown/unsupported/008-active-v1.md) | Historical pre-overhaul document rejected with `STORE_SCHEMA_UNSUPPORTED`. |

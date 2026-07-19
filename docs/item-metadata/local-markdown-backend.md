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
claimEpoch: 0
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

## Top-level fields

| Field | Required | Type / format | Meaning and behavior |
| --- | --- | --- | --- |
| `title` | Yes | Non-empty scalar | Display title. Wrighty also derives the filename slug from it. Editing the title may rename the file without changing its numeric identity. |
| `status` | Yes | Non-empty scalar | Workflow status. It must match one of the statuses configured in `.wrighty.json`; Wrighty writes the configured canonical spelling. |
| `priority` | No | Scalar | Optional configured priority such as `P1`. Clearing priority removes this key. |
| `createdAt` | Yes | Timestamp | Creation time. Wrighty writes UTC using the round-trip ISO 8601 format. |
| `updatedAt` | Yes | Timestamp | Time of the latest Wrighty-managed item or claim change. Wrighty writes UTC using the round-trip ISO 8601 format. |
| `claimEpoch` | Yes | Non-negative integer | Claim-generation revision used by views and document tracking. It starts at `0` and increments on acquisition and takeover. It is not an authorization token and does not replace `claim.claimToken`. Release and archive remove `claim` without resetting this value. |
| `wrighty-auto` | No | Boolean | Managed opt-in permission for unattended worker execution. |
| `wrighty-agent` | No | Scalar | Optional managed preferred vendor: `claude`, `codex`, or `copilot`. |
| `wrighty-worker-state` | No | Scalar | Managed dispatch state: `needs-attention` or `queued`. Absence is the normal state. |
| `claim` | No | Mapping | Current Local Markdown claim v2. It is absent for ordinary unclaimed, released, or archived items. A queued session retains an inactive `claim` mapping with `state: requeued` so its resume address survives without ownership. |
| `creation` | No | Mapping | Retry-safe creation metadata. Wrighty-created items contain it; the parser permits it to be absent for compatible imported or manually managed documents. |

These names are Wrighty-managed and reserved. The case-insensitive name `wrighty` and every
case-insensitive `x-wrighty-` prefix are also reserved. Other top-level keys are custom fields.

## `claim` fields

The authoritative current owner is the tuple
`(workerIdentity, claimantId, claimToken)`. Claim attribution fields are descriptive and are not
sufficient authorization on their own.

| Field | Required | Type / format | Meaning and behavior |
| --- | --- | --- | --- |
| `claim.version` | Yes | Integer | Must be `2` for an active claim handled by the current protocol. An active pre-v2 claim fails safely with `CLAIM_FORMAT_UNSUPPORTED`. |
| `claim.workerIdentity` | Yes | Non-empty scalar | Stable identity of the Wrighty installation that owns the lease. It is separate from the human, agent, or automation claimant. |
| `claim.claimantId` | Yes | Non-empty opaque scalar | Identity of the particular human surface, agent session, or automation run. Direct human CLI commands default to the installation-local `human-cli` identity. Automation requires an explicit ID. |
| `claim.claimToken` | Yes | Non-empty opaque scalar | Current fencing generation. It changes on acquisition and takeover and must be presented unchanged by later mutations. It is operational metadata, not a password, but callers must never discover and adopt it from storage. |
| `claim.workspacePath` | Worker claims only | Absolute path | Directory in which the vendor session was started; used to resume after takeover. |
| `claim.agentType` | Agent claims only when known | Scalar | Descriptive agent family, normally `codex`, `claude`, `copilot`, or `other`. It is omitted for ordinary human and automation claims. |
| `claim.sessionId` | No | Opaque scalar | Optional vendor or caller session identifier used for attribution and correlation. It is not authorization. |
| `claim.claimantKind` | Written by Wrighty | Scalar enum | Descriptive claimant category: `agent`, `human`, `automation`, or `unknown`. |
| `claim.claimedAt` | Yes | Timestamp | Acquisition or takeover time for the current generation. A takeover replaces the previous value. |
| `claim.expiresAt` | Yes | Timestamp | Lease expiry for the current generation. Expired claims do not authorize mutation; normal acquisition is used after expiry. |
| `claim.state` | No | Enum | Omitted for an active claim. `requeued` makes the mapping inactive while preserving its agent/session/workspace address for a later worker acquisition. |

`claimToken` is visible in a local file by design. Fencing works because a mutation must present the
token it already retained and Wrighty compares that same token at the locked mutation boundary.
Reading the current token from frontmatter and adopting it would defeat that contract.

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
title, status, priority, createdAt, updatedAt, claimEpoch, claim, creation
```

Duplicate or non-scalar top-level keys make a document invalid. YAML comments and scalar style are
not guaranteed to round-trip because the YAML representation model does not preserve comments and
may normalize quoting.

## Lifecycle representation

| Scenario | `claimEpoch` | `claim` mapping | File location |
| --- | ---: | --- | --- |
| Never claimed | `0` | Absent | `items/` |
| Active acquisition | Incremented | Current claimant and token | `items/` |
| Takeover | Incremented again | Atomically replaced with the new claimant and token | `items/` |
| Normal or override release | Preserved | Removed | `items/` |
| Archive | Preserved | Removed as part of archive | `archive/` |
| Unarchive | Preserved | Absent; a new claim is required before mutation | `items/` |

## Examples

The deterministic examples are documentation fixtures:

| File | Scenario |
| --- | --- |
| [`examples/local-markdown/001-unclaimed.md`](examples/local-markdown/001-unclaimed.md) | Newly created and never claimed |
| [`examples/local-markdown/002-claimed-human.md`](examples/local-markdown/002-claimed-human.md) | Active direct-human CLI claim |
| [`examples/local-markdown/003-claimed-agent.md`](examples/local-markdown/003-claimed-agent.md) | Active Codex claim with explicit claimant and session IDs |
| [`examples/local-markdown/004-claimed-automation.md`](examples/local-markdown/004-claimed-automation.md) | Active automation claim with its required explicit claimant ID |
| [`examples/local-markdown/005-taken-over.md`](examples/local-markdown/005-taken-over.md) | Agent claim explicitly taken over by a web human claimant |
| [`examples/local-markdown/006-released.md`](examples/local-markdown/006-released.md) | Previously claimed item after normal release |
| [`examples/local-markdown/archive/007-archived.md`](examples/local-markdown/archive/007-archived.md) | Previously claimed item after archive |
| [`examples/local-markdown/unsupported/008-active-v1.md`](examples/local-markdown/unsupported/008-active-v1.md) | Historical active v1 shape that must fail with `CLAIM_FORMAT_UNSUPPORTED` |

The unsupported example is intentionally not valid for mutation with a v2 binary. Before upgrading,
finish or release active claims with the previous Wrighty version, and never run old and new
Wrighty binaries concurrently against one store.

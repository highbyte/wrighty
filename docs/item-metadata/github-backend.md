# GitHub backend metadata

The GitHub backend composes one Wrighty item from a repository issue, its configured GitHub Project
item, and Wrighty claim comments on the issue. No local work-item Markdown file is authoritative.

```text
repository issue
├── number, title, body
├── durable worker lifecycle label
│   └── wrighty:dispatch-state=<state>
├── temporary creation-recovery label (only while create is incomplete)
└── append-only Wrighty claim v3 comments

configured Project item
├── Status and Priority
├── Wrighty policy - execution and Wrighty policy - agent
├── Wrighty creation - attempt ID
├── display-only claimant projection
├── display-only worker recovery projection
└── native archived state
```

Wrighty uses GitHub's versioned REST Project endpoints for schema discovery, filtered active-item
reads, item creation, and field updates. One REST `PATCH` carries all fields in a logical
projection, such as claimant or dispatch metadata. If those endpoints are unavailable for the
GitHub host or token type, Wrighty falls back to server-filtered GraphQL queries and mutations.
Archived and include-archived reads, plus native Project archive/unarchive operations, continue to
use GraphQL because the REST Project API does not expose archived items or equivalent mutations.

## Issue metadata

| GitHub value | Required | Wrighty meaning and behavior |
| --- | --- | --- |
| Repository owner and name | Yes | Together with the issue number, forms the canonical ID `github:OWNER/REPOSITORY#N`. |
| Issue number | Yes | Server-allocated identity within the repository. Node IDs and Project item IDs remain internal. |
| Issue title | Yes | Authoritative work-item title. |
| Issue body | No | Authoritative Markdown body. Wrighty does not insert tracker markers into it. |
| Issue state | Existing issue property | Wrighty archive does not close or reopen the issue. Issue state is not Wrighty's archive state. |
| `Wrighty policy - profile` Project field | No | Single-select carrying an item's execution profile. Options are title-cased on the board (`Deep`) while the stored vocabulary is lowercase (`deep`). Provisioned by `wrighty init` only when `worker.executionProfiles` is configured; its options come from that list rather than a fixed set. |
| `wrighty:dispatch-state=<state>` issue label | No | Durable ordinary label recording managed dispatch state. Valid states are `needs-attention`, `queued`, `retry-scheduled`, and `handoff-queued`; absence means the ordinary/normal state. Exact retry/handoff data remains machine-local. |
| `sit-create-ATTEMPT_ID` issue label | Transient during create | Bridges an ambiguous issue-creation response. Wrighty removes the label and deletes its repository definition after successful reconciliation. |
| Issue comments | Required for claims | Comments carrying the exact `wrighty-claim:v3` marker form the authoritative claim event chain. Other comments are ignored by claim resolution. |

## Project item metadata

The configured Project determines which repository issues are tracked. Removing an issue from the
Project removes it from Wrighty's tracked set even though the repository issue still exists.
No title convention, issue-body marker, ordinary label, or Creation attempt value is required.
Issues created in GitHub's configured Project are immediately valid Wrighty items.

**Field ownership rule:** operators manage the `Wrighty policy - *` fields (plus Status and
Priority); Wrighty manages everything else. No other field on the Project is meant to be edited by
hand. One nuance: with the worker queue enabled (`worker.useWorkerQueue`, on by default), Wrighty
writes `Wrighty policy - execution` and cycles `Wrighty policy - context approval` *on the
operator's behalf* when they move an item into the pick-from status. Moving out revokes execution
only. The gesture and authority are still the operator's; Wrighty only transcribes them.

| Project value | Type | Authority and behavior |
| --- | --- | --- |
| Project membership | Project item | Authoritative tracked-item membership. |
| `Status` | Single select | Authoritative workflow status. The actual field name is configurable. |
| `Priority` | Single select | Optional authoritative priority. The actual field name is configurable. |
| `Wrighty policy - execution` | Single select (`Manual only`, `Automatic allowed`) | Sole GitHub authorization for unattended worker launch. Only `Automatic allowed` authorizes work; unset is safely treated as manual-only. The field name is configurable. |
| `Wrighty policy - context approval` | Single select (`Needs review`, `Approved`) | Authoritative content approval. The Needs review → Approved cycle establishes a fresh batch cutoff; the queue rule performs that same cycle rather than bypassing the field. Editing covered issue content still invalidates the approval at launch. |
| `Wrighty policy - agent` | Single select (`Repository default`, `Claude`, `Codex`, `Copilot`, `OpenCode`) | Authoritative item routing policy after an explicit worker `--agent` override. Unset and Repository default mean no item-specific override. The field name is configurable. |
| `Wrighty creation - attempt ID` | Text | Durable retry identity after create succeeds. The actual field name is configurable. |
| Native archived state | Project item state | Authoritative archive state. Archive neither closes the issue nor removes it from the Project. |
| `Wrighty claim - claimant type` | Single select | Display-only projection of `agent`, `human`, `automation`, or `unknown`. Never read for authorization. |
| `Wrighty claim - claimant` | Text | **Optional.** Display-only shortened claimant ID. It is deliberately unsuitable for recovering an exact handle. Not created by `wrighty init`; projected only on a Project that already has it. |
| `Wrighty claim - agent` | Single select | Display-only agent-family attribution when applicable. |
| `Wrighty claim - session ID` | Text | **Optional.** Display-only correlation metadata when available. Not created by `wrighty init`; projected only on a Project that already has it. |
| `Wrighty claim - workspace path` | Text | **Optional.** Display-only absolute worktree path. Not created by `wrighty init`; projected only on a Project that already has it, and even then left **blank by default** — it is only written when `worker.shareLocalPaths` is explicitly enabled (see below). Never read by Wrighty. |
| `Wrighty dispatch - state` | Single select | Display-only projection of `Needs attention`, `Resume queued`, `Retry scheduled`, or `Handoff queued`. The issue label remains authoritative. |
| `Wrighty dispatch - not before` | Text | Display-only full ISO-8601 retry timestamp. Exact scheduling remains installation-local. Created by `wrighty init`, but tolerated absent — writes skip it when the Project lacks it. |
| `Wrighty dispatch - agent` | Single select | Display-only agent expected to act on retained recovery — for needs-attention, the session agent whose retained session an operator would resume; it does not change the agent policy. |
| `Wrighty dispatch - detail` | Text | Short sanitized recovery summary. For retry/handoff states it includes bounded attempt progress and relevant authoritative policy changes; for needs-attention it is the one-line stop reason (the full explanation stays in the status comment). |

The claimant projection fields are reconciled after acquisition, takeover, renewal, and exact
`AlreadyOwned`, and cleared after release. Projection failure does not roll back or transfer a
claim. Expired attribution may remain visible until a later claim operation reconciles it.
`claimToken` is never projected.

The dispatch fields are one-way presentation. `wrighty init` provisions the canonical schema on a
fresh Project (excluding the optional forensics fields noted above, whose authoritative copies live
in the claim comments and the machine-local runtime store). Wrighty writes not-before time, agent,
and detail from the installation-local dispatch record only after the label transition succeeds.
Missing fields or a failed Project-field mutation never invalidates the label, local dispatch
record, or claim release. The projection is cleared when the authoritative lifecycle leaves
deferred recovery. Provider circuits themselves are never stored in GitHub; they remain
installation-local.

`Wrighty claim - workspace path` is special: it exists only on a Project whose operator added it by
hand, and even there `worker.shareLocalPaths` defaults to `false`, so the field is written as
empty — the absolute path (which embeds the OS username) never leaves the machine. It is populated
only when an operator sets `worker.shareLocalPaths: true` to opt every collaborator with repository
access into seeing local machine paths. Either way the field is a one-way display projection: Wrighty never reads it back
(the authoritative path for resume lives in the machine-local work-item runtime store). The same
`shareLocalPaths` switch governs whether the path appears in the claim-comment JSON and the
[status comment](../reference/worker.md#github-status-comment); the status comment's host line
likewise shows the placeholder `anonymous` unless a symbolic label is set with
`wrighty config user host set` (see [user settings](../reference/user-settings.md)).

The Creation attempt field may be blank for a GitHub-native or adopted issue. Wrighty's list, get,
claim, edit, finish, and archive paths do not require it. Adoption deliberately leaves it blank
because adoption preserves an existing issue identity rather than pretending Wrighty created it.

Pre-overhaul Projects are rejected with `PROJECT_SCHEMA_UNSUPPORTED`. Wrighty detects the former
field names only to explain why a fresh Project is required; it never migrates, renames, or copies
their values.

Project write access is therefore an execution-authorization role. Restrict it to owners and
selected maintainers; repository issue-creation permission must not imply Project write access.
Issue title and body remain untrusted input even after a maintainer selects Automatic.

## Creation recovery metadata

GitHub cannot create an issue and update a Project transactionally. Wrighty uses two representations
during creation:

| Representation | Lifetime | Contents and purpose |
| --- | --- | --- |
| `sit-create-ATTEMPT_ID` label | Temporary | Applied atomically with issue creation. Its description contains the normalized request hash as `SIT create sha256:HASH`, allowing recovery after an ambiguous response. |
| `Wrighty creation - attempt ID` Project field | Durable while tracked | Normalized 32-character lowercase UUID identifying the logical create operation. It allows a retry to find and reconcile the original Project item. |

Once Project membership, creation ID, status, priority, optional archive, and the final read all
succeed, Wrighty removes the temporary label. The issue body remains exactly user-authored.

## Authoritative claim comments

Each transition is a new issue comment containing a human-readable line and a hidden JSON payload:

```markdown
_Wrighty: claimed by agent **codex:019f…** (codex)._

<!-- wrighty-claim:v3
{"version":3,"eventId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ...}
-->
```

GitHub's server-supplied comment `created_at` and numeric comment ID determine event order. They are
not duplicated inside the payload. Resolution sorts by `created_at`, then comment ID.

### Claim-event payload fields

| JSON field | Required | Type / format | Meaning and behavior |
| --- | --- | --- | --- |
| `version` | Yes | Integer | Must be `3`. |
| `eventId` | Yes | Non-empty opaque scalar | Client-generated identity for this transition event. |
| `installationId` | Yes | Non-empty opaque scalar | Wrighty installation identity. Takeover and ending transitions are valid only within the authorized installation. |
| `claimedAt` | Yes | Timestamp | Client observation time when this event was created. |
| `expiresAt` | Yes | Timestamp later than `claimedAt` | Lease expiry carried by the event. Acquisition and takeover establish the active generation's expiry. |
| `eventType` | Yes | Enum | `acquired`, `takenOver`, `released`, `overrideReleased`, `renewed`, or `requeued`. |
| `claimantId` | Yes | Non-empty opaque scalar | Human surface, agent session, or automation-run identity represented by this event. |
| `claimToken` | Yes | Non-empty opaque scalar | Opaque generation installed by acquisition/takeover. Ending events also carry an event token, but resolution ends the referenced active generation. |
| `previousClaimToken` | Every event except `acquired` | Non-empty opaque scalar | Exact resolved generation this transition attempts to replace, end, or renew. |
| `agent` | No | Scalar | Descriptive normalized agent family, normally `codex`, `claude`, `copilot`, or `other`. |
| `sessionId` | No | Opaque scalar | Optional correlation metadata. Invalid, control-character, or over-200-character values are discarded. |
| `claimantKind` | Written by Wrighty | Scalar enum | Descriptive `agent`, `human`, `automation`, or `unknown`. |

### Transition validity

- `acquired` starts a chain only when no claim is active at that event's server time.
- A later transition applies only when `previousClaimToken` matches the resolved current token.
- The transition's installation must be authorized for the current chain.
- If two takeovers reference one token, the first server-ordered valid event wins.
- `requeued` rotates and ends the active generation while retaining its agent session address; a
  later acquisition may start a new active generation from that address.
- Stale release, takeover, renewal, or requeue events remain comments but are ignored.
- Any pre-v3 Wrighty claim comment blocks v3 acquisition with
  `CLAIM_SCHEMA_UNSUPPORTED`; Wrighty never mixes claim protocols on one issue.

Best-effort cleanup may retain only the newest inactive events up to `claimHistoryLimit`, but it
does not delete inactive history other than superseded renewals while an active chain resolves. As a
targeted exception, each renewal collapses the active generation's earlier `renewed` events (a
worker renews on spawn, on session capture, and on the keep-alive cadence, which otherwise
accumulate as near-duplicate comments): every renewal of a generation shares that generation's claim
token and points its `previousClaimToken` at the acquisition, so only the newest renewal is retained
and resolution is unchanged. The acquisition and any takeover/release events are never collapsed.

## Mutation guarantees

Before each issue or Project write, Wrighty resolves the comment chain and validates the exact
installation, claimant ID, and token. It resolves again after the write. If ownership changed,
Wrighty returns `CLAIM_LOST_DURING_UPDATE` with applied and pending stages and does not attempt an
automatic rollback.

GitHub cannot condition an issue or Project write on Wrighty's claim token. A write already in
flight may therefore land after takeover. This is a detected best-effort fence, unlike the atomic
store-lock guarantee of the Local Markdown backend.

## Examples

| File | Representation |
| --- | --- |
| [`examples/github/project-item.md`](examples/github/project-item.md) | Issue and Project metadata, including the display-only claimant projection |
| [`examples/github/claim-event-acquired.md`](examples/github/claim-event-acquired.md) | Initial v3 acquisition comment |
| [`examples/github/claim-event-taken-over.md`](examples/github/claim-event-taken-over.md) | Same-installation takeover referencing the acquired token |
| [`examples/github/claim-event-released.md`](examples/github/claim-event-released.md) | Exact release referencing the takeover token |

Read the three event examples in server order to obtain one complete chain:
`acquired → takenOver → released`.

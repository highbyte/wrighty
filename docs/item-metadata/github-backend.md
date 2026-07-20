# GitHub backend metadata

The GitHub backend composes one Wrighty item from a repository issue, its configured GitHub Project
item, and Wrighty claim comments on the issue. No local work-item Markdown file is authoritative.

```text
repository issue
├── number, title, body
├── temporary creation-recovery label (only while create is incomplete)
└── append-only Wrighty claim v2 comments

configured Project item
├── Status and Priority
├── Creation attempt ID
├── display-only claimant projection
└── native archived state
```

## Issue metadata

| GitHub value | Required | Wrighty meaning and behavior |
| --- | --- | --- |
| Repository owner and name | Yes | Together with the issue number, forms the canonical ID `github:OWNER/REPOSITORY#N`. |
| Issue number | Yes | Server-allocated identity within the repository. Node IDs and Project item IDs remain internal. |
| Issue title | Yes | Authoritative work-item title. |
| Issue body | No | Authoritative Markdown body. Wrighty does not insert tracker markers into it. |
| Issue state | Existing issue property | Wrighty archive does not close or reopen the issue. Issue state is not Wrighty's archive state. |
| Repository labels | Transient during create | `sit-create-ATTEMPT_ID` bridges an ambiguous issue-creation response. Wrighty removes the label and deletes its repository definition after successful reconciliation. |
| Issue comments | Required for claims | Comments carrying the exact `wrighty-claim:v2` marker form the authoritative claim event chain. Other comments are ignored by claim resolution. |

## Project item metadata

The configured Project determines which repository issues are tracked. Removing an issue from the
Project removes it from Wrighty's tracked set even though the repository issue still exists.
No title convention, issue-body marker, ordinary label, or Creation attempt value is required.
Issues created in GitHub's configured Project are immediately valid Wrighty items.

| Project value | Type | Authority and behavior |
| --- | --- | --- |
| Project membership | Project item | Authoritative tracked-item membership. |
| `Status` | Single select | Authoritative workflow status. The actual field name is configurable. |
| `Priority` | Single select | Optional authoritative priority. The actual field name is configurable. |
| `Creation attempt ID` | Text | Durable retry identity after create succeeds. The actual field name is configurable. |
| Native archived state | Project item state | Authoritative archive state. Archive neither closes the issue nor removes it from the Project. |
| `Current claimant kind` | Single select | Display-only projection of `agent`, `human`, `automation`, or `unknown`. Never read for authorization. |
| `Current claimant` | Text | Display-only shortened claimant ID. It is deliberately unsuitable for recovering an exact handle. |
| `Current agent type` | Single select | Display-only agent-family attribution when applicable. |
| `Current session ID` | Text | Display-only correlation metadata when available. |

The claimant projection fields, including `Current workspace path`, are reconciled after acquisition, takeover, renewal, and exact
`AlreadyOwned`, and cleared after release. Projection failure does not roll back or transfer a
claim. Expired attribution may remain visible until a later claim operation reconciles it.
`claimToken` is never projected.

The Creation attempt field may be blank for a GitHub-native or adopted issue. Wrighty's list, get,
claim, edit, finish, and archive paths do not require it. Adoption deliberately leaves it blank
because adoption preserves an existing issue identity rather than pretending Wrighty created it.

Repository label `wrighty:auto` is authorization for unattended worker processing. It is not
Project membership metadata and should not be used as a harmless auto-add marker.

## Creation recovery metadata

GitHub cannot create an issue and update a Project transactionally. Wrighty uses two representations
during creation:

| Representation | Lifetime | Contents and purpose |
| --- | --- | --- |
| `sit-create-ATTEMPT_ID` label | Temporary | Applied atomically with issue creation. Its description contains the normalized request hash as `SIT create sha256:HASH`, allowing recovery after an ambiguous response. |
| `Creation attempt ID` Project field | Durable while tracked | Normalized 32-character lowercase UUID identifying the logical create operation. It allows a retry to find and reconcile the original Project item. |

Once Project membership, creation ID, status, priority, optional archive, and the final read all
succeed, Wrighty removes the temporary label. The issue body remains exactly user-authored.

## Authoritative claim comments

Each transition is a new issue comment containing a human-readable line and a hidden JSON payload:

```markdown
_Wrighty: claimed by agent **codex:019f…** (codex)._

<!-- wrighty-claim:v2
{"version":2,"eventId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", ...}
-->
```

GitHub's server-supplied comment `created_at` and numeric comment ID determine event order. They are
not duplicated inside the payload. Resolution sorts by `created_at`, then comment ID.

### Claim-event payload fields

| JSON field | Required | Type / format | Meaning and behavior |
| --- | --- | --- | --- |
| `version` | Yes | Integer | Must be `2`. |
| `eventId` | Yes | Non-empty opaque scalar | Client-generated identity for this transition event. |
| `workerIdentity` | Yes | Non-empty opaque scalar | Wrighty installation identity. Takeover and ending transitions are valid only within the authorized installation. |
| `claimedAt` | Yes | Timestamp | Client observation time when this event was created. |
| `expiresAt` | Yes | Timestamp later than `claimedAt` | Lease expiry carried by the event. Acquisition and takeover establish the active generation's expiry. |
| `eventType` | Yes | Enum | `acquired`, `takenOver`, `released`, `overrideReleased`, `renewed`, or `requeued`. |
| `claimantId` | Yes | Non-empty opaque scalar | Human surface, agent session, or automation-run identity represented by this event. |
| `claimToken` | Yes | Non-empty opaque scalar | Opaque generation installed by acquisition/takeover. Ending events also carry an event token, but resolution ends the referenced active generation. |
| `previousClaimToken` | Every event except `acquired` | Non-empty opaque scalar | Exact resolved generation this transition attempts to replace, end, or renew. |
| `agentType` | No | Scalar | Descriptive normalized agent family, normally `codex`, `claude`, `copilot`, or `other`. |
| `sessionId` | No | Opaque scalar | Optional correlation metadata. Invalid, control-character, or over-200-character values are discarded. |
| `claimantKind` | Written by Wrighty | Scalar enum | Descriptive `agent`, `human`, `automation`, or `unknown`. |
| `claimAttemptId` | Derived compatibility projection | Scalar | Serialized alias of `eventId`; not independently authoritative. |
| `state` | Derived compatibility projection | Scalar | Serialized as `active`, `released` for release events, or `queued` for `requeued`; v2 resolution uses `eventType`, not this field. |

### Transition validity

- `acquired` starts a chain only when no claim is active at that event's server time.
- A later transition applies only when `previousClaimToken` matches the resolved current token.
- The transition's installation must be authorized for the current chain.
- If two takeovers reference one token, the first server-ordered valid event wins.
- `requeued` rotates and ends the active generation while retaining its agent session address; a
  later acquisition may start a new active generation from that address.
- Stale release, takeover, renewal, or requeue events remain comments but are ignored.
- Active v1 comments block v2 acquisition with `CLAIM_FORMAT_UNSUPPORTED`; inactive v1 history is
  ignored.

Best-effort cleanup may retain only the newest inactive events up to `claimHistoryLimit`, but it
does not delete any event while an active chain resolves.

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
| [`examples/github/claim-event-acquired.md`](examples/github/claim-event-acquired.md) | Initial v2 acquisition comment |
| [`examples/github/claim-event-taken-over.md`](examples/github/claim-event-taken-over.md) | Same-installation takeover referencing the acquired token |
| [`examples/github/claim-event-released.md`](examples/github/claim-event-released.md) | Exact release referencing the takeover token |

Read the three event examples in server order to obtain one complete chain:
`acquired → takenOver → released`.

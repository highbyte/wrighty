# Claim protocol v1

## Purpose

Provide deterministic best-effort claim arbitration using GitHub's server-ordered issue
comments. The protocol does not turn a GitHub lease into a fenced lock: GitHub cannot reject a
write from a lease holder whose lease expired during the write.

## Storage

Each acquisition attempt is stored as an issue comment. The comment begins with a concise
human-readable summary, followed by a hidden HTML marker and JSON payload:

```markdown
_Wrighty: claimed by worker **8a31c0be11af** until 2026-07-13 11:00:00 UTC._

<!-- wrighty-claim:v1
{"version":1,"claimAttemptId":"ba4b...","workerIdentity":"8a31c0be11af","agentType":"codex","sessionId":"019f5c48-5c2b-7862-aeac-80eb638a7b5c","claimedAt":"2026-07-13T10:00:00+00:00","expiresAt":"2026-07-13T11:00:00+00:00","state":"active"}
-->
```

On release, the visible summary changes to:

```markdown
_Wrighty: claim released by worker **8a31c0be11af**._
```

The summary is informational. Claim resolution uses only the hidden payload and GitHub's
server-provided comment metadata.

Fields:

- `version`: protocol version; must be `1`.
- `claimAttemptId`: client-generated UUID without separators, unique per acquisition attempt.
- `workerIdentity`: first 12 lowercase hexadecimal characters of SHA-256 over the per-install
  UUID. It identifies a tracker installation, not a physical machine or agent session.
- `agentType` (optional): normalized agent runtime family, currently `codex`, `claude`,
  `copilot`, or explicitly supplied `other`. It identifies the runtime, not its selected model.
- `sessionId` (optional): opaque conversation-level identifier supplied by the runtime or
  caller. It is correlation metadata, not authentication or ownership identity.
- `claimedAt`: client observation time, used for operations and display but not arbitration.
- `expiresAt`: expiry time calculated from repository configuration.
- `state`: `active` or `released`.

The GitHub comment ID and `created_at` value are authoritative server metadata and are not
duplicated in the payload.

`agentType` and `sessionId` are informational only. Readers discard malformed optional metadata
without discarding an otherwise valid ownership event. Unknown well-formed agent types remain
readable for forward compatibility. Session IDs are limited to 200 characters and cannot be
URLs or contain control characters. They inherit the visibility of the issue comment.

Readers also accept the legacy v1 names `attempt` and `agent` for existing comments. Writers
always emit `claimAttemptId` and `workerIdentity`. If both a current and legacy name are present
with different values, the comment is malformed and ignored.

## Resolution

At observation time `now`:

1. Read all issue comments, following pagination.
2. Parse comments containing the exact v1 marker; ignore malformed or unknown versions.
3. Ignore claims whose state is not `active`.
4. Ignore claims where `expiresAt <= now`.
5. Order remaining claims by the server-provided comment `created_at`, then numeric comment ID.
6. The first event is the sole resolved owner.

Every client applying these rules to the same comment set selects the same winner.

## Acquisition

1. Resolve the current owner. Return `CLAIM_HELD` if another active owner exists, or success if
   the current installation already owns it.
2. Create a new active claim comment.
3. Read and resolve the comments again.
4. Return success only when this attempt is the resolved winner.
5. A losing client deletes its own attempt comment on a best-effort basis. Failure to delete is
   harmless because the earlier claim remains the winner.

The second read is required. A successful comment creation alone is never proof of ownership.

## Release

1. Resolve the current owner.
2. Compare its worker identity with the locally derived identity.
3. Reject a mismatch with `CLAIM_NOT_OWNER`.
4. Edit the winning comment and change its state to `released`.

## History retention

After a successful acquisition or release, the client best-effort deletes older inactive claim
comments until at most `claimHistoryLimit` remain on the issue. Inactive means explicitly
released or expired at cleanup time. The newest inactive comments are retained by server
`created_at`, then comment ID; active, unexpired claims are never deleted.

The setting defaults to `10`, accepts values from `0` through `1000`, and is housekeeping only:
cleanup failure does not fail the claim operation. A value of `0` removes inactive claim history
immediately. Each acquisition still creates a new comment because immutable server ordering is
required for arbitration; retained comments are not reused.

## Writes after acquisition

The client must resolve the claim again immediately before changing a Project field. If it is
no longer the owner, it must not write.

## Project display projection

The configured Project contains two display-only custom fields:

- `Current agent type`: single-select with Codex, Claude, Copilot, and Other options.
- `Current session ID`: text containing the complete opaque session ID.

`wrighty init` creates or validates these fields and is safe to run repeatedly. It never replaces
an incompatible field or guesses among duplicate names. `wrighty init --check` performs the same
authoritative validation without changing Project schema or local cache state.

After winning or confirming ownership, a client re-resolves the claim and projects its optional
agent context. Release clears both values. Missing context also clears the corresponding value so
an earlier claim is not shown as current. Projection writes are not part of arbitration and are
not atomic with the issue comment. If projection fails, the claim operation reports a partial
update but retains the authoritative comment state.

Lease expiry alone cannot trigger a field write. An expired claim may therefore remain visible
until a later claim-related operation reconciles that Project item.

## MVP limits

- No heartbeat or renewal.
- No native GitHub agent-session registration, session URL, or transcript storage.
- No fencing token; a lease is not a true lock.
- Expired comments are ignored during resolution and are eligible for retention cleanup.
- Client clocks determine expiry. A long TTL should be used to reduce skew and mid-operation
  expiry risk.
- Live validation was run on 2026-07-13 against two concurrently launched clients with
  isolated installation identities. One returned `Acquired`; the other returned
  `CLAIM_HELD`, and the winner released successfully. Deterministic same-timestamp and
  competing-event resolution remains covered by unit tests because a true simultaneous
  double-post cannot be reliably forced over the public GitHub API.

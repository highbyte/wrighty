# Tracker workflow

## Inspect

- List concise active work: `wrighty list --compact`.
- List structured work: `wrighty list --json`.
- Inspect one item: `wrighty get <id> --json`.
- Filter Local Markdown custom fields with repeatable `wrighty list --field name=value --json`;
  filters are AND-combined.
- Use archive flags only when the user asks for archived work.

## Start work

For a specified item:

```text
wrighty claim <id> --claimant-kind agent --json
wrighty get <id> --json
```

For the next available item:

```text
wrighty pick --claimant-kind agent --json
```

Do not implement pick as list followed by claim. `pick` handles contention in priority order.
Retain `result.claimantId` and `result.claimToken` (for pick, the handle is alongside `result.item`).
Call them `<claimantId>` and `<claimToken>` below.

## Worker-spawned sessions

When Wrighty worker mode starts you, the item is already claimed. Read the exact handle from
`WRIGHTY_CLAIMANT_ID` and `WRIGHTY_CLAIM_TOKEN`; do not run `claim` or `pick` again. Get the item
with `wrighty get <id> --json`, and pass the environment-provided handle on every later mutation.
If any mutation returns `CLAIM_STALE`, stop immediately: a human took over the item. Do not reclaim
it, retry the mutation, or keep editing the workspace.

If the item is blocked or needs clarification, do not call `finish`. Explain the blocker clearly in
your final response and exit. The worker will report `needs-attention`, stop renewing, and retain
the resumable claim until its lease expires so an operator can take it over.
Wrighty owns lease renewal and expiry decisions: do not speculate that `expiresAt` may have elapsed
from its timestamp alone, report possible expiry without a command failure, or attempt to reclaim.
Only `CLAIM_EXPIRED` or `CLAIM_STALE` returned by a Wrighty mutation is authoritative for the run.
After an operator clarifies and saves the item, they may resume the recorded session headlessly with
the fenced command Wrighty displays: `wrighty worker --resume <id> --yes`. That command performs the
human-to-agent claim rotation before the vendor process starts; the session must not reclaim itself.

## Create

Generate and retain the ID before sending the create request:

```text
wrighty creation-attempt new --json
wrighty create --creation-attempt-id <creationAttemptId> --title <title> [options] --json
```

On interruption, timeout, `PARTIAL_CREATE`, or an unknown response, retry the identical request with
the same Creation attempt ID. Never reuse that ID for changed title, body, status, priority, custom
fields, or archive intent.

## Update

Use `wrighty edit <id> ... --claimant-id <claimantId> --claim-token <claimToken> --json` for title, body, status, priority, or Local Markdown custom-field
changes. Custom fields appear in `get --json` as `result.fields`; set them with repeatable
`--field name=value` and delete with `--field name=`. Use `wrighty move <id> <status> --claimant-id <claimantId> --claim-token <claimToken> --json` for a
status-only transition. Both require the exact claimant ID and token generation and recheck that
same handle at the backend mutation boundary.

Use `wrighty import <path...> --dry-run --json` before importing existing Markdown into a Local
Markdown store. Import is intentionally unavailable on GitHub.

Do not retry an entire multi-field edit after `PARTIAL_UPDATE`. Retry only fields listed as pending
in the structured error.

## Complete or stop

After the requested verification succeeds, complete with:

```text
wrighty finish <id> --claimant-id <claimantId> --claim-token <claimToken> --json
```

`finish` converges status update, configured archive-on-status, and claim release. Retry the same
command after `PARTIAL_FINISH`.

If work stops without completion and no mutation is ambiguous, run:

```text
wrighty release <id> --claimant-id <claimantId> --claim-token <claimToken> --json
```

Use `wrighty archive <id> --claimant-id <claimantId> --claim-token <claimToken> --json` only for deliberate archival. Archiving is not issue closure or
deletion. Use `wrighty unarchive <id> --json` only when explicitly restoring archived work.

## Context recovery

After compaction, use the known claimant ID and token. If either was lost, inspect with read-only
commands and ask the user how to proceed. Never read or adopt a token from claim storage. Never
invoke takeover merely to recover context; takeover requires an explicit user instruction.

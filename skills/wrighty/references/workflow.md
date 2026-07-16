# Tracker workflow

## Inspect

- List concise active work: `wrighty list --compact`.
- List structured work: `wrighty list --json`.
- Inspect one item: `wrighty get <id> --json`.
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

## Create

Generate and retain the ID before sending the create request:

```text
wrighty creation-attempt new --json
wrighty create --creation-attempt-id <creationAttemptId> --title <title> [options] --json
```

On interruption, timeout, `PARTIAL_CREATE`, or an unknown response, retry the identical request with
the same Creation attempt ID. Never reuse that ID for changed title, body, status, priority, or
archive intent.

## Update

Use `wrighty edit <id> ... --json` for title, body, status, or priority changes. Use
`wrighty move <id> <status> --json` for a status-only transition. Both require the current
installation's claim and recheck it before backend mutations.

Do not retry an entire multi-field edit after `PARTIAL_UPDATE`. Retry only fields listed as pending
in the structured error.

## Complete or stop

After the requested verification succeeds, complete with:

```text
wrighty finish <id> --json
```

`finish` converges status update, configured archive-on-status, and claim release. Retry the same
command after `PARTIAL_FINISH`.

If work stops without completion and no mutation is ambiguous, run:

```text
wrighty release <id> --json
```

Use `wrighty archive <id> --json` only for deliberate archival. Archiving is not issue closure or
deletion. Use `wrighty unarchive <id> --json` only when explicitly restoring archived work.

## Context recovery

If the conversation is compacted but the item ID is known, invoke
`wrighty claim <id> --claimant-kind agent --json`.
`AlreadyOwned` confirms the stable installation identity still owns it. If the item ID is unknown,
do not guess or claim another item; ask the user or inspect likely work without mutation.

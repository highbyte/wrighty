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

This applies even when the requested work is only a title, body, priority, worker-eligibility, or
preferred-agent edit. The AI session is still the claimant executing the mutation. Never substitute
`--claimant-kind human` merely because a human asked for the change: explicit claimant options take
precedence over Wrighty's Claude, Codex, or Copilot runtime detection and would publish incorrect
attribution. A human claimant is reserved for an explicitly requested human takeover workflow.

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
the resumable claim until its lease expires so an operator can take it over. That state is an
operator pause: a continuous worker will not retry it until the operator explicitly queues the
recorded session after clarification.
Wrighty owns lease renewal and expiry decisions: do not speculate that `expiresAt` may have elapsed
from its timestamp alone, report possible expiry without a command failure, or attempt to reclaim.
Only `CLAIM_EXPIRED` or `CLAIM_STALE` returned by a Wrighty mutation is authoritative for the run.
After an operator clarifies the item, they may queue the recorded session for an already-running
continuous worker with the web editor's **Save and queue for worker** action (the web dashboard is
Local Markdown only) or the backend-neutral atomic CLI form
`wrighty edit <id> --takeover --yes --body-file requirements.md --requeue`. They may instead resume
it immediately with the fenced command Wrighty displays: `wrighty worker --item <id> --yes`. Wrighty
performs the human-to-agent claim rotation before the vendor process starts; the session must not
reclaim itself.

## Create

For a substantial new item, separate collaborative authoring from the tracked mutation:

1. Clarify the desired outcome and any material ambiguity before creating the item.
2. Draft a concise title and a Markdown body using only relevant sections from:
   - motivation or problem;
   - desired outcome and scope;
   - acceptance criteria;
   - constraints and dependencies;
   - verification;
   - non-goals or unresolved questions.
3. Do not invent missing requirements. When the user asked to collaborate on the specification,
   show the proposed title and body before creating it and incorporate their revisions.
4. Set status, priority, custom fields, worker eligibility, and preferred agent only from the
   user's request or confirmed choices. A preferred agent does not imply `--auto`; unattended
   execution requires explicit authorization.
5. Stabilize the complete payload before generating the Creation attempt ID. For multiline bodies,
   use `--body-file` so the exact content can be reviewed and retried.

Then generate and retain the ID before sending the create request:

```text
wrighty creation-attempt new --json
wrighty create --creation-attempt-id <creationAttemptId> --title <title> --body-file <bodyFile> [options] --json
```

On interruption, timeout, `PARTIAL_CREATE`, or an unknown response, retry the identical request with
the same Creation attempt ID. Never reuse that ID for changed title, body, status, priority, custom
fields, or archive intent.

Draft-first is the default: collaborate outside tracked state, then create the agreed item once.
If the user explicitly wants an early tracked draft, create it with an honest draft title/body and
do not enable `--auto` unless requested. Before each later revision, claim the item and retain the
returned handle:

```text
wrighty claim <id> --claimant-kind agent --json
wrighty edit <id> --body-file <bodyFile> --claimant-id <claimantId> --claim-token <claimToken> --json
```

Do not mutate Local Markdown directly. A standalone feature document requested by the user may be
edited as a normal project file, but the Wrighty item must still be created or updated through the
CLI.

## Choose what happens after authoring

After creating an actionable item, or materially clarifying one, ask what should happen next unless
the user already decided. Use the surface's choice UI when available and offer:

1. **Start implementation in this session** — keep using the current AI agent process.
2. **Mark for automatic processing** — make it eligible for a separately running Wrighty worker.
3. **Do nothing for now** — leave it tracked without starting or scheduling work.

Do not ask only “Want me to implement it?” These choices have different claim, process, and billing
effects.

For **Start implementation in this session**, retain the claim already used to edit the item, or
acquire an unclaimed item with `wrighty claim <id> --claimant-kind agent --json`. Then inspect and
implement it in the current conversation. Do not invoke `wrighty worker`, `claude`, `codex`, or
`copilot`: this path must not create another agent session or process. This choice does not imply
`--auto`.

For **Mark for automatic processing**, treat the selection as explicit authorization for `--auto`.
Read `result.worker.defaultAgent` from the earlier `wrighty init --check --json` response, then ask
which worker agent to use. A null value means no repository default is configured:

- When a default is configured, show **Use repository default (<vendor>)** as the recommended
  option, plus explicit Claude, Codex, and Copilot pinning choices. Selecting the repository default
  leaves `wrighty-agent` unset (or clears an existing preference); selecting a vendor writes
  `--agent <vendor>`.
- When no default is configured, say so and require an explicit Claude, Codex, or Copilot choice.
  Never infer the worker vendor from the agent that authored the item.

If creation left the item unclaimed, first acquire it with
`wrighty claim <id> --claimant-kind agent --json`. Using that or the current edit handle, apply
`--auto` and the chosen agent preference with `wrighty edit`, then release the claim so a continuous
worker can pick the `Todo` item. State plainly that a Wrighty worker process must be running;
Wrighty does not start one as a side effect of the edit. If the item is not in the configured worker
source status, explain that and ask before moving it.

For **Do nothing for now**, do not set `--auto`; release any unambiguous claim held for editing.
Tell the user the item remains tracked but unscheduled. Explain that they can later:

- ask in the same agent conversation to start implementing the canonical item ID;
- open `wrighty web` (Local Markdown only), enable **Eligible for worker processing**, choose a
  preferred agent or the configured default, and **Save and release**; or
- after making the item worker-eligible, run
  `wrighty worker --item <id> --agent <vendor> --yes` for immediate headless processing.

Do not imply that a standalone human-shell command can make the already-open AI agent start
reasoning. The in-session path begins when the user asks that agent to implement the item; `claim`
is the Wrighty ownership primitive the agent then uses. `wrighty worker` is the separate headless
process path.

## Update

Use `wrighty edit <id> ... --claimant-id <claimantId> --claim-token <claimToken> --json` for title, body, status, priority, or Local Markdown custom-field
changes. Custom fields appear in `get --json` as `result.fields`; set them with repeatable
`--field name=value` and delete with `--field name=`. Use `wrighty move <id> <status> --claimant-id <claimantId> --claim-token <claimToken> --json` for a
status-only transition. Both require the exact claimant ID and token generation and recheck that
same handle at the backend mutation boundary.

For an existing item, worker eligibility and preferred agent are ordinary claim-aware edits:

```text
wrighty edit <id> --auto --agent claude --claimant-id <claimantId> --claim-token <claimToken> --json
wrighty edit <id> --no-auto --clear-agent --claimant-id <claimantId> --claim-token <claimToken> --json
```

Ask when the user's intent is ambiguous. `--auto` grants unattended-processing eligibility;
`--agent` only records a preference and never implies `--auto`.
If the AI session does not already hold the item, first acquire it with
`wrighty claim <id> --claimant-kind agent --json`; do not acquire a human claim for this
metadata-only update.

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

## Complete a finished worktree item

When the user asks to complete, wrap up, integrate, or archive an item a worker already
finished in a git worktree, guide the completion instead of acting unilaterally. Read the
recorded workspace and branch from `wrighty get <id> --json` (`result.session.workspacePath`
and `result.session.branch`); never guess paths or branch names.

1. **Show the work.** Summarize `git status` and the diff from the recorded workspace. If the
   changes are already committed on the recorded branch, summarize `git log` and the diff
   against the base instead.
2. **Commit with approval.** When changes are uncommitted (the default `inspect` policy),
   propose a commit message referencing the item and commit only after the user approves it.
   Never commit silently.
3. **Integrate per the user's preference.** Read `worker.completion.integration` from
   `.wrighty.json` when present; otherwise ask. For `merge-local`, run
   `git merge --ff-only <branch>` from the main checkout; if fast-forward fails, stop and show
   the state rather than resolving conflicts unprompted. For `push-pr`, push the branch with
   `git push -u origin <branch>` and leave PR creation to the user unless asked.
4. **Clean up.** After a successful merge, `git worktree remove <workspacePath>` and then
   `git branch -d <branch>` — in that order, because git refuses to delete a branch that is still
   checked out in a worktree. Rely on git's own guards: never force-remove a dirty worktree or
   force-delete an unmerged branch.
5. **Archive.** Claim the item, then archive:
   `wrighty claim <id> --json` followed by
   `wrighty archive <id> --claimant-id <claimantId> --claim-token <claimToken> --json`.

Every git command must be visible to the user, and steps 2–5 each require the user's go-ahead
unless the user has already asked for the whole completion in one instruction. This flow works
in the resumed vendor session (which retains the implementation context) and equally in a fresh
session that only has the item ID.

## Context recovery

After compaction, use the known claimant ID and token. If either was lost, inspect with read-only
commands and ask the user how to proceed. Never read or adopt a token from claim storage. Never
invoke takeover merely to recover context; takeover requires an explicit user instruction.

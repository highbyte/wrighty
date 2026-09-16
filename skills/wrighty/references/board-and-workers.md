# Board and worker overview

Use these workflows when the user asks to see their Wrighty board, triage blocked work, discover
workers, or assess whether a worker could pick up a named item. These are observations, not
permission to change items or launch processes.

## Board by workflow status

Run `wrighty list --json`. Its existing `result` array contains canonical IDs, workflow status,
priority, claims, retained sessions, and operational state. The additive `listing` block supplies:

- `statusOrder` and `statusOrderSource`: use configured column order when supplied. If the source
  is `unknown`, group by the returned status names and say configured ordering is unavailable.
  Do not invent a standard Todo/In Progress/Done workflow.
- `archiveScope`, `statusFilter`, `fields`, and `limit`: describe the scope actually requested.
- `returnedCount`, `countScope`, and `completeness`: counts describe returned items. A
  `possibly-truncated` listing cannot establish whole-board totals.

For full-board counts, omit `--limit`, count the full returned array by workflow status, then
present a bounded selection with the total and omitted count. For a deliberately limited view,
use `--limit <n>` and label counts as that subset. Honor `--status`, `--archived`, and
`--include-archived` when requested. Never mix archived history into the default active board.
Show empty configured columns as zero only when the requested scope is complete and unfiltered.

Keep workflow columns separate from operational state. For example, an active-work column may
contain an item that is paused, awaiting clarification, or retry-scheduled. Include canonical IDs
in concise rows so follow-up requests identify the intended items unambiguously.

## Triage and available actions

Use `wrighty status --json` for operational groups, and `wrighty get <id> --json` to inspect a
specific blocker. Read `lastRun` and dispatch details before explaining what happened.
Retry-scheduled and handoff-queued work is deferred; do not classify it as awaiting clarification.

Use `wrighty actions <id> --json` for available actions; add `--all` when the user asks why an
alternative is unavailable. Use action names, reasons, recommendation, and execution metadata
from the response. A recommendation is advice, not execution authority.

## Individual workflow actions

For an authorized Queue, Send back, or Resume request, inspect the selected action and its
consequence with `wrighty actions <id> <name> --json`. On Local Markdown, these actions report
`execution: "supported"`. Explain any automatic-processing consequence if the user's request has
not already authorized it; do not add another confirmation once that exact effect is authorized.
Then execute the stable name with the returned `result.stateVersion`:

```shell
wrighty actions <id> queue --exec --yes --expected-version <stateVersion> --json
```

Use `send-back` or `resume` for those intents. Queue authorizes automatic processing when the
worker-queue policy is enabled; Send back revokes that authorization. With that policy disabled,
execution authorization stays independent. Resume queues the recorded session and preserves the
requirements, context, and execution selection; it does not start a worker. Interactive
`resume-session` is a different action and remains manual-only in this catalogue.

The command revalidates current state under the backend's mutation lock. On `ACTION_STATE_CHANGED`,
claim contention, missing session, or backend refusal, inspect again and report the specific reason.
Do not force takeover, substitute another action, or imitate these operations with a generic
status move or direct Markdown edit. These executors currently support Local Markdown only.

Read `result.outcome`, `before`, and `after` to report the applied transition, then use `workers`
for refreshed pickup prospects. An applied result with `refreshError` means the mutation succeeded
but worker assessment failed: inspect again without replaying the mutation. If a command fails
without a definitive outcome, re-read the item before any retry. Other catalogue entries remain
manual-only; use their documented focused procedure only within the user's authorization.

## Batch workflow actions

For a requested Local Markdown batch, freeze the selection before authorizing execution:

```shell
wrighty batch preview queue --status "Todo" --json
wrighty batch preview send-back --id local:12 --id local:19 --json
wrighty batch show <preview-id> --json
wrighty batch execute <preview-id> --yes --json
```

Use configured statuses, or explicit IDs from the user's selection. A status selection can include
`--field name=value` filters with the same exact-match AND semantics as `list`. Use `resume` for
retained sessions. Review `result.preview.candidates`, each item's `consequence`, `selectedCount`,
`eligibleCount`, `limited`, and `expiresAt`. At most 100 eligible items are frozen for five minutes.
If limited, explain which exact subset will run; never silently process subsequent batches.

Pass `--yes` only when the user's authorization covers the frozen items and their consequences.
If that effect is already authorized, proceed without another confirmation. Execution starts no
worker and never claims newly eligible items outside the frozen selection. Each candidate's
reviewed state is revalidated under the same mutation lock as individual actions.

Read stdout JSON even on exit 6 (partial result) or 130 (cancellation). Report `result.items` as
applied, skipped, failed, or unprocessed; explain `code` and `mutationMayHaveApplied`. A systemic
failure stops the rest without rolling back applied items. Repeating execute returns the stored
result; it does not retry skipped, failed, or unprocessed items. After a process interruption,
`batch show` recovers the journal and flags an in-flight item as uncertain. Inspect that item's
current state before proposing a new preview. Do not recreate and replay the whole batch blindly.

A missing/expired preview requires a fresh review. Previews/results belong to the same local
configuration and cache directory across processes and restarts; do not copy or edit the files,
or treat a preview ID as authority to execute. Use `workers --item <id> --json` for pickup evidence
after a successful action when the user's request needs it.

## Workers and pickup prospects

Run `wrighty workers --json` for worker discovery alone. It reads the configuration-scoped local
registry and checks the recorded PIDs and process-start identities; it does not contact the
tracker or inspect retained worktrees. Each run has a `runId`. Several web-hosted runs may share
one PID, so do not collapse them into one worker.

The result includes observation time, scope/coverage, `localWorkers`, named `liveness`, `origin`,
`reportedState`, heartbeat, structured startup `instance.scheduling`, loop `instance.progress`, intake, remaining
allowance, and configuration drift. Report old or unreadable scheduling as unknown. Do not parse
`invocationSummary`, use current agent as the worker's only supported agent, or reconstruct its
startup settings from current repository configuration.

For a named item, run `wrighty workers --item <id> --json`. This additionally reads the tracker and
shared selection/admission evidence. For each run, read `pickup.outcome`, `code`, `message`,
`alreadyProcessing`, and `afterCurrentItem`:

- `alreadyProcessing`: identify that run/session; do not launch competing work.
- `could-pick-up`: observed selection allows pickup, possibly after its current item. This is
  neither a reservation nor a timing guarantee; another item may be selected first.
- `cannot-pick-up`: explain the reason, such as closed intake, exhausted allowance, a different
  targeted item, filters, claim ownership, workflow state, or provider deferral.
- `unknown`: explain the missing evidence. Do not equate it with absence or launch a replacement
  automatically. Older registrations, sandbox denial, configuration drift, and inaccessible
  workspace evidence can produce this result.

An empty complete scope means no registrations were observed in this local configuration.
Unregistered workers, other configurations/accounts, and remote workers may still exist.
Incomplete/unavailable coverage cannot establish even that scoped absence. A stale registration
is not evidence of open intake. Capability fields do not waive fresh identity checks for control.

Assessment describes the item's current state, not its state after a proposed Queue/Resume action.
After an authorized mutation, re-read the item and assessment before describing pickup prospects.
Queueing and launching are separate permissions. With no suitable observed worker, explain the
reason and offer an appropriate bounded or continuous launch only if the user's intent calls for
it. A next-item `worker --once` does not target a named item; `worker --item <id>` does.

Waiting must be bounded or explicitly hosted by the agent platform. Recheck only when asked to
wait or when confirming an authorized operation, and stop on meaningful progress, failure, or a
needed user decision. A conversation is not a guarantee of unattended monitoring. Worker-spawned
implementation sessions must not recursively launch workers. Never run a paid provider probe
merely to strengthen an assessment.

For authorized launches and cooperative control, read [worker-control.md](worker-control.md).

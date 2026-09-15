# Action discovery and workflow execution

Use `wrighty actions` to inspect what you can do with an item before choosing an operation:

```shell
wrighty actions local:42
wrighty actions local:42 --all --json
wrighty actions local:42 open-item
```

The command supports Local Markdown and GitHub IDs, including the same short-ID resolution as
`get`. By default it shows available actions. `--all` includes blocked alternatives and their stable
reason codes. An optional action name selects one descriptor; an unknown name returns
`ACTION_UNKNOWN`, while a known unavailable selection returns its refusal code. These errors use
Wrighty's normal nonzero exit status and stderr JSON contract. Do not combine a selected name with
`--all`.

Discovery is read-only. Queue, Send back, and Resume report `execution: "supported"`; other
catalogue entries remain `manual-only`. Listing an action never claims an item, starts a vendor
session, grants permission, or overrides a pending retry.

## Execute one workflow action

```shell
wrighty actions local:42 queue --json
wrighty actions local:42 queue --exec --yes --expected-version <stateVersion> --json
wrighty actions local:42 send-back --exec --yes --json
wrighty actions local:42 resume --exec --yes --json
```

These three executors support Local Markdown and share the web Board's policy and backend
operation. `--exec` requires one action name and cannot use `--all`. An interactive invocation
shows the consequence and prompts; redirected input and JSON require `--yes`. This authorizes
only the named operation. Neither discovery nor `--yes` grants takeover or starts a worker.

Supply the discovery's `stateVersion` with `--expected-version` when executing a reviewed
snapshot. Wrighty always reads current state again, then validates and mutates under the local
store lock. A changed item, claim, session, or configuration refuses the old version with
`ACTION_STATE_CHANGED` (or the current action's more specific refusal). The fingerprint is not a
reservation or a credential. Without it, execution uses a fresh observation from this invocation.
Manual-only actions still return `ACTION_EXECUTION_UNSUPPORTED`; commands, URLs, and item text
are never interpreted as executors or shell input.

Execution JSON has `schemaVersion: 1` and a `result` with `itemId`, `action`, `outcome: "applied"`,
`observedAt`, `stateVersion`, `before`, `after`, and `startsWorker: false`. Each state contains the
workflow status, operational status, execution authorization, and dispatch marker. `workers`
contains a fresh pickup assessment after mutation. If that follow-up fails, `refreshError` is
`WORKER_REFRESH_UNAVAILABLE` and the result still says applied; inspect before attempting another
mutation. A worker may claim the item immediately afterward, so even a successful result remains
an observation rather than a reservation.

## Action vocabulary

| Name | Meaning |
| --- | --- |
| `open-item` | Review the source issue or open the local web console. |
| `clarify` | Use the existing human-edit/takeover flow to clarify requirements. An agent must not infer takeover permission from this suggestion. |
| `answer-on-issue` | Add GitHub clarification using the configured context-approval or trusted-author workflow. |
| `clarify-and-continue` | Local Markdown guidance for editing requirements and then explicitly continuing that item. |
| `queue` | Move an untouched Local Markdown backlog item to the configured worker queue. |
| `send-back` | Return an untouched queued Local Markdown item to the inferred configured backlog. |
| `resume` | Queue an eligible retained Local Markdown session for a continuous worker. |
| `continue-worker` | Start targeted headless continuation, including a directed handoff where applicable. |
| `resume-session` | Open the recorded vendor session interactively on its recording installation. |
| `retry-now` | Explicitly override a scheduled retry timer. |
| `inspect-recovery` | Read the item and operational status for current recovery details. |

Names are stable selectors; titles and descriptions are presentation. Queue, Send back, and Resume
share the corresponding Board controls' eligibility and execution path.
With worker-queue authorization enabled, Queue authorizes automatic processing and Send back revokes
that authorization. When it is disabled, execution policy remains independent. Resume queues the
recorded session and does not start a worker. These actions are not interchangeable status moves.

A clarification pause may have a recommended action. Other states can legitimately have no
recommendation. A scheduled retry or handoff is deferred work, not an instruction to start another
process immediately. Recommendations never authorize execution.

## JSON and state freshness

`--json` returns `schemaVersion: 1` and `result` containing:

- `itemId`, `stateObservedAt`, `stateVersion`, and nullable `recommendedAction`;
- `actions[]` with `name`, `title`, `description`, and `recommended`;
- `availability`, `unavailableCode`, and `unavailableReason`;
- `kind`, `execution`, `confirmation`, `requiresTty`, and `startsProcess`; and
- separate `commands`, `url`, and `agentPrompt` presentation fields.

URLs are links, not shell commands. Command sequences and agent prompts remain inert guidance.
Never replay serialized descriptors as execution authority or parse a human title to choose an
action. The actual operation revalidates current claims, context, permissions, and runtime state.
A manual command can still fail if state changes after discovery or a later launch check refuses it.

A missing, incomplete, remote, or unavailable workspace blocks local session actions. An active
claimant blocks competing session actions. Recorded worker continuation uses the existing
read-only targeted-worker preflight; no provider usage probe runs during discovery. Missing local
admission evidence is `ACTION_STATE_UNVERIFIED`, rather than permission to guess. Failed local
checks leave unrelated review actions visible.

`get --json` exposes the catalogue under `result.actions`. Status group items expose it under their
`actions` field; both additions preserve existing fields. Human `get` shows concise next actions,
and human `status` points high-attention items to the full discovery command. Worker attention,
retry, and handoff guidance uses shared named factories. Existing handovers are snapshots; refresh
with `wrighty actions` before deciding what is currently available.

See [worker lifecycle](worker.md), [claims and ownership](claims.md), and
[operator actions by surface](operator-actions.md) for the underlying procedures.

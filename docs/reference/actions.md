# Action discovery

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

Discovery is read-only. All descriptors currently report `execution: "manual-only"`, and `--exec`
returns `ACTION_EXECUTION_UNSUPPORTED`. Review the displayed guidance and use the existing focused
CLI command or web control when you have authorized the operation. Listing an action never claims
an item, starts a vendor session, grants permission, or overrides a pending retry.

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
currently describe the corresponding Board controls; they do not have generic CLI executors.
With worker-queue authorization enabled, Queue authorizes automatic processing and Send back revokes
that authorization. When it is disabled, execution policy remains independent. Resume queues the
recorded session and does not start a worker. These actions are not interchangeable status moves.

A clarification pause may have a recommended action. Other states can legitimately have no
recommendation. A scheduled retry or handoff is deferred work, not an instruction to start another
process immediately. Recommendations never authorize execution.

## JSON and state freshness

`--json` returns `schemaVersion: 1` and `result` containing:

- `itemId`, `stateObservedAt`, and nullable `recommendedAction`;
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

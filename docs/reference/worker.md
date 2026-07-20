# Autonomous worker mode

`wrighty worker` schedules one explicitly eligible item at a time, claims it with a fenced handle,
starts Claude Code, Codex, or Copilot headlessly, renews the claim for a fixed budget, and records
the workspace and vendor session address. Wrighty is the scheduler; the vendor CLI remains the
agent runtime.

Worker mode runs an unattended agent with broad permissions. Start with a dry run and one item:

```shell
wrighty create --title "Automate this" --body "..." --auto --agent claude
wrighty worker --dry-run --once --agent claude
wrighty worker --once --agent claude --workspace-mode worktree --item-timeout 30m
```

Dry runs never claim an item or start an agent and do not require confirmation. Before a live run,
Wrighty performs a read-only preflight and reports how many items are currently claimable plus the
first candidate. With `--once`, no claimable item means it prints the candidate diagnostics and
exits without a risk warning or confirmation prompt. A continuous worker prints the same complete
initial diagnostics, confirms once because it may process future items, and then uses compact
one-line `idle` events while polling. Non-interactive and JSON live runs must acknowledge the risk
with `--yes`. Preflight is only a snapshot; the contention-safe atomic pick still occurs after
confirmation.

Eligibility is opt-in. Local Markdown stores managed `wrighty-auto: true` and optional
`wrighty-agent`; GitHub uses `wrighty:auto` and `wrighty:agent=<vendor>` labels. Vendor resolution
is `--agent`, then the item preference, then `worker.defaultAgent`; Wrighty errors instead of
guessing. A generic worker started without either `--agent` or `worker.defaultAgent` prints an
informational notice that only item-pinned work can run. If automation-enabled items without a
resolved agent later appear during continuous polling, Wrighty reports that changed condition once
and then returns to compact idle messages. `--filter key=value` adds AND filters, `--max-items`
bounds spend, `--idle-timeout` bounds idle waiting, and `--json` emits one JSON lifecycle event per
line. `wrighty worker --check` runs a short, read-only vendor probe and verifies a usable session
handle; the probe still invokes the vendor and may incur usage.

`wrighty init` ensures the GitHub worker labels exist for every supported vendor and, unless
`--skip-issue-forms` is selected in the approved initialization plan, scaffolds a backlog form, a
default-agent worker form, and one agent-specific form per vendor. The default-agent form applies
only `wrighty:auto`; the worker must resolve its vendor from `--agent` or `worker.defaultAgent`. Agent
availability remains a property of the worker machine: choosing a form records intent, while worker
preflight still reports a missing or unsupported local vendor executable. Use
`wrighty init --skip-issue-forms` when the repository manages its own issue-template experience.
Interactive initialization asks whether to commit and push forms it changed. Automation must opt in
with `wrighty init --yes --publish-issue-forms`; `--yes` by itself does not publish repository files.
Wrighty's generated chooser configuration disables blank issues for contributors, although GitHub
continues to expose a maintainer-only blank option to users with Write access or above.

## Terminal color and machine output

Human worker output uses semantic color on event prefixes when `--color auto` (the default) detects
that the individual output stream is an interactive, ANSI-capable terminal. Standard output and
standard error are detected independently. Redirected output and writers without declared terminal
capability remain plain text.

Use `--color never` for durable human-readable logs, or `--color always` when an explicit consumer
such as `less -R` should receive ANSI sequences:

```shell
wrighty worker --yes --color never >worker.log 2>&1 &
wrighty worker --yes --color always | less -R
```

In automatic mode, the presence of `NO_COLOR` or `TERM=dumb` disables color. Explicit
`--color always` or `--color never` overrides those automatic checks. `--color always`
deliberately writes ANSI sequences even when human output is redirected.

`--json` always wins over color selection: every standard-output line remains unstyled JSON under
`--color auto`, `always`, and `never`. A background NDJSON worker can be started safely with:

```shell
wrighty worker --yes --json >>worker.ndjson 2>>worker-errors.log &
```

Color changes only the trusted event or warning prefix and resets immediately. Event names and
all existing text remain present, while paths, arguments, messages, session IDs, and operator
commands are never wrapped in styling. Color selection does not affect confirmation or `--yes`.

Worker dispatch state is separate from workflow status and eligibility. Wrighty manages
`wrighty-worker-state` locally and `wrighty:worker-state=<state>` on GitHub; operators should use
the CLI or web controls rather than edit it directly:

| State | Meaning | Continuous-worker behavior |
| --- | --- | --- |
| absent | Ordinary item | Eligible from `Todo` when `wrighty-auto=true`; this preserves compatibility with existing auto-tagged items. |
| `needs-attention` | A vendor session stopped for clarification or another operator decision | Shown prominently, but never retried automatically. |
| `queued` | Clarification is saved and the recorded session is ready to continue | Resumed before fresh `Todo` work. |

`wrighty-auto` remains the durable permission for unattended execution. Queuing is a deliberate
one-time dispatch decision; it does not require toggling automation off and back on.

The Local Markdown web editor exposes these managed values as **Eligible for worker processing**
and **Preferred agent**. If no item can be claimed, the worker reports how many active items it
considered in the source status, how many lack `wrighty-auto` or an item-level `wrighty-agent`,
how many filters excluded, how many cannot resolve a supported agent, and how many otherwise
eligible items were unavailable because of an active claim or claim contention.

Preassigned Claude and Copilot handles are stable for one claim generation but change when an item
is acquired again; deliberate continuation uses the session ID recorded on the active claim.

Workspace handling is a worker setting, not a work-item field. Resolution is the explicit
`--workspace-mode` option, then `worker.workspaceMode` in `.wrighty.json`, then `current`:

| Mode | Directory | Concurrency behavior |
| --- | --- | --- |
| `current` (default) | Current repository checkout | Takes an exclusive Wrighty worker lock. A second worker targeting the same canonical directory gets `WORKSPACE_BUSY` before it claims an item or starts an agent. |
| `shared` | Current repository checkout | Explicitly disables the worker lock. Multiple workers may run there concurrently. Wrighty warns because it cannot detect or resolve file, staging, build, or commit conflicts. |
| `worktree` | Fresh directory under `<repo>.worktrees` | Gives each item an isolated branch and checkout. Recommended for unattended or concurrent workers. |

`shared` is an unsafe opt-out for an operator who accepts responsibility for coordinating the
items. Agents may not recognize that a changed or staged file belongs to another concurrent agent.
Select it explicitly for one invocation, or deliberately make it the repository default:

```shell
wrighty worker --workspace-mode shared --yes
```

```json
{
  "worker": {
    "workspaceMode": "shared"
  }
}
```

Every live run resolved to `shared` prints the additional collision warning, including runs using
the configured default.

In `worktree` mode, Wrighty deliberately does not merge, push, or open PRs. A successful clean
worktree is removed while its branch remains; dirty or failed worktrees are retained. Pass
`--keep-workspace` to retain a successful worktree too. Wrighty passes the absolute original
tracker configuration path to the child agent as `WRIGHTY_CONFIG_PATH`. Consequently, Local
Markdown `get`, mutation, renewal, and finish commands operate on the authoritative original store
rather than a stale copy checked out in the agent worktree.

After an item is genuinely finished, Wrighty prints a `review:` command that opens the completed
vendor session interactively when its workspace still exists. The command invokes the vendor
directly, carries no Wrighty claimant ID or token, and does not reacquire the completed item. It is
always available in `current` and `shared` modes when the checkout still exists. In `worktree` mode, use
`--keep-workspace` if you want to retain a clean successful worktree for later review:

```shell
wrighty worker --once --workspace-mode worktree --keep-workspace
# finished: ...
#   review: cd '...' && claude --resume '...'
```

The suggested follow-ups for Plan 014 propose persisting completed-session addresses and adding
`wrighty review-command <id>`.

Renewal occurs at lease half-life and has a fixed spawn-time budget equal to `--item-timeout`. It
can never renew past that deadline, so the maximum hold after a hung run is
`--item-timeout + leaseMinutes`. On `CLAIM_STALE` or `CLAIM_EXPIRED`, the default
`--on-fenced kill` stops the process tree. `detach` is available for deliberate operator use, but a
detached process can keep editing files and is unsafe in a shared checkout.

While a vendor process is running, the worker emits a single-line operational heartbeat every five
minutes. It reports elapsed time, the current claim-expiry time, remaining fixed timeout budget,
and workspace mode:

```text
2026-07-19T14:20:00.0000000+00:00 running: local:22 [claude] — 20m elapsed; claim valid until 2026-07-19T15:00:00.0000000+00:00; timeout in 40m; workspace worktree
```

This is intentionally process-level visibility rather than an agent transcript. Wrighty does not
stream model responses, tool calls, or reasoning, and the optional web dashboard does not become an
agent frontend. In another terminal, use `wrighty get <id>` to inspect the durable claim, session,
workspace, and lease state. When the worker runs under a service or with redirected output, ordinary
process logs retain the same heartbeat and lifecycle lines.

Vendor process success is not item completion. An item is `finished` only when the agent calls
`wrighty finish` and the configured completion state is observed. If a successful agent turn exits
while its exact claim remains active, the worker emits `needs-attention`, leaves the item
`In Progress`, sets its worker state to `needs-attention`, stops renewing, and retains the
session/workspace claim until its finite lease expires. A continuous worker does not retry that
state automatically. `--once` returns exit code 10 for this outcome.

The `needs-attention` footer is organized by what the operator wants to do. Its recommended
clarification path is `wrighty web`: open the item, choose **Take over for editing** while its claim
is active or **Claim for editing** after expiry, and edit the title or body. Then choose
**Save and queue for worker** to end human ownership and make the recorded session available to an
already-running continuous worker. Choose **Save and hand back to <agent>** when you instead want
the interactive vendor resume command immediately. Choose **Finish** when the tracked work is
already complete. To close the item without further agent work, save it and choose **Archive** from
the item view. The web claim path preserves a complete local recorded session across expiry.

The CLI equivalent is atomic and does not require copying claim environment variables:

```shell
wrighty edit <id> --takeover --yes --body-file requirements.md --requeue
```

`--requeue` requires a complete recorded agent/session/workspace address. It clears active human
ownership, rotates the terminal fencing generation, and marks the session `queued`. A normal
continuous `wrighty worker` scans queued `In Progress` items before fresh `Todo` candidates and
resumes the recorded vendor session. `wrighty requeue <id>` is available when the caller already
holds and supplies the exact claim handle.

After saving the clarification, continue headlessly with:

```shell
wrighty worker --item <id> --yes
```

That command works both while the current claim is active and after it expires. Wrighty infers
whether to take over the active local session, recover an expired session under a new claim
generation, or start a new session when no recorded address exists. Claim expiry invalidates
authorization, not the vendor's durable session; an expired token is never revived or reused.
Automatic recovery is limited to the installation that created the session, where its recorded
workspace and vendor state are meaningful. Another installation must use `--fresh` explicitly
after expiry.

For CLI editing while the current claim is still active, use either the interactive editor or
direct edit options:

```shell
wrighty edit <id> --takeover
wrighty edit <id> --takeover --yes --title "Clear title" --body-file requirements.md
```

The first command prompts before displacing an active claimant; the scripted example uses `--yes`.
Both also work after expiry, acquiring a new human editing claim without a takeover prompt while
preserving a recoverable local session. They apply the edit with the resulting handle inside one
Wrighty process, retain human ownership, and print the headless continuation command. No environment
variables need to be copied.

`--item <id>` processes exactly that item and chooses from claim state: an active same-installation
session is taken over and resumed; an expired session is reacquired under a new claim and resumed;
an item with no recorded session starts new. It never takes over another installation's active
claim, and it refuses to silently discard an incomplete or missing-workspace session address.
Use Boolean intent assertions when inference is not desired:

```shell
wrighty worker --item <id> --resume   # require a recoverable existing session
wrighty worker --item <id> --fresh    # require an unclaimed item and start a new session
```

`--resume` and `--fresh` are mutually exclusive and fail when current state does not match the
requested intent. Fresh starts still require normal worker eligibility and accept the configured
source or active status. Add `--dry-run` to print the inferred or asserted action without claiming,
taking over, or spawning.

For takeover, run:

```shell
wrighty takeover <id> --yes --print-resume-command
```

This rotates the fencing token and preserves the recorded vendor session/workspace address. With
`--print-resume-command`, an agent takeover prints both interactive and headless-worker alternatives;
a human takeover prints the safe headless-worker continuation. The separate
`wrighty resume-command <id>` prints only the recorded interactive vendor address without rotating
the claim. Takeover is limited to the same Wrighty installation. A worker elsewhere cannot be
seized on demand; wait at most
`--item-timeout + leaseMinutes` for expiry or coordinate with that installation.

The web UI provides the equivalent flow: **Take over for editing**, clarify, then choose
**Save and hand back to _Agent_** for interactive continuation, or plain **Save** for a headless
worker continuation command. Handback rotates the claim to a fresh agent claimant and displays the
environment-prefixed interactive command plus the headless alternative. Plain Save keeps human
ownership and displays only the headless command, which performs the transfer when run. For an
interactive continuation, enter the adjacent vendor-specific follow-up prompt to explicitly load
the Wrighty skill, re-read the clarified item, and continue.
Release ends ownership without discarding recovery state: the recorded session/workspace address
is a durable machine-local record that survives release and expiry, so a released item can still
be resumed later with `wrighty worker --item <id>` on the installation that recorded the session.

## Verified vendor capability matrix

Verified on 2026-07-16 with Claude Code 2.1.210, codex-cli 0.144.1, and GitHub Copilot CLI 1.0.71:

| Capability | Claude | Codex | Copilot |
| --- | --- | --- | --- |
| Headless start | `-p` | `exec` | `-p` |
| Machine output | JSON | JSONL (`--json`) | JSONL |
| Session handle | preassigned UUID | parsed from `thread.started` | preassigned name |
| Headless resume | `-p --resume` | `exec resume` | `-p --resume=` |
| Working directory | process cwd | `-C` | `-C` |
| Autonomy | `--dangerously-skip-permissions` | headless sandbox | `--allow-all-tools` |

These CLI surfaces are version-sensitive. Validate vendor upgrades in a throwaway repository before
unattended use.

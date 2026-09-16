# Worker launch and control

Read [board-and-workers.md](board-and-workers.md) for discovery and pickup evidence. Queueing,
resuming an item, and launching a worker are separate effects. Use the user's existing authorization
when it covers the selected items, agent, processing limits, and unattended execution; do not ask
again for an already authorized effect.

## Decide whether to launch

Before launching for a named item, run `get <id> --json`, `actions <id> --json`, and
`workers --item <id> --json`. For queue processing, inspect `workers --json` and the requested board
selection. If a run already processes the item, report it; do not launch competing work. If a
suitable worker could pick it up, wait only when requested, with a bounded observation period.
Unknown or incomplete evidence is not permission to launch a replacement. Explain the missing
information and resolve it or get an explicit decision about launching despite that uncertainty.

Choose a worker agent from explicit user intent, item policy, or configured default. The agent
hosting this conversation is not a default. Preserve configured workspace and profile choices
unless the user requests an override. Never probe a paid vendor merely to check readiness.

Use the smallest processing scope that meets the request:

```shell
wrighty worker --item <id> --item-timeout 30m --yes --json
wrighty worker --once --item-timeout 30m --yes --json
wrighty worker --max-items 3 --idle-timeout 5m --item-timeout 30m --yes --json
```

`--item` selects exactly one item, including existing continuation rules; `--once` selects the next
eligible item. Do not replace one with the other. Use `--agent`, `--profile`, `--filter name=value`,
`--workspace-mode`, `--from`, and `--to` only within the requested scope. `--item-timeout` bounds each
item, not the whole worker. `--max-items` bounds item count, and `--idle-timeout` bounds an idle
period. A targeted run already has an effective item limit of one. Do not take over claims or force
`--fresh`, `--resume`, or `--handoff` merely to get past a refusal.

## Own the process honestly

For Codex, Claude, Copilot, and OpenCode surfaces, foreground execution is the portable path.
Keep a bounded run attached to the invoking command, consuming its output through completion.
For authorized continuous work, use a user-owned terminal or a host-provided retained terminal
whose lifetime is established. For example, in that terminal:

```shell
wrighty worker --idle-timeout 30m --item-timeout 30m --yes --json
```

State the terminal/process owner and its actual cancellation and exit behavior before launching.
A retained terminal keeps the executable running without a model reasoning loop, but is not a
promise of survival across task cancellation, app exit, logout, or restart. There is no Wrighty
detach command. Do not emulate one with background shell syntax or invent a keepalive loop.
If the available tool cannot retain the process for the required duration, provide the exact
foreground command for the user's terminal and say it has not been started.

Existing web-hosted runs can also provide continuous processing. Their launch remains in the web
console; starting `wrighty web` alone does not start a worker. Do not spoof its browser requests.
No vendor-specific detached launch path is offered by this skill.

Worker-spawned sessions must not start workers recursively. `WRIGHTY_WORKER_CHILD=1` marks that
context and live launch returns `WORKER_RECURSIVE_LAUNCH`; do not remove the marker to bypass it.

## Read the launch outcome

`worker --json` streams NDJSON. Preserve the `worker-run-started` receipt: its `runId`, `registered`,
configuration scope/revision, structured `scheduling`, foreground `owner`, `statusCommand`, and
`logs` identify the actual run and limits. Registration precedes work execution; it is not proof
that an agent has started or that an item succeeded. Item events report that progress.

Run inspection/control in the same configuration and cache context as the launch. Logs remain in
the invoking terminal's stdout/stderr; Wrighty does not create a durable CLI run-log file. Save or
retain that output using the host's supported facility when the request needs it.

`registered: false` means execution may continue without registry control. Keep the owning
command attached and observe it; do not start a duplicate to obtain a run ID. An empty `--once`
preflight exits without a launch receipt. `worker-run-completed` reports the summary and stop
reason after the host has unwound. Read item state as well when interruption or failure needs
recovery. An error or missing receipt alone does not establish that nothing started: inspect
workers, item/session state, and terminal output before any retry.

## Inspect, drain, or interrupt an exact run

```shell
wrighty workers show <run-id> --json
wrighty workers drain <run-id> --yes --json
wrighty workers interrupt <run-id> --yes --json
```

Inspect the exact run and explain the requested effect unless already authorized. Drain closes
intake and lets the active item and bookkeeping finish. Interrupt cancels the active agent process
tree and invokes the existing bounded finalizer; it is not a normal successful completion.

Both CLI and web-hosted runs use the existing registry/control protocol. Never kill a PID: hosted
runs can share a web process. Control re-reads the record and verifies process/start identity,
configuration scope, protocol, and supported mode. Stale, unknown, changed, or legacy identities
produce a refusal. Do not bypass it or fall back to an OS process kill.

An accepted response has `completed: false`: the request was persisted, not necessarily observed
by the worker yet. Observe the owning terminal and re-inspect the run within a bounded interval.
A missing registration is not proof that item bookkeeping succeeded; inspect the item after exit.
Interrupt can escalate a pending drain; a later drain never downgrades an interrupt. If a control
response is ambiguous, refresh before retrying the same run; never select a replacement run
implicitly. An external supervisor may restart a stopped process; stopping does not disable that
supervisor. OS services, startup installation, automatic restart, and boot persistence remain
outside this skill's workflow.

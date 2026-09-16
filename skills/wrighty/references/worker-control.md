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

Default to one attached run: `--item <id>` for a named item or `--once` for the next eligible item.
Use `--max-items N` only when the user explicitly requests processing a bounded number of eligible
items. Include item and idle timeouts appropriate to that request and keep the run attached:

```shell
wrighty worker --item <id> --item-timeout 30m --yes --json
wrighty worker --once --item-timeout 30m --yes --json
wrighty worker --max-items 3 --idle-timeout 5m --item-timeout 30m --yes --json
```

`--item` selects exactly one item, including existing continuation rules; `--once` selects the next
eligible item. Do not replace one with the other. Use `--agent`, `--profile`, `--filter name=value`,
`--workspace-mode`, `--from`, and `--to` only within the requested scope. `--item-timeout` bounds each
item, not the whole worker. `--max-items` bounds item count, and `--idle-timeout` bounds an idle
period. The count limit is not a total wall-clock deadline or a frozen selection of named items;
the worker selects eligible work as it proceeds. Do not translate a request for specific IDs into
`--max-items N`. A targeted run already has an effective item limit of one. Do not take over claims or force
`--fresh`, `--resume`, or `--handoff` merely to get past a refusal.

## Own the process honestly

For Codex, Claude, Copilot, and OpenCode surfaces, keep every skill-launched run attached to its
invoking command and consume its output through completion. Do not start a continuous worker,
even when the user requests one or the host offers a retained terminal. An idle or item timeout
alone does not make a continuous worker an allowed finite run. Do not emulate continuous work by
repeatedly launching `--once` or `--max-items` runs or by scheduling a keepalive/relaunch loop.

For continuous processing, give the user the foreground command to run in their own terminal, or
direct them to **Start worker** in the web console. Explain that you have not started it. For example:

```shell
wrighty worker --idle-timeout 30m --item-timeout 30m --yes --json
```

For an allowed finite launch, state the invoking command/process owner and its actual cancellation
and exit behavior. If the available tool cannot keep the run attached through completion, provide
the command for the user's terminal instead. Do not promise survival across task cancellation,
app exit, logout, or restart. There is no Wrighty detach command; do not emulate one with background
shell syntax, a detached session, or a terminal-opening tool.

The skill may inspect, assess pickup from, drain, or interrupt existing continuous workers within
the user's authorization. Web-hosted launches remain a user action in the web console; do not
launch one through browser automation or spoofed requests. Starting `wrighty web` alone does not
start a worker. No vendor-specific detached launch path is offered by this skill.

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

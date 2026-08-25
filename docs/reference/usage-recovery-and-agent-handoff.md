# Usage recovery and agent handoff

When a coding agent runs out of subscription capacity mid-task, the default outcome elsewhere is a
dead session and a developer who finds out hours later. Wrighty treats that as a scheduling problem
instead: the work item, the vendor session address, and the retained workspace all outlive the
failed run, so the work can resume after the provider's reset — or, if you have more than one
subscription, continue under a different agent.

This page is the authority for that behavior. [Autonomous worker mode](worker.md) covers unattended
processing generally; this page covers what happens when the *provider* is the thing that failed.

- [What counts as a usage failure](#what-counts-as-a-usage-failure)
- [Same-agent retry](#same-agent-retry)
- [The provider circuit](#the-provider-circuit)
- [Cross-agent handoff](#cross-agent-handoff)
- [Where recovery state is visible](#where-recovery-state-is-visible)
- [Configuration](#configuration)
- [Operator actions](#operator-actions)
- [Privacy and installation locality](#privacy-and-installation-locality)
- [Limitations](#limitations)

## What counts as a usage failure

Not every agent failure is a capacity failure, and treating them alike would either waste retries or
mask real bugs. Each adapter normalizes its vendor's output into a bounded, sanitized record: a
normalized kind, an optional provider code and retry timing, whether it is retryable, and whether
the classification is `authoritative` (the provider said so) or `inferred` (Wrighty concluded it).

| Kind | Example | Recovered automatically |
| --- | --- | --- |
| Subscription usage exhaustion | The plan's allowance is spent until a stated reset | Yes |
| Temporary rate limiting | Short-window throttling, often with `Retry-After` | Yes |
| Provider outage | The vendor's service is failing | No — surfaces as needs-attention |
| Authentication | Not logged in, expired credentials | No |
| Billing | Payment or plan problem | No |
| Permission | The profile forbade what the agent tried | No |
| Context limit | The conversation exceeded the model's window | No |
| Ordinary agent failure | The agent errored or exited non-zero | No |
| Unknown | Unrecognized output | No |

Only the first two open recovery. The distinction matters because a retry against an
authentication failure would loop forever, and a needs-attention on a quota reset would page you
for something that fixes itself.

Near-miss text does not count. Classification is tested against sanitized captured fixtures per
supported CLI version specifically so that prose merely *mentioning* quotas is not read as
exhaustion.

## Same-agent retry

The default action for a retryable usage or rate-limit failure is to keep the same vendor session
and the same workspace, write the `retry-scheduled` dispatch state, and release the claim. Nothing
is lost: the agent's accumulated context lives in the vendor's own session, and Wrighty holds the
address.

The retry time is chosen in strict precedence:

1. An **exact provider reset**, when the vendor states one.
2. A **`Retry-After`** delay.
3. **Exponential fallback** from `initialRetryMinutes`, multiplied by `backoffMultiplier`, capped
   at `maxRetryHours`.

An exact reset gets `resetGraceMinutes` added, plus up to 30 seconds of deterministic
per-installation jitter — the grace absorbs clock skew, and the jitter stops several installations
from stampeding the provider at the same instant. A reset timestamp already in the past becomes a
near-immediate jittered retry rather than a tight loop.

Each due attempt takes a **new claim generation** but resumes the **existing vendor session**. After
`maxAttempts`, the item stops retrying and moves to `needs-attention` for a human.

An item displayed as `attempt 5 of 5` has its fifth and final retry scheduled but not yet consumed.
If that run also encounters a retryable capacity failure and cross-agent handoff is not enabled (or
no target is available), Wrighty clears the schedule and moves the item to `needs-attention` while
retaining the same-agent session for an explicit operator action.

## The provider circuit

Retrying one item is useful; letting fifty items each discover the same exhausted subscription is a
spawn storm. So the first usage or rate-limit failure also opens a sanitized **provider circuit** in
the installation cache (`provider-availability-v1.json`).

While the circuit is open, automatic selection skips every fresh item resolved to that provider
*before* taking a claim, preparing a workspace, or spawning the vendor — the cheap checks come
first, so an exhausted provider costs nothing per item.

When the circuit and a retained retry both come due, one worker atomically acquires a **probe
lease** and the others observe `probe-in-progress` and wait. That single probe is either a registered
read-only capacity probe, or — where no such probe exists — the one due retained-session resume,
which doubles as the capacity test. A successful result closes the circuit; another capacity
failure reopens it and increments the failure count.

Crucially, a *non-capacity* result also closes the circuit. Authentication, permission, context, and
ordinary failures do not poison capacity state, because they say nothing about capacity.

To test a provider without claiming or changing any item:

```shell
wrighty provider probe copilot
wrighty provider probe copilot --yes --json
```

The probe starts the provider's CLI with a bounded check request, so **it may consume subscription
usage**. It records the same short lease, so a probe and a worker cannot race. It can run whether
the circuit is absent, available, or unavailable; explicit confirmation is required (`--yes` for
JSON or non-interactive use); and it never claims an item, prepares a worktree, or changes item
state. A successful probe leaves capacity available; a usage or rate-limit response opens or
extends the circuit through the normal bounded retry policy; any other failure leaves or makes the
circuit closed.

## Cross-agent handoff

Handoff is **opt-in** and is the second stage of recovery, not the first. With
`allowCrossAgentHandoff` enabled, an item that exhausts its same-agent retries continues under a
different installed agent instead of stopping at needs-attention.

What a handoff is:

- a **new session** with the target vendor;
- in the **same retained workspace**, with the work already done still on disk;
- seeded with a **bounded, redacted context packet** built from the source session's transcript —
  previous run facts, git-observed workspace changes, and, where the source vendor's local session
  surface supports it, selected conversation excerpts.

What it is not: a cross-vendor session resume. There is no such thing. Vendors cannot import each
other's native sessions, and Wrighty does not pretend otherwise — the target starts fresh and is
told what happened.

Target selection filters the configured fallbacks by what is actually installed and not behind an
open circuit, so a handoff never targets an agent that is itself exhausted. The target runs under
its **own permission profile**, not the source's. The replaced source address is retained as session
lineage, so the chain remains inspectable. Total automatic recovery is bounded: same-agent retries
consume `maxAttempts`, and handoffs may add at most the configured fallback count on top before the
item moves to `needs-attention`.

Handoff has two triggers beyond the automatic usage-failure path, because running out of quota is
not the only reason to change agent:

```shell
wrighty worker --item <id> --handoff --agent codex --yes
```

is the explicit "switch target agent" action; it supersedes a needs-attention item's retained claim
rather than waiting out its lease. Alternatively, **setting the item's agent field to a different
vendor than the recorded session** directs the next poll to hand the work there — the board-native
gesture, available from the web console's Agent dropdown and from the
`Wrighty policy - agent` field on a GitHub Project — including for a needs-attention item, whose
retained claim the directed handover supersedes. Every handoff also writes that field to the
target, so a field/session mismatch always means a handover is pending rather than a stale display.
The configured `worker.defaultAgent` is a selection fallback, never a direction — only the
item-level field directs. An explicit `--agent` naming the recorded vendor overrides the direction
and resumes the recorded session instead.

Per-vendor export support, and what happens when a transcript is unavailable, are in the
[session export matrix](worker.md#session-export-for-cross-agent-handoff). The short version: the
handoff still happens, with the workspace but no conversation history, and the target is told why.

## Where recovery state is visible

Recovery is never invisible. The authoritative record is the dispatch state; each surface projects
it.

| Surface | Retry and handoff visibility |
| --- | --- |
| CLI | `wrighty status` (and `--json`) groups items by operational status and shows non-available provider circuits, distinguishing `unavailable-until` from an active probe lease. |
| Local Markdown web | Board cards show the resolved provider as unavailable; item details explain the automatic deferral and the explicit override. Provider state participates in the board ETag, so closure refreshes without an item mutation. |
| Local Markdown files | `wrighty.dispatch.state` in front matter carries the category; exact timing stays in the machine-local sidecar. |
| GitHub | The `wrighty:dispatch-state=<state>` issue label is authoritative. Display-only `Wrighty dispatch - …` Project fields and the single rolling handover comment carry provider/retry detail and suggested commands. |

Provider *capacity* is deliberately not GitHub state — it is installation-local, because it
describes this machine's subscription, not the repository's.

## Configuration

Everything lives under `worker.usageFailure`. Full option table in
[Configuration](configuration.md#workerusagefailure).

**Single subscription — park and resume (the default).** No configuration needed; this is what you
get out of the box:

```json
{
  "worker": {
    "usageFailure": {
      "action": "retry",
      "initialRetryMinutes": 30,
      "backoffMultiplier": 2,
      "maxRetryHours": 6,
      "maxAttempts": 5
    }
  }
}
```

**Multiple subscriptions — retry, then hand off.** Same-agent retry is still tried first; handoff
catches what retry could not recover:

```json
{
  "worker": {
    "usageFailure": {
      "action": "retry",
      "allowCrossAgentHandoff": true,
      "fallbacks": {
        "claude": ["codex", "copilot"],
        "codex": ["claude", "copilot"],
        "copilot": ["codex", "claude"]
      }
    }
  }
}
```

**Hand off immediately**, without waiting out retries, with `"action": "handoff"`.

**Never recover automatically**, with `"action": "needs-attention"` — every usage failure becomes a
human decision.

Two things that are easy to get wrong:

- **Listing fallbacks does not enable handoff.** Fallbacks are an ordering, not a switch. Handoff
  requires `action: "handoff"` or `allowCrossAgentHandoff: true`. The defaults above are already
  populated even when handoff is off.
- **Listing fallbacks does not change an item; a started handoff does.** Handoff targets are
  agents you configured and authenticated. When a handoff starts, Wrighty updates the item's
  agent policy field to the target agent so the board names the agent now responsible; merely
  listing fallbacks never rewrites the field.

Interactive `wrighty init` offers to enable handoff (defaulting to yes) when more than one supported
agent is installed, including on a rerun over existing configuration.

## Operator actions

| You want to | Do this |
| --- | --- |
| Process an item now, ignoring its retry timer and the provider circuit | `wrighty worker --item <id> --yes` |
| Test whether capacity is back | `wrighty provider probe <agent>` |
| Hand a specific item to a specific agent | `wrighty worker --item <id> --handoff --agent <vendor> --yes` |
| Hand work over from the board | Set the item's agent field to a different vendor |
| See what is parked and why | `wrighty status` |

`wrighty worker --item <id> --yes` is the deliberate scoped override: it applies to that one item,
not to the provider globally. Clearing a healthy circuit outright, without a probe, is tracked
separately in [issue #42](https://github.com/highbyte/wrighty/issues/42); normal recovery is the
probe.

## Privacy and installation locality

Wrighty retains a normalized failure kind, an optional provider code, and retry timing. It does not
retain or publish the provider's raw account response, account identifiers, or balances. Handoff
packets are bounded and redacted, with truncations recorded, and are written as a machine-local
inspection artifact — transcripts are never published to GitHub.

Provider circuits are keyed by installation cache plus normalized agent type, because the supported
CLIs expose no safe, non-secret account-scope key. The practical consequence: a circuit describes
*this installation's* view of capacity. Another machine sharing the same subscription will discover
exhaustion independently rather than inheriting it.

OpenCode can route different provider-qualified models through one agent executable. Until Wrighty
has a safe capacity identity below the agent level, an OpenCode usage failure opens the circuit for
OpenCode as a whole and an explicit probe tests its configured default model. Configure OpenCode
fallbacks deliberately; Wrighty does not add them to the shipped fallback graph.

## Limitations

Wrighty maximizes the use of the capacity you already have. It does not:

- increase your quota, buy credits, enable overages, or change billing settings;
- guarantee an exact reset time when a provider does not expose one — the documented fallback is a
  bounded backoff schedule marked `inferred`;
- resume a vendor session inside a different vendor;
- move workspaces between machines or installations;
- bypass provider terms, authentication, or rate limits.

A handoff also cannot recover what the source agent never wrote down. Context that existed only in
the model's reasoning, and not in the transcript or the workspace, does not survive the change of
agent.

## Related

- [Autonomous worker mode](worker.md) — eligibility, workspace modes, needs-attention, and the
  vendor capability matrices.
- [Configuration](configuration.md) — the full `worker.usageFailure` option table.
- [Item metadata](../item-metadata/README.md) — exactly what each backend stores for dispatch state.

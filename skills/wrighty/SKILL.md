---
name: wrighty
description: Safely operate Wrighty through the `wrighty` CLI. Use only when the user explicitly asks to use Wrighty, the Wrighty CLI, or a Wrighty work-item ID, including requests to list, inspect, create, pick, claim, edit, move, finish, archive, or release tracked work. Do not trigger for generic tasks, GitHub issues, planning, backlogs, or project management that do not explicitly identify Wrighty.
---

# Wrighty

<!-- wrighty-skill-version: 0.14.0 -->

Operate Wrighty state only through the `wrighty` command. Never mutate tracked state by editing
local Markdown, invoking `gh`, calling GitHub APIs/MCP, writing claim comments, or changing Project
fields directly.

## Workflow

1. Verify `wrighty` is callable.
2. Before the first mutation, run `wrighty init --check --json`. If configuration is missing or
   invalid, explain the failure and stop; never initialize implicitly.
3. Use `--json` for decisions and error handling. Use `list --compact` only for concise display.
4. To answer "what should I work on?" or "what is stuck?", start with `wrighty status --json`: it
   groups the active items by operational status — needs-attention, completed (retained worktree),
   paused (resumable), active, queued, retry-scheduled, and handoff-queued — the machine-side
   counterpart to the web console, and the primary discovery surface for the GitHub backend. Read
   the `lastRun` block to learn *why* an item is blocked before clarifying it. Distinguish blocked
   from deferred: `retry-scheduled` and `handoff-queued` items are already scheduled to continue.
5. Use composite commands instead of recreating their steps:
   - next work: `wrighty pick --claimant-kind agent --json`;
   - completion: `wrighty finish <id> --claimant-id <claimantId> --claim-token <claimToken> --json`;
   - hand the recorded session back to a continuous worker: `wrighty requeue <id> --claimant-id <claimantId> --claim-token <claimToken> --json`.
6. Keep the canonical item ID, claimant ID, claim token, and Creation attempt ID in context. After
   compaction, recover an approved launch context with
   `wrighty context <id> --revision <revision> --json`.
7. Branch on `error.code`, never error prose.
8. Do not release a claim while a Wrighty mutation has an ambiguous result.

Read [references/workflow.md](references/workflow.md) completely before mutating Wrighty state.
Read [references/errors.md](references/errors.md) when a command fails or is being retried.

## Invariants

- Claim a specified item before editing it. In an AI agent session, always acquire ordinary work
  with `--claimant-kind agent`, including title, body, metadata, eligibility, or preferred-agent
  edits. Never pass `--claimant-kind human` merely because the user requested the mutation:
  explicit claimant options override runtime detection and would misattribute the agent's work.
- Retain the `claimantId` and `claimToken` returned by claim, pick, or an explicitly requested
  takeover. Pass both on every edit, move, finish, archive, release, or renewal.
- Treat `CLAIM_STALE` as a hard stop. Never reclaim or take over automatically.
- In worker-spawned sessions, use `WRIGHTY_CLAIMANT_ID` / `WRIGHTY_CLAIM_TOKEN`; the item is
  pre-claimed and must not be claimed again.
- In worker-spawned sessions, let Wrighty manage lease renewal. Do not infer expiry from
  `expiresAt`; only a `CLAIM_EXPIRED` or `CLAIM_STALE` mutation response is authoritative.
- When blocked or missing required clarification, do not finish or invent work. Explain the blocker
  and exit so the worker can report `needs-attention` and preserve the resumable claim temporarily.
- Treat `retry-scheduled` and `handoff-queued` as deferred, not failed. Wrighty has already decided
  to continue that work; report the state and reason rather than claiming the item or restarting
  its work in this session. Only `needs-attention` is waiting on a person.
- Never run `wrighty provider probe` as a diagnostic. It starts the vendor CLI and consumes
  subscription usage; run it only when the user explicitly asks.
- Describe a handoff as a new session with the target agent in the same retained workspace, seeded
  with a bounded summary. Never describe or promise one vendor resuming another vendor's session.
- Set an item's model and effort only through `--profile <name>`, never by naming a vendor model on
  the item. Profile names are shared repository vocabulary; what they mean in models and effort is
  a user setting, so a name you cannot verify against the repository's configured list is a guess.
- Never invent a model name for the user. If they want a specific model, that is a user-scoped
  override they make with `wrighty config profile set`, because only they know their entitlements.
- Invoke `takeover` only when the user explicitly asks for a human takeover of that item. Do not use
  takeover as a shortcut for an agent's ordinary edit.
- For a substantial new item, collaborate on and settle the exact title, body, and metadata before
  creating it. Do not create a placeholder unless the user explicitly wants a tracked draft.
- Judge requirements readiness by meaning, not by headings or a fixed Markdown template, before
  creating or materially updating an actionable item and before beginning its implementation. An
  item is ready when its intended outcome and completion evidence are clear, no material
  user-owned decision remains, and repository evidence can resolve ordinary implementation detail.
  Make low-risk reversible assumptions when appropriate. Otherwise ask only for the smallest
  clarification that would unblock the work; do not silently rewrite the user's requirements.
- A user-requested tracked draft may remain incomplete, but describe it as a draft and do not mark
  it for automatic processing or present it as implementation-ready until it passes the same
  semantic assessment.
- After creating or materially clarifying an actionable item, if the user has not chosen what
  happens next, offer three choices: implement in this agent session, mark it for automatic worker
  processing, or do nothing for now. Never reduce this decision to a yes/no implementation question.
- Never infer autonomous-worker permission from a agent policy or from using an AI to author the
  item. Pass `--auto` only when the user explicitly authorizes unattended processing.
- Generate a Creation attempt ID before create and reuse it for every retry.
- Use `wrighty adopt` only when the user explicitly asks to enroll one or more named existing
  GitHub issues. Never scan for or adopt arbitrary repository issues.
- Treat `AlreadyOwned`, resumed create, and already-finished results as success.
- Never bypass another worker's claim.
- Never invent replacement work after a not-found or no-item result.
- Read Local Markdown custom fields from `get --json` under `result.fields`. Write them only with
  repeatable `--field name=value`; use `--field name=` to delete. A `NOT_SUPPORTED` response means
  the configured backend does not provide custom fields.
- Do not equate commits, tests, pushes, pull requests, issue closure, or code completion with Wrighty
  completion; invoke `finish` only when the tracked work is actually complete.

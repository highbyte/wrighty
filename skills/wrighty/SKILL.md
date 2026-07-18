---
name: wrighty
description: Safely operate Wrighty through the `wrighty` CLI. Use only when the user explicitly asks to use Wrighty, the Wrighty CLI, or a Wrighty work-item ID, including requests to list, inspect, create, pick, claim, edit, move, finish, archive, or release tracked work. Do not trigger for generic tasks, GitHub issues, planning, backlogs, or project management that do not explicitly identify Wrighty.
---

# Wrighty

<!-- wrighty-skill-version: 0.4.0 -->

Operate Wrighty state only through the `wrighty` command. Never mutate tracked state by editing
local Markdown, invoking `gh`, calling GitHub APIs/MCP, writing claim comments, or changing Project
fields directly.

## Workflow

1. Verify `wrighty` is callable.
2. Before the first mutation, run `wrighty init --check --json`. If configuration is missing or
   invalid, explain the failure and stop; never initialize implicitly.
3. Use `--json` for decisions and error handling. Use `list --compact` only for concise display.
4. Use composite commands instead of recreating their steps:
   - next work: `wrighty pick --claimant-kind agent --json`;
   - completion: `wrighty finish <id> --claimant-id <claimantId> --claim-token <claimToken> --json`.
5. Keep the canonical item ID, claimant ID, claim token, and Creation attempt ID in context.
6. Branch on `error.code`, never error prose.
7. Do not release a claim while a Wrighty mutation has an ambiguous result.

Read [references/workflow.md](references/workflow.md) completely before mutating Wrighty state.
Read [references/errors.md](references/errors.md) when a command fails or is being retried.

## Invariants

- Claim a specified item before editing it.
- Retain the `claimantId` and `claimToken` returned by claim, pick, or an explicitly requested
  takeover. Pass both on every edit, move, finish, archive, release, or renewal.
- Treat `CLAIM_STALE` as a hard stop. Never reclaim or take over automatically.
- In worker-spawned sessions, use `WRIGHTY_CLAIMANT_ID` / `WRIGHTY_CLAIM_TOKEN`; the item is
  pre-claimed and must not be claimed again.
- In worker-spawned sessions, let Wrighty manage lease renewal. Do not infer expiry from
  `expiresAt`; only a `CLAIM_EXPIRED` or `CLAIM_STALE` mutation response is authoritative.
- When blocked or missing required clarification, do not finish or invent work. Explain the blocker
  and exit so the worker can report `needs-attention` and preserve the resumable claim temporarily.
- Invoke `takeover` only when the user explicitly asks to take over that item.
- Generate a Creation attempt ID before create and reuse it for every retry.
- Treat `AlreadyOwned`, resumed create, and already-finished results as success.
- Never bypass another worker's claim.
- Never invent replacement work after a not-found or no-item result.
- Read Local Markdown custom fields from `get --json` under `result.fields`. Write them only with
  repeatable `--field name=value`; use `--field name=` to delete. A `NOT_SUPPORTED` response means
  the configured backend does not provide custom fields.
- Do not equate commits, tests, pushes, pull requests, issue closure, or code completion with Wrighty
  completion; invoke `finish` only when the tracked work is actually complete.

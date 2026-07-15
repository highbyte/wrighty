---
name: wrighty
description: Safely operate Wrighty through the `wrighty` CLI. Use only when the user explicitly asks to use Wrighty, the Wrighty CLI, or a Wrighty work-item ID, including requests to list, inspect, create, pick, claim, edit, move, finish, archive, or release tracked work. Do not trigger for generic tasks, GitHub issues, planning, backlogs, or project management that do not explicitly identify Wrighty.
---

# Wrighty

<!-- wrighty-skill-version: 0.1.0 -->

Operate Wrighty state only through the `wrighty` command. Never mutate tracked state by editing
local Markdown, invoking `gh`, calling GitHub APIs/MCP, writing claim comments, or changing Project
fields directly.

## Workflow

1. Verify `wrighty` is callable.
2. Before the first mutation, run `wrighty init --check --json`. If configuration is missing or
   invalid, explain the failure and stop; never initialize implicitly.
3. Use `--json` for decisions and error handling. Use `list --compact` only for concise display.
4. Use composite commands instead of recreating their steps:
   - next work: `wrighty pick --json`;
   - completion: `wrighty finish <id> --json`.
5. Keep the canonical item ID and Creation attempt ID in context.
6. Branch on `error.code`, never error prose.
7. Do not release a claim while a Wrighty mutation has an ambiguous result.

Read [references/workflow.md](references/workflow.md) completely before mutating Wrighty state.
Read [references/errors.md](references/errors.md) when a command fails or is being retried.

## Invariants

- Claim a specified item before editing it.
- Generate a Creation attempt ID before create and reuse it for every retry.
- Treat `AlreadyOwned`, resumed create, and already-finished results as success.
- Never bypass another worker's claim.
- Never invent replacement work after a not-found or no-item result.
- Do not equate commits, tests, pushes, pull requests, issue closure, or code completion with Wrighty
  completion; invoke `finish` only when the tracked work is actually complete.

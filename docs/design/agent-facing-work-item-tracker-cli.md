## Scope note

**Implementation status (2026-07-14):** The first MVP described by this document has been
implemented. This document remains the architectural rationale and includes
some historical recommendations and open questions that no longer describe the current state
exactly. The remaining gaps, deferred capabilities, validation work, and decisions that should
guide future work are tracked in
[Post-MVP implementation gaps and future work](post-mvp-implementation-gaps-and-future-work.md).

This is an idea for a general-purpose developer tool for a simple tracking work items that works for both
AI agents and developers. Idea was inspired by [kanban-md](https://github.com/antopolskiy/kanban-md).

## Problem

Goal: run AI agent sessions on **different computers, concurrently**, sharing one work-item
tracker (features, bugs, tasks) for the same project.

A tracker that coordinates multiple machines needs durable IDs and a claim arbiter that does not
depend on Git synchronization, independent clones, or a shared local filesystem. Those mechanisms
must be supplied by the selected backend rather than inferred from a working tree.

## Idea

Provide a compact, agent-friendly CLI surface over a **pluggable backend**.

### What is actually invariant across backends

Storage is **backend-defined**, not a property of the tool. Under the GitHub backend there are
deliberately **no local `.md` files** (see below). So "markdown-native" cannot mean
"markdown files on disk".

What holds for every backend:

- the **CLI surface** (`list`, `create`, `pick`, `move`, `edit -a`, `--compact`)
- **item bodies are markdown** (a local `.md` body or a GitHub issue body; both are markdown)

That is the real invariant, and it is enough.

### Draw the abstraction at the atomic primitive, not at storage

The tempting port is "read/write items somewhere". That is precisely what makes the
distributed case unfixable: read-then-write races occur regardless of where the bytes live.

But **only the claim needs compare-and-set.** This is the standard lease pattern: one CAS'd
lock protects many un-synchronised writes. Once an agent holds a valid claim on an item, it is
the only writer, so `update` can be a blind last-writer-wins write.

```
list(filter)                    -> [item]
get(id)                         -> item
create(fields, body)            -> id       // backend allocates the ID
update(id, patch)               -> ok       // safe: caller holds the claim
tryClaim(id, agent)             -> ok | HeldBy(other, expiresAt)   // MUST be CAS
release(id, agent)              -> ok | NotOwner   // enforcement is backend-dependent
```

Every backend must supply:

1. **An arbiter for `tryClaim`:** something imposing a total order on competing claims.
2. **An ID allocator.**

The native local Markdown backend supplies both primitives within one filesystem; the GitHub
backend supplies durable server-allocated issue IDs and a timeline-ordered claim protocol.

Two things deliberately *not* in the port:

- **`ttl` is backend configuration, not a call argument.** A per-call TTL cannot be honoured
  consistently across backends.
- **`pick` is not a backend primitive.** It is composed in the wrapper as `list` → `tryClaim`
  in priority order until one succeeds. Delegating it to backends would let the local backend
  offer atomicity that the remote backend silently cannot, and the abstraction would leak
  exactly where it hurts.

Note this supersedes an earlier, over-specified version of this design that put an
`expectedVersion` token on `update()`. Only `tryClaim` needs CAS.

### Rule: derive the port from the hardest backend

**Derive the port from GitHub, the hardest backend.** This prevents single-filesystem
assumptions from leaking into the cross-machine coordination contract.

## Claim metadata and agent identity

The claim should record more than a name. But be clear about why.

### It does not buy correctness

Storing host / session / pid **cannot** make the lease a lock. Fencing tokens require the
*storage* to reject writes from a stale lease-holder, and GitHub has no conditional update. No
amount of identity metadata closes the expired-lease-mid-write window.

This is an **operations and recovery** feature, not a safety one. Anyone reaching for it hoping
to fix the concurrency hole should be disabused early.

### What it does buy

1. **Recovery from claim amnesia.** If the stored identity is *deterministically derivable*
   (install UUID, not a random name), a compacted agent session can **recompute** its identity
   and match it against the stored claim, rather than remembering `frost-maple`. This is what
   makes `release` and `renew` survive context loss, and it is the mechanism behind
   "fix agent identity in the tool, not the prompt" below.
2. **Confident lease-breaking.** `claimed_by: frost-maple` says nothing about liveness.
   Host + heartbeat says plenty. For the multi-computer goal specifically: seeing that an item
   is held by the work laptop while you sit at the home desktop is immediately actionable.
3. **Same-host reaping.** If `host` matches and the pid is dead, reap immediately instead of
   waiting out the TTL. Cross-host this degrades to a heartbeat timestamp
   (renew every *N*, TTL = 3*N*).
4. **Stale-write *detection*.** Store a monotonic **claim epoch**. The issue timeline is
   server-ordered, so "this is the *N*th claim on this issue" is well-defined. A writer stamps
   its epoch on each write; the reconciler flags any write bearing an older epoch. Not a
   fencing token: it cannot *prevent* it, but it converts silent clobbers into detectable ones.
5. **Emulated ownership on `release()`.** Read the claim identity, compare against the
   recomputed local identity, then release. This remains TOCTOU, but restores the intended
   ownership check where a backend cannot enforce it atomically.

### Two costs to design around

- **Privacy leakage.** Hostnames and usernames in issue comments is a real hazard when the
  backend repo may be public. Store a **short hash or a per-install UUID**, generated once and
  kept in the node-ID cache dir, already the only permitted local state and already declared
  regenerable. Stable per machine, leaks nothing. Keep a local map to friendly names if wanted.
- **Heartbeat churn.** Heartbeats are writes. One comment per heartbeat spams the timeline,
  burns rate limit, and triggers notifications. Keep the claim in **one comment edited in
  place** (edits do not notify) or in a label. Consider not heartbeating at all: a long TTL
  plus explicit `release` may suffice, accepting orphan risk.

### Shape

```yaml
claim:
  agent:        <derived: hash(install-uuid)>   # recomputable, never remembered
  epoch:        7                               # Nth claim; enables stale-write detection
  claimed_at:   2026-07-09T00:18:36Z
  expires_at:   2026-07-09T01:18:36Z
  heartbeat_at: 2026-07-09T00:44:02Z            # optional
```

The implemented protocol permits optional `sessionId` correlation metadata. It never participates
in ownership, arbitration, release authorization, or recovery. Correctness continues to rely on
the deterministically derived worker identity, so missing or unstable host session identifiers do
not weaken the claim protocol.

## Candidate backends

### 1. Local: native Markdown files with YAML frontmatter

The selected local backend is implemented directly in the .NET application. It stores one work
item per Markdown file and uses an application-owned directory structure:

```text
.wrighty/
├── items/001-develop-login-feature.md
├── archive/002-finished-research.md
└── .lock
```

The numeric filename prefix is the item identity. The title slug is a portable, human-readable
projection and changes when the title changes without changing the canonical ID (`local:1`). ID
is deliberately not duplicated in frontmatter. The body after frontmatter is ordinary Markdown.

The local backend supplies both required primitives under one store-wide operating-system lock:

- ID allocation scans numeric prefixes from active and archived files and allocates `max + 1`;
- claim comparison and publication occur in the same exclusive critical section.

Updates verify ownership and atomically publish a complete replacement document while still
holding the lock. Archive moves an item from `items/` to `archive/` and releases the claim in the
same local transaction. This is stronger local fencing than the GitHub backend can provide.

The implementation uses YamlDotNet for YAML nodes and owns its stable error/JSON contract. Local
Markdown reserves `title`, `status`, `priority`, `createdAt`, `updatedAt`, `claimEpoch`, `claim`,
`creation`, `wrighty`, and `x-wrighty-*`. All other YAML nodes are custom fields and are preserved;
comments and exact scalar style are not guaranteed by YamlDotNet. Existing keys update in place and
new managed keys use canonical placement to avoid ordering churn.

The backend-neutral detail model exposes custom fields as JSON values and optional raw frontmatter.
Local `get --json` returns them, while GitHub supplies an empty field set and no raw frontmatter.
Local `create`/`edit --field name=value` writes string fields, `edit --field name=` deletes one, and
`list --field name=value` combines repeatable exact-match filters with AND semantics. GitHub returns
`NOT_SUPPORTED` for all custom-field write/filter requests rather than ignoring them.

`wrighty import <path...>` is the only forgiving ingestion boundary. It accepts files and
directories, supports recursion, dry-run, copy-by-default or verified move, resolves titles from
frontmatter/H1/filename, maps status and priority source keys, preserves custom YAML, and stages an
entire contiguous-ID batch under the store lock before commit. The normal loader remains strict and
points unmanaged filenames to import. The web item view renders custom fields and escaped raw
frontmatter read-only, using a self-hosted YAML-only syntax highlighter; the browser never reads
tracker files directly and no CDN dependency is introduced.

This remains a **single-filesystem backend**. Git synchronization, independent clones, cloud file
sync, or unrelated network mounts do not turn its local lock into distributed arbitration. Use the
GitHub backend when agents on different computers must coordinate.

### 2. GitHub Issues + Projects v2  ← primary target

**Explicit decision: this backend has no local `.md` documents.** The CLI wrapper always reads
from and writes to GitHub. (Cloning the backend repo to read issues by hand is a separate,
independent choice; the wrapper never assumes or maintains a local copy.)

Consequences, accepted deliberately:

- No offline operation. Every read is a network call.
- Therefore `--compact` output and node-ID caching are **load-bearing, not optimisations**.
  They are the difference between a usable and unusable agent loop.
- The only permitted local state is a **cache of opaque node IDs** (project / field / option
  IDs). That is configuration, not content; it never becomes a source of truth.

#### Why Projects v2 and not just raw issues

Projects v2 supplies the human-supervision half for free: a real kanban board in the browser,
columns driven by a Status field, cross-repo, with built-in workflows (e.g. auto-move to Done
on close). Strictly better than a local TUI, and no UI code to write.

#### The invariant: the claim is never a field

Projects v2 is **GraphQL-only** and offers **no ETag, no `If-Match`, and no conditional
update**. `updateProjectV2ItemFieldValue` is a blind last-writer-wins write. The same is true of
`setIssueFieldValue`. That looks fatal but is not, given the lease pattern above:

| Concern | Lives on | Why |
|---------|----------|-----|
| **Claim / lease** | the **Issue** (a comment, or a `claim:<agent>` label) | the issue timeline is **server-ordered**, so "who claimed first" is decidable; it is a real arbiter |
| **Status, priority, estimate, due, class** | a **field store** (see below) | blind writes are safe: only the claim holder writes them |

GitHub serialises writes per issue. That ordering *is* the compare-and-set.

**The claim must never be stored in a field, in any strategy.** Fields have no arbiter; the
timeline does. This single invariant is what keeps the field-storage choice below a narrow,
swappable detail rather than a correctness concern.

#### `FieldStore`: a sub-port, not a second backend

Where *field values* live is configurable. Where the *claim* lives is not. So this is a strategy
**inside** the GitHub backend:

```
GitHubBackend
  ├── ClaimProtocol   (timeline-ordered)   <- invariant, never varies
  └── FieldStore      (strategy)           <- the only thing that swaps
```

The outer port (`list`/`get`/`create`/`update`/`tryClaim`/`release`), the CLI surface, and the
agent skills are all unaffected by the choice.

**Three strategies:**

| Strategy | Surfaces | User-owned repo? | Typed? | Server-side search? |
|----------|----------|------------------|--------|---------------------|
| **Labels** (`status:todo`) | 1 (issue) | yes | no number/date | yes |
| **Project fields** | 2 (issue + project) | yes | yes | no; must paginate project items |
| **Issue Fields** (GA 2026-07-02) | 1 (issue) | **no; org only** | yes | yes |

A useful alignment: **Project fields and Issue Fields support exactly the same four types:**
single-select, text, number, date. One `FieldValue` model covers both; the abstraction does not
have to be forced.

#### Capability detection, not a config boolean

A user-set boolean can name a store the repo cannot honour (`field_store: issue` against a
user-owned repo fails on every write). **Detect, then allow override:**

```
probe:  owner is an Organization?      -> issue-fields available
        a project is linked?           -> project-fields available
        always                         -> labels available
select: highest available, unless overridden
```

Config is `field_store: auto | issue | project | label`, defaulting to `auto`. The override
exists for testing and forced downgrade, not as the primary mechanism.

#### Keeping it clean: strategies declare capabilities

The strategies differ in **atomicity**, so the reconciler needs different invariants per
strategy. Hardcoding `if (usingIssueFields)` would rot the abstraction immediately. Instead the
strategy declares what it can do, and the reconciler is generic over that:

```
FieldStore.capabilities() -> {
  sameSurfaceAsIssue: bool,   // false for project -> reconcile "item missing from project"
  atomicCreate:       bool,   // can fields be set in the create call?
  supportsTypes:      [...],  // label store rejects number/date
  nativeSearch:       bool,   // can list() filter server-side?
}
```

The reconciler asks *"does this store need the item-on-board check?"*, never *"which store am
I?"*. Same posture as the reconciliation section below: capabilities drive behaviour.

`supportsTypes` keeps the label store honest: it declares it cannot hold `due` (date) or
`estimate` (number), so the CLI rejects those upfront instead of silently dropping them.

#### Why Issue Fields matter (when available)

1. **They collapse the two-surface split.** Status and claim then live on the *same object*.
   The "non-atomic multi-surface write" hazard (create issue → add to project → set Status)
   largely disappears, and the reconciler shrinks.
2. **`nativeSearch` makes the read path far cheaper.** Issue-field values are searchable via
   issue search, so `list` becomes one server-side query. Project fields have no issue-level
   search; you must paginate project items and filter client-side. With no local mirror and
   every read a network call, this is the *stronger* of the two benefits.

**Blocker:** Issue Fields are **organisation-scoped** ("organization-level fields",
"Organization issue fields REST API"). Issue Fields are therefore **not available** when the
configured repository is owned by an individual user rather than an organization.

**Workaround, if wanted:** the tracker backend need not be the code repo. A **free GitHub org
hosting a private tracker repository** unlocks Issue Fields (the GA post lists Free), while the
code repository stays user-owned, public, and untouched. Cheap and reversible.

#### Field mapping

| Work-item concept | GitHub |
|-----------|--------|
| `id` | canonical `github:owner/repository#number`; number is **server-allocated** |
| `status` | field store: single-select (or `status:*` label) |
| `priority` | field store: single-select |
| `estimate` | field store: number (**unavailable in label store**) |
| `due` | field store: date (**unavailable in label store**) |
| `class` (expedite/standard/…) | field store: single-select |
| `tags` | issue labels |
| `assignee` | issue assignees |
| body | issue body (markdown) |
| `parent` / `depends_on` | native sub-issues, or a relation field |
| `claimed_by` / `claimed_at` | issue comment or label; **never a field, in any strategy** |

#### Identifier contract

The backend-agnostic command API treats IDs as opaque values. GitHub formats durable references
as `github:owner/repository#42`, while `42` and `#42` remain convenient shorthand within the
configured repository. Repository-qualified forms and configured-host issue URLs resolve to the
same canonical value. The issue number is allocated by GitHub; the wrapper maintains no counter
and does not create an ID Project field. Opaque issue, Project-item, field, and option node IDs
remain internal plumbing and are not part of CLI JSON output.

#### Hard constraints

1. **Draft issues cannot be claimed.** Projects v2 permits draft items that are not real
   issues; they have no comments and no timeline, hence no arbiter. The wrapper must force
   real issues and never create drafts.
2. **A lease is not a lock.** GitHub has no fencing tokens, so it cannot reject a write from
   an agent whose lease expired mid-operation. Agent A's lease lapses, B claims, both write →
   clobber. Mitigate with TTL ≫ operation time and re-verify-claim-before-write; the window
   never fully closes. For planning data the worst case is a stomped status field, recoverable
   from the issue's own history. **Best-effort by construction, but now for a precisely known
   reason.**
3. **ID plumbing is brutal.** `gh project item-edit` requires `--project-id`, `--id` (item ID),
   `--field-id`, and for single-selects `--single-select-option-id`: four opaque node IDs per
   field write. This is *the* strongest argument for the wrapper existing at all: it caches
   those IDs so an agent can type `move 42 in-progress`.
4. **Rate limits.** GraphQL is 5000 points/hr; content-creating requests hit secondary limits.
   Each claim is ~2 calls. Fine at single-project scale; not fine for a chatty polling loop.
5. **Token scope.** `gh project` requires the `project` scope. Verified absent from a default
   `repo,workflow,gist,read:org` token; the first setup step is `gh auth refresh -s project`.

Private planning stays private if issues and the project are private.

### 3. Custom endpoint

- A **Cloudflare Durable Object** is single-threaded and serialisable by design, effectively a
  hosted mutex with storage attached. Real leases, real CAS, sub-100 ms, no polling. It is the
  only option that can offer **fencing tokens**, and therefore the only one where a lease is a
  true lock.
- Lowest-novelty option technically, and there is existing Cloudflare Worker experience in this
  project (see `documents/ideas/cloudflare-hosting-for-browser-wasm-apps.md`).
- Cost is not the code; it is owning uptime, auth, and backups for planning data.
- No free board UI. This is what you give up relative to Projects v2.

## Defensive design: reconciliation, not repair

The GitHub backend has distributed and partial failure modes rather than local file-repair
concerns. **Eliminating a failure class beats healing it.**

- **Non-atomic multi-surface writes:** the most serious, and created by our own claim/fields
  split.** Creating an item is: create issue → add to project → set Status. Three API calls,
  no transaction. A crash between them leaves an issue that is not on the board, or a board
  item with no Status.
- **Retry non-idempotency.** A network timeout *after* a successful create silently produces a
  duplicate issue on retry. GitHub has no idempotency-key header. The selected protocol uses a
  client-generated Creation attempt ID, a unique temporary repository label written atomically
  with issue creation, and a durable **Creation attempt ID** Project text field. The label bridges
  a lost create response and is deleted after verified initialization. The issue body remains
  entirely user-owned and contains no tracker marker.
- **Orphaned claims.** An agent dies; the lease expires but nothing reaps the claim
  label/comment.
- **Duplicate claim events** from retries: resolve by earliest-timeline-wins, loser cleans up
  after itself.
- **Stale node-ID cache:** the one piece of permitted local state.

### The model: reconcile, don't fsck

Desired state vs. observed state, converged on every invocation:

```
issue has a `claim:` label but no project item   -> add it to the project
project item has empty Status                    -> set the default
claim lease expired                              -> treat as unclaimed
cached node ID returns 404                       -> invalidate, refetch
two claim events on one issue                    -> earliest wins; loser removes its own
```

Creation recovery is targeted rather than an unconditional repository scan: an explicit Creation
attempt ID or known ambiguous create outcome authorizes lookup and resumption.

### Cache guarantee

The GitHub backend *must* keep hidden state (the node-ID cache). The required guarantee is that
the cache remains **regenerable and never authoritative**, enforced by the 404-invalidation rule.

## Recommended path

1. **Do not build the pluggable tool first.** Build the thinnest thing that tests the premise:
   a wrapper over `gh` providing (a) token-efficient `--compact` listing, (b) node-ID caching,
   and (c) atomic `pick`/`claim` via the issue-timeline ordering protocol.
   Zero hosting. If plain `gh` plus a claim convention proves tolerable, the project is
   unnecessary.
2. Define the port above once the wrapper's shape is known. Define the `FieldStore` seam at the
   same time, but implement **only the project-fields strategy**: it works on the current
   user-owned repos today, needs no org, and gives the board for free. Add the issue-fields
   strategy only after (a) a planning org exists and (b) the board-grouping question below is
   settled. Build the label store only if something forces it.
3. Add the Durable Object backend only if GitHub latency, rate limits, or the absence of
   fencing tokens actually bite.
4. Implement the native local Markdown backend for a test double, offline mode, and the fast loop for
   developing the wrapper's own CLI and agent skills.

## Framing

This is primarily an *agent-facing CLI adapter* over a backend-defined tracker. The GitHub backend
delegates storage, IDs, board UI, permissions, and history to GitHub. The local backend deliberately
owns a small Markdown/frontmatter store for offline and single-filesystem workflows, without trying
to reproduce a hosted tracker UI or permission model.

The tool's purpose is a **CLI-first, token-efficient, agent-shaped interface** with Markdown
bodies and backend-appropriate coordination across machines.

Under the GitHub backend the wrapper's job shrinks to three things worth doing: **ID
plumbing**, **compact output**, and **the claim protocol**. That is a small tool, and
correspondingly more likely to get built.

**GitHub's own MCP server narrows this further.** The Issue Fields GA post notes that AI tools
can already read and set field values through GitHub's MCP server. So an agent can manipulate
typed work items *without this tool at all*. What MCP does **not** give you is the claim
protocol, atomic `pick`, or token-efficient listing, which are exactly the three things above.

This is a sharpening, not a threat: it confirms the scope, and it means anything the MCP server
already does well should **not** be reimplemented here. Re-evaluate this boundary before
writing code: if GitHub's MCP server ever ships claim/lease semantics, this tool has no
reason to exist.

## Agent skills

The CLI stability and retry-safety gate has passed. The implementation ships an installable Agent Skill for
Codex, Claude Code, and GitHub Copilot so agent usage becomes deterministic rather than improvised.
The skill remains a documented CLI contract; backend arbitration and recovery stay in the tool.

### Skill design principles

1. **Separate mechanics from activation policy.** The *how to drive the CLI* body is tool-owned
   and may be overwritten; the `description:` trigger is host-owned policy and must not be
   clobbered. **The tool should own the mechanics and let the host own when to invoke.**
2. **Ship narrow triggers by default.** Generic task-management wording can hijack unrelated
   prompts in repositories that already have their own work-tracking system. Default to explicit
   opt-in; let the host broaden it.
3. **Prefer the CLI and enforce separately when required.** Current Claude documentation says
   `allowed-tools` pre-approves listed tools but does not remove other tools. The portable skill
   cannot use it as a restrictive whitelist. It forbids direct storage mutation; host permission
   rules or hooks may enforce that policy separately.
4. **Version-stamp the skill and provide a `check`.** A skill is a versioned contract and needs
   supported `skill check` and `skill update` operations.
5. **A decision-tree table plus DO/DO NOT pitfalls** is a good skill template. Steal the shape.

### Determinism comes from the CLI, not the skill

A skill cannot rescue an ambiguous CLI. The real levers are affordances:

- **Structured errors with stable `code` values** (`TASK_CLAIMED`) so an agent branches on a
  code, never on prose.
- **Meaningful exit codes.**
- **Composite atomic commands** (`pick` = find + claim + move) so agents execute one call
  rather than authoring a multi-step sequence they can deviate from.
- **Token-efficient output** (`--compact`): less context pressure means fewer mistakes.

### Fix agent identity in the tool, not in the prompt

**The CLI should derive a stable agent identity itself** (e.g. from host + user + session),
and/or make `release`/`renew` able to recover identity by reading the current claim from the
backend. This removes an entire failure class rather than delegating it to prompt discipline,
the same principle as the reconciliation section above.

### Multiple agent targets

All three targets support the open Agent Skills `SKILL.md` shape. Project installation uses
`.agents/skills/wrighty` for Codex and Copilot and
`.claude/skills/wrighty` for Claude Code. The installer detects one current runtime or
requires `--agent`; `--agent all` writes the two distinct host locations.


## Open questions

- Is `gh` sufficient, or is raw GraphQL needed? `gh project item-edit` covers field writes, but
  the claim protocol needs the issue **timeline** API, which may require `gh api graphql`.
- How expensive is a `list` in tokens once a board has ~100 items and no local mirror to grep?
  This determines whether `--compact` is enough or whether server-side filtering is mandatory.
- Does the timeline-ordering claim protocol behave when an agent dies mid-claim? Lease expiry
  is convention-only; nothing reaps abandoned claims.
- Can labels carry `tags` without the label namespace becoming unmanageable across projects?
- **BLOCKING: can a Projects v2 board group its kanban columns by an *issue* field?** Docs
  confirm issue fields can appear as *columns in project views*, but board **grouping** is the
  load-bearing question. If a board can only group by *project* fields, then issue-fields mode
  would need Status in **both** places to render a board: dual-write, two surfaces, and the
  partial-write hazard returns *worse* than project-only. This single unknown decides whether
  the Issue Fields strategy is a genuine improvement or a trap. **Test before building it.**
- Are issue-field changes emitted as first-class **server-ordered timeline events** that can be
  read back? Docs say changes are "tracked in the timeline", but whether they are ordered,
  readable events is what the claim-epoch / stale-write-detection scheme depends on.
- **"Heal on every invocation" conflicts with "every read is a network call".** Reconciliation
  is free on the local backend but costs API calls against GitHub, where rate limits bite and
  there is no local mirror to check against. Does reconciliation run on every command, only on
  writes, only on `pick`, or on a sampled/backoff schedule? This tension is unresolved and is
  the first thing to settle before implementing the GitHub backend.
- Heartbeat, or long TTL plus explicit `release`? Heartbeats give prompt orphan reaping but
  cost writes, rate limit, and timeline noise on every beat. A long TTL costs nothing until an
  agent dies, then blocks the item for the full TTL. Which failure is cheaper in practice?
- Does a long agent session reliably remember its own claim identity across context compaction?
  Any claim scheme keyed on agent-supplied identity inherits that risk.

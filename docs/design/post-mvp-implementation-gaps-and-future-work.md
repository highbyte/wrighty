# Post-MVP implementation gaps and future work

Status: Design basis for future work

Snapshot date: 2026-07-14

## Purpose

The first Wrighty MVP has been implemented. This document records the remaining gap between the ideas in
[Agent-facing work-item tracker CLI](agent-facing-work-item-tracker-cli.md) and the current
implementation. It is the starting point for future design and work tracking; it is not a commitment to
implement every idea from the original design.

The original design remains useful as architectural rationale. When its historical recommendations
or open questions differ from the implemented behavior, this document describes the current status.
The concrete CLI behavior remains defined by the README, command help, configuration examples, and
tests.

## Implemented baseline

The current implementation establishes the intended MVP foundation:

- a .NET 10 console application with a backend-neutral command surface;
- GitHub Issues plus Projects v2 and local Markdown files as pluggable backends;
- backend-assigned durable item identifiers and user-facing item numbers;
- create, list, show, pick, claim, release, move, edit, finish, and archive workflows;
- compare-and-set claim behavior, stable worker identity, session metadata, claim attempt identity,
  and configurable claim-comment presentation;
- idempotent initialization and validation, including GitHub Project and custom-field setup;
- current agent type and session ID projection into GitHub Project fields;
- local Markdown filenames containing both item number and a portable title slug;
- optional automatic archiving and an archive lifecycle shared by both backends;
- targeted recovery for interrupted creation and cache invalidation without a local GitHub mirror;
- installable Codex, Claude, and Copilot skills that direct agents through the Wrighty CLI;
- synthetic pagination coverage through 500 items and an opt-in persistent GitHub fixture validated
  with 101 items.

This baseline is sufficient for the first MVP. The following sections describe potential extensions,
not defects that automatically block its use.

## Capability gaps

### Claim renewal, recovery, and abandoned leases

Claims currently use a long lease plus explicit release. There is no heartbeat or renewal command,
proactive abandoned-claim reaper, or authoritative `wrighty current` command for recovering the
claims associated with the active worker or session.

Before adding heartbeats, measure whether expired claims cause more practical disruption than the
extra writes, API usage, and GitHub timeline noise would. A useful first increment may be a read-only
`current` command and explicit renewal rather than an automatic heartbeat.

### Stale-write detection and fencing

Local records contain a claim epoch, but GitHub field updates are not stamped with an epoch and no
reconciler detects an update made by an older claim after a lease has changed owners. The present
protocol prevents competing claim acquisition but does not provide full fencing against a delayed
writer.

If real concurrent use exposes stale writes, design an epoch-bearing update contract that works for
each backend. Do not assume that GitHub Project field history has the ordering or visibility needed
until that behavior has been verified against GitHub.

### Additional GitHub field stores

The GitHub backend currently uses Projects v2 fields. It does not support GitHub Issue Fields, a
labels-based strategy, a `field_store` configuration choice, or runtime capability selection between
strategies. The original design's generalized `FieldStore` abstraction was therefore not needed for
the MVP; the implementation uses a Project-specific client.

Extract a field-store port only when introducing a second real strategy. Before implementing Issue
Fields, verify that they can support board grouping and that their changes expose sufficiently
ordered history for any stale-write scheme. Avoid dual-writing Status into two stores unless there
is an explicit consistency design.

### Richer work-item metadata

The portable model currently focuses on title, Markdown body, status, priority, claim metadata, and
archive state. It does not yet formalize estimates, due dates, work-item class, tags, assignees,
parent/child relationships, or dependencies.

New fields should be added first to the backend-neutral model and command contract, with an explicit
capability and degradation policy for every backend. A backend-specific field should not silently
become part of the portable CLI contract.

### Per-status claim policy

Mutating an item currently follows a uniform claim policy. The original design considered allowing
configuration such as `require_claim` per status, but this has not been implemented.

Only add this if actual workflows need unclaimed edits or stricter status-specific rules. The design
must keep skill behavior deterministic and make failures consistent across backends.

### Broader reconciliation

The implementation performs targeted recovery and validation: interrupted creation can be resumed,
caches can be invalidated, and schemas or projections are checked where needed. It does not scan and
heal the entire backend on every invocation, and it has no durable repository-wide index of all
creation attempts.

This is intentional for the network-backed case. Any broader reconciler needs a bounded API budget,
clear triggers, and an idempotent repair contract. Prefer explicit validation, write-time repair, or
sampled/backoff reconciliation over an unconditional full scan.

### Custom hosted coordination backend

The original design describes a possible custom endpoint, such as a Durable Object, for stronger
coordination and fencing. No such backend exists today.

This would add deployment, authentication, availability, and operational responsibilities. Build it
only if GitHub's claim protocol proves insufficient and the required guarantees cannot be achieved
within the existing backends.

### Native agent-session integration

The tracker records portable agent type, stable worker identity, session ID, and claim attempt ID.
For GitHub it can project current agent type and session ID into custom Project fields. It does not
register a native GitHub agent session, attach a native session URL or transcript, or cause a local
Codex, Claude, or Copilot process to appear as one of GitHub's hosted agents.

Portable tracker identity and a GitHub-native agent session are separate concepts. Future native
integration depends on supported provider APIs and should not overload worker identity or session ID
with a URL.

### Mechanical agent enforcement

The installed skills instruct agents to use the CLI, but guidance is not a security boundary. The
repository does not install host permission rules or hooks that mechanically prevent an agent from
editing local tracker files or calling GitHub directly.

Permission templates or hooks could be added per host if evidence shows instruction-only compliance
is unreliable. Such controls must remain opt-in because agent hosts expose different permission
models and users may need direct administrative access.

### Skill distribution

Skills can be installed from this repository into Codex, Claude, and Copilot locations. They are not
published as a Codex plugin, Claude marketplace plugin, or GitHub-distributed skill package.

Publication should follow contract stability and real cross-agent use so that update and
compatibility expectations are understood first.

## Validation gaps

The implementation has unit, integration, synthetic scale, and opt-in live GitHub coverage. The
remaining validation work is primarily about supported environments and agent behavior:

- record fresh end-to-end behavioral runs for Codex, Claude, and Copilot using the installed skill;
- verify that each agent consistently uses `wrighty` instead of backend-specific shortcuts during
  create, pick, edit, finish, release, and failure recovery;
- add continuous integration across macOS, Linux, and Windows for installation, path handling,
  portable filenames, local locking, and generated `.gitignore` behavior;
- retain the persistent GitHub scale fixture for opt-in live pagination checks instead of recreating
  it during ordinary test runs;
- add focused live tests only where GitHub API semantics cannot be represented faithfully by the
  synthetic client.

The absence of a live test for every command is not itself a reason to expand the default test suite.
Live tests consume API quota, depend on external state, and should remain explicit.

## Status of the original open questions

Several questions at the end of the original design have now been answered by implementation:

- `gh` is sufficient as the authenticated transport; the implementation uses `gh api` with REST and
  GraphQL where appropriate.
- Project listing beyond 100 items is covered by pagination tests, including the persistent 101-item
  GitHub fixture and 500-item synthetic coverage.
- Agent identity survives context compaction through tracker-managed stable worker identity rather
  than an agent-supplied name held only in conversation context.
- The MVP chose a long claim lease plus explicit release; automatic heartbeat and renewal remain
  deferred.
- Reconciliation is targeted rather than a full heal-on-every-command scan.

Questions that remain genuinely open are conditional on future capabilities:

- whether Issue Fields can drive the required Project board grouping semantics;
- whether Issue Field history exposes readable, server-ordered events suitable for stale-write
  detection;
- whether labels can represent tags without an unmanageable namespace;
- whether observed claim failures justify heartbeat, renewal, recovery, or a stronger coordinator.

## Recommended sequence for future work

1. Complete and record the Codex, Claude, and Copilot behavioral validation matrix.
2. Gather evidence from real multi-machine and multi-agent use, especially claim expiry, stale
   writes, direct-backend bypasses, and missing metadata.
3. Choose the next capability from observed need. Claim recovery/renewal and richer portable metadata
   are the most likely candidates, but neither should be assumed in advance.
4. Resolve the relevant GitHub API questions with a focused prototype before designing additional
   field-store strategies or fencing around their history.
5. Create a Wrighty item for the selected capability and put its implementation plan there,
   including backend-neutral contracts, degradation behavior, migration expectations, and validation.
6. Consider broader skill publication only after the command and skill contracts have demonstrated
   stability.

Do not combine the custom coordinator, Issue Fields, rich metadata, heartbeat, enforcement hooks,
and skill publication into one milestone. They solve different observed risks and should remain
independently justifiable and reversible.

## Tracker-item planning rule

A future item from this document should become a planned Wrighty item only when its problem statement,
required guarantees, backend behavior, and acceptance evidence are clear. When that item is implemented,
update this document to mark the gap closed or replace it with the newly discovered limitation.

# Operator actions by surface

Use this guide to answer two questions:

1. Which surface can perform the action I need?
2. Where is the authoritative procedure documented?

This is a comparison and routing guide, not a second command reference. **Direct** means the
surface can complete the action itself; **policy** means it changes a durable policy but does not
start an operation; **guidance** means it presents state or instructions whose action runs
elsewhere; and **view** means read-only visibility. Follow the linked reference for prerequisites,
commands, claim behavior, and edge cases.

The three surfaces are:

- **Local web** — `wrighty web`, a shared local operations console for both backends; Local
  Markdown additionally supplies its board/editor.
- **GitHub** — the issue, Project fields, labels, and Wrighty's single status comment.
- **CLI** — works with both Local Markdown and GitHub.

## State and authority

Before acting, distinguish policy, portable lifecycle state, local operational state, and
presentation:

| Kind | Authority | Where to use it |
| --- | --- | --- |
| Execution and agent policy | GitHub Project policy fields, or the corresponding Local Markdown item values | Change through supported Project, web, or CLI controls; see [GitHub initialization and worker policy](configuration.md#initialize-the-github-backend). |
| Portable worker lifecycle category | Managed Local Markdown frontmatter or the GitHub `wrighty:dispatch-state=...` issue label | Use to find and classify queued, blocked, and deferred work; see [worker dispatch state](worker.md#usage-exhaustion-and-deferred-retry). |
| Exact session, workspace, retry, and provider state | The installation-local work-item runtime and provider-capacity stores | Inspect and operate through Wrighty on the recording installation; see [storage and version control](storage.md). |
| GitHub recovery presentation | Display-only Project fields and the single status comment | Treat as best-effort guidance, not as scheduling controls; see [GitHub status comment](worker.md#github-status-comment). |

Native edits to a display-only recovery field or dispatch-state label do not perform a fenced
Wrighty transition. Use the supported action linked below.

## Inspect and understand work

| Action | Local web | GitHub | CLI | Authoritative procedure |
| --- | --- | --- | --- | --- |
| Browse and filter active work | **Direct:** visual status board | **Direct:** Project views and issue search | **Direct:** human or JSON listing | [Inspect and organize work](../workflows.md#inspect-and-organize-work) |
| Inspect one item's content and metadata | **View:** rendered and raw Markdown, policy, operational status, and claim state | **View:** issue plus Project fields and labels | **Direct:** full operational detail | [Work items](work-items.md) |
| Determine claim ownership or takeover eligibility | **View:** current ownership and available controls | **View:** claim projection/comment; exact recovery remains installation-aware | **Direct:** complete ownership inspection | [Claims and ownership](claims.md#claim-ownership-fencing-and-takeover) |
| Inspect a retained session or workspace | **View:** local session address and bounded workspace state | **Guidance:** status comment identifies the recording host/branch according to privacy policy | **Direct:** session, resume, and workspace inventory | [Retained workspaces](worker.md#retained-workspaces) |
| Find blocked, queued, retrying, or completed work | **View:** operational-status badges and callouts | **View:** authoritative lifecycle label plus display fields | **Direct:** grouped operational status | [Discovering what needs attention](worker.md#discovering-what-needs-attention-wrighty-status) |

## Create, organize, and authorize work

| Action | Local web | GitHub | CLI | Authoritative procedure |
| --- | --- | --- | --- | --- |
| Create a work item | **Direct:** structured Local Markdown form | **Direct:** configured Project/issue form paths | **Direct:** retry-safe creation on either backend | [Collaboratively author a substantial work item](../workflows.md#collaboratively-author-a-substantial-work-item) |
| Change title, instructions, status, or priority | **Direct:** requires a suitable editing claim | **Direct:** native issue/Project editing exists; Wrighty claim coordination still applies | **Direct:** claim-aware editing and moving | [Moving and editing](work-items.md#moving-and-editing) |
| Allow or prevent automatic execution | **Direct:** execution-policy editor control | **Policy:** edit the authoritative Wrighty policy - execution field | **Direct:** create/edit policy options | [Create and dispatch one unattended item](../workflows.md#create-and-dispatch-one-unattended-item) |
| Choose the agent policy | **Direct:** agent-policy editor control when the item is not locked to a retained retry | **Policy:** edit the authoritative Wrighty policy - agent field | **Direct:** create/edit policy options or worker-level override | [GitHub worker policy](configuration.md#initialize-the-github-backend) |
| Inspect or approve execution context | Not applicable: Local content is approved by definition | **Direct:** native Project field or web repository control plane | **Direct:** `context` and `approve` | [Approve GitHub context and invalidate edits](../workflows.md#approve-github-context-and-invalidate-edits) |
| Initialize or validate backend resources | **View:** explicit read-only GitHub target validation; no migration | **View:** resources created by initialization | **Direct:** discovery, initialization, and validation | [Configuration](configuration.md) |

Changing policy does not itself launch a worker. A retained vendor-native retry also remains bound
to its recorded agent; see [usage exhaustion and deferred retry](worker.md#usage-exhaustion-and-deferred-retry).

## Claim, edit, and hand work back

| Action | Local web | GitHub | CLI | Authoritative procedure |
| --- | --- | --- | --- | --- |
| Claim for human editing | **Direct** | No native fenced Wrighty action | **Direct** | [Claims](claims.md#claims) |
| Take over abandoned or conflicting work | **Direct:** same-installation controls when eligible | **Guidance:** status comment points to the installation-aware path | **Direct:** guarded takeover and recovery | [Take over abandoned or conflicting work](../workflows.md#take-over-abandoned-or-conflicting-work) |
| Release a claim | **Direct:** own-claim and guarded override controls | No native fenced Wrighty action | **Direct** | [Recovery paths](claims.md#recovery-paths) |
| Clarify a paused item and preserve its recorded session | **Direct:** edit with explicit queue, hand-back, release, or retain choices | **Direct:** issue content can be clarified; use Wrighty for the claim/session transition | **Direct:** atomic edit/takeover and continuation paths | [Clarify and resume the same session](../workflows.md#clarify-an-item-and-resume-the-same-agent-session) |
| Queue a paused recorded session for a continuous worker | **Direct** | **Guidance:** status comment supplies the Wrighty path | **Direct** | [Clarify and resume the same session](../workflows.md#clarify-an-item-and-resume-the-same-agent-session) |
| Hand a claim back for interactive continuation | **Direct:** produces the fenced resume command and can open its CLI on macOS | **Guidance:** status comment supplies the recording-installation path | **Direct:** produces or executes the resume command | [The two-path resume model](worker.md#the-two-path-resume-model) |

## Run and resume agents

| Action | Local web | GitHub | CLI | Authoritative procedure |
| --- | --- | --- | --- | --- |
| Start or stop a continuous worker | Not available | Not available | **Direct** | [Run a continuous unattended worker](../workflows.md#run-a-continuous-unattended-worker) |
| Process one exact item | **Guidance:** shows a copyable command where relevant | **Guidance:** status comment supplies the command | **Direct** | [Create and dispatch one unattended item](../workflows.md#create-and-dispatch-one-unattended-item) |
| Resume a retained session headlessly | **Guidance** or queue for a continuous worker | **Guidance:** recording-installation command | **Direct** | [Clarify and resume the same session](../workflows.md#clarify-an-item-and-resume-the-same-agent-session) |
| Resume a retained session interactively | **Direct on macOS:** confirmed CLI launch under the exact agent claim, or human-supervised Desktop launch when supported; copy fallback remains | **Guidance:** status comment exposes the safe path | **Direct:** generate or execute the vendor resume command | [Reviewing the session](worker.md#reviewing-the-session) |
| Recover from another installation | **View:** reports when exact local details are unavailable | **Guidance:** distinguishes recording host from cross-machine takeover | **Direct:** guarded fresh/takeover path; the remote native session cannot move | [The two-path resume model](worker.md#the-two-path-resume-model) |

## Recover from provider capacity limits

| Action | Local web | GitHub | CLI | Authoritative procedure |
| --- | --- | --- | --- | --- |
| Understand a scheduled retry | **View:** retry time, attempt, reason, agent, and local ownership when available | **View:** label, optional Project projections, and status-comment explanation | **Direct:** compact, detailed, or grouped inspection | [Usage exhaustion and deferred retry](worker.md#usage-exhaustion-and-deferred-retry) |
| Wait for bounded automatic retry | **View:** monitor state; the worker runs separately | **View:** monitor portable state and presentation | **Direct:** run a continuous worker | [Usage exhaustion and deferred retry](worker.md#usage-exhaustion-and-deferred-retry) |
| Probe one provider without claiming an item | **Direct:** confirmed contextual or header action | **Guidance:** status comment supplies the recording-installation command | **Direct:** confirmed bounded probe | [Usage exhaustion and deferred retry](worker.md#usage-exhaustion-and-deferred-retry) |
| Probe all configured providers | **Direct:** one independently leased probe per provider | Not available | No combined command; probe providers individually | [Local operations console](web-dashboard.md) |
| Override the retry timer/provider circuit for one item | **Guidance:** displays the explicit-item command; it does not launch the worker | **Guidance:** status comment supplies the command | **Direct** | [Usage exhaustion and deferred retry](worker.md#usage-exhaustion-and-deferred-retry) |
| Clarify while preserving the scheduled timer | **Direct:** ordinary save/release paths preserve it | **Direct:** issue clarification does not itself perform a recovery transition | **Direct:** claim-aware editing; inspect afterward | [Usage exhaustion and deferred retry](worker.md#usage-exhaustion-and-deferred-retry) |
| Queue the retained session before its timer | **Direct:** explicit save-and-queue transition | **Guidance:** no Project-field or label edit performs this | **Direct:** use the ordinary requeue meaning, “resume now” | [Usage exhaustion and deferred retry](worker.md#usage-exhaustion-and-deferred-retry) |
| Prevent unattended continuation | **Direct:** disabling eligibility or leaving the active worker status cancels the local schedule | **Policy:** Manual prevents automatic selection but cannot erase another installation's local record | No dedicated retry-cancellation command | [Usage exhaustion and deferred retry](worker.md#usage-exhaustion-and-deferred-retry) |
| Reschedule to an operator-selected time | Not available | Not available | Not available | [Current recovery limitations](worker.md#usage-exhaustion-and-deferred-retry) |
| Switch recovery to another agent | Not available; agent policy is locked during a retained retry | Not available; changing agent policy does not convert the retained session | Not available | [Current recovery limitations](worker.md#usage-exhaustion-and-deferred-retry) |

Provider state and exact retry ownership remain installation-local. GitHub receives sanitized,
best-effort presentation only; see [GitHub status comment](worker.md#github-status-comment).

## Complete, integrate, archive, and clean up

| Action | Local web | GitHub | CLI | Authoritative procedure |
| --- | --- | --- | --- | --- |
| Finish claimed work | **Direct:** when the web editing session owns it | No native fenced Wrighty action | **Direct** | [Complete, review, and archive](../workflows.md#complete-review-and-archive) |
| Review retained changes and session | **View:** bounded worktree state; use the supplied terminal path for vendor review | **Guidance:** retained-work status comment | **Direct:** workspace/session inspection and review commands | [Completing a finished item](worker.md#completing-a-finished-item) |
| Integrate a retained worker branch | Not available | **Guidance:** status comment presents the configured completion route | **Direct:** guided local merge or push/PR preparation | [Completing a finished item](worker.md#completing-a-finished-item) |
| Clean up a retained worktree | Not available | **Guidance:** status comment supplies the recording-installation path | **Direct** | [Retained workspaces](worker.md#retained-workspaces) |
| Archive or restore an item | **Direct** | **View:** GitHub Project archive state; use Wrighty for claim-aware transitions | **Direct** | [Archiving](work-items.md#archiving) |

## Intentionally unavailable or asymmetric actions

Not every surface is meant to reach parity:

- The web application is an operations console for both backends, but its board, general item
  mutation, and validated macOS launch of recorded local CLI/Desktop sessions are Local
  Markdown-only. Its narrow GitHub context approve/reapprove action lives in the repository control
  plane; it never duplicates general GitHub issue/Project editing or starts headless workers. See
  [Web operations console](web-dashboard.md).
- GitHub provides policy, portable state, and human guidance. Exact session, retry, provider, and
  workspace operations execute through Wrighty on the recording installation. See
  [GitHub status comment](worker.md#github-status-comment).
- Cross-agent handoff, operator-selected retry rescheduling, and first-class retry cancellation are
  not implemented. The current `handoff` recovery policy stops at needs-attention; see
  [`worker.usageFailure`](configuration.md#workerusagefailure).
- A provider-native session and local workspace cannot be moved to another installation. See
  [the two-path resume model](worker.md#the-two-path-resume-model).

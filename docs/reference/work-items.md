# Work items

## Work-item IDs and creation

Wrighty has three creation choices for each backend:

| Backend | CLI | Agent skill | Interactive UI |
| --- | --- | --- | --- |
| Local Markdown | `wrighty create` | Wrighty skill through the CLI | **New item** in `wrighty web` |
| GitHub | `wrighty create` | Wrighty skill through the CLI | GitHub's native Issue/Project creation UI |

For GitHub, configured Project membership—not a title prefix, body marker, label, or Creation
attempt value—determines whether an issue is a Wrighty item. An issue created directly in the
configured Project is already tracked. An issue created elsewhere becomes tracked when it is added
to that Project. A GitHub-native item may legitimately have a blank `Creation attempt ID`; that
field records recovery identity for Wrighty-controlled creation and is not a membership marker.

The common CLI treats IDs as opaque backend references. The GitHub backend emits durable IDs in
the form `github:owner/repository#42` and accepts all of these equivalent inputs:

```text
42
#42
owner/repository#42
github:owner/repository#42
https://github.com/owner/repository/issues/42
```

Shorthand resolves within the configured repository. Explicit references to another repository
are rejected. Human and compact output use `#42`; JSON uses both canonical `id` and `displayId`.
GitHub node IDs and Project item IDs are internal and never appear in the public JSON contract.

The local backend emits `local:42`, accepts `42`, `#42`, and its generated filename/path, and uses
`#42` for human output. Canonical IDs never contain an absolute path or title slug.

For GitHub, `create` creates a real issue, adds it to the configured Project, and assigns the
requested status and priority. Locally it atomically allocates the next numeric prefix and writes
one Markdown document. An omitted status uses `defaultPickFrom`. Use `--body-file -` to read
multiline markdown from stdin. `--body` and `--body-file` are mutually exclusive.

Every create has a Creation attempt ID. Supply one explicitly when an agent must recover after a
hard interruption:

```shell
wrighty create --creation-attempt-id 019f5c485c2b7862aeac80eb638a7b5c \
  --title "Example" --body-file description.md --priority P1
```

UUID `N` and `D` forms are accepted and normalized to 32 lowercase hexadecimal characters. When
the option is omitted, the CLI generates an ID and reports it in human and JSON output. Repeating
the same command with the same ID returns the original item with disposition `resumed`. Local
Markdown stores the ID and request hash in YAML frontmatter.

Agents should generate the ID before create so it survives an ambiguous creation result:

```shell
wrighty creation-attempt new --json
wrighty create --creation-attempt-id ID --title "Example" --json
```

GitHub cannot make issue creation and Project updates transactional. Wrighty bridges an ambiguous
issue-creation response with temporary repository metadata, then records the durable attempt ID in
the Project. The issue body remains exactly user-authored. See
[GitHub creation recovery metadata](../item-metadata/github-backend.md#creation-recovery-metadata)
for the physical fields and cleanup sequence.

Retry-safe GitHub creation requires repository permission to apply labels during issue creation.
If that cannot be established, `create` returns `GITHUB_PERMISSION_REQUIRED` before allocating an
issue. A later-stage failure returns `PARTIAL_CREATE` with the canonical ID, Creation attempt ID,
and failed stage. Retry the same request with the same Creation attempt ID; do not generate a new
one. Duplicate evidence is reported without closing, deleting, or otherwise modifying either issue.

The GitHub Project is authoritative tracked-item state. If someone removes a completed item from
the Project after creation cleanup, its Creation attempt ID is no longer discoverable from the
repository issue alone.

## Importing and adopting

The identity rule is: **import creates an identity; adopt preserves an identity**.

- `wrighty import feature.md` creates a new backend-native item from a Markdown document.
- `wrighty import --in-place .wrighty/items/feature.md` allocates a new Local Markdown identity
  while safely normalizing an unmanaged file already dropped into the store.
- `wrighty adopt 123 --status Todo` enrolls existing GitHub issue `#123` without changing its
  issue number, title, body, or Creation attempt metadata.
- `wrighty import --from-store local-markdown` copies a configured Local Markdown corpus to the
  selected GitHub backend through a durable manifest.

GitHub standalone import is deliberately one-file and copy-only. Unsupported YAML fields fail
before any issue write unless `--preserve-custom-fields` is explicit; that option appends the
shared `wrighty:frontmatter` fenced YAML block. Keep or explicitly reuse
`--creation-attempt-id` after an ambiguous result.

Adoption defaults newly added or Status-less Project items to `defaultPickFrom`, preserves
existing Status and Priority otherwise, and changes worker labels only when requested. `--agent`
does not imply `--auto`. Adoption never claims the issue and never unarchives an archived Project
item.

Whole-store import is explicit, copy-only, preflighted, resumable, and non-transactional across
GitHub items:

```shell
wrighty import --from-store local-markdown --dry-run
wrighty import --from-store local-markdown \
  --include-archived \
  --map-status "In Review=In progress" \
  --map-priority "P0=Urgent"
```

The manifest records stable per-source destination attempts and the `local:N` to GitHub mapping
before and after each mutation. Active source claims and ambiguous local `#N` references block
writes by default. `--copy-as-released` deliberately omits all claim/session/workspace state;
`--allow-unmapped-references` preserves and records reference warnings. The source store is never
changed, and Wrighty never changes the selected backend automatically.

## Moving and editing

`move` and `edit` require an active claim owned by the current tracker installation. They never
claim, renew, release, or transfer ownership automatically:

```shell
wrighty claim 42
wrighty move 42 "In Progress"
wrighty edit 42 --title "Revised title" --priority P0
wrighty edit 42 --body-file work-item.md --status Done
wrighty edit 42 --clear-priority
wrighty release 42
```

`edit` supports title, markdown body, status, and priority. Use `--body-file -` to read the new
body from standard input. An empty `--body` clears the body, while `--clear-priority` clears the
configured Project priority. `move ID STATUS` uses the same mutation pipeline as
`edit ID --status STATUS`.

To acquire or take over a human editing claim without copying its fencing handle into the shell:

```shell
wrighty edit 42 --takeover
wrighty edit 42 --takeover --yes --title "Revised title" --body-file work-item.md
```

An `edit` with no patch options opens a temporary document containing the current title and Markdown
body in `VISUAL`, or `EDITOR` when `VISUAL` is unset. `--takeover` means “ensure a human editing
claim”: it acquires an unclaimed or expired item without prompting, preserves a recoverable local
agent session, or confirms before displacing an active same-installation claimant. It never seizes
another installation's active claim. Wrighty applies the edit with the resulting handle in the same
process, retains the human claim, and prints the exact `wrighty worker --item <id> --yes`
continuation. A missing or malformed editor setting is rejected before any claim change. If the
configured editor later fails to start, exits unsuccessfully, or returns an invalid document, the
editing claim remains active and the command can be retried.

With the Local Markdown backend, `create` and `edit` also accept repeatable custom fields:

```shell
wrighty create --title "Investigate cache" --field epic=PLAT-3 --field owner=ana
wrighty edit 42 --field estimate=5 --field owner=
wrighty list --field epic=PLAT-3 --field owner=ana
```

`--field name=` deletes that field on edit. Repeated list filters use AND semantics and exact
string comparison. Custom-field create, edit, and filtering return `NOT_SUPPORTED` on GitHub;
they are never silently ignored.

Normal `wrighty list` and `wrighty get` output includes the worker-facing operational state as well
as workflow status. The default list adds automation eligibility and an activity such as `Ready`,
`Needs attention`, `Claude processing`, `Queued to resume`, or `Retry 16:05`; active claims also show their
remaining lease. `wrighty get` includes claim attribution, the complete recorded session address,
identifies claims created by a Wrighty worker, and shows the exact local deferred-dispatch decision
when this installation owns it. A worker-originated claim and a recently renewed
lease are operational coordination signals, not proof that the vendor process is making progress.
`--compact` keeps the same signals on one line.
Structured output groups the same information under `automation`, `worker`, `claim`, and `session`
objects without exposing a claim token. The `worker.dispatch` and `session.lastRun.failure`
projections contain bounded recovery details when available. The claim object includes the session/workspace address,
whether its claimant ID identifies a Wrighty worker run, and the remaining lease in seconds.

All requested values are validated before the first write. GitHub cannot atomically update an
issue and several Project fields, so the tool applies issue title/body first, priority second,
and workflow status last. Claim ownership is checked again immediately before every physical
write. A failure after any successful field returns `PARTIAL_UPDATE` with applied and pending
fields; successful writes are not rolled back and the claim is retained. Retrying the same patch
while still owning the claim is convergent because already matching fields are skipped.

An edit whose values already match succeeds without mutating GitHub, but ownership is still
required. GitHub leases are best-effort coordination rather than fenced locks: ownership checks
narrow, but cannot eliminate, the expired-lease-mid-write race.

The local backend performs ownership verification and document replacement under the same store
lock, so cooperating local processes cannot write through an expired claim after another process
has acquired it.

Complete claimed work with `wrighty finish ID --json`. It moves the item to `defaultFinishTo`
(or `--status`), honors archive-on-status, and releases the claim. A retry after success returns
`already-finished`; `PARTIAL_FINISH` instructs the caller to retry the same command.

## Archiving

Archived is a lifecycle state separate from workflow Status. Moving an item to `Done` leaves it
active by default so humans can review completed work. Archive explicitly when it should disappear
from normal lists and `pick`:

```shell
wrighty claim 42
wrighty archive 42
wrighty list --archived
wrighty unarchive 42
```

Archiving an active item requires the current claim and releases that claim. The claim is released
**before** the item is archived, so a failure leaves the item unarchived and still claimed (a clean,
retryable state) rather than archived with a stranded claim. Retrying archive is an idempotent
no-op. Unarchive restores the previous Status and Priority and requires no active claim. `get` finds
both states; normal `list` and `pick` use active items only. Should a claim ever remain on an
already-archived GitHub item (for example, from an interrupted older run), `wrighty release <id>`
with the recorded claim handle clears it — the release posts the authoritative claim event on the
issue and skips the project-field projection, which GitHub does not permit on an archived item.

Locally, archive moves the Markdown document between `items/` and `archive/` atomically. On GitHub,
it uses the native Projects v2 archived-item state; it neither closes the issue nor removes it from
the Project. See the [metadata comparison](../item-metadata/README.md) for both physical
representations.

Automatic archiving is opt-in and applies to statuses written through this CLI:

```json
{
  "archive": {
    "onStatuses": ["Done"]
  }
}
```

The default is an empty list. GitHub status changes made externally require a GitHub built-in
workflow if they should also archive automatically.

The project can also be packed and installed as a .NET tool whose command is `wrighty`:

```shell
dotnet pack src/Highbyte.Wrighty.Cli
dotnet tool install --global --add-source src/Highbyte.Wrighty.Cli/bin/Release Highbyte.Wrighty.Tool
```

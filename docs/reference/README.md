# Wrighty reference

The complete behavior contract, split by topic. The [repository README](../../README.md) is the
quickstart; these pages are the authority for options, guarantees, and edge cases.

- [Configuration](configuration.md) — backend selection, `wrighty init`, `.wrighty.json`,
  validation.
- [Work items](work-items.md) — IDs, creation and retry safety, editing, moving, archiving,
  custom fields, and import.
- [Claims and ownership](claims.md) — claimant attribution, the ownership rules, fencing
  guarantees per backend, recovery paths, and escape hatches.
- [Autonomous worker mode](worker.md) — eligibility, workspace modes, needs-attention and
  requeue, session resume, and the verified vendor capability matrix.
- [Local web dashboard](web-dashboard.md) — the embedded Local Markdown dashboard.
- [Agent skills](agent-skills.md) — installing and updating the bundled skill per agent surface.
- [Storage and version control](storage.md) — what each backend stores where, and what to
  commit.

Related: [workflow guide](../workflows.md), [item metadata](../item-metadata/README.md), and
[design documents](../design/).

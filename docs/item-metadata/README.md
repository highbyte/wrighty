# Wrighty item metadata

Wrighty exposes one logical work-item model through two storage backends. The model is shared, but
the physical metadata and concurrency guarantees are intentionally backend-specific:

- [Local Markdown backend metadata](local-markdown-backend.md)
- [GitHub backend metadata](github-backend.md)

## Storage comparison

| Logical concept | Local Markdown backend | GitHub backend |
| --- | --- | --- |
| Canonical item ID | Numeric filename prefix, exposed as `local:N` | Repository plus issue number, exposed as `github:OWNER/REPOSITORY#N` |
| Tracked-item membership | Markdown document in the configured store | Issue added to the configured GitHub Project |
| Title | `title` frontmatter | Issue title |
| Body | Markdown after frontmatter | Issue body |
| Status | `status` frontmatter | Configured Project single-select field |
| Priority | Optional `priority` frontmatter | Configured Project single-select field |
| Custom fields | Non-reserved YAML keys | Not supported |
| Creation retry metadata | `wrighty.creation` mapping | Temporary repository label, then durable Project text field |
| Claim authority | `claims` entry in `.wrighty-runtime-v1.json` | Append-only v3 issue-comment chain |
| Claim display | Local runtime projection | Display-only Project projection fields |
| Archive state | Document location under `items/` or `archive/` | Native Project item archive state |
| Atomicity | Store lock covers authorization and document replacement | Separate GitHub API writes with pre/post claim checks |

## Authority boundary

Do not infer authorization from fields that only look similar across backends:

- Local Markdown's runtime sidecar (`.wrighty-runtime-v1.json`) is authoritative and is checked
  under the store lock. Item documents never contain claim state.
- GitHub's **Wrighty claim - claimant type**, **Wrighty claim - claimant**, **Wrighty claim - agent**,
  **Wrighty claim - session ID**, and **Wrighty claim - workspace path** Project fields are display-only. The
  issue-comment chain is authoritative. **Wrighty claim - workspace path** is additionally blank by default —
  it is only written when `worker.shareLocalPaths` is enabled.
- GitHub's **Wrighty dispatch - state**, **Wrighty dispatch - not before**, **Wrighty dispatch - agent**, and **Wrighty dispatch - detail**
  fields are display-only recovery projections. The dispatch-state issue label and
  installation-local dispatch record remain authoritative, and provider circuits stay local.
- No sidecar or Project display field substitutes for the caller-held `claimToken`.
- GitHub Project fields never contain a claim token.

The examples are grouped by physical backend:

```text
examples/
├── local-markdown/
└── github/
```

The current GitHub claim marker version and field contract are documented in the
[GitHub backend metadata reference](github-backend.md#authoritative-claim-comments).

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
| Creation retry metadata | `creation` mapping | Temporary repository label, then durable Project text field |
| Claim authority | Current `claim` mapping | Append-only v2 issue-comment chain |
| Claim display | The authoritative `claim` mapping itself | Display-only Project projection fields |
| Archive state | Document location under `items/` or `archive/` | Native Project item archive state |
| Atomicity | Store lock covers authorization and document replacement | Separate GitHub API writes with pre/post claim checks |

## Authority boundary

Do not infer authorization from fields that only look similar across backends:

- Local Markdown's `claim` mapping is authoritative and is checked under the store lock.
- GitHub's **Current claimant kind**, **Current claimant**, **Current agent type**, and
  **Current session ID** Project fields are display-only. The issue-comment chain is authoritative.
- Neither `claimEpoch` nor a Project display field substitutes for the caller-held `claimToken`.
- GitHub Project fields never contain a claim token.

The examples are grouped by physical backend:

```text
examples/
├── local-markdown/
└── github/
```

Protocol design and transition-resolution details live in
[claim protocol v2](../design/claim-protocol-v2.md).

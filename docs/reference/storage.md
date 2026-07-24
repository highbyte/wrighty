# Storage and version control

See the [item metadata reference](../item-metadata/README.md) for a backend comparison, complete
field tables, authority boundaries, and deterministic Local Markdown and GitHub examples.

## Local Markdown backend

The default local setup creates:

```text
.wrighty/
├── items/
├── archive/
├── .lock
└── .runtime-state.json
```

Local paths are resolved relative to `.wrighty.json`. The configured `items/` and `archive/`
directories contain the authoritative work-item content. Each item is a human-readable Markdown
file with YAML frontmatter and a filename such as `001-develop-login-feature.md`. The numeric
prefix is the identity; editing the title renames the file without changing `local:1`.

Frontmatter holds managed item metadata plus optional custom YAML fields. Live claims, recorded
agent sessions, normalized run failures, and exact deferred-dispatch timers are machine-local
runtime state in the `.runtime-state.json` sidecar, so claiming,
renewing, and releasing never modify the committed Markdown documents. The
[Local Markdown metadata reference](../item-metadata/local-markdown-backend.md) defines every
field, reserved names, canonical ordering, YAML round-trip behavior, lifecycle representation, the
sidecar format, and deterministic examples. `wrighty get` exposes custom fields, and the web item
view provides a read-only **Frontmatter** disclosure.

Import existing Markdown explicitly; the normal store loader remains strict:

```shell
wrighty import notes.md docs/ --recursive --dry-run
wrighty import notes.md docs/ --recursive
wrighty import old/ --move --map status=state --force-status Todo
```

Import copies by default. `--move` removes a source only after the complete staged batch has been
validated and committed. Title resolution is frontmatter `title`, then the first H1, then filename
stem. Status and priority are validated against local configuration; `--map status=state` or
`--map priority=rank` selects alternate source keys, while `--force-status` resolves an invalid or
missing source status. Custom frontmatter nodes are preserved. Batch IDs are contiguous and the
store lock plus staging makes a failed batch leave no imported items. An ordinary `.md` file copied
directly into `items/` still fails strict loading and the error points to `wrighty import`.

The store-wide lock coordinates processes sharing the same filesystem.

Commit the authoritative work-item documents under the configured `items/` and `archive/`
directories; they preserve the backlog, completed work, and their history with the repository. Do
not ignore the entire local tracker directory.

When a local Markdown store is inside a Git worktree, a mutating `wrighty init` creates a
`.gitignore` in the tracker root with these rules:

```gitignore
# Wrighty runtime state
/.lock
.*.tmp
/.runtime-state.json
```

The rules ignore the store-wide runtime lock, the machine-local runtime-state sidecar, and
interrupted atomic-write temporary files at any level below the tracker root. Existing `.gitignore` content is preserved; initialization appends
only missing tracker rules. Repeated initialization is idempotent. Outside a Git worktree no
`.gitignore` is created, and `wrighty init --check` never creates or changes one. The generated
`.gitignore` should itself be committed.

If a parent `.gitignore` excludes the entire tracker directory, the nested rules cannot make its
work-item documents visible to Git; remove that parent exclusion. Git records and transports local
tracker state, but does not provide distributed claim or ID allocation. A solo developer using
multiple machines should finish or release claims, commit and push tracker changes, and pull before
mutating the tracker elsewhere. Teams or concurrent agents on different machines should use the
GitHub backend.

Claims never touch the item documents, so items can be committed at any time without transient
claim metadata. Only the managed `wrighty-worker-state` dispatch marker and ordinary content edits
change a document.

## GitHub backend

Repository issues, configured Project item state, and authoritative claim comments compose the
GitHub work item; no local work-item directory is created. See the
[GitHub metadata reference](../item-metadata/github-backend.md) for their field-level authority.
Only machine-local operational and regenerable state is stored locally:

- opaque GitHub project, field, and option node IDs, including agent-context projection fields;
- a per-install UUID used to derive a privacy-preserving 12-character worker identity.
- recorded vendor session/workspace addresses, normalized run failures, and exact deferred retry
  decisions in `sessions-v1.json`.

No GitHub work-item content, creation results, or authoritative claim state is cached locally.
Invalid node
IDs are discarded and rediscovered once. The machine-local cache must not be committed.

Set `WRIGHTY_CACHE_DIR` to override the cache directory. This is useful for
isolating worker identities during integration tests; normal installations should leave it
unset.

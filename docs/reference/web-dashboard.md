# Local web dashboard

The Local Markdown backend includes an offline dashboard for developers. Run it from the directory
containing `.wrighty.json` or any child directory:

```shell
wrighty web
```

Wrighty binds an ephemeral port on `127.0.0.1`, prints the address, and opens the dashboard in the
default browser. Use a fixed port or keep the browser closed when needed:

```shell
wrighty web --port 8080
wrighty web --no-open
```

The dashboard shows configured status columns, priority and claim state, supports active/archived
filtering, and renders each item's Markdown. A developer can claim an item, edit its structured
title/body/status/priority fields, save and release it, finish it, or archive it. YAML frontmatter is
never exposed as editable content. If the file changes after an edit form was opened, Wrighty keeps
the browser draft and shows the current version beside it instead of overwriting either version.

Claims belonging to another claimant session are read-only in the web application. For a claim on
this installation, **Take over for editing…** confirms an explicit transfer and opens the editor
only after the browser session owns the new token generation. **Release existing claim…** clears an
abandoned same-installation claim without taking it over. The legacy `protectNonHumanClaims`
setting no longer weakens authorization: claimant fencing is always enforced.
For a resumable agent claim, plain **Save** retains human ownership. **Save and hand back to
_Agent_** performs a second fenced transfer to a fresh agent claimant and only then exposes the
agent-scoped interactive resume command. After plain Save, the web UI instead exposes a copyable
`wrighty worker --item <id> --resume --yes` command that explicitly performs that transfer and continues the
recorded session headlessly under worker supervision.

The web command currently supports only `backend: local-markdown`. It serves all browser assets from
the executable and makes no CDN requests. Tracker fragments require the per-process token in the URL
printed by `wrighty web`; treat that URL like a short-lived local credential. The server listens only
on IPv4 loopback and stops with Ctrl+C. Failed web requests are logged to the same terminal with the
HTTP method, safe request target, status, Wrighty error code, and exception details. Launch and claim
tokens are never logged. The browser response continues to redact the tracker root. Agents and
scripts should continue to use the stable CLI/JSON contract rather than automate this
developer-facing HTML surface.

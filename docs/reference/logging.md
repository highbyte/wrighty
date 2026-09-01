# Diagnostics and log capture

Wrighty keeps command output and operational diagnostics on separate streams so people, scripts,
and service managers can capture each one appropriately.

| Destination | What Wrighty writes there |
| --- | --- |
| Standard output | Command results, prompts, worker lifecycle events, and worker NDJSON |
| Standard error | Safety warnings, command errors, and internal operational diagnostics |
| Web console worker log | A bounded, allowlisted view of a web-hosted worker's lifecycle events |

Console diagnostics include a timestamp, severity, category, event ID, and message. For example, a
failed web request may produce:

```text
2026-09-01T15:05:14.296+02:00 warn: Highbyte.Wrighty.Web.WebDiagnostics[2001] Web request GET /not-found returned 404 HTTP_404.
```

Request diagnostics include only an allowlisted request target. Launch tokens, claim tokens, agent
transcripts, model responses, reasoning, tool calls, secrets, and environment values are not logged.

## Relevant command options

Wrighty currently has no logging-specific command options.

- `wrighty worker --json` makes every worker standard-output line NDJSON. Diagnostics still go to
  standard error and never contaminate that stream.
- `wrighty worker --color auto|always|never` controls human worker-event and safety-warning prefix
  presentation. It does not change diagnostic routing.
- `wrighty web --no-open` suppresses the browser launch. Web request diagnostics still go to
  standard error.

Native file logging is a current limitation: Wrighty has no `--log-file`, `--log-level`, rotation,
retention, or telemetry configuration. Redirect standard error or let a service manager or log
collector own capture and retention.

## Keep machine output and diagnostics separate

For a machine-readable worker, capture stdout and stderr independently:

```shell
wrighty worker --yes --json >worker.ndjson 2>worker-diagnostics.log
```

```powershell
wrighty worker --yes --json 1>worker.ndjson 2>worker-diagnostics.log
```

Every non-empty line in `worker.ndjson` remains a worker event. `worker-diagnostics.log` contains
safety warnings, command errors, and operational diagnostics.

## Keep a combined human-readable log

For a chronological terminal view and log file, combine the streams before `tee`. Disable worker
color so the saved file contains no ANSI escape sequences:

```shell
wrighty worker --yes --color never 2>&1 | tee -a worker.log
```

```powershell
wrighty worker --yes --color never 2>&1 |
    Tee-Object -FilePath worker.log -Append
```

Combining the streams loses the stdout/stderr distinction. A pipeline can also hide Wrighty's exit
code; see [Autonomous worker mode](worker.md#common-logging-scenarios) for exit-code-preserving and
background-service examples.

## Capture web diagnostics

Keep startup information and diagnostics separate when running the web console manually:

```shell
wrighty web --no-open >web-output.log 2>web-diagnostics.log
```

```powershell
wrighty web --no-open 1>web-output.log 2>web-diagnostics.log
```

Client request failures use warning severity; server failures use error severity. External log
collectors can filter the rendered severity, category, event ID, HTTP method, safe target, status,
or Wrighty error code. For a managed service, leave both streams attached and configure capture,
rotation, retention, and forwarding in the service manager rather than shell redirection.

# GitHub Project item example

This is an illustrative logical view, not a raw GraphQL response.

## Repository issue

| Property | Value |
| --- | --- |
| Repository | `highbyte/wrighty` |
| Issue number | `42` |
| Canonical Wrighty ID | `github:highbyte/wrighty#42` |
| Title | `Implement claim fencing` |
| Body | User-authored Markdown; no Wrighty marker is inserted |
| Issue state | `open` |

## Project item

| Field or state | Value | Authority |
| --- | --- | --- |
| Membership | Present in configured Project | Authoritative tracked-item membership |
| Status | `In Progress` | Authoritative |
| Priority | `P1` | Authoritative |
| Creation attempt ID | `019f5c485c2b7862aeac80eb638a7b5c` | Authoritative retry metadata |
| Native archive state | Not archived | Authoritative lifecycle state |
| Current claimant kind | `Human` | Display-only |
| Current claimant | `web:browser-session-42` | Display-only |
| Current agent type | Empty | Display-only |
| Current session ID | Empty | Display-only |
| Worker activity | `Retry scheduled` | Display-only; issue label is authoritative |
| Worker retry at | `2026-07-24T04:02:00.0000000+00:00` | Display-only; exact record is installation-local |
| Worker target agent | `Claude` | Display-only |
| Worker status | `Waiting for Claude usage; attempt 2 of 5` | Display-only, sanitized |

The current claim token is intentionally absent. Resolve the issue-comment chain for ownership;
never authorize from these claimant display fields.

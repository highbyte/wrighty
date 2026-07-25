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
| Wrighty policy - execution | `Automatic allowed` | Authoritative |
| Wrighty policy - agent | `Claude` | Authoritative |
| Wrighty creation - attempt ID | `019f5c485c2b7862aeac80eb638a7b5c` | Authoritative retry metadata |
| Native archive state | Not archived | Authoritative lifecycle state |
| Wrighty claim - claimant type | `Human` | Display-only |
| Wrighty claim - claimant | `web:browser-session-42` | Display-only |
| Wrighty claim - agent | Empty | Display-only |
| Wrighty claim - session ID | Empty | Display-only |
| Wrighty dispatch - state | `Retry scheduled` | Display-only; issue label is authoritative |
| Wrighty dispatch - not before | `2026-07-24T04:02:00.0000000+00:00` | Display-only; exact record is installation-local |
| Wrighty dispatch - agent | `Claude` | Display-only |
| Wrighty dispatch - detail | `Waiting for Claude usage; attempt 2 of 5` | Display-only, sanitized |

The current claim token is intentionally absent. Resolve the issue-comment chain for ownership;
never authorize from these claimant display fields.

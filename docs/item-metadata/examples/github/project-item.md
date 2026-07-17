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

The current claim token is intentionally absent. Resolve the issue-comment chain for ownership;
never authorize from these claimant display fields.

# Stable error handling

Always request `--json` and branch on `error.code`.

| Code | Required action |
|---|---|
| `CONFIG_NOT_FOUND`, `CONFIG_INVALID` | Stop and explain setup; never initialize implicitly. |
| `CLAIM_HELD` | Do not mutate or bypass the owner. |
| `CLAIM_HELD_BY_LOCAL_CLAIMANT` | Stop. Takeover is available only with explicit user authorization. |
| `CLAIM_STALE` | Hard stop; do not reclaim, retry, release, or take over automatically. |
| `CLAIM_TOKEN_REQUIRED` | Stop and recover the handle from task context; never scrape storage. |
| `CLAIM_FORMAT_UNSUPPORTED` | Stop; old and new Wrighty binaries/active claims must not be mixed. |
| `CLAIM_LOST_DURING_UPDATE` | Report applied and pending stages; do not roll back automatically. |
| `CLAIM_REQUIRED`, `CLAIM_NOT_OWNER` | Do not write or release; re-establish the intended item. |
| `NO_ITEM_AVAILABLE` | Report no matching claimable item; do not invent work. |
| `WORK_ITEM_NOT_FOUND`, `PROJECT_ITEM_NOT_FOUND` | Recheck the supplied ID; do not create a replacement. |
| `PARTIAL_CREATE` | Retry identical create with the same Creation attempt ID. |
| `PARTIAL_ADOPT` | Report the canonical ID and applied/pending stages; retry the same named issue adoption. |
| `ADOPT_SOURCE_NOT_FOUND`, `ADOPT_SOURCE_UNSUPPORTED`, `ADOPT_REPOSITORY_MISMATCH` | Stop; do not substitute, scan for, or create another issue. |
| `IMPORT_FIELDS_UNSUPPORTED` | Stop before creation; preserve explicitly or remove the unsupported source fields after user review. |
| `IMPORT_INTENT_CONFLICT`, `IMPORT_REFERENCES_UNMAPPED`, `IMPORT_ACTIVE_CLAIMS` | Stop before further import writes and require an explicit operator decision. |
| `IMPORT_INCOMPLETE` | Retain the manifest and rerun the same import/options; never delete successful target issues. |
| `CREATION_ATTEMPT_CONFLICT`, `CREATION_ATTEMPT_DUPLICATE` | Stop; never change intent or delete duplicates automatically. |
| `PARTIAL_UPDATE` | Retry only `error.details.pendingFields`. |
| `PARTIAL_FINISH` | Retry the same `finish` command. |
| `SKILL_AGENT_REQUIRED` | Select the intended host explicitly. |
| `SKILL_MODIFIED` | Preserve local changes; require an explicit update/force decision. |

For an unknown code, stop before further mutation and report the complete structured error. Never
infer that a failed or missing response means a write did not happen.

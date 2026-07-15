# Stable error handling

Always request `--json` and branch on `error.code`.

| Code | Required action |
|---|---|
| `CONFIG_NOT_FOUND`, `CONFIG_INVALID` | Stop and explain setup; never initialize implicitly. |
| `CLAIM_HELD` | Do not mutate or bypass the owner. |
| `CLAIM_REQUIRED`, `CLAIM_NOT_OWNER` | Do not write or release; re-establish the intended item. |
| `NO_ITEM_AVAILABLE` | Report no matching claimable item; do not invent work. |
| `WORK_ITEM_NOT_FOUND`, `PROJECT_ITEM_NOT_FOUND` | Recheck the supplied ID; do not create a replacement. |
| `PARTIAL_CREATE` | Retry identical create with the same Creation attempt ID. |
| `CREATION_ATTEMPT_CONFLICT`, `CREATION_ATTEMPT_DUPLICATE` | Stop; never change intent or delete duplicates automatically. |
| `PARTIAL_UPDATE` | Retry only `error.details.pendingFields`. |
| `PARTIAL_FINISH` | Retry the same `finish` command. |
| `SKILL_AGENT_REQUIRED` | Select the intended host explicitly. |
| `SKILL_MODIFIED` | Preserve local changes; require an explicit update/force decision. |

For an unknown code, stop before further mutation and report the complete structured error. Never
infer that a failed or missing response means a write did not happen.

# GitHub v2 takeover comment

Illustrative server metadata:

| Property | Value |
| --- | --- |
| Comment ID | `1002` |
| `created_at` | `2026-07-17T10:20:00Z` |

Comment body:

```markdown
_Wrighty: claim taken over by human **web:browser-…**._

<!-- wrighty-claim:v2
{"version":2,"eventId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","workerIdentity":"8a31c0be11af","claimedAt":"2026-07-17T10:20:00+00:00","expiresAt":"2026-07-17T11:20:00+00:00","eventType":"takenOver","claimantId":"web:browser-session-42","claimToken":"22222222222222222222222222222222","previousClaimToken":"11111111111111111111111111111111","claimantKind":"human","claimAttemptId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","state":"active"}
-->
```

This transition is valid only if token `1111…` is still resolved when GitHub orders the comment.
It installs token `2222…`.

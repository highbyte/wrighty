# GitHub v2 release comment

Illustrative server metadata:

| Property | Value |
| --- | --- |
| Comment ID | `1003` |
| `created_at` | `2026-07-17T10:35:00Z` |

Comment body:

```markdown
_Wrighty: claim released by human **web:browser-…**._

<!-- wrighty-claim:v2
{"version":2,"eventId":"cccccccccccccccccccccccccccccccc","workerIdentity":"8a31c0be11af","claimedAt":"2026-07-17T10:35:00+00:00","expiresAt":"2026-07-17T11:35:00+00:00","eventType":"released","claimantId":"web:browser-session-42","claimToken":"33333333333333333333333333333333","previousClaimToken":"22222222222222222222222222222222","claimantKind":"human","claimAttemptId":"cccccccccccccccccccccccccccccccc","state":"released"}
-->
```

This transition ends the chain only if token `2222…` is still current. The event's own token does
not become an active generation.

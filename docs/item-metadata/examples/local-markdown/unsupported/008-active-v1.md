---
title: Unsupported active v1 claim example
status: In Progress
priority: P1
createdAt: 2026-07-17T10:00:00.0000000+00:00
updatedAt: 2026-07-17T10:40:00.0000000+00:00
claimEpoch: 1
claim:
  version: 1
  workerIdentity: 8a31c0be11af
  agentType: codex
  sessionId: historical-session
  claimantKind: agent
  claimedAt: 2026-07-17T10:40:00.0000000+00:00
  expiresAt: 2030-07-17T11:40:00.0000000+00:00
creation:
  version: 1
  attemptId: 88888888888888888888888888888888
  requestHash: 123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef0
---
This historical active claim has no claimant ID or fencing token. A v2 mutation must fail with
`CLAIM_FORMAT_UNSUPPORTED`; it must not treat the item as unclaimed.

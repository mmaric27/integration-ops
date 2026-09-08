# ADR-0004: Durable Background Processing

**Date:** 2026-09-08  
**Status:** Accepted

## Context

The [intake decision](0003-idempotent-integration-run-intake.md) durably accepts idempotent synthetic requests without executing them inline. This decision adds a small PostgreSQL-coordinated lifecycle inside the existing API host, following the [architecture direction](0001-architecture-direction.md) and extending the [persistence decision](0002-postgresql-persistence.md).

## Decision

Use one BackgroundService per enabled API instance, default disabled and enabled only in Development. Process only intake-origin rows. Original sample snapshots never execute. Existing and new Pending intake becomes eligible when processing is enabled. No new public status or response field is needed: Pending includes initial, leased and retry-waiting shapes.

Add only nullable UUID lease_token and UTC microsecond lease_expires_at/next_attempt_at. Pair/origin/finite-time and complete intake state constraints enforce supported durable shapes. Attempts count committed claim allocations, not provable external calls. Maximum 3; lease 60 seconds; execution deadline 10 seconds; retries 5 then 10 seconds; no jitter or renewal. Idle delay 500 ms; recognized database-unavailability delay 2 seconds; host shutdown budget 30 seconds.

The execution deadline uses cooperative cancellation; it does not forcibly terminate a processor that ignores cancellation. Lease fencing still prevents a result from committing after ownership expires.

Every iteration recovers at most one expired allocation, then attempts one ready claim. Short Read Committed transactions use FOR UPDATE SKIP LOCKED, recheck state with fresh PostgreSQL clock_timestamp, conditionally mutate and commit. Every ready claim orders by COALESCE(next_attempt_at, received_at), received_at, id ascending. An older due retry therefore precedes later new intake; locked rows can be bypassed. The matching partial expression index is owned by migration SQL. A real PostgreSQL catalog test verifies expression, key order, predicate, B-tree type, non-unique, ready and valid flags. EF model drift separately validates mapped objects and cannot prove the expression index exists.

A claim assigns a fresh random lease token and increments attempts exactly once. Execution starts only after acknowledged commit and outside every database scope. Completion locks and conditionally checks ID, Pending, token, attempt number and unexpired database-clock ownership. Completion uses GREATEST(database_time, received_at). Ownership loss discards the result. Recovery clears expired ownership and schedules another attempt or exhausts the third; recovery never increments attempts.

Claim acknowledgement ambiguity means no execution. Completion ambiguity means no blind repeat. A committed outcome remains authoritative; an uncommitted outcome leaves an expiring allocation. A resumed stale computation cannot commit state, but leases alone cannot prevent duplicate external effects. This processor has no such effects.

## Failure Ownership and Safe Data

Processor execution/result validation has its own exception boundary. Unexpected processor errors become terminal internal_processing_failure. Deadline is retryable; host cancellation stops work and leaves its allocation for recovery. Recognized database connection/transport unavailability delays coordination. Schema, integrity and programming defects in coordination propagate and stop the host rather than failing an individual run.

A closed mapping permits only synthetic_retryable_failure, synthetic_permanent_failure, processing_deadline_exceeded, attempt_abandoned, attempt_exhausted and internal_processing_failure. Retry-waiting permits the first retryable/deadline/abandoned codes. Terminal errors permit permanent/internal or exhausted at attempt 3. Clear last_error on a new allocation and success. No arbitrary processor text, exception message, stack, SQL or partner body is persisted. Logs use fixed events, run ID, attempt and safe outcome, with framework diagnostics restricted locally.

## Synthetic Contract

Ordinary operations succeed with count 1. Exact ordinal Synthetic:RetryOnce fails retryably on allocation 1 then succeeds; Synthetic:AlwaysRetry exhausts; Synthetic:Fail fails permanently. No fault endpoints or request-shape changes. Controlled blocking is substituted only inside tests. HTTP replay returns current state. The POST accepted snapshot may be followed by an already-progressed resource. UI remains manual refresh only; readiness checks schema rather than worker progress.

## Migration and Compatibility

The generated third migration adds nullable columns without backfill. It rejects incompatible intake state atomically with a static message before constraints/indexes. Down takes ACCESS EXCLUSIVE and refuses any processing metadata or attempted intake row. No deletion/coercion/reset; stop all application versions for migration. The model snapshot includes mapped columns, checks and expired index; the ready index has explicit migration SQL ownership.

## Alternatives and Limitations

A broker, separate worker executable, public Processing state, long execution transaction, generic repository, serializable isolation and advisory locks add no necessary capability here. Heartbeats/renewal are deferred until work duration requires them. No new packages are needed. There is no attempt history, per-instance policy tuning, new telemetry stack, production integration, authentication or pagination. Clock jumps, suspended processes and retained unbounded local data remain limitations.

## Verification

Real PostgreSQL tests must exercise fairness, exclusive allocation, stale fencing, safe outcomes, atomic migration refusal, shutdown and commit uncertainty. A deterministic post-commit fault exercises acknowledged-response uncertainty without claiming exhaustive network simulation.

## Current Scope

The hosted worker processes synthetic intake within the API host using PostgreSQL for allocation and recovery. It makes no external calls; lease fencing protects stored state, not exactly-once external effects. The [intake contract](0003-idempotent-integration-run-intake.md) continues to return current run state, and the UI remains a manually refreshed snapshot. See the [project scope](../../PROJECT-SCOPE.md) for supported behavior and limits, and the [README](../../README.md) for enabling processing, synthetic operations and verification commands.

**AI-assisted:** Yes

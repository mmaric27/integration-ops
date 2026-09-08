# ADR-0003: Idempotent Integration-Run Intake

**Date:** 2026-09-07  
**Status:** Accepted

## Context

The initial persistence schema stores synthetic snapshots. A newly accepted request has never executed, so intake must permit zero attempts without fabricating work. The design retains one API project, one test project, Minimal API and a scoped DbContext. This decision extends the [architecture direction](0001-architecture-direction.md) and [initial persistence schema](0002-postgresql-persistence.md) for intake; [durable background processing](0004-durable-background-processing.md) subsequently defines execution within the same API host.

## Decision

POST /api/integration-runs accepts exactly normalized partner and operation. All operational state is server-owned: new Guid, Pending, zero attempts/records, UTC microsecond receivedAt, null completion/error. GET by ID is a useful resource address and supplies the named-route Location for 201.

The mandatory Idempotency-Key has one effective 1-128-character case-sensitive ASCII value from A-Z, a-z, 0-9, dot, underscore and hyphen, with no normalization. Its scope is global in this local unauthenticated application/database, never derived from partner text. Only SHA-256 of exact UTF-8 key bytes (no BOM) is persisted. Keys are retained with the row; no TTL or HMAC secret is introduced. Different keys intentionally mean distinct acceptance, even with identical bodies.

Labels reject invalid Unicode, Cc controls and U+2028/U+2029 before trimming. Trim Unicode edge whitespace, normalize NFC, preserve case/internal permitted whitespace, then require 1-160 scalars. Cf is permitted. The JSON object rejects duplicate/unknown properties and malformed/comment/trailing-comma input. A bounded BCL/System.Text.Json reader enforces 8192 transport bytes independently of normalized label limits; only application/json with absent/UTF-8 charset and absent/identity content encoding is supported.

The semantic fingerprint is SHA-256 of this exact framing:

1. UTF-8 `IntegrationOps:POST:/api/integration-runs:v1`.
2. One zero byte.
3. Unsigned 32-bit big-endian partner UTF-8 byte length, then normalized partner UTF-8 bytes.
4. Unsigned 32-bit big-endian operation UTF-8 byte length, then normalized operation UTF-8 bytes.

The key, ID and mutable operational fields do not participate. No version column or response snapshot is added. Normalization/framing changes later require explicit compatibility design; versioning the prefix alone does not make old keys automatically compatible.

Two nullable bytea columns on integration_runs store key hash and fingerprint. Each is 32 bytes when present; a check enforces both-null or both-present. Legacy samples remain null/null. A partial unique B-tree index named ux_integration_runs_idempotency_key_hash covers non-null key hashes. Separate idempotency storage would add joins and another atomicity concern without a present requirement.

## Concurrency and Responses

Validate, normalize and hash before constructing the candidate. Attempt one SaveChangesAsync with the complete row. PostgreSQL uniqueness arbitrates races without a preliminary read, process lock, advisory lock or stronger isolation. A committed winner returns 201. Only DbUpdateException containing PostgresException SQLSTATE 23505 with the exact key-index name enters resolution.

After the failed save transaction has ended, detach only the candidate and perform a fresh AsNoTracking lookup. Npgsql translates byte-array SequenceEqual to bytea value equality. Compare the fingerprint by value and both labels ordinally. Matching content returns 200 with CURRENT run state; differing content returns safe 409. An absent expected winner returns safe 503 and never triggers a second insert. Other integrity errors remain generic 500; recognized transport/schema failures are generic 503.

One run and its hashes are one row/save atomic unit. No explicit surrounding transaction or automatic execution-strategy retry is added. Cancellation flows through body and DB I/O, but a response may be lost after commit. A retry with the same key resolves the committed row or creates when nothing committed. The guarantee concerns side effects and identity, not byte-identical historical responses.

All application POST responses use no-store and safe Problem Details where applicable. Named-route Location avoids manual URI assembly. 201 represents the durable resource already created, not completion of processing. Intake does not execute the operation inline; an enabled worker can subsequently advance the resource. Replay 200 distinguishes existing acceptance.

## Migration and Alternatives

Replace the named attempt constraint with >= 0 without changing samples. Add nullable metadata, checks and the partial unique index in a second EF migration. No fabricated keys, startup migration or seeding is introduced. Down locks the table in its normal transaction and refuses if either metadata field is present or any attempt count is zero. Only compatible legacy-only data can downgrade. No data deletion/coercion or rollback subsystem is justified.

Alternatives rejected: raw key storage (avoidable disclosure), raw JSON fingerprinting (formatting-dependent semantics), HMAC (unneeded secret lifecycle), separate key table (no current benefit), select-before-insert alone (racy), response snapshots (unneeded historical state), 202 (the intake decision creates a durable resource without executing it), generic repository/mediator/validation libraries (no responsibility that requires them).

## Consequences and Verification

The UI parser accepts zero attempts; the UI still reads one snapshot on mount and has no POST form. Listing is deliberately unpaginated for small synthetic use; intake can grow it, so no large-dataset guarantee is made. All existing statuses and the public nine-field response remain unchanged. Metadata is never exposed.

Real PostgreSQL tests cover concurrent HTTP arbitration, an uncommitted key holder, byte equality, direct constraints, migration preservation/downgrade, cancellation and retry after committed acceptance. Input/OpenAPI tests also run without DB I/O.

## Current Scope

[ADR-0004](0004-durable-background-processing.md) defines worker eligibility, claims, attempts, retries and transitions. Original Pending snapshots never become worker inputs. Intake metadata identifies intake provenance but is not itself a lease, queue or processing policy. Authentication, capacity/retention controls, dedicated telemetry, CI and deployment are outside this intake decision. See the [project scope](../../PROJECT-SCOPE.md) for current limits and the [README](../../README.md) for request examples and verification commands.

**AI-assisted:** Yes

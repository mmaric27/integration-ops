# ADR-0002: PostgreSQL Persistence for the Read-Only Slice

**Date:** 2026-09-07  
**Status:** Accepted

## Context

The initial read-only persistence slice replaces the fixed array while preserving its HTTP contract and small structure. This ADR defines that initial schema, not the complete current schema. The [intake decision](0003-idempotent-integration-run-intake.md) subsequently adds zero-attempt acceptance and idempotency metadata; the [processing decision](0004-durable-background-processing.md) adds leases, retries and lifecycle constraints.

## Persistence Boundary

Use one scoped IntegrationOpsDbContext directly from cohesive endpoint, health and seed code inside the existing API project. Data/ owns persistence, IntegrationRuns/ owns the public response and listing. No new .NET project or generic repository/unit-of-work wrapper is needed: EF already owns tracking and transactions.

Persistence uses its own entity, mapped explicitly to IntegrationRunResponse. Returning EF entities would couple public fields and serializer behavior to future storage changes. There is no separate application/domain layer without a responsibility that requires one.

## Initial Schema

Table: integration_runs. Snake-case SQL names are explicit.

| Column | PostgreSQL | Persistence .NET type | Nullable | Durable rule |
| --- | --- | --- | --- | --- |
| id | uuid | Guid | No | Primary key, no database-generated value |
| partner | character varying(160) | string | No | char_length > 0 |
| operation | character varying(160) | string | No | char_length > 0 |
| status | character varying(16) | IntegrationRunStatus with string conversion | No | Pending, Succeeded or Failed |
| attempt_count | integer | int | No | >= 1 |
| record_count | integer | int | No | >= 0 |
| received_at | timestamp(6) with time zone | UTC DateTime | No | Finite supported range |
| completed_at | timestamp(6) with time zone | UTC DateTime? | Yes | Finite supported range; >= received_at when present |
| last_error | character varying(1000) | string? | Yes | Fictional safe operator summary only |

In this initial schema, the primary key is the only uniqueness requirement and attempt_count must be >= 1. A btree index on (received_at, id) supports ascending deterministic listing. PostgreSQL enforces required fields, lengths, counters, status and time invariants even when a caller bypasses EF. EF mapping declares those constraints and column types; entity setters enforce UTC and normalize precision before database I/O.

The initial schema does not forbid whitespace-only labels or an empty last_error, and introduces no relationship between status, attempts, completion and error presence. Safe diagnostic content is a semantic rule for the synthetic seed and application validation, not a claim that a SQL constraint detects secrets. Later intake validation and processing constraints extend these rules without turning the original snapshots into processing inputs.

Store status as constrained text rather than a PostgreSQL enum or integer. Text stays readable in SQL and a check constraint is straightforward to change in a migration. A PostgreSQL enum would add provider/type lifecycle work for three values; integers would obscure stored state. Public enum serialization is unchanged.

## Timestamp Decision

PostgreSQL timestamptz stores an instant, not the source offset or named timezone. Its textual rendering can depend on the session timezone. Npgsql's natural representation is DateTime with Kind=Utc.

UtcTimestamp.NormalizeUtc rejects Local and Unspecified kinds, then constructs a UTC DateTime from ticks - ticks % 10. This truncates the seventh fractional second digit; it never rounds into the next microsecond or past the maximum year. Entity setters apply it before persistence.

UtcTimestamp.ToPersistence converts an input DateTimeOffset to UtcDateTime and normalizes it. UtcTimestamp.ToResponse first requires UTC, then constructs DateTimeOffset(value, TimeSpan.Zero). Nullable functions preserve null. The public response retains DateTimeOffset and DateTimeOffset?.

Disable Npgsql infinity conversions before the first Npgsql use. PostgreSQL constraints require isfinite and inclusive bounds 0001-01-01T00:00:00Z through 9999-12-31T23:59:59.999999Z. Database tests also bypass EF to test invalid ranges and infinity directly.

Compared with keeping DateTimeOffset in persistence, UTC DateTime follows the provider default and avoids implying support for stored nonzero offsets. DateTimeOffset remains valuable at the public boundary. Round trips preserve the normalized instant at microsecond precision, not the original seventh digit, offset or timezone.

## Listing and Failure Semantics

The EF query uses `AsNoTracking` and orders by `received_at ASC, id ASC` in SQL, with the request `CancellationToken` passed to `ToArrayAsync`. All nine initial persisted columns are needed by the response; explicit UTC-to-response mapping occurs after materialization, without a custom client-side sort or synchronous database I/O.

This preserves the original sample ordering and response JSON. The unique tie-breaker makes equal received times deterministic. Pagination was deferred for the initial read-only dataset. Current intake can grow that dataset, but listing remains deliberately unpaginated and limited to small local use.

An empty migrated table is a successful empty array. Query dependency failures return generic 503 Problem Details and no-store, not an empty success. Liveness remains /health; /health/ready reads all required table columns. Even an empty table must resolve the expected schema. Neither endpoint returns SQL or connection details.

## Migration and Seed Strategy

Use generated EF Core migrations and a pinned local dotnet-ef tool through the normal application configuration. Runtime registration supplies the context without a design-time factory. Normal startup never applies migrations or seeds.

Explicit database update applies the initial schema. Tests use MigrateAsync and verify migration history and repeat application. The standard migrations has-pending-model-changes command is the source/model drift gate; there is no redundant custom model-drift test.

Rolling back this initial migration drops `integration_runs` and all of its data. Compatibility guards in the later intake and processing downgrades do not make an entire rollback to an empty database non-destructive.

The Development-only --seed-synthetic command exits without starting HTTP or processing. In one transaction it reads only the three predefined sample IDs, compares all persisted content, and inserts missing sample rows. Exact matches and unrelated rows remain unchanged. Any sample conflict fails with no committed seed changes. Concurrent seeds may fail safely on the primary key; seeding does not use the intake-idempotency protocol.

Migration seed data was rejected because examples are not a schema upgrade obligation. Test-only seeding would leave the local dashboard empty. An explicit development command keeps samples available, repeatable and separate from migrations, without startup side effects. Reset is an explicit destructive deletion of the local synthetic volume followed by bootstrap, migration and seed.

## Development Database and Credentials

Compose contains PostgreSQL only, publishes 127.0.0.1:5432:5432 and mounts a named volume at PostgreSQL 18's /var/lib/postgresql parent layout. Both interactive and test images use the pinned postgres:18.6-bookworm digest. The digest fixes the artifact; updating it is a deliberate dependency change rather than receiving silent tag changes.

An empty volume runs initialize.sh. Shell validates the private environment values; a quoted heredoc prevents shell expansion of SQL. psql getenv imports the app password and SQL-literal variable quoting supplies it to CREATE ROLE. The dedicated login is not superuser and cannot create roles/databases. The script creates integration_ops_dev owned by that login.

The bootstrap administrator never enters API configuration. The API password comes from User Secrets or process environment, not appsettings. The ignored .env is for Compose initialization; its example contains placeholders only. Database ownership simplifies local migrations; separate runtime/migrator privileges belong to a later deployment security decision.

## Test Strategy

One serialized xUnit collection shares a disposable PostgreSQL container. Each test gets a new uniquely named database, migrated before use and dropped on disposal. This is simpler and more reliable at this scale than transactional HTTP-test resets across pooled connections or a database-reset library.

Testcontainers resolves its hostname and random mapped port. Tests do not require literal loopback, modify Docker networking or share the interactive database. Their synthetic, disposable infrastructure has a different boundary, recorded in the threat model.

HTTP tests prove persisted listing, empty state, deterministic order, new-host durability, schema/outage failures, liveness/readiness separation, safe errors, real query cancellation and the existing wire/OpenAPI contract. Direct PostgreSQL tests prove constraints, UTC/range/precision, fresh/repeated migrations and seed transaction behavior. Seed CLI tests reserve a port to prove the command does not start an HTTP listener. Targeted pure tests cover normalization branches.

Missing Docker fails full verification and database fixture setup; tests never skip. Compile success is not PostgreSQL behavior evidence.

## Dependency Alignment and Consequences

The initial persistence dependency alignment uses explicit Microsoft ASP.NET Core packages, EF Core, EF Design and local dotnet-ef at 10.0.11. Npgsql provider stays 10.0.3 and Testcontainers.PostgreSql stays 4.14.0. net10.0 and SDK 10.0.400 remain unchanged.

Microsoft.EntityFrameworkCore.Relational is explicitly pinned to 10.0.11 as well. Without that pin, the provider's lower minimum resolved to 10.0.4 in the test graph while the API's private Design dependency resolved 10.0.11, producing an actual warning-as-error assembly conflict. Pinning the already-required relational dependency removes that inconsistency; it does not add another persistence abstraction.

Docker and credential setup are prerequisites. Unpaginated reads, database-owner application privileges and manual local reset are deliberate limits of the synthetic boundary. This initial persistence decision introduces no broker, processing state machine, retries, audit storage, telemetry, CI or cloud deployment.

## Current Scope

[ADR-0003](0003-idempotent-integration-run-intake.md) extends the initial schema with nonnegative attempts, key-hash uniqueness and request fingerprints. [ADR-0004](0004-durable-background-processing.md) adds durable processing metadata, constraints and indexes. The initial >= 1 attempt rule and primary-key-only uniqueness above describe the read-only schema, not current intake. See the [project scope](../../PROJECT-SCOPE.md) for current requirements and the [README](../../README.md) for migration, seeding, usage and verification commands.

**AI-assisted:** Yes

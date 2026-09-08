# Project Scope

## Purpose

Define the supported behavior and boundaries of Integration Ops: accepting a request once, processing it through a durable lifecycle and exposing its current state. Synthetic operations make concurrency, retries and recovery reproducible without external partner systems.

The [README](README.md) covers setup and usage. This document defines the behavioral requirements; the [ADRs](docs/adr/0001-architecture-direction.md) explain the design decisions.

## Supported Use

The application runs locally with fictional data and small datasets. The API and worker share one .NET host, PostgreSQL supplies durable state and coordination, and a separate React development server provides the operator view. There is no authentication; interactive API, UI and database access must remain on loopback. Interactive Compose uses a trusted workstation-local Docker engine, including local Docker Desktop/WSL2, rather than a remote Docker endpoint.

## In Scope

- PostgreSQL 18.6 with one EF Core DbContext, explicit migrations and database-enforced durable invariants.
- Separate persistence entities and an explicit public response contract.
- Development-only, transactional, non-destructive synthetic sample seeding.
- Async, cancellation-aware list/by-ID reads and insert-first idempotent POST.
- Strict normalized two-field intake, required key digest/fingerprint, PostgreSQL unique arbitration and zero-attempt Pending creation.
- Intake and processing migrations with compatibility guards; explicit initial-schema creation and removal.
- Opt-in Development worker, ready-time database claiming, fixed leases, safe errors and bounded retries.
- Process liveness and separate required-schema readiness.
- PostgreSQL-only Compose development setup with a dedicated non-superuser login.
- Real PostgreSQL integration tests using migrations, one shared Testcontainers container and isolated databases.
- HTTP contract, OpenAPI and Host-filter tests, plus frontend rendering and runtime payload-validation tests.

## Out of Scope

- Real partner connectivity, credentials or operational data.
- Message brokers, separate worker deployments or distributed service topology.
- Lease renewal, attempt-history storage, reconciliation or an audit trail.
- Authentication, authorization or multi-tenancy.
- UI submission forms, automatic polling or pagination.
- Cloud infrastructure, production deployment or a dedicated telemetry stack.

## Behavioral Requirements

### Persistence and Setup

1. A fresh database applies the checked-in migrations and records migration history. Reapplying them is safe. Normal startup never migrates or seeds.
2. The explicit seed inserts three predefined snapshots into an empty database, fills missing samples, preserves exact matches and unrelated rows, and commits nothing when a sample ID conflicts with different content.
3. Stored data survives replacement of the application host and container restart when the database volume is retained. This does not provide backup or recovery from volume loss.
4. PostgreSQL enforces uniqueness, supported statuses, field lengths, counter bounds and finite timestamp ranges, including completion at or after receipt when present.
5. Persistence accepts UTC `DateTime` values, truncates sub-microsecond precision and maps explicitly to zero-offset `DateTimeOffset` responses.
6. Intake and processing migration guards reject incompatible stored state instead of deleting or coercing it. Rolling back the initial persistence migration drops the table and its data. Migrations require stopped API instances; mixed-version operation is unsupported.

### Idempotent Intake

1. Requests accept exactly `partner` and `operation`. Invalid JSON, Unicode, key or transport input receives a controlled `400`, `413` or `415` response without database mutation.
2. First acceptance commits one normalized `Pending` row with zero attempts and records, a server-generated ID and UTC receipt timestamp, plus the key hash and semantic fingerprint. It returns `201` and a working resource `Location`.
3. Sequential and concurrent requests with the same key and equivalent input resolve to one run. Different input conflicts without overwrite; different keys create distinct runs.
4. Only a uniqueness violation from the named key-hash index enters replay resolution after the failed save has rolled back and its candidate is detached. Resolution performs no second insert or automatic save retry.
5. Replay returns current operational state, including after an application restart. Cancellation or a lost response does not imply rollback; the caller can retry using the same key and input.
6. Key hash and fingerprint remain internal. HTTP responses expose the nine-field run contract, not persistence entities or coordination metadata. Errors do not expose request values or database diagnostics.

Normalization, key scope, hash framing and response semantics are specified in the [intake ADR](docs/adr/0003-idempotent-integration-run-intake.md).

### Background Processing

1. Processing is disabled by default and can be enabled only in Development. Existing and new intake-origin Pending rows become eligible; seeded snapshots never execute.
2. Short PostgreSQL transactions allocate work by ready time, skipping locked rows. Execution starts after acknowledged claim commit and runs outside the database transaction.
3. Each committed allocation consumes one of at most three attempts, even if interrupted before execution. Leases expire after 60 seconds; execution has a 10-second deadline, with retry delays of 5 then 10 seconds.
4. Completion requires the current unexpired lease token and attempt number. Stale computations cannot overwrite authoritative state. Expired allocations are recovered into retry waiting or terminal exhaustion.
5. Processor failures produce controlled run outcomes and safe error codes. Unexpected coordination or schema defects stop the host instead of being recorded as individual run failures.
6. Shutdown stops new claims and cancels execution, leaving interrupted allocations for expiry recovery. Ambiguous commit acknowledgements do not authorize blind re-execution or completion retries.

The [processing ADR](docs/adr/0004-durable-background-processing.md) defines lifecycle shapes, synthetic outcomes, fencing and recovery rules.

### Reads, Health and Operator View

1. Listing returns persisted runs ordered by receipt time and then unique ID, or an empty array for an empty migrated table. By-ID reads distinguish a found run, an absent ID and malformed ID input.
2. Query cancellation reaches database I/O. Recognized database or schema unavailability returns generic `503` responses; it is not represented as an empty successful result. Run responses use `Cache-Control: no-store`.
3. Liveness is independent of PostgreSQL. Readiness checks connectivity and required columns, not worker progress. The same API host can recover when the database returns at the same endpoint.
4. The UI validates the response before rendering and handles loading, empty, network, HTTP and invalid-payload states. It shows a snapshot with UTC timestamps and nonnegative counters; reloading fetches updated state.

## Verification Requirements

- Full verification requires locked dependency restore, warning-as-error build, formatting, EF model-drift checks, dependency audits, backend tests, frontend lint/tests and a production UI build.
- Database tests use real PostgreSQL and checked-in migrations to exercise concurrency, constraints, binary hash equality, migration guards, seed conflicts, cancellation and recovery. Missing Docker fails verification rather than silently skipping tests.
- Testcontainers provides isolated test databases through its resolved Docker hostname and random mapped ports. Tests do not connect to or reset the interactive database; the Docker engine must be trusted.
- HTTP/component tests do not establish real-listener TLS behavior, browser layout or accessibility. Those require separate runtime and browser checks.

See [README: Tests](README.md#tests) for the verification command.

## Operational Limits

- Idempotency applies to accepted runs, not exactly-once external effects. The synthetic processor makes no external calls; leases protect stored state only.
- Listing and retained data are unbounded. There is no key expiry; deleting a run or resetting its database ends its replay guarantee.
- Seeded snapshots preserve their original counters and error descriptions, including a Pending snapshot with one attempt. They are display data, not processing inputs.
- Intake validates and normalizes labels. Direct SQL does not inherit the API's complete Unicode policy, and the UI parser is not a database-integrity or lifecycle validator.
- The development login owns its database for migrations. It is not a least-privilege production runtime identity. Credentials belong in ignored local configuration, User Secrets or the process environment, never in source.

Real data, wider network access or deployment requires a separate security and operational design. The [threat model](docs/THREAT-MODEL.md) defines those boundaries and controls.

## Scope Changes

Additional services, infrastructure or capabilities should address a concrete requirement that the existing boundaries cannot meet. The non-goals above are deliberate limits, not an implementation roadmap.

**AI-assisted:** Yes

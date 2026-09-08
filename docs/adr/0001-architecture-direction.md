# ADR-0001: Architecture Direction

**Date:** 2026-09-07  
**Status:** Accepted

## Context

The project needs to demonstrate reliable partner-integration concepts without beginning as a distributed system. The initial architecture considered application boundaries, data integrity, idempotency, asynchronous processing, reconciliation, operational visibility and incremental deployment.

At decision time, the implementation contained a read-only API, PostgreSQL persistence, explicit synthetic setup and an operator UI. This decision establishes an architectural direction, not a commitment to implement every capability considered.

## Decision

Use a modular monolith as the target application architecture:

- .NET 10 for the API and background processing
- PostgreSQL for persistence
- React and TypeScript for a minimal operator UI
- HTTPS for the local development API profile
- Explicit application modules when behavior justifies boundaries

Keep API and processing behavior inside one deployable application. Do not introduce message brokers, microservices, Kubernetes or distributed coordination until concrete requirements justify them.

The initial direction also considered OpenTelemetry-compatible instrumentation for processing, Azure App Service with managed PostgreSQL for deployment, and Bicep for infrastructure definition. These remain deferred options, not implemented capabilities or a committed roadmap. The local synthetic scope does not currently require them.

## Implementation Boundary at Decision Time

The initial implementation covered:

- .NET 10 API
- React and TypeScript UI
- HTTPS local API profile
- PostgreSQL persistence, explicit EF migrations and synthetic seeding
- Separate liveness and required-schema readiness
- HTTP, real PostgreSQL Testcontainers and UI tests

Intake, idempotency, workers and retries were outside that initial implementation and were subsequently introduced by the [intake decision](0003-idempotent-integration-run-intake.md) and [processing decision](0004-durable-background-processing.md). The worker shares the API host. Data/ and IntegrationRuns/ are cohesive folders inside the API, not separate projects or evidence of implemented application modules. Reconciliation, audit storage, dedicated telemetry and cloud deployment remain outside the current scope. The API and Vite run as separate development processes; there is no combined API/UI deployment artifact.

## Alternatives Considered

### Microservices from the Start

Rejected because the initial scope had no independent scaling, deployment or ownership requirements. It would have added network, consistency and operational complexity before the core workflow existed. The current local workflow still does not require separate services.

### Serverless Functions as the Primary Model

Deferred because trigger and workload characteristics were not yet known. Functions may be appropriate for specific adapters if required, but they are not required for the local synthetic application.

### Single API Without Planned Module Boundaries

Rejected as a long-term direction because integration intake, execution, reconciliation and audit have different responsibilities. The current small API remains physically simple; explicit module boundaries depend on concrete behavior, not a commitment to add reconciliation or audit.

## Consequences

Positive consequences:

- Low operational complexity for the local synthetic workflow
- Transactional consistency remains possible within one persistence boundary
- Architecture can evolve through measured module extraction
- Local development remains accessible

Trade-offs:

- Module discipline must be enforced in code rather than by network boundaries
- Long-running work requires an explicit durable processing model, supplied for the current synthetic workflow by [ADR-0004](0004-durable-background-processing.md)
- A single deployment may eventually become a scaling constraint
- PostgreSQL and containerized integration tests now add Docker, credentials and explicit local database setup prerequisites

## Verification Strategy

HTTP contract tests, real PostgreSQL integration tests and UI tests exercise the application boundaries. Persistence, intake and processing tests check database invariants, concurrency and recovery within the shared host. They do not establish cloud deployment behavior or independent-service scaling.

## Current Scope

[ADR-0002](0002-postgresql-persistence.md) defines the initial persistence schema. [ADR-0003](0003-idempotent-integration-run-intake.md) extends it for idempotent intake, and [ADR-0004](0004-durable-background-processing.md) adds durable processing in the same API host. The modular monolith remains the target direction; no separate module projects or worker deployment are implied. See the [project scope](../../PROJECT-SCOPE.md) for supported behavior and limits, and the [README](../../README.md) for setup, usage and verification commands.

**AI-assisted:** Yes

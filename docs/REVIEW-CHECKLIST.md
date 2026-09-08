# Review Checklist

Use this checklist to verify changes against the [project scope](../PROJECT-SCOPE.md). Record results and supporting evidence with the reviewed revision in its pull request or release record.

## Preparation

- Follow the [README prerequisites](../README.md#prerequisites). Run commands from the project root in PowerShell 7.1 or later; listener and `curl.exe` examples below assume Windows.
- Record the source revision, any working-tree differences, verification date, tool versions and evidence locations. Include .NET SDK/EF tool, Docker Engine/Compose, Node.js/npm, PowerShell and the browser used.
- Stop API and UI development processes before verification. On Windows, Vite can hold a native build module open while `npm ci` replaces `node_modules`.
- Use a trusted Linux Docker engine. Testcontainers creates isolated migrated test databases on resolved Docker endpoints; automated verification must not connect to or reset the interactive development database.
- Clear inherited processing configuration, rather than setting it to `false`: absence is required by the missing-configuration default test.

```powershell
$env:Processing__Enabled = $null
./scripts/Verify.ps1
```

Do not continue past a failed gate. A missing tool, unavailable Docker engine, skipped database test or incomplete audit is not a successful verification.

## Automated Gates

Require the complete [verification script](../scripts/Verify.ps1), then inspect its output for the following criteria.

| Gate | Acceptance criterion |
| --- | --- |
| Restore and build | Pinned local EF tool restores; locked .NET restore and build succeed with warnings treated as errors. |
| Backend tests | All expected tests execute and pass, including real PostgreSQL Testcontainers cases, with zero skips. |
| Formatting | `dotnet format --verify-no-changes` succeeds without changing source. |
| Model drift | The pinned EF tool reports no pending model changes against checked-in migrations. |
| NuGet audit | Complete direct/transitive report covers both .NET projects with no vulnerability entries at any severity. |
| Frontend | `npm ci`, lint, all frontend tests and the production build succeed. |
| npm audit | Development dependencies are included; no unresolved moderate-or-higher findings. Review and record all low findings and applicability decisions. |

- Inspect the full NuGet JSON report for both the API and test project. The script validates report version, a nonempty project array, diagnostics and vulnerability entries; it does not enforce the expected project count or guarantee complete coverage.
- A package-list command's exit code alone is not the vulnerability gate. Audit errors, unavailable feeds, diagnostics or missing report coverage remain unverified, not clean results.
- Compare test discovery and execution with the reviewed source. Script success alone does not enforce an expected test count or prove that no tests were skipped.
- The script's EF model check uses password-free child-process configuration and does not contact the interactive database. Do not substitute runtime credentials or database reset for model inspection.

Require automated coverage of these behaviors, not just a successful build:

| Area | Required coverage |
| --- | --- |
| Persistence | Fresh/repeated and forward migrations, guarded downgrades, exact/partial/conflicting seed, constraints, ordering, timestamp precision, cancellation and retained-volume durability. |
| Intake | Strict key/JSON/Unicode/transport validation, normalized zero-attempt creation, sequential/concurrent arbitration, SQL binary hash equality, conflict without overwrite, cancelled inserts and committed-response retry. |
| HTTP contract | Every required wire property, including nullable and zero-valued fields; by-ID behavior; parsed `no-store`; safe errors; Host rejection and OpenAPI schemas. |
| Processing | Disabled default, Development-only opt-in, seed exclusion, ready-time ordering/index, exclusive `SKIP LOCKED` claims, multiple hosts and execution only after acknowledged commit. |
| Recovery | Retry/exhaustion, expiry recovery, stale-token fencing, loop fairness, deadlines/shutdown, durable invariants, safe error vocabulary, coordination-defect shutdown and tested commit-uncertainty cases. |
| UI | Loading, empty, success, network/HTTP failure and invalid-response handling; runtime payload validation and nonnegative counters. |

## Local Runtime

Follow [Database Setup](../README.md#database-setup) only through migration: defer its seed command until step 3 below. Use [Run Locally](../README.md#run-locally) to start the API and UI for the empty-state check. Keep processing disabled for baseline and intake checks. All manual mutations and intentionally error-producing HTTP steps below are for disposable, synthetic, non-production local data only.

Before supplying credentials or starting/resetting Compose resources, confirm the effective Docker endpoint belongs to this workstation, including its local Docker Desktop/WSL2 environment. Check the selected context and any environment or command-line overrides. Compose port bindings and named volumes belong to the engine host; a reported loopback binding alone does not prove workstation-local placement. A trusted remote engine is supported only for disposable Testcontainers verification.

1. Stop all API instances before migrations. On a fresh database, apply every checked-in migration and confirm that reapplying them is safe.
2. Before seeding, start the API and verify that the migrated empty table returns `[]`; inspect the UI empty state. Normal startup must not create schema or sample rows.
3. Stop the API, run the explicit Development seed and repeat it: expect 3 inserted rows, then 0. Restart the API and UI for the seeded checks. Use the README reset procedure only when intentionally discarding local synthetic data; reset is not part of automated verification.
4. Verify Compose bootstrap and the dedicated application login. Inspect privileges: it owns the development database but is not a superuser and cannot create roles or databases.
5. Trust the development certificate as documented. Use real Kestrel HTTPS at `https://localhost:7004` without `-k`, `SkipCertificateCheck` or another client-side TLS bypass.

Inspect the actual listeners and Compose publication independently:

```powershell
Get-NetTCPConnection -State Listen |
    Where-Object { $_.LocalPort -in 7004, 5173, 5432 } |
    Select-Object LocalAddress, LocalPort, OwningProcess
docker compose --env-file .env ps
```

- Match API/UI listeners to the correct processes; only `127.0.0.1` and/or `::1` are acceptable. Wildcard or LAN bindings fail the local boundary.
- Compose PostgreSQL publication must be exactly `127.0.0.1:5432`. Docker forwarding may not appear as an ordinary Windows process listener, so the listener query alone is insufficient.
- With Vite running, a second `npm --prefix ui run dev` must fail for occupied port 5173 rather than select 5174.
- TestServer and Host-filter tests do not prove real listener or TLS behavior. Vite's documented fixed-loopback `secure: false` proxy setting is not evidence of certificate validation; direct HTTPS checks must validate TLS.

| Request/check | Expected result |
| --- | --- |
| `/health` and `/health/ready` on Kestrel | `200 Healthy` while PostgreSQL and required schema are available. |
| Direct `GET /api/integration-runs` | Three persisted synthetic snapshots after fresh seed, with all required fields and parsed `Cache-Control: no-store`. |
| Proxied GET through `http://localhost:5173` | Same persisted data and `no-store` as the direct API response. |
| Request with `Host: untrusted.example` | HTTP 400 from the real listener. |
| Development `/openapi/v1.json` | List, by-ID and intake operations, required key, strict request body and response schemas. |
| Staging/Production OpenAPI | HTTP 404 in both environments; require the corresponding automated tests to pass and identify them as automated evidence. |

For the invalid-Host check, retain the trusted HTTPS URL and change only the header:

```powershell
curl.exe --silent --show-error --output NUL --write-out "%{http_code}\n" --header "Host: untrusted.example" https://localhost:7004/api/integration-runs
```

## Idempotent Intake

Use the [README intake example](../README.md#submit-a-run) with processing still disabled. Retain one fresh key and its synthetic request for replay checks.

| Step | Acceptance criterion |
| --- | --- |
| New key and valid body | POST 201; server-generated ID/receipt time, `Pending`, attemptCount 0, recordCount 0, null completedAt/lastError and resource `Location`. |
| Follow Location | GET 200 for the same ID and durable state. |
| Same key, equivalent input | POST 200 for the same ID; the list contains the run exactly once. |
| Same key, different operation | POST 409; the existing run is not overwritten. |
| Responses and UI | Create/replay/by-ID responses include parsed `no-store`; reload displays the new zero-attempt Pending row. |

## Processing and Restart

Stop the API first with Ctrl+C. Set the opt-in flag in that same terminal, then restart in Development as documented in [Background Processing](../README.md#background-processing):

```powershell
$env:Processing__Enabled = 'true'
dotnet run --launch-profile https --project src/IntegrationOps.Api/IntegrationOps.Api.csproj
```

Existing intake-origin Pending rows are now eligible. Submit a fresh key for each synthetic operation; each initial POST must return 201. Poll by ID or reload to observe these terminal outcomes:

| Operation | Terminal status | attemptCount | recordCount | lastError |
| --- | --- | --- | --- | --- |
| Ordinary operation | Succeeded | 1 | 1 | null |
| `Synthetic:RetryOnce` | Succeeded | 2 | 1 | null |
| `Synthetic:Fail` | Failed | 1 | 0 | `synthetic_permanent_failure` |
| `Synthetic:AlwaysRetry` | Failed | 3 | 0 | `attempt_exhausted` |

- Replay a completed run with the same key/body: expect 200, the same ID and current terminal state, not the original Pending snapshot.
- Verify that the three seeded snapshots remain unchanged, including the Pending snapshot without intake metadata. Reload the UI to inspect new lifecycle states.
- For restart from durable retry waiting, submit another `Synthetic:RetryOnce`. Observe `Pending`, attemptCount 1, recordCount 0 and `synthetic_retryable_failure`; stop the API before the retry is claimed, then restart with processing enabled. Expect the same ID to reach Succeeded on attempt 2 with recordCount 1 and null lastError.
- The retry window is short. If the stop misses it, do not claim that restart scenario was exercised; repeat with a fresh key. Record the observed pre-stop state and stop method.
- Retry waiting is not an active lease. Use deterministic database tests for interrupted allocations, expiry recovery and stale fencing; do not infer an active-lease manual interruption from the retry-waiting walkthrough or from `Pending` alone.
- These checks establish only the exercised database-state behavior, not exhaustive network-commit fault simulation or exactly-once external effects.
- After processing checks, stop the API and clear `$env:Processing__Enabled = $null` before restarting without processing or rerunning automated verification.

## Outage and Browser

Keep the API running while deliberately stopping or pausing only the local Compose PostgreSQL service. Retain its volume and stable endpoint.

| Condition | Acceptance criterion |
| --- | --- |
| Database unavailable | Liveness remains 200; readiness becomes 503; list GET returns generic 503 Problem Details with `application/problem+json` and `no-store`, not an empty successful list. |
| Safe failure | No SQL, credentials, connection details, host diagnostics or exception text is exposed. |
| Database restored | Start/unpause PostgreSQL and wait for health. The same API process recovers readiness and GET to 200 with persisted data intact. |

- Inspect the real browser at desktop and narrow/mobile widths. Check loading, empty and populated views, readable counters, clipping, overlap and horizontal overflow. Compare displayed timestamps with known UTC input while using a non-UTC browser timezone; the current component tests do not assert formatted UTC output.
- Inspect console and network behavior: successful static assets and API requests, expected cache policy and no unexplained errors. Distinguish expected failed requests during deliberate outage checks from unrelated failures.
- Stop the API while leaving the UI available, then reload. Verify the controlled unavailable view without diagnostic disclosure; restart the API and reload to confirm recovery.
- Verify keyboard navigation, focus, semantics and any other behavior needed to support public accessibility claims. Source markup and jsdom tests do not establish browser accessibility or layout.
- If browser access is unavailable, record those checks as unverified; source inspection is not a substitute.

## Source Hygiene

- Before publishing, inspect the complete proposed source inventory for credentials, tokens, private keys, credential-bearing connection strings, generated output and private machine paths. Keep local secrets/configuration out of published evidence.
- Use a named, versioned secret scanner with redacted output against the proposed source tree and the complete committed tree and history intended for publication. A directory scan does not establish history coverage.
- Verify ignore behavior against generated/secret-file examples and review the actual tracked-file inventory. Ignore rules do not remove already tracked content.
- Record scanner version, rules/scope, reviewed revision and redacted findings/resolutions. Resolve findings before publishing; a zero-result scan is defense in depth, not proof of secret absence.
- Verify that samples are fictional, original material can be licensed, and third-party/generated assets have traceable provenance. Exclude client, employer and production material.
- Check documentation links, commands and technical claims against the reviewed source and supported limits.

## Dependency Licenses

The 2026-09-08 package-metadata assessment used exact locked versions from official [npm](https://registry.npmjs.org/) and [NuGet](https://api.nuget.org/v3/index.json) metadata. It is a dated technical assessment, not a legal opinion, artifact SBOM or current vulnerability-audit result.

| Scope | Assessed inventory and license metadata |
| --- | --- |
| npm | 159 package-version records including optional platforms: MIT 131; MPL-2.0 12; Apache-2.0 5; ISC 3; BSD-2-Clause 2; BSD-3-Clause 2; MIT-0 2; BlueOak-1.0.0 1; CC0-1.0 1. |
| NuGet | 86 distinct locked versions across both projects: MIT 76; PostgreSQL 2; Apache-2.0 7; one legacy license-URL limitation below. |
| Local EF tool | `dotnet-ef` 10.0.11: MIT. |

- Original source is [MIT](../LICENSE); dependencies retain their own licenses. The assessment identified no incompatibility for original source plus dependency references, not blanket clearance for distributed artifacts.
- React/React DOM/Scheduler are MIT. Lightning CSS 1.33.0 and its 11 platform packages are MPL-2.0 build-only dependencies. Tool use does not relicense application CSS; tool redistribution requires applicable notices and covered-source obligations.
- `xunit.abstractions` 2.0.3 exposes a legacy publisher URL indicating Apache-2.0. Exact-version license text remains unresolved; do not claim it was verified from packaged license text.
- For shipped browser assets, .NET binaries or bundled tools, inspect actual contents and preserve applicable copyright, license and notice text. Package metadata does not enumerate all embedded components.
- A Compose image reference is not container redistribution. Redistributed PostgreSQL image layers include OS components with separate licenses and source-availability obligations; assess the actual image. Docker Desktop has separate usage terms.
- Reevaluate changed dependencies, generated assets and distribution scope using exact-version evidence. Do not inherit license conclusions from parent packages or treat this assessment as a future audit pass.

**AI-assisted:** Yes

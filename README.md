# URL Shortener + Governed Agentic Engineering System

A C# / ASP.NET Core (.NET 10) URL shortener, plus a governed orchestration layer in which OpenAI-backed
agents propose changes to that shortener. Nothing the model proposes is applied or trusted until
independent validation and human approval gates pass.

| Concern | Choice |
|---|---|
| Language / framework | C#, ASP.NET Core minimal APIs, **.NET 10** |
| Database | **SQLite** via **EF Core** (`Microsoft.EntityFrameworkCore.Sqlite` 10.0.x) |
| AI provider | **OpenAI API** through the official `OpenAI` .NET SDK (2.x), behind `IEngineerProvider` |
| Tests | **xUnit** (deterministic fakes by default; live OpenAI tests are opt-in) |
| Containers | Dockerfile + **Docker Compose** |

> **Cost and network note.** Real workflow runs and the live tests call the OpenAI API, which is billed
> separately by OpenAI. They need a valid `OPENAI_API_KEY` and outbound network access. Standard tests make
> no API calls.

## Solution layout

```
source/LinkForge.Persistence   EF Core + SQLite: link store, run repository, persistence models
source/LinkForge.Engine        Pipeline coordinator, OpenAI provider (AiProviders), safeguards
source/LinkForge.Service       HTTP surface, run dispatcher
testProjects/LinkForge.Tests       xUnit tests (+ test-only fakes)
docs/architecture.md        Design, graph, control flow, decisions, limitations
testScripts/                    demo.ps1, smoke.ps1
```

## Configuration

| Variable | Purpose | Default |
|---|---|---|
| `OPENAI_API_KEY` | OpenAI credential. **Environment only**; never logged, persisted or sent to child processes. | *(none)* |
| `OpenAI__Model` | Model for all stages (`OPENAI_MODEL` in Compose). | `gpt-4.1` |
| `OpenAI__FallbackModel` | One-shot fallback after the primary exhausts retries. | *(none)* |
| `OpenAI__MaxAttempts`, `OpenAI__RequestTimeoutSeconds` | Bounded retry budget and per-request timeout. | `3`, `90` |
| `DATA_DIRECTORY` | Folder for the SQLite file `linkforge.db`. | `./data` (`/data` in Docker) |
| `BASE_URL` | Base for returned short URLs. | `http://localhost:3000` |
| `Workflow__AllowFaultInjection` | Enables the `forceTestFailure` demo switch. | `false` (`true` in Development) |
| `Workspace__SourceRoot` | Source tree the validator copies into its sandbox. | auto-discovered (`/workspace` in Docker) |
| `Workspace__AllowOverwrite` | Lets a human-approved change replace existing `source/` files (never existing `testProjects/` files). | `false` (`true` in Compose) |

Put the key in a git-ignored `.env` (copy `.env.example`) or export it in your shell. `.env` is in `.gitignore`
and `.dockerignore`. Do not paste the key into source files.

## Run locally (Docker Compose)

```powershell
copy .env.example .env        # then set OPENAI_API_KEY in .env
docker compose up --build api # API on http://localhost:3000, SQLite in the linkforge-data volume
curl http://localhost:3000/health
```

The container uses the .NET **SDK** image on purpose: the validator runs `dotnet build` and `dotnet test`
against an isolated copy of the source. The compose file binds to loopback only; the API has no authentication
(out of scope), so do not expose it publicly.

Without Docker: `dotnet run --project source/LinkForge.Service` (Development profile, port 3000).

## Try it

Three scenarios are built in (`GET /api/scenarios`):

| Scenario | Goal | What it exercises |
|---|---|---|
| `greenfield` | Optional link expiry for the URL shortener | Full decomposition, new code and tests, both approval gates |
| `brownfield` | Harden click analytics without changing existing behavior | Existing-code context, additive-only change, regression focus |
| `ambiguous` | Define a safe, easier-to-share experience | Open questions vs assumptions, re-planning after clarification, release blocked on human review |

Behaviours you can exercise on any scenario:

| Behaviour | How to trigger it | Expected outcome |
|---|---|---|
| Happy path | `demo.ps1 -Scenario <name>`, approve both gates | `succeeded`, valid audit chain |
| Failure, retry, rollback, safe-stop | `POST /api/runs` with `{"scenarioId":"greenfield","forceTestFailure":true}` (needs `Workflow__AllowFaultInjection=true`, on in Development) | node retry, proposed change rolled back, run `safe_stopped` |
| Human rejection | Answer anything but `y` at the change-approval prompt, or `POST .../approval` with `"approved":false` | run `safe_stopped`, change discarded |
| Manual re-plan | `POST /api/runs/<id>/replan` while the run waits at a checkpoint | plan version bumped, downstream nodes invalidated, decision lineage recorded |
| Automatic re-plan | Happens once per run if pre-approval repair is exhausted | rollback, new plan version, graph restarts; a second exhaustion safe-stops |
| Operator stop | `POST /api/runs/<id>/stop` | run `safe_stopped` |
| Restart recovery | Restart the API during a run | waiting runs are restored; interrupted runs are safe-stopped, not resumed |

```powershell
./testScripts/smoke.ps1   # non-AI checks: URL API + governance endpoints (no OpenAI calls)
./testScripts/demo.ps1    # real AI run of a scenario; pauses for you to approve the change set
```

Manual flow:

```powershell
curl -X POST localhost:3000/api/urls -H "Content-Type: application/json" -d '{"url":"https://example.com","alias":"demo"}'
curl -X POST localhost:3000/api/runs -H "Content-Type: application/json" -d '{"scenarioId":"greenfield"}'
curl localhost:3000/api/runs/<id>                      # poll until status == waiting_approval
# read nodes["change-approval"].output.changeSetDigest, review the proposal, then:
curl -X POST localhost:3000/api/runs/<id>/approval -H "Content-Type: application/json" \
     -d '{"approved":true,"reason":"Reviewed","changeSetDigest":"<digest>"}'
```

Endpoints: `GET /health`, `POST|GET /api/urls`, `GET /{code}` (redirect), `GET /api/scenarios`,
`POST|GET /api/runs`, `GET /api/runs/{id}`, `/summary` (final engineering summary), `/evidence`, `/audit/verify`, `POST /api/runs/{id}/approval|replan|stop`
(optional `actor` field), `GET /api/metrics`, and the OpenAPI document at `GET /openapi/v1.json` (a copy is in `docs/openapi.json`).
There is no Swagger UI; to browse the endpoints, import the OpenAPI JSON into any OpenAPI viewer or Postman.

## Manual testing (local, without Docker)

1. **Run the automated suite first** (no API calls, no cost): `dotnet test`. Expect 0 failed; the 4 opt-in tests show as skipped.
2. **Start the API** in one terminal. Set the key in the shell (never in a file you commit). The default model is `gpt-4.1`
   (override with `OpenAI__Model`); enable overwrite so brownfield changes can replace existing files:

   ```powershell
   $env:ASPNETCORE_ENVIRONMENT = "Development"
   $env:OPENAI_API_KEY = "<your key>"
   $env:Workspace__AllowOverwrite = "true"
   dotnet run --project source/LinkForge.Service
   ```

   Check `http://localhost:3000/health`: `aiConfigured` must be `true` for live runs.
3. **Non-AI smoke test** (second terminal, free): `./testScripts/smoke.ps1`, expect "Smoke test passed."
4. **URL shortener by hand**: create a link with `POST /api/urls`, open `http://localhost:3000/<code>` to confirm the redirect,
   and list links with `GET /api/urls`.
5. **Live AI scenario** (billable): `./testScripts/demo.ps1 -Scenario greenfield|brownfield|ambiguous`.
   - The script prints the pipeline progress. At `change-approval` it shows the proposed files and asks
     `Approve this change set for sandbox validation? (y/N)`. Type `y` to approve; anything else rejects and safe-stops the run.
   - The release-readiness approval is sent automatically. Add `-AutoApprove` to skip the prompt entirely.
   - To approve by hand instead, use the `POST /api/runs/<id>/approval` call from the manual flow above.
6. **Read the outcome**: the run ends as `succeeded` or `safe_stopped`. Inspect `GET /api/runs/<id>/summary`,
   `/evidence`, `/audit/verify` and `GET /api/metrics`.

**What a safe-stop means:** a gate failed (for example the generated code did not build or pass tests after the bounded repair
rounds and one automatic re-plan), the model returned no usable change set, you rejected the change, or a non-retryable provider
error occurred. Nothing was applied or deployed. This is the designed behaviour, and because generated code depends on the model,
a live run can safe-stop; start a new run to try again.

**Where outputs live:** all stage outputs, artifact records, audit events and approvals are in the SQLite file
(`source/LinkForge.Service/data/linkforge.db` locally). Agents write no files into the source tree; a proposed change is applied
only to a temporary sandbox copy for validation, which is deleted afterwards.

## Tests

```powershell
dotnet test                                        # standard suite, no API calls
docker compose --profile test run --rm test         # same, in a container

# Opt-in, billable: real OpenAI calls (provider call + end-to-end governed workflow)
$env:OPENAI_API_KEY = "<your key>"; $env:RUN_LIVE_OPENAI_TESTS = "1"
dotnet test --filter FullyQualifiedName~LiveOpenAiTests
docker compose --profile live-tests run --rm live-tests

# Opt-in, slow: real sandbox `dotnet build` + `dotnet test` of a proposed change
$env:RUN_SANDBOX_TESTS = "1"; dotnet test --filter FullyQualifiedName~SandboxIntegration
```

Live and sandbox tests are reported as **skipped** (never passed) unless their switch is set. Deterministic
fakes (`FakeEngineerProvider`, `FakeChangeVerifier`) exist only in the test project and are never registered
by the application, so they cannot be mistaken for real AI execution.

## Sample run evidence

`docs/evidence/` holds the recorded outcome of one real run per scenario (live OpenAI calls, model `gpt-4.1`), captured with
`./testScripts/demo.ps1 -Scenario <name> -AutoApprove -SaveEvidence`. Each file has node statuses, metrics, every validation
check (including failed attempts), approvals with digests, artifact hashes, and the audit-chain verification. They contain no
secrets or machine paths and are not proof for future runs: model output varies between runs.

| Scenario | Outcome | Plan version | Agent calls | Retries | Rollbacks | Duration | Audit chain |
|---|---|---|---|---|---|---|---|
| `greenfield` | succeeded | 1 | 8 | 1 | 0 | 157 s | valid (24 events) |
| `brownfield` | succeeded | 2 (automatic re-plan) | 19 | 6 | 1 | 486 s | valid (46 events) |
| `ambiguous` | succeeded | 1 | 8 | 1 | 0 | 152 s | valid (24 events) |

Honest note: in the capture session the first greenfield attempt safe-stopped at the pre-approval build gate (the generated
change did not build after the bounded repair rounds), and only the second attempt succeeded and was recorded. This is the
designed behaviour and is why live success is never assumed.

## Requirement coverage

Where each core requirement of the assignment is implemented and tested.

| # | Requirement | Implementation | Test evidence |
|---|---|---|---|
| 1 | Requirement understanding | `requirements` stage: normalized requirement, assumptions, acceptance criteria and open questions (`ScenarioLibrary`, `OpenAiEngineerProvider`) | `HappyPath_PausesAtBothApprovalGatesAndSucceeds`; `ambiguous` scenario |
| 2 | Task decomposition | 10-node dependency graph with sequencing (`ScenarioLibrary`) | `ParallelBranches_RunConcurrently` |
| 3 | Codebase reasoning (brownfield) | `CodebaseContext` source map; `BlastRadiusAnalyzer` (dependents, endpoints, entities) | `BlastRadiusAnalyzerTests`, `CodebaseContextTests`; `brownfield` scenario |
| 4 | Workflow orchestration | `PipelineCoordinator`: entry/exit gates, parallel paths, decision lineage, approval checkpoints, bounded retries, fallback model, rollback, safe-stop, auto and manual re-plan, audit ledger, metrics | `PipelineCoordinatorTests` (e.g. `PersistentFailure_IsBoundedByRetryLimit`, `ValidationFailure_RetriesThenRollsBackAndSafeStops`, `Replan_InvalidatesDownstreamAndBumpsVersion`, `Stop_CancelsAnInFlightRun`, `Restart_RecoversWaitingRunsAndSafeStopsInterruptedOnes`, `AuditLedger_IsVerifiableAndDetectsTampering`, `Metrics_AggregateAcrossRuns`); `ProviderTests` (`ExhaustedRetries_TryFallbackModelExactlyOnce`, `RateLimit_HonoursRetryAfterHeader`, `HungProvider_TimesOutAsRetryableTimeout`) |
| 5 | Engineering output | Generated `.cs` changes and tests, OpenAPI at `/openapi/v1.json` (`docs/openapi.json`), architecture doc | `ApiTests`, `LinkStoreTests` |
| 6 | Validation and risk control | `ChangeRules` policy, `SandboxChangeVerifier` (`dotnet build` + `dotnet test` in a temp copy), pre-approval repair loop, member-preservation check | `SafeguardTests` (e.g. `OverwriteThatDropsExistingMembers_IsRejectedButFullReplacementPasses`), `PolicyViolatingProposal_FailsTheGateBeforeApproval`, opt-in `SandboxIntegrationTests` |
| 7 | Controlled autonomy | Two human gates, digest-bound approval, no apply/deploy | `Approval_RequiresMatchingDigest`, `Rejection_SafeStopsAndNothingIsValidated` |
| 8 | Final engineering summary | `GET /api/runs/{id}/summary`, derived from recorded facts | `Run_FlowsThroughApprovalsAndExposesEvidenceAuditAndMetrics` |

## Guardrails (summary)

- **Human approval** at the change set (bound to a SHA-256 digest of the exact files) and at release readiness.
- **Independent validation** of every change in a throw-away copy: policy, secret scan, forbidden APIs,
  `dotnet build`, `dotnet test`. This runs *before* the approval request too, with up to 3 bounded repair rounds that feed
  compiler/test output back to the model; if repair is exhausted the run re-plans once automatically, then safe-stops. A successful model response is never
  treated as proof of correctness.
- **Allowlisted execution** (`dotnet build|test` only, no shell, secrets stripped from the child environment).
- **No deploy / push / delete**: proposals are `.cs` files under fixed directories (existing `source/` files only with overwrite enabled, and an overwrite must keep every existing public/internal member or it is rejected and repaired); the real workspace is never modified.
- **Bounded resilience**: per-request timeout, exponential backoff with jitter honoring `Retry-After`, max attempts,
  one fallback-model try, error classification (rate limit, quota, auth, transient, timeout, invalid response).
- **Observability**: task IDs, plan versions, model/provider metadata, tokens, latency, outcomes, validation results,
  artifact SHA-256 references, and a tamper-evident hash-chained audit log. Prompts, responses and secrets are not logged.

See [docs/architecture.md](docs/architecture.md) for details, scenarios and known limitations.


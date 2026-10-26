# Architecture

## Components

```
Client ──HTTP──> LinkForge.Service
                   ├─ URL endpoints ──> SqliteLinkStore ───┐
                   └─ Run endpoints ──> PipelineCoordinator ─┼─> SqliteRunRepository ──> SQLite (EF Core)
                                            │         │
                                            ├─ IEngineerProvider = ResilientEngineerProvider ─> OpenAiEngineerProvider ─> OpenAI API
                                            ├─ IChangeVerifier  = SandboxChangeVerifier ─> AllowlistedProcessRunner (dotnet build|test)
                                            └─ ChangeRules, SecretScrubber, AuditLedger
```

- **LinkForge.Persistence**: EF Core entities and DbContext, `SqliteLinkStore` (the workload), `SqliteRunRepository`.
- **LinkForge.Engine**: pipeline coordinator and scenario library (`Pipeline/`), AI provider and resilience (`AiProviders/`), policy, sandbox verification, scrubbing and audit ledger (`Safeguards/`).
- **LinkForge.Service**: minimal API and run dispatcher.
- One SQLite file holds URL data and workflow state: runs (JSON payload), hash-chained audit events, agent calls,
  artifact metadata, validation results and approvals.

## Orchestration model

Each run is a dependency graph of nodes with entry gates, exit gates, retry limits and approval flags.

```
requirements → architecture → ┬ implementation ┬→ change-approval (HUMAN) → ┬ validation    ┬→ review → release-approval (HUMAN) → release-readiness
                              └ tests          ┘                           └ documentation ┘
```

- **Parallel paths**: `implementation ‖ tests` and `validation ‖ documentation` run concurrently (`Task.WhenAll`).
- **Entry gate**: a node starts only when all dependencies have `succeeded`.
- **Exit gate**: an artifact must satisfy the stage contract. Proposed files must also pass `ChangeRules`
  (≤6 files, ≤64 KB each, `.cs` only under `source/LinkForge.Service|Persistence` and `testProjects/LinkForge.Tests`, new files by default
  and existing `source/` files only when overwrite is enabled, no path traversal, no secrets, no forbidden APIs).
  An overwrite must keep every existing public/internal type and member of the file (`policy.preserves-existing-members`);
  a partial rewrite is rejected before approval and fed back to the implementation stage for repair.
  Implementation may only propose `source/` files and the testing stage only `testProjects/` files.
- **Retries**: node-level, bounded (`implementation`, `tests`, `validation`: 1 retry). Non-retryable provider errors
  (auth, quota, invalid request, not configured) stop immediately. Provider-level retries are separate and also bounded.
- **Rollback**: when a node fails after the change set exists, the proposed change nodes move to `rolled_back` and their
  output is discarded. The real workspace was never touched, so rollback means discarding the proposal.
- **Safe-stop**: retries exhausted, non-retryable failure, rejection, operator stop, or a restart that interrupts a run.
  Nothing is rolled forward and nothing is deployed.
- **Re-planning**: `POST /replan` at a checkpoint bumps the requirement version, invalidates all downstream nodes,
  and records the decision lineage. The coordinator also re-plans **automatically, once per run**, when pre-approval
  repair is exhausted: the change set is rolled back, the plan version is bumped, the last failure is appended to the
  requirements, and the graph restarts (lineage decision `auto-replan`). A second exhaustion safe-stops.
- **Impact analysis**: `BlastRadiusAnalyzer` (name-based static analysis of the existing source) reports, for the proposed
  change set, modified vs new files, direct and transitive dependents, affected HTTP endpoints and persisted entities.
  It is shown to the approver in the change-approval output. It is a heuristic, not a semantic proof.
- **Approvals**: approving the change set requires the pending `changeSetDigest`; validation re-checks the digest so a
  different change set cannot be validated under an earlier approval.

## Control flow of a run

1. `POST /api/runs` creates the run (202) and executes it in the background.
2. Agents produce requirements, architecture, an implementation proposal and test proposal (real OpenAI calls).
3. The merged change set is policy-checked and built/tested in the sandbox (with bounded repair on failure). Only then does
   the run pause at **change-approval** with the digest and file list.
4. A reviewer approves with the digest. Validation copies the workload to a temp directory, applies the files, runs
   `dotnet build` and `dotnet test`, then deletes the copy. Documentation runs in parallel.
5. Review summarises evidence honestly, then the run pauses at **release-approval**.
6. After approval, release-readiness produces a checklist. **Deployment is out of scope.**

## Scenarios

| Scenario | Goal | What it exercises |
|---|---|---|
| `greenfield` | Optional link expiry | Full decomposition, new code and tests, both approval gates |
| `brownfield` | Harden click analytics without changing behavior | Source-map context for existing code, additive-only change, regression focus |
| `ambiguous` | "Easier to share, while safe" | Open questions vs assumptions, replanning after clarification, release blocked on human review |

Each scenario can also be run with `forceTestFailure` (when enabled) to demonstrate retry → rollback → safe-stop.

| Behaviour on any scenario | Trigger | Outcome |
|---|---|---|
| Happy path | approve both gates | `succeeded`, valid audit chain |
| Forced failure | `forceTestFailure: true` (needs `Workflow__AllowFaultInjection`) | retry, rollback, `safe_stopped` |
| Human rejection | `approved: false` at a checkpoint | `safe_stopped`, change discarded |
| Manual re-plan | `POST /replan` at a checkpoint | plan version bumped, downstream invalidated, lineage recorded |
| Automatic re-plan | repair exhausted (once per run) | rollback, new plan version, restart; second exhaustion safe-stops |
| Operator stop | `POST /stop` | `safe_stopped` |
| Restart recovery | API restarted mid-run | waiting runs restored, interrupted runs safe-stopped |

## AI provider and resilience

- `IEngineerProvider` hides the provider. The application registers `ResilientEngineerProvider(OpenAiEngineerProvider)`.
- `OpenAiEngineerProvider` uses the official SDK `ChatClient` with a JSON-object response format. The SDK's own
  retries are disabled so every attempt is counted and bounded by our policy.
- `ResilientEngineerProvider`: per-attempt timeout, exponential backoff with jitter, `Retry-After` honored (capped),
  max attempts (≤6), one fallback-model attempt, no retry for non-transient errors, caller cancellation respected.
- Model output is untrusted: it is parsed against a bounded contract, secret-redacted before persistence, and gated.
- The model is configured only through `OpenAI:Model` (and `FallbackModel`).

## Observability and audit

Every event is appended to a per-run SHA-256 hash chain (`GET /api/runs/{id}/audit/verify` detects tampering).
Records include task IDs, node attempts, plan (requirement) versions, provider/model/response IDs, token counts,
latency, outcomes, validation checks, artifact hashes and approval decisions. Metrics (`GET /api/metrics`):
success rate, retries/run, rollbacks/run, mean time to recovery, end-to-end and active time, calls and tokens.

## Key decisions and trade-offs

- **SQLite + EF Core** (the mandated stack, and a good fit for a prototype): free, no database server to run, a single
  small file that ships easily in a Docker volume. SQLite serialises writers rather than writing concurrently, so WAL is
  enabled (readers are not blocked), writes are short and click counters use atomic `ExecuteUpdate`. A multi-instance
  production deployment would move to PostgreSQL or SQL Server (not done here).
- **`EnsureCreated`, not migrations**: simple for a prototype; schema changes require recreating the database.
- **Run state is held in memory while active** and persisted at each event. On restart, waiting runs are restored and
  interrupted runs are safe-stopped rather than resumed.
- **Change policy**: new `.cs` files by default. With `Workspace__AllowOverwrite=true` (set in `docker-compose.yml`), existing
  `source/` files may be replaced by full-file proposals; existing `testProjects/` files never. Overwrites are listed in the policy check
  and covered by the human-approved digest.
- **Pre-approval validation and bounded repair**: before asking for change approval, the merged change set is built and tested
  in the sandbox. On failure the compiler/test output is sent back to the stage that owns the failure (production-code
  errors repair the implementation, everything else the tests; the last round repairs both, in order) for up to 3 repair
  rounds. If it still fails, one automatic re-plan is attempted; after that the run safe-stops. The reviewer therefore
  approves only a change set that already built, and the digest covers the repaired content. Stages are repaired
  sequentially because parallel repair let one stage's fix break the other.
- **The validator image needs the SDK**, making the container larger than a runtime-only image.
- Validation does not establish a baseline build before applying the change.

## Known limitations

- Live OpenAI behavior depends on model quality: a generated change may fail validation, and the workflow then
  safe-stops by design. Live tests assert governed behavior rather than guaranteed success.
- Authentication and authorization are a deliberate scope decision: the assignment does not require them and the API is
  bound to loopback only. The approver name (`actor`) is therefore unauthenticated audit metadata. A shared deployment
  would add OIDC/JWT and role-based approvers (requester cannot approve their own change).
- Test doubles never stand in for AI in the live tests, but they do in the default suite by design.
- No deployment, push or release automation exists, by design.

## Final engineering summary

`GET /api/runs/{id}/summary` returns it, derived only from recorded facts (status, plan version, agent calls/models/tokens,
retries, rollbacks, MTTR, latest validation checks, approvals with digests, failed/incomplete nodes, assumptions, audit-chain
verification). Shape:

```
Run <id> (scenario, plan vN): status <succeeded|safe_stopped>
Agent calls: n (model, tokens in/out)   Retries: n   Rollbacks: n   MTTR: ms
Validation: build <pass/fail>, tests <pass/fail>, policy/security <pass/fail>
Approvals: change-set <actor, digest>, release <actor>
Open risks / questions: ...
Audit chain verified: <yes/no>
```

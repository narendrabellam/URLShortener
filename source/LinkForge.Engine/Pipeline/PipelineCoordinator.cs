using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinkForge;

public sealed class PipelineCoordinator(
    IRunRepository repository,
    IEngineerProvider agent,
    IChangeVerifier validator,
    ICodebaseContext workload,
    IOptions<WorkflowOptions> options,
    ILogger<PipelineCoordinator> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> ChangeSetNodes = [ScenarioLibrary.ImplementationNode, ScenarioLibrary.TestsNode];
    private static readonly HashSet<string> CodeAwareStages = ["architecture", "implementation", "testing"];

    private readonly ConcurrentDictionary<string, WorkflowRun> runs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> runGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> faultRuns = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim auditGate = new(1, 1);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        foreach (var payload in await repository.LoadRunsAsync(cancellationToken))
        {
            var run = DeserializeRun(payload);
            runs[run.Id] = run;
            if (run.Status != "running") continue;
            foreach (var node in run.Nodes.Values.Where(node => node.Status == "running"))
            {
                node.Status = "failed";
                node.Error = "Interrupted when the service restarted.";
            }
            await SafeStopAsync(run, "Process restarted while this run was active.", null, cancellationToken);
        }
    }

    public async Task<WorkflowRun> CreateRunAsync(
        string scenarioId, bool injectValidationFailure = false, CancellationToken cancellationToken = default)
    {
        var scenario = ScenarioLibrary.Find(scenarioId) ?? throw new WorkflowException(404, "Unknown scenario.");
        if (injectValidationFailure && !options.Value.AllowFaultInjection)
            throw new WorkflowException(400, "Fault injection is disabled (Workflow:AllowFaultInjection).");
        var run = new WorkflowRun
        {
            Id = Guid.NewGuid().ToString("N"),
            ScenarioId = scenario.Id,
            ScenarioTitle = scenario.Title,
            Requirements = scenario.Requirements,
            Assumptions = [.. scenario.Assumptions],
            AcceptanceCriteria = [.. scenario.AcceptanceCriteria],
            StartedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Nodes = scenario.Nodes.ToDictionary(node => node.Id, node => new WorkflowNodeState(node))
        };
        runs[run.Id] = run;
        if (injectValidationFailure) faultRuns[run.Id] = true;
        await RecordAsync(run, "run.started", new { scenarioId = scenario.Id, planVersion = 1 }, cancellationToken);
        return Snapshot(run);
    }

    /// <summary>Convenience for tests and CLI use: create and execute inline until the first checkpoint or end.</summary>
    public async Task<WorkflowRun> StartAsync(
        string scenarioId, bool injectValidationFailure = false, CancellationToken cancellationToken = default)
    {
        var created = await CreateRunAsync(scenarioId, injectValidationFailure, cancellationToken);
        return await ContinueAsync(created.Id, cancellationToken);
    }

    public WorkflowRun? Get(string runId) => runs.TryGetValue(runId, out var run) ? Snapshot(run) : null;

    public IReadOnlyList<WorkflowRun> List() =>
        runs.Values.OrderBy(run => run.StartedAt).Select(Snapshot).ToArray();

    public AggregateMetrics GetMetrics()
    {
        var all = runs.Values.ToArray();
        var terminal = all.Count(run => run.Status is "succeeded" or "safe_stopped");
        var succeeded = all.Count(run => run.Status == "succeeded");
        var recovered = all.Where(run => run.Metrics.Recoveries > 0).ToArray();
        var finished = all.Where(run => run.Status is "succeeded" or "safe_stopped").ToArray();
        return new AggregateMetrics(
            all.Length, succeeded, all.Count(run => run.Status == "safe_stopped"),
            all.Count(run => run.Status == "waiting_approval"), all.Count(run => run.Status == "running"),
            terminal == 0 ? 0 : (double)succeeded / terminal,
            all.Length == 0 ? 0 : all.Average(run => run.Metrics.Retries),
            all.Length == 0 ? 0 : all.Average(run => run.Metrics.Rollbacks),
            recovered.Length == 0 ? 0 : (long)recovered.Average(run => run.Metrics.MeanTimeToRecoveryMs),
            finished.Length == 0 ? 0 : (long)finished.Average(run => run.Metrics.DurationMs),
            finished.Length == 0 ? 0 : (long)finished.Average(run => run.Metrics.ActiveMs),
            all.Sum(run => run.Metrics.AgentCalls), all.Sum(run => run.Metrics.InputTokens),
            all.Sum(run => run.Metrics.OutputTokens));
    }

    public async Task<WorkflowRun> ContinueAsync(string runId, CancellationToken cancellationToken = default)
    {
        var gate = runGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var run = RequireRun(runId);
            using var runSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            active[runId] = runSource;
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await ExecuteAsync(run, runSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await SafeStopAsync(run, "Stopped by an operator.", null, CancellationToken.None);
            }
            catch (Exception error) when (error is not (OperationCanceledException or WorkflowException))
            {
                logger.LogError(error, "Workflow run {RunId} failed unexpectedly.", runId);
                await SafeStopAsync(run, "Unexpected internal error; run safe-stopped.", null, CancellationToken.None);
            }
            finally
            {
                lock (run) run.Metrics.ActiveMs += stopwatch.ElapsedMilliseconds;
                active.TryRemove(runId, out _);
            }
            return Snapshot(run);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<WorkflowRun> DecideAsync(
        string runId, ApprovalRequest request, CancellationToken cancellationToken = default)
    {
        var gate = runGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var run = RequireRun(runId);
            var checkpoint = run.Nodes.Values.SingleOrDefault(node => node.Status == "waiting_approval");
            if (checkpoint is null || run.Status != "waiting_approval")
                throw new WorkflowException(409, "This run is not waiting for an approval decision.");
            if (string.IsNullOrWhiteSpace(request.Actor) || request.Actor.Length > 128 ||
                request.Reason is { Length: > 1000 })
                throw new WorkflowException(400, "Actor is required (max 128 characters); reason must not exceed 1000 characters.");

            var digest = checkpoint.Id == ScenarioLibrary.ChangeApprovalNode ? ChangeSetDigest(run) : "";
            if (request.Approved && digest.Length > 0)
            {
                if (string.IsNullOrWhiteSpace(request.ChangeSetDigest))
                    throw new WorkflowException(400, "changeSetDigest is required to approve a change set.");
                if (!string.Equals(request.ChangeSetDigest.Trim(), digest, StringComparison.OrdinalIgnoreCase))
                    throw new WorkflowException(409, "changeSetDigest does not match the pending change set; re-review the current proposal.");
            }

            var actor = request.Actor.Trim();
            var decidedAt = DateTimeOffset.UtcNow;
            var approval = new { approved = request.Approved, actor, reason = request.Reason ?? "", at = decidedAt, changeSetDigest = digest };
            checkpoint.Status = request.Approved ? "succeeded" : "rejected";
            checkpoint.Output = JsonSerializer.SerializeToElement(approval, JsonOptions);
            lock (run)
                run.DecisionLineage.Add(JsonSerializer.SerializeToElement(new { nodeId = checkpoint.Id, planVersion = run.RequirementVersion, approval }, JsonOptions));
            await repository.AddApprovalAsync(new ApprovalEntity
            {
                RunId = run.Id, NodeId = checkpoint.Id, PlanVersion = run.RequirementVersion,
                Approved = request.Approved, Actor = actor, Reason = SecretScrubber.Redact(request.Reason),
                ChangeSetDigest = digest, DecidedAt = decidedAt
            }, cancellationToken);
            await RecordAsync(run, request.Approved ? "approval.granted" : "approval.rejected",
                new { nodeId = checkpoint.Id, actor, reason = SecretScrubber.Redact(request.Reason), changeSetDigest = digest },
                cancellationToken);
            if (!request.Approved)
            {
                await SafeStopAsync(run, "Rejected at an approval checkpoint.", checkpoint.Id, cancellationToken);
                return Snapshot(run);
            }
            run.Status = "running";
            return Snapshot(run);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Inline decide-and-continue, for tests and scripts.</summary>
    public async Task<WorkflowRun> ApproveAsync(
        string runId, ApprovalRequest request, CancellationToken cancellationToken = default)
    {
        var decided = await DecideAsync(runId, request, cancellationToken);
        return decided.Status == "running" ? await ContinueAsync(runId, cancellationToken) : decided;
    }

    public async Task<WorkflowRun> PrepareReplanAsync(
        string runId, ReplanRequest request, CancellationToken cancellationToken = default)
    {
        var gate = runGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var run = RequireRun(runId);
            if (run.Status is not ("waiting_approval" or "succeeded" or "safe_stopped"))
                throw new WorkflowException(409, "Replanning is allowed only at a paused or completed checkpoint.");
            if (string.IsNullOrWhiteSpace(request.Requirements) || request.Requirements.Length > 4000)
                throw new WorkflowException(400, "Updated requirements must be 1-4000 characters.");

            await ApplyReplanAsync(run, request.Requirements.Trim(), "upstream-change", cancellationToken);
            return Snapshot(run);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Bumps the plan version, invalidates every downstream node and records the lineage. Caller holds the run gate.</summary>
    private async Task ApplyReplanAsync(WorkflowRun run, string requirements, string decision, CancellationToken cancellationToken)
    {
        var oldVersion = run.RequirementVersion;
        var invalidated = Descendants(run, ScenarioLibrary.RequirementsNode);
        run.Requirements = requirements;
        run.RequirementVersion++;
        foreach (var nodeId in new[] { ScenarioLibrary.RequirementsNode }.Concat(invalidated))
        {
            var node = run.Nodes[nodeId];
            node.Status = "pending";
            node.Attempts = 0;
            node.Output = null;
            node.Error = null;
            node.ErrorKind = null;
            node.FirstFailureAt = null;
        }
        lock (run)
            run.DecisionLineage.Add(JsonSerializer.SerializeToElement(new
            {
                nodeId = ScenarioLibrary.RequirementsNode, decision,
                fromVersion = oldVersion, toVersion = run.RequirementVersion, invalidated
            }, JsonOptions));
        run.Status = "running";
        await RecordAsync(run, "run.replanned",
            new { fromVersion = oldVersion, toVersion = run.RequirementVersion, invalidated, trigger = decision }, cancellationToken);
    }

    public async Task<WorkflowRun> ReplanAsync(
        string runId, ReplanRequest request, CancellationToken cancellationToken = default)
    {
        await PrepareReplanAsync(runId, request, cancellationToken);
        return await ContinueAsync(runId, cancellationToken);
    }

    /// <summary>Safe-stop control: halts a running or waiting run. Nothing is deployed or rolled forward.</summary>
    public async Task<WorkflowRun> StopAsync(
        string runId, string actor, string? reason, CancellationToken cancellationToken = default)
    {
        var run = RequireRun(runId);
        if (string.IsNullOrWhiteSpace(actor) || reason is { Length: > 1000 })
            throw new WorkflowException(400, "Actor is required; reason must not exceed 1000 characters.");
        if (active.TryGetValue(runId, out var source))
        {
            await RecordAsync(run, "run.stop_requested", new { actor = actor.Trim(), reason = SecretScrubber.Redact(reason) }, cancellationToken);
            source.Cancel();
            return Snapshot(run);
        }
        var gate = runGates.GetOrAdd(runId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (run.Status != "waiting_approval")
                throw new WorkflowException(409, "Only a running or waiting run can be stopped.");
            await RecordAsync(run, "run.stop_requested", new { actor = actor.Trim(), reason = SecretScrubber.Redact(reason) }, cancellationToken);
            await SafeStopAsync(run, "Stopped by an operator.", null, cancellationToken);
            return Snapshot(run);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<(bool Valid, int? BrokenAtSequence, int Events)> VerifyAuditAsync(
        string runId, CancellationToken cancellationToken = default)
    {
        RequireRun(runId);
        var events = await repository.GetAuditAsync(runId, cancellationToken);
        var (valid, broken) = AuditLedger.Verify(events);
        return (valid, broken, events.Count);
    }

    public async Task<object> GetEvidenceAsync(string runId, CancellationToken cancellationToken = default)
    {
        RequireRun(runId);
        return new
        {
            agentCalls = await repository.GetAgentCallsAsync(runId, cancellationToken),
            artifacts = await repository.GetArtifactsAsync(runId, cancellationToken),
            validations = await repository.GetValidationsAsync(runId, cancellationToken),
            approvals = await repository.GetApprovalsAsync(runId, cancellationToken)
        };
    }

    /// <summary>Final engineering summary derived only from recorded facts (nodes, validations, approvals, audit chain).</summary>
    public async Task<object> GetSummaryAsync(string runId, CancellationToken cancellationToken = default)
    {
        var run = RequireRun(runId);
        var validations = await repository.GetValidationsAsync(runId, cancellationToken);
        var approvals = await repository.GetApprovalsAsync(runId, cancellationToken);
        var calls = await repository.GetAgentCallsAsync(runId, cancellationToken);
        var audit = await VerifyAuditAsync(runId, cancellationToken);
        var snapshot = Snapshot(run);

        var latestChecks = validations
            .GroupBy(v => v.Check)
            .Select(g => g.OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id).First())
            .Select(v => new { check = v.Check, passed = v.Passed, node = v.NodeId, details = v.Details })
            .ToArray();
        var failedNodes = snapshot.Nodes.Values.Where(n => n.Status is "failed" or "rolled_back")
            .Select(n => new { node = n.Id, status = n.Status, errorKind = n.ErrorKind, error = n.Error }).ToArray();
        var pending = snapshot.Nodes.Values.Where(n => n.Status is not "succeeded")
            .Select(n => new { node = n.Id, status = n.Status }).ToArray();

        return new
        {
            runId,
            scenario = snapshot.ScenarioId,
            planVersion = snapshot.RequirementVersion,
            status = snapshot.Status,
            startedAt = snapshot.StartedAt,
            durationMs = snapshot.Metrics.DurationMs,
            agentCalls = new
            {
                count = calls.Count,
                models = calls.Select(c => c.Model).Distinct().ToArray(),
                inputTokens = snapshot.Metrics.InputTokens,
                outputTokens = snapshot.Metrics.OutputTokens,
                fallbackUsed = calls.Any(c => c.UsedFallbackModel)
            },
            retries = snapshot.Metrics.Retries,
            rollbacks = snapshot.Metrics.Rollbacks,
            meanTimeToRecoveryMs = snapshot.Metrics.MeanTimeToRecoveryMs,
            validation = new
            {
                checks = latestChecks,
                allPassed = latestChecks.Length > 0 && latestChecks.All(c => c.passed) && failedNodes.Length == 0
            },
            approvals = approvals.Select(a => new
            {
                node = a.NodeId, approved = a.Approved, actor = a.Actor, digest = a.ChangeSetDigest, decidedAt = a.DecidedAt
            }).ToArray(),
            failedNodes,
            incompleteNodes = pending,
            plan = new
            {
                requirements = snapshot.Requirements,
                acceptanceCriteria = snapshot.AcceptanceCriteria,
                graph = snapshot.Nodes.Values.Select(n => new { node = n.Id, n.Stage, dependsOn = n.DependsOn, status = n.Status, attempts = n.Attempts }).ToArray(),
                decisionLineage = snapshot.DecisionLineage
            },
            artifacts = (await repository.GetArtifactsAsync(runId, cancellationToken))
                .Select(a => new { node = a.NodeId, a.Kind, sha256 = a.Sha256, a.SizeBytes, planVersion = a.PlanVersion }).ToArray(),
            limitations = new[]
            {
                "Release is readiness-only; nothing is deployed or pushed.",
                "Model output is nondeterministic; a proposal that fails validation safe-stops the run.",
                "SQLite is single-writer; the schema is created with EnsureCreated, not migrations."
            },
            openAssumptions = snapshot.Assumptions,
            auditChain = new { verified = audit.Valid, events = audit.Events, brokenAtSequence = audit.BrokenAtSequence },
            note = "Derived from recorded events only. A successful model response is not proof of correctness; see validation checks."
        };
    }

    private async Task ExecuteAsync(WorkflowRun run, CancellationToken cancellationToken)
    {
        run.Status = "running";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ready = run.Nodes.Values.Where(node =>
                node.Status == "pending" &&
                node.DependsOn.All(dependency => run.Nodes[dependency].Status == "succeeded")).ToArray();

            if (ready.Length == 0)
            {
                if (run.Nodes.Values.Any(node => node.Status == "waiting_approval"))
                {
                    run.Status = "waiting_approval";
                    await RecordAsync(run, "run.waiting", new { }, cancellationToken);
                    return;
                }
                if (run.Nodes.Values.All(node => node.Status == "succeeded"))
                {
                    run.Status = "succeeded";
                    Finish(run);
                    await RecordAsync(run, "run.succeeded", new { planVersion = run.RequirementVersion }, cancellationToken);
                    return;
                }
                var reason = run.Nodes.Values.Any(node => node.Status is "failed" or "rolled_back")
                    ? "A required dependency did not pass."
                    : "Workflow execution stalled without a valid next step.";
                await SafeStopAsync(run, reason, null, cancellationToken);
                return;
            }

            var approvalNode = ready.FirstOrDefault(node => node.ApprovalRequired);
            var parallelNodes = ready.Where(node => !node.ApprovalRequired).ToArray();
            await Task.WhenAll(parallelNodes.Select(node => ExecuteNodeAsync(run, node, cancellationToken)));
            if (run.Status == "safe_stopped") return;
            if (approvalNode is not null)
            {
                if (approvalNode.Id == ScenarioLibrary.ChangeApprovalNode &&
                    (await CombinedChangeSetFailsPolicyAsync(run, approvalNode, cancellationToken) ||
                     await PreValidationFailsAsync(run, approvalNode, cancellationToken)))
                {
                    if (run.Status == "safe_stopped") return;
                    continue; // re-planned automatically: the graph restarts from the new plan version
                }
                var digest = approvalNode.Id == ScenarioLibrary.ChangeApprovalNode ? ChangeSetDigest(run) : "";
                var changeFiles = ChangeSetFiles(run);
                var impact = approvalNode.Id == ScenarioLibrary.ChangeApprovalNode
                    ? workload.Impact([.. changeFiles.Select(file => file.Path)])
                    : BlastRadiusReport.Empty;
                approvalNode.Status = "waiting_approval";
                approvalNode.Output = JsonSerializer.SerializeToElement(new
                {
                    changeSetDigest = digest,
                    files = changeFiles.Select(file => new { file.Path, characters = file.Content.Length }),
                    impact
                }, JsonOptions);
                run.Status = "waiting_approval";
                await RecordAsync(run, "approval.requested", new
                {
                    nodeId = approvalNode.Id, highImpact = approvalNode.HighImpact,
                    approvalNode.EntryGate, approvalNode.ExitGate, changeSetDigest = digest,
                    impactedFiles = impact.DirectDependents.Length + impact.TransitiveDependents.Length,
                    affectedEndpoints = impact.AffectedEndpoints.Length
                }, cancellationToken);
                return;
            }
        }
    }

    /// <summary>Policy-checks the merged implementation+tests proposal so a reviewer is never asked to approve one that cannot pass.</summary>
    private async Task<bool> CombinedChangeSetFailsPolicyAsync(
        WorkflowRun run, WorkflowNodeState approvalNode, CancellationToken cancellationToken)
    {
        var checks = ChangeRules.Evaluate(ChangeSetFiles(run));
        await repository.AddValidationsAsync(checks.Select(check => ToEntity(run, approvalNode, check)), cancellationToken);
        var failed = checks.Where(check => !check.Passed).ToArray();
        if (failed.Length == 0) return false;

        approvalNode.Status = "failed";
        approvalNode.ErrorKind = "gate";
        approvalNode.Error = "Combined change set failed policy: " +
            string.Join("; ", failed.Select(check => $"{check.Name}: {check.Details}"));
        await RecordAsync(run, "node.failed", new
        {
            nodeId = approvalNode.Id, classification = "gate", retryable = false, error = approvalNode.Error
        }, cancellationToken);
        await RollbackAsync(run, approvalNode, cancellationToken);
        await SafeStopAsync(run, "Combined change set failed policy before approval.", approvalNode.Id, cancellationToken);
        return true;
    }

    private const int MaximumRepairRounds = 3;
    private const int MaximumAutoReplans = 1;
    private const string AutoReplanDecision = "auto-replan";

    private static int AutoReplanCount(WorkflowRun run)
    {
        lock (run)
            return run.DecisionLineage.Count(entry =>
                entry.TryGetProperty("decision", out var decision) && decision.GetString() == AutoReplanDecision);
    }

    /// <summary>
    /// Builds and tests the merged proposal in the sandbox BEFORE asking a human to approve it. On failure the real
    /// compiler/test output is fed back to the change-set stages for a bounded number of repair rounds, so the reviewer
    /// approves a change set that has already built. The approved digest therefore always covers the repaired content.
    /// </summary>
    private async Task<bool> PreValidationFailsAsync(
        WorkflowRun run, WorkflowNodeState approvalNode, CancellationToken cancellationToken)
    {
        for (var round = 0; ; round++)
        {
            var report = await validator.ValidateAsync(ChangeSetFiles(run), cancellationToken);
            await repository.AddValidationsAsync(report.Checks.Select(check => ToEntity(run, approvalNode, check)), cancellationToken);
            if (report.Passed) return false;

            var failedChecks = report.Checks.Where(check => !check.Passed).ToArray();
            var summary = SecretScrubber.Redact(string.Join("\n", failedChecks.Select(check => $"{check.Name}: {check.Details}")));
            if (round >= MaximumRepairRounds)
            {
                if (AutoReplanCount(run) < MaximumAutoReplans)
                {
                    await RollbackAsync(run, approvalNode, cancellationToken);
                    var lesson = summary.Length > 700 ? summary[..700] : summary;
                    var baseText = run.Requirements.Length > 3000 ? run.Requirements[..3000] : run.Requirements;
                    await ApplyReplanAsync(run,
                        baseText + "\n\n[Automatic re-plan] The previous plan could not be made to build and pass tests. " +
                        "Prefer the smallest, most additive design that reuses existing members exactly as declared. Last failure:\n" + lesson,
                        AutoReplanDecision, cancellationToken);
                    return true;
                }

                approvalNode.Status = "failed";
                approvalNode.ErrorKind = "gate";
                approvalNode.Error = "Pre-approval validation failed after repair: " + string.Join(", ", failedChecks.Select(check => check.Name));
                await RecordAsync(run, "node.failed", new
                {
                    nodeId = approvalNode.Id, classification = "gate", retryable = false, error = approvalNode.Error
                }, cancellationToken);
                await RollbackAsync(run, approvalNode, cancellationToken);
                await SafeStopAsync(run, "Change set did not build or pass tests before approval.", approvalNode.Id, cancellationToken);
                return true;
            }

            await RecordAsync(run, "repair.requested", new
            {
                round = round + 1, failedChecks = failedChecks.Select(check => check.Name)
            }, cancellationToken);
            lock (run) run.Metrics.Retries++;
            var previous = ChangeSetNodes.ToDictionary(id => id, id => run.Nodes[id].Output);
            try
            {
                var feedback = summary.Length > 8000 ? summary[^8000..] : summary;
                // Repair one stage at a time: fixing both in parallel let each undo the other's fix.
                foreach (var id in StagesToRepair(feedback, round))
                {
                    await RepairNodeAsync(run, run.Nodes[id], feedback, previous, cancellationToken);
                    previous[id] = run.Nodes[id].Output; // the next stage sees the repaired sibling
                }
            }
            catch (Exception error) when (error is AgentException or GateFailedException)
            {
                approvalNode.Status = "failed";
                approvalNode.ErrorKind = "gate";
                approvalNode.Error = "Repair attempt failed: " + SecretScrubber.Redact(error.Message);
                await RecordAsync(run, "node.failed", new
                {
                    nodeId = approvalNode.Id, classification = "gate", retryable = false, error = approvalNode.Error
                }, cancellationToken);
                await RollbackAsync(run, approvalNode, cancellationToken);
                await SafeStopAsync(run, "Repair attempt failed before approval.", approvalNode.Id, cancellationToken);
                return true;
            }

            if (await CombinedChangeSetFailsPolicyAsync(run, approvalNode, cancellationToken)) return true;
        }
    }

    /// <summary>Production-code errors repair the implementation first; anything else repairs the tests. The last round repairs both, in order.</summary>
    private static IEnumerable<string> StagesToRepair(string feedback, int round)
    {
        if (round >= MaximumRepairRounds - 1) return [ScenarioLibrary.ImplementationNode, ScenarioLibrary.TestsNode];
        var productionError = feedback.Contains("policy.preserves-existing-members", StringComparison.Ordinal)
            || Regex.IsMatch(feedback, @"(^|[\s'""(])source[\\/][^\s:(]+\.cs\(\d+,\d+\): error");
        return [productionError ? ScenarioLibrary.ImplementationNode : ScenarioLibrary.TestsNode];
    }

    private async Task RepairNodeAsync(
        WorkflowRun run, WorkflowNodeState node, string feedback,
        IReadOnlyDictionary<string, JsonElement?> previous, CancellationToken cancellationToken)
    {
        node.Attempts++;
        await RunAgentAsync(run, node, cancellationToken, feedback, previous);
        await RecordAsync(run, "node.repaired", new { nodeId = node.Id, taskId = node.TaskId, attempt = node.Attempts }, cancellationToken);
    }

    private async Task ExecuteNodeAsync(WorkflowRun run, WorkflowNodeState node, CancellationToken cancellationToken)
    {
        node.Status = "running";
        node.TaskId = Guid.NewGuid().ToString("N");
        await RecordAsync(run, "node.started", new
        {
            nodeId = node.Id, taskId = node.TaskId, stage = node.Stage, node.EntryGate, planVersion = run.RequirementVersion
        }, cancellationToken);

        var maximumAttempts = node.RetryLimit + 1;
        while (node.Attempts < maximumAttempts)
        {
            node.Attempts++;
            try
            {
                if (node.Id == ScenarioLibrary.ValidationNode) await RunValidationAsync(run, node, cancellationToken);
                else await RunAgentAsync(run, node, cancellationToken);

                node.Status = "succeeded";
                node.Error = null;
                node.ErrorKind = null;
                if (node.FirstFailureAt is { } firstFailure)
                    lock (run)
                    {
                        run.Metrics.Recoveries++;
                        run.Metrics.RecoveryMsTotal += (long)(DateTimeOffset.UtcNow - firstFailure).TotalMilliseconds;
                    }
                await RecordAsync(run, "node.succeeded",
                    new { nodeId = node.Id, taskId = node.TaskId, attempt = node.Attempts, node.ExitGate }, cancellationToken);
                return;
            }
            catch (Exception error) when (error is AgentException or GateFailedException)
            {
                var kind = error is AgentException agentError ? agentError.Kind.ToString() : "gate";
                var retryable = error is not AgentException { IsNodeRetryable: false };
                node.Error = SecretScrubber.Redact(error.Message);
                node.ErrorKind = kind;
                node.FirstFailureAt ??= DateTimeOffset.UtcNow;
                if (retryable && node.Attempts < maximumAttempts)
                {
                    lock (run) run.Metrics.Retries++;
                    await RecordAsync(run, "node.retry", new
                    {
                        nodeId = node.Id, taskId = node.TaskId, attempt = node.Attempts, maximumAttempts,
                        classification = kind, error = node.Error
                    }, cancellationToken);
                    continue;
                }

                node.Status = "failed";
                await RecordAsync(run, "node.failed", new
                {
                    nodeId = node.Id, taskId = node.TaskId, classification = kind, retryable, error = node.Error
                }, cancellationToken);
                await RollbackAsync(run, node, cancellationToken);
                await SafeStopAsync(run, retryable ? "Retry budget exhausted." : $"Non-retryable failure ({kind}).", node.Id, cancellationToken);
                return;
            }
        }
    }

    private async Task RunAgentAsync(
        WorkflowRun run, WorkflowNodeState node, CancellationToken cancellationToken,
        string? repairFeedback = null, IReadOnlyDictionary<string, JsonElement?>? previousOutputs = null)
    {
        var started = DateTimeOffset.UtcNow;
        var request = CreateAgentRequest(run, node);
        if (repairFeedback is not null)
            request = request with
            {
                RepairFeedback = repairFeedback,
                PreviousFiles = FilesOf(previousOutputs?.GetValueOrDefault(node.Id)),
                SiblingFiles = [.. ChangeSetNodes.Where(id => id != node.Id).SelectMany(id => FilesOf(previousOutputs?.GetValueOrDefault(id)))]
            };
        AgentResult result;
        try
        {
            result = await agent.GenerateAsync(request, cancellationToken);
        }
        catch (AgentException error)
        {
            await repository.AddAgentCallAsync(NewCall(run, node, started, "failed", error.Kind.ToString()), cancellationToken);
            throw;
        }

        var artifact = Sanitize(result.Artifact);
        if (repairFeedback is not null)
        {
            // During repair each node owns only its own half of the change set; foreign files are dropped, and a node
            // that has nothing to fix keeps its previous files.
            var ownsTests = node.Id == ScenarioLibrary.TestsNode;
            var own = artifact.Files.Where(file => file.Path.StartsWith("testProjects/", StringComparison.Ordinal) == ownsTests).ToArray();
            artifact = artifact with { ProposedFiles = own.Length > 0 ? own : request.PreviousFiles ?? [] };
        }
        if (repairFeedback is not null && artifact.Tests.Length == 0 && node.Output is { } before &&
            before.TryGetProperty("tests", out var priorTests) && priorTests.ValueKind == JsonValueKind.Array)
            artifact = artifact with { Tests = [.. priorTests.EnumerateArray().Select(item => item.GetString() ?? "")] };
        var call = NewCall(run, node, started, "completed", null);
        call.Provider = result.Metadata.Provider;
        call.Model = result.Metadata.Model;
        call.ResponseId = result.Metadata.ResponseId;
        call.ProviderAttempts = result.Metadata.ProviderAttempts;
        call.UsedFallbackModel = result.Metadata.UsedFallbackModel;
        call.InputTokens = result.Metadata.InputTokens;
        call.OutputTokens = result.Metadata.OutputTokens;
        call.LatencyMs = result.Metadata.LatencyMs;
        await repository.AddAgentCallAsync(call, cancellationToken);
        lock (run)
        {
            run.Metrics.AgentCalls++;
            run.Metrics.InputTokens += result.Metadata.InputTokens;
            run.Metrics.OutputTokens += result.Metadata.OutputTokens;
        }

        await EvaluateExitGateAsync(run, node, artifact, cancellationToken);
        var output = JsonSerializer.SerializeToElement(artifact, JsonOptions);
        node.Output = output;
        var raw = output.GetRawText();
        await repository.AddArtifactAsync(new ArtifactEntity
        {
            RunId = run.Id, NodeId = node.Id, PlanVersion = run.RequirementVersion, Kind = node.Stage,
            Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant(),
            SizeBytes = Encoding.UTF8.GetByteCount(raw), CreatedAt = DateTimeOffset.UtcNow
        }, cancellationToken);
    }

    private async Task EvaluateExitGateAsync(
        WorkflowRun run, WorkflowNodeState node, EngineeringArtifact artifact, CancellationToken cancellationToken)
    {
        if (node.Id == ScenarioLibrary.TestsNode && (artifact.Tests.Length == 0 || artifact.Tests.Any(string.IsNullOrWhiteSpace)))
            throw new GateFailedException("Testing stage did not produce concrete test checks.");

        var files = artifact.Files;
        if (node.Id == ScenarioLibrary.ImplementationNode && files.Length == 0)
            throw new GateFailedException("Implementation stage produced no change set.");
        if (files.Length == 0) return;

        const string testsPrefix = "testProjects/";
        if (node.Id == ScenarioLibrary.ImplementationNode &&
            files.Any(file => file.Path.StartsWith(testsPrefix, StringComparison.Ordinal)))
            throw new GateFailedException("Implementation stage must not propose test files; the testing stage owns testProjects/.");
        if (node.Id == ScenarioLibrary.TestsNode &&
            files.Any(file => !file.Path.StartsWith(testsPrefix, StringComparison.Ordinal)))
            throw new GateFailedException("Testing stage may only propose files under testProjects/.");

        var checks = ChangeRules.Evaluate(files);
        await repository.AddValidationsAsync(checks.Select(check => ToEntity(run, node, check)), cancellationToken);
        var failed = checks.Where(check => !check.Passed).ToArray();
        if (failed.Length > 0)
            throw new GateFailedException("Change policy failed: " + string.Join("; ", failed.Select(check => $"{check.Name}: {check.Details}")));
    }

    private async Task RunValidationAsync(WorkflowRun run, WorkflowNodeState node, CancellationToken cancellationToken)
    {
        if (faultRuns.ContainsKey(run.Id)) throw new GateFailedException("Injected deterministic validation failure.");
        var files = ChangeSetFiles(run);
        var approved = run.Nodes[ScenarioLibrary.ChangeApprovalNode].Output?.TryGetProperty("changeSetDigest", out var value) == true
            ? value.GetString() : null;
        var digest = ChangeRules.Digest(files);
        if (!string.Equals(approved, digest, StringComparison.Ordinal))
            throw new GateFailedException("Change set differs from the digest a human approved.");

        var report = await validator.ValidateAsync(files, cancellationToken);
        await repository.AddValidationsAsync(report.Checks.Select(check => ToEntity(run, node, check)), cancellationToken);
        node.Output = JsonSerializer.SerializeToElement(new
        {
            passed = report.Passed,
            changeSetDigest = digest,
            checks = report.Checks.Select(check => new
            {
                check.Name, check.Passed, details = SecretScrubber.Redact(check.Details), check.DurationMs
            })
        }, JsonOptions);
        if (!report.Passed)
            throw new GateFailedException("Validation failed: " +
                string.Join(", ", report.Checks.Where(check => !check.Passed).Select(check => check.Name)));
    }

    private EngineerProviderRequest CreateAgentRequest(WorkflowRun run, WorkflowNodeState node)
    {
        var inputs = node.DependsOn.ToDictionary(
            dependency => dependency,
            dependency => run.Nodes[dependency].Output?.Clone() ?? JsonSerializer.SerializeToElement<object?>(null, JsonOptions),
            StringComparer.Ordinal);
        return new EngineerProviderRequest(
            run.ScenarioId, run.ScenarioTitle, node.Stage, run.Requirements, run.RequirementVersion,
            [.. run.Assumptions], [.. run.AcceptanceCriteria], inputs,
            CodeAwareStages.Contains(node.Stage) ? workload.Describe() : null);
    }

    private static AgentCallEntity NewCall(WorkflowRun run, WorkflowNodeState node, DateTimeOffset started, string outcome, string? errorKind) =>
        new()
        {
            RunId = run.Id, TaskId = node.TaskId ?? "", NodeId = node.Id, PlanVersion = run.RequirementVersion,
            NodeAttempt = node.Attempts, Provider = "openai", Model = "", Outcome = outcome, ErrorKind = errorKind,
            StartedAt = started, LatencyMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds
        };

    private static ValidationEntity ToEntity(WorkflowRun run, WorkflowNodeState node, ValidationCheck check) => new()
    {
        RunId = run.Id, NodeId = node.Id, PlanVersion = run.RequirementVersion, Check = check.Name,
        Passed = check.Passed, Details = SecretScrubber.Redact(check.Details), DurationMs = check.DurationMs,
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static EngineeringArtifact Sanitize(EngineeringArtifact artifact)
    {
        static string[] Clean(string[] values) => values.Select(SecretScrubber.Redact).ToArray();
        return artifact with
        {
            Summary = SecretScrubber.Redact(artifact.Summary), Artifacts = Clean(artifact.Artifacts),
            Risks = Clean(artifact.Risks), OpenQuestions = Clean(artifact.OpenQuestions),
            ProposedChanges = Clean(artifact.ProposedChanges), Tests = Clean(artifact.Tests),
            Decisions = Clean(artifact.Decisions), ProposedFiles = artifact.ProposedFiles ?? []
        };
    }

    private static IReadOnlyList<ProposedFile> ChangeSetFiles(WorkflowRun run)
    {
        var files = new List<ProposedFile>();
        foreach (var id in ChangeSetNodes)
        {
            if (run.Nodes[id].Output is not { } output || !output.TryGetProperty("proposedFiles", out var array) ||
                array.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in array.EnumerateArray())
                files.Add(new ProposedFile(item.GetProperty("path").GetString() ?? "", item.GetProperty("content").GetString() ?? ""));
        }
        return files;
    }

    private static ProposedFile[] FilesOf(JsonElement? output)
    {
        if (output is not { } element || !element.TryGetProperty("proposedFiles", out var array) || array.ValueKind != JsonValueKind.Array)
            return [];
        return [.. array.EnumerateArray().Select(item =>
            new ProposedFile(item.GetProperty("path").GetString() ?? "", item.GetProperty("content").GetString() ?? ""))];
    }

    private static string ChangeSetDigest(WorkflowRun run) => ChangeRules.Digest(ChangeSetFiles(run));

    private async Task RollbackAsync(WorkflowRun run, WorkflowNodeState failedNode, CancellationToken cancellationToken)
    {
        var rolledBack = new List<string>();
        foreach (var id in ChangeSetNodes)
        {
            var node = run.Nodes[id];
            if (node.Status != "succeeded" || node.Id == failedNode.Id) continue;
            node.Status = "rolled_back";
            node.Output = null;
            rolledBack.Add(id);
        }
        if (rolledBack.Count == 0) return;
        lock (run) run.Metrics.Rollbacks++;
        await RecordAsync(run, "node.rolled_back",
            new { nodes = rolledBack, trigger = failedNode.Id, note = "The proposed change set was discarded; nothing was applied to the real workspace." },
            cancellationToken);
    }

    private async Task SafeStopAsync(WorkflowRun run, string reason, string? nodeId, CancellationToken cancellationToken)
    {
        run.Status = "safe_stopped";
        Finish(run);
        await RecordAsync(run, "run.safe_stopped", new { reason, nodeId }, cancellationToken);
    }

    private static string[] Descendants(WorkflowRun run, string rootId)
    {
        var impacted = new HashSet<string>(StringComparer.Ordinal);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var node in run.Nodes.Values)
                if (node.Id != rootId && !impacted.Contains(node.Id) &&
                    node.DependsOn.Any(dependency => dependency == rootId || impacted.Contains(dependency)))
                    changed |= impacted.Add(node.Id);
        }
        return impacted.ToArray();
    }

    private WorkflowRun RequireRun(string runId) =>
        runs.TryGetValue(runId, out var run) ? run : throw new WorkflowException(404, "Run not found.");

    private static void Finish(WorkflowRun run)
    {
        run.UpdatedAt = DateTimeOffset.UtcNow;
        run.Metrics.DurationMs = Math.Max(0, (long)(run.UpdatedAt - run.StartedAt).TotalMilliseconds);
    }

    private WorkflowRun Snapshot(WorkflowRun run)
    {
        lock (run)
        {
            var snapshot = new WorkflowRun
            {
                Id = run.Id, ScenarioId = run.ScenarioId, ScenarioTitle = run.ScenarioTitle,
                Requirements = run.Requirements, Assumptions = [.. run.Assumptions],
                AcceptanceCriteria = [.. run.AcceptanceCriteria], RequirementVersion = run.RequirementVersion,
                Status = run.Status, StartedAt = run.StartedAt, UpdatedAt = run.UpdatedAt,
                Metrics = new WorkflowMetrics
                {
                    Retries = run.Metrics.Retries, Rollbacks = run.Metrics.Rollbacks, Recoveries = run.Metrics.Recoveries,
                    RecoveryMsTotal = run.Metrics.RecoveryMsTotal, DurationMs = run.Metrics.DurationMs,
                    ActiveMs = run.Metrics.ActiveMs, AgentCalls = run.Metrics.AgentCalls,
                    InputTokens = run.Metrics.InputTokens, OutputTokens = run.Metrics.OutputTokens
                },
                Nodes = run.Nodes.ToDictionary(pair => pair.Key, pair => CloneNode(pair.Value), StringComparer.Ordinal)
            };
            snapshot.Audit.AddRange(run.Audit);
            snapshot.DecisionLineage.AddRange(run.DecisionLineage.Select(item => item.Clone()));
            return snapshot;
        }
    }

    private static WorkflowNodeState CloneNode(WorkflowNodeState node) =>
        new(new WorkflowNodeDefinition(node.Id, node.Stage, [.. node.DependsOn], node.EntryGate, node.ExitGate,
            node.RetryLimit, node.ApprovalRequired, node.HighImpact))
        {
            Status = node.Status, Attempts = node.Attempts, Output = node.Output?.Clone(), Error = node.Error,
            ErrorKind = node.ErrorKind, TaskId = node.TaskId, FirstFailureAt = node.FirstFailureAt
        };

    private async Task RecordAsync(WorkflowRun run, string eventName, object details, CancellationToken cancellationToken)
    {
        var detailsElement = JsonSerializer.SerializeToElement(details, JsonOptions);
        await auditGate.WaitAsync(cancellationToken);
        try
        {
            WorkflowAuditEvent entry;
            string payload;
            lock (run)
            {
                var previous = run.Audit.Count == 0 ? AuditLedger.Genesis : run.Audit[^1].Hash;
                var sequence = run.Audit.Count + 1;
                var timestamp = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks, TimeSpan.Zero);
                var rawDetails = detailsElement.GetRawText();
                var hash = AuditLedger.ComputeHash(previous, sequence, timestamp, run.Id, run.RequirementVersion, eventName, rawDetails);
                entry = new WorkflowAuditEvent(timestamp, run.Id, run.ScenarioId, run.RequirementVersion,
                    eventName, detailsElement, sequence, previous, hash);
                run.Audit.Add(entry);
                run.UpdatedAt = entry.Timestamp;
                payload = SerializeRun(run);
            }
            await repository.SaveRunAndAuditAsync(
                run.Id, run.ScenarioId, run.Status, run.RequirementVersion, run.StartedAt, run.UpdatedAt, payload,
                new WorkflowAuditEntity
                {
                    RunId = run.Id, Sequence = entry.Sequence, Timestamp = entry.Timestamp,
                    RequirementVersion = entry.RequirementVersion, Event = entry.Event,
                    Details = entry.Details.GetRawText(), PreviousHash = entry.PreviousHash, Hash = entry.Hash
                }, cancellationToken);
        }
        finally
        {
            auditGate.Release();
        }
    }

    private static string SerializeRun(WorkflowRun run)
    {
        var dto = new PersistedRun(
            run.Id, run.ScenarioId, run.ScenarioTitle, run.Requirements, run.Assumptions, run.AcceptanceCriteria,
            run.RequirementVersion, run.Status, run.StartedAt, run.UpdatedAt,
            run.Nodes.Values.Select(node => new PersistedNode(
                node.Id, node.Stage, node.DependsOn, node.EntryGate, node.ExitGate, node.ApprovalRequired,
                node.HighImpact, node.RetryLimit, node.Status, node.Attempts, node.Output, node.Error,
                node.ErrorKind, node.TaskId, node.FirstFailureAt)).ToArray(),
            run.Audit.ToArray(), run.DecisionLineage.ToArray(), run.Metrics);
        return JsonSerializer.Serialize(dto, JsonOptions);
    }

    private static WorkflowRun DeserializeRun(string payload)
    {
        var data = JsonSerializer.Deserialize<PersistedRun>(payload, JsonOptions)
            ?? throw new InvalidDataException("Stored workflow run is empty.");
        var run = new WorkflowRun
        {
            Id = data.Id, ScenarioId = data.ScenarioId, ScenarioTitle = data.ScenarioTitle,
            Requirements = data.Requirements, Assumptions = data.Assumptions, AcceptanceCriteria = data.AcceptanceCriteria,
            RequirementVersion = data.RequirementVersion, Status = data.Status, StartedAt = data.StartedAt,
            UpdatedAt = data.UpdatedAt, Metrics = data.Metrics,
            Nodes = data.Nodes.ToDictionary(node => node.Id, node =>
                new WorkflowNodeState(new WorkflowNodeDefinition(node.Id, node.Stage, node.DependsOn, node.EntryGate,
                    node.ExitGate, node.RetryLimit, node.ApprovalRequired, node.HighImpact))
                {
                    Status = node.Status, Attempts = node.Attempts, Output = node.Output, Error = node.Error,
                    ErrorKind = node.ErrorKind, TaskId = node.TaskId, FirstFailureAt = node.FirstFailureAt
                }, StringComparer.Ordinal)
        };
        run.Audit.AddRange(data.Audit);
        run.DecisionLineage.AddRange(data.DecisionLineage);
        return run;
    }

    private sealed record PersistedRun(
        string Id, string ScenarioId, string ScenarioTitle, string Requirements, string[] Assumptions,
        string[] AcceptanceCriteria, int RequirementVersion, string Status, DateTimeOffset StartedAt,
        DateTimeOffset UpdatedAt, PersistedNode[] Nodes, WorkflowAuditEvent[] Audit,
        JsonElement[] DecisionLineage, WorkflowMetrics Metrics);

    private sealed record PersistedNode(
        string Id, string Stage, string[] DependsOn, string EntryGate, string ExitGate,
        bool ApprovalRequired, bool HighImpact, int RetryLimit, string Status, int Attempts,
        JsonElement? Output, string? Error, string? ErrorKind, string? TaskId, DateTimeOffset? FirstFailureAt);
}

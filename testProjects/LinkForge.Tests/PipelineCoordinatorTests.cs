using Microsoft.EntityFrameworkCore;

namespace LinkForge.Tests;

public sealed class PipelineCoordinatorTests : IDisposable
{
    private readonly TestDatabase db = new();
    public void Dispose() => db.Dispose();

    [Fact]
    public async Task HappyPath_PausesAtBothApprovalGatesAndSucceeds()
    {
        var agent = new FakeEngineerProvider();
        var validator = new FakeChangeVerifier();
        var coordinator = Harness.Create(db, agent, validator);

        var run = await coordinator.StartAsync("greenfield");
        Assert.Equal("waiting_approval", run.Status);
        Assert.Equal("waiting_approval", run.Nodes[ScenarioLibrary.ChangeApprovalNode].Status);
        Assert.Equal("pending", run.Nodes[ScenarioLibrary.ValidationNode].Status);
        Assert.Equal(1, validator.Calls); // pre-approval sandbox check of the merged change set

        run = await coordinator.ApproveAsync(run.Id, Harness.Approve(run));
        Assert.Equal("waiting_approval", run.Status);
        Assert.Equal("waiting_approval", run.Nodes["release-approval"].Status);
        Assert.Equal(2, validator.Calls);

        run = await coordinator.ApproveAsync(run.Id, new ApprovalRequest(true, "reviewer", "Ship readiness."));
        Assert.Equal("succeeded", run.Status);
        Assert.All(run.Nodes.Values, node => Assert.Equal("succeeded", node.Status));
        Assert.Equal(7, run.Metrics.AgentCalls);
        Assert.Equal(700, run.Metrics.InputTokens);
    }

    [Fact]
    public async Task ParallelBranches_RunConcurrently()
    {
        var both = new TaskCompletionSource();
        var arrived = 0;
        var agent = new FakeEngineerProvider();
        agent.Override = request =>
        {
            if (request.Stage is "implementation" or "testing")
            {
                if (Interlocked.Increment(ref arrived) == 2) both.TrySetResult();
                both.Task.Wait(TimeSpan.FromSeconds(10));
            }
            return null;
        };
        var run = await Harness.Create(db, agent).StartAsync("greenfield");
        Assert.True(both.Task.IsCompletedSuccessfully);
        Assert.Equal("waiting_approval", run.Status);
    }

    [Fact]
    public async Task Approval_RequiresMatchingDigest()
    {
        var coordinator = Harness.Create(db, new FakeEngineerProvider());
        var run = await coordinator.StartAsync("greenfield");

        var missing = await Assert.ThrowsAsync<WorkflowException>(() =>
            coordinator.DecideAsync(run.Id, new ApprovalRequest(true, "reviewer", null)));
        Assert.Equal(400, missing.StatusCode);
        var wrong = await Assert.ThrowsAsync<WorkflowException>(() =>
            coordinator.DecideAsync(run.Id, new ApprovalRequest(true, "reviewer", null, "deadbeef")));
        Assert.Equal(409, wrong.StatusCode);
        Assert.Equal("waiting_approval", coordinator.Get(run.Id)!.Status);
    }

    [Fact]
    public async Task Rejection_SafeStopsAndNothingIsValidated()
    {
        var validator = new FakeChangeVerifier();
        var coordinator = Harness.Create(db, new FakeEngineerProvider(), validator);
        var run = await coordinator.StartAsync("brownfield");

        run = await coordinator.DecideAsync(run.Id, new ApprovalRequest(false, "reviewer", "Too broad."));
        Assert.Equal("safe_stopped", run.Status);
        Assert.Equal("rejected", run.Nodes[ScenarioLibrary.ChangeApprovalNode].Status);
        Assert.Equal(1, validator.Calls); // only the pre-approval check; nothing after the rejection
    }

    [Fact]
    public async Task DecideAsync_WhenNotWaiting_Conflicts()
    {
        var coordinator = Harness.Create(db, new FakeEngineerProvider());
        var run = await coordinator.CreateRunAsync("greenfield");
        var error = await Assert.ThrowsAsync<WorkflowException>(() =>
            coordinator.DecideAsync(run.Id, new ApprovalRequest(true, "reviewer", null)));
        Assert.Equal(409, error.StatusCode);
    }

    [Fact]
    public async Task TransientNodeFailure_RetriesOnceAndRecordsRecovery()
    {
        var failedOnce = false;
        var agent = new FakeEngineerProvider
        {
            Fault = (request, _) =>
            {
                if (request.Stage != "testing" || failedOnce) return null;
                failedOnce = true;
                return new AgentException(AgentErrorKind.InvalidResponse, "bad json");
            }
        };
        var coordinator = Harness.Create(db, agent);
        var run = await coordinator.StartAsync("greenfield");

        Assert.Equal("waiting_approval", run.Status);
        Assert.Equal(1, run.Metrics.Retries);
        Assert.Equal(1, run.Metrics.Recoveries);
        Assert.Equal(2, run.Nodes[ScenarioLibrary.TestsNode].Attempts);
    }

    [Theory]
    [InlineData(AgentErrorKind.Authentication)]
    [InlineData(AgentErrorKind.QuotaExceeded)]
    [InlineData(AgentErrorKind.InvalidRequest)]
    public async Task NonRetryableProviderFailure_SafeStopsWithoutRetry(AgentErrorKind kind)
    {
        var agent = new FakeEngineerProvider { Fault = (request, _) => request.Stage == "requirements" ? new AgentException(kind, "nope") : null };
        var coordinator = Harness.Create(db, agent);

        var run = await coordinator.StartAsync("greenfield");

        Assert.Equal("safe_stopped", run.Status);
        Assert.Equal(0, run.Metrics.Retries);
        Assert.Equal(1, agent.Calls);
        Assert.Equal(kind.ToString(), run.Nodes[ScenarioLibrary.RequirementsNode].ErrorKind);
    }

    [Fact]
    public async Task PersistentFailure_IsBoundedByRetryLimit()
    {
        var agent = new FakeEngineerProvider { Fault = (request, _) => request.Stage == "requirements" ? new AgentException(AgentErrorKind.Transient, "503") : null };
        var run = await Harness.Create(db, agent).StartAsync("greenfield");

        Assert.Equal("safe_stopped", run.Status);
        Assert.Equal(1, run.Nodes[ScenarioLibrary.RequirementsNode].Attempts);
    }

    [Fact]
    public async Task ValidationFailure_RetriesThenRollsBackAndSafeStops()
    {
        var validator = new FakeChangeVerifier { PassOnCall = call => call == 1 };
        var coordinator = Harness.Create(db, new FakeEngineerProvider(), validator);
        var run = await coordinator.StartAsync("greenfield");

        run = await coordinator.ApproveAsync(run.Id, Harness.Approve(run));

        Assert.Equal("safe_stopped", run.Status);
        Assert.Equal(3, validator.Calls);
        Assert.Equal("failed", run.Nodes[ScenarioLibrary.ValidationNode].Status);
        Assert.Equal("rolled_back", run.Nodes[ScenarioLibrary.ImplementationNode].Status);
        Assert.Equal("rolled_back", run.Nodes[ScenarioLibrary.TestsNode].Status);
        Assert.Null(run.Nodes[ScenarioLibrary.ImplementationNode].Output);
        Assert.Equal(1, run.Metrics.Rollbacks);
        Assert.Equal(1, run.Metrics.Retries);
    }

    [Fact]
    public async Task FailedPreApprovalBuild_IsRepairedWithCompilerFeedbackThenApproved()
    {
        var feedback = new List<string?>();
        var agent = new FakeEngineerProvider
        {
            Override = request =>
            {
                if (request.RepairFeedback is not null) lock (feedback) feedback.Add(request.RepairFeedback);
                // A repair reply that omits the prose checks must not fail the testing gate.
                return request is { Stage: "testing", RepairFeedback: not null } ? new("t", [], [], [], [], [], [], []) : null;
            }
        };
        var validator = new FakeChangeVerifier { PassOnCall = call => call >= 2 };
        var run = await Harness.Create(db, agent, validator).StartAsync("greenfield");

        Assert.Equal("waiting_approval", run.Status);
        Assert.Equal("waiting_approval", run.Nodes[ScenarioLibrary.ChangeApprovalNode].Status);
        Assert.Equal(2, validator.Calls);
        Assert.Single(feedback); // no source/ path in the error, so only the tests stage is repaired
        Assert.All(feedback, text => Assert.Contains("error CS1002", text));
    }

    [Fact]
    public async Task ProductionCodeCompileError_RepairsOnlyTheImplementationStage()
    {
        var repaired = new List<string>();
        var agent = new FakeEngineerProvider
        {
            Override = request =>
            {
                if (request.RepairFeedback is not null) lock (repaired) repaired.Add(request.Stage);
                return null;
            }
        };
        var validator = new FakeChangeVerifier
        {
            PassOnCall = call => call >= 2,
            FailureDetails = "source/LinkForge.Persistence/SqliteLinkStore.cs(10,5): error CS1002: ; expected"
        };
        var run = await Harness.Create(db, agent, validator).StartAsync("greenfield");

        Assert.Equal("waiting_approval", run.Status);
        Assert.Equal(["implementation"], repaired);
    }

    [Fact]
    public async Task PersistentPreApprovalBuildFailure_ReplansOnceThenSafeStops()
    {
        var validator = new FakeChangeVerifier { Pass = false };
        var run = await Harness.Create(db, new FakeEngineerProvider(), validator).StartAsync("greenfield");

        Assert.Equal("safe_stopped", run.Status);
        Assert.Equal(8, validator.Calls); // 4 per plan version, one automatic re-plan only
        Assert.Equal(2, run.RequirementVersion);
        Assert.Equal(1, run.DecisionLineage.Count(entry => entry.GetProperty("decision").GetString() == "auto-replan"));
        Assert.Equal("failed", run.Nodes[ScenarioLibrary.ChangeApprovalNode].Status);
        Assert.Equal("rolled_back", run.Nodes[ScenarioLibrary.ImplementationNode].Status);
        Assert.Equal("rolled_back", run.Nodes[ScenarioLibrary.TestsNode].Status);
    }

    [Fact]
    public async Task ExhaustedRepair_AutoReplansAndReachesApprovalWithNewPlanVersion()
    {
        var validator = new FakeChangeVerifier { PassOnCall = call => call >= 5 };
        var run = await Harness.Create(db, new FakeEngineerProvider(), validator).StartAsync("greenfield");

        Assert.Equal("waiting_approval", run.Status);
        Assert.Equal(2, run.RequirementVersion);
        Assert.Contains("[Automatic re-plan]", run.Requirements);
        Assert.Equal(1, run.Metrics.Rollbacks);
    }

    [Fact]
    public async Task PolicyViolatingProposal_FailsTheGateBeforeApproval()
    {
        var agent = new FakeEngineerProvider
        {
            Override = request => request.Stage == "implementation"
                ? new("bad", [], [], [], [], [], [], [new ProposedFile("../../evil.cs", "class Evil {}")])
                : null
        };
        var run = await Harness.Create(db, agent).StartAsync("greenfield");

        Assert.Equal("safe_stopped", run.Status);
        Assert.Equal("gate", run.Nodes[ScenarioLibrary.ImplementationNode].ErrorKind);
        Assert.NotEqual("waiting_approval", run.Nodes[ScenarioLibrary.ChangeApprovalNode].Status);
    }

    [Fact]
    public async Task ImplementationProposingTestFiles_FailsItsExitGate()
    {
        var agent = new FakeEngineerProvider
        {
            Override = request => request.Stage == "implementation"
                ? new("bad", [], [], [], [], [], [], [new ProposedFile("testProjects/LinkForge.Tests/Sneaky.cs", "namespace LinkForge; class S {}")])
                : null
        };
        var run = await Harness.Create(db, agent).StartAsync("greenfield");

        Assert.Equal("safe_stopped", run.Status);
        Assert.Equal("gate", run.Nodes[ScenarioLibrary.ImplementationNode].ErrorKind);
        Assert.Contains("must not propose test files", run.Nodes[ScenarioLibrary.ImplementationNode].Error);
    }

    [Fact]
    public async Task OversizedCombinedChangeSet_IsStoppedBeforeApprovalIsRequested()
    {
        var agent = new FakeEngineerProvider
        {
            Override = request => request.Stage switch
            {
                "implementation" => new("i", [], [], [], [], [], [], [.. Enumerable.Range(0, 4).Select(i => FakeEngineerProvider.GoodFile($"Impl{i}"))]),
                "testing" => new("t", [], [], [], [], ["check"], [],
                    [.. Enumerable.Range(0, 3).Select(i => new ProposedFile($"testProjects/LinkForge.Tests/T{i}.cs", "namespace LinkForge; class T {}"))]),
                _ => null
            }
        };
        var validator = new FakeChangeVerifier();
        var run = await Harness.Create(db, agent, validator).StartAsync("greenfield");

        Assert.Equal("safe_stopped", run.Status);
        Assert.Equal("failed", run.Nodes[ScenarioLibrary.ChangeApprovalNode].Status);
        Assert.Contains("policy.change-set-size", run.Nodes[ScenarioLibrary.ChangeApprovalNode].Error);
        Assert.Equal("rolled_back", run.Nodes[ScenarioLibrary.ImplementationNode].Status);
        Assert.Equal(0, validator.Calls);
    }

    [Fact]
    public async Task TestingStageProposingProductionFiles_FailsItsExitGate()
    {
        var agent = new FakeEngineerProvider
        {
            Override = request => request.Stage == "testing"
                ? new("t", [], [], [], [], ["check"], [], [FakeEngineerProvider.GoodFile("Prod")])
                : null
        };
        var run = await Harness.Create(db, agent).StartAsync("greenfield");
        Assert.Equal("safe_stopped", run.Status);
        Assert.Contains("under testProjects/", run.Nodes[ScenarioLibrary.TestsNode].Error);
    }

    [Fact]
    public async Task FaultInjection_IsRejectedUnlessEnabled()
    {
        var disabled = Harness.Create(db, new FakeEngineerProvider());
        var error = await Assert.ThrowsAsync<WorkflowException>(() => disabled.CreateRunAsync("greenfield", true));
        Assert.Equal(400, error.StatusCode);

        var enabled = Harness.Create(db, new FakeEngineerProvider(), faultInjection: true);
        var run = await enabled.StartAsync("greenfield", injectValidationFailure: true);
        run = await enabled.ApproveAsync(run.Id, Harness.Approve(run));
        Assert.Equal("safe_stopped", run.Status);
        Assert.Equal("failed", run.Nodes[ScenarioLibrary.ValidationNode].Status);
    }

    [Fact]
    public async Task Replan_InvalidatesDownstreamAndBumpsVersion()
    {
        var agent = new FakeEngineerProvider();
        var coordinator = Harness.Create(db, agent);
        var run = await coordinator.StartAsync("ambiguous");

        run = await coordinator.ReplanAsync(run.Id, new ReplanRequest("Share links using readable aliases only."));

        Assert.Equal(2, run.RequirementVersion);
        Assert.Equal("waiting_approval", run.Status);
        Assert.Equal("Share links using readable aliases only.", run.Requirements);
        Assert.Contains(run.DecisionLineage, item => item.GetProperty("decision").GetString() == "upstream-change");
        Assert.Equal(8, agent.Stages.Count);
        Assert.Equal(2, agent.Stages.Count(stage => stage == "requirements"));
    }

    [Fact]
    public async Task Replan_RejectsEmptyRequirementsAndRunningRuns()
    {
        var coordinator = Harness.Create(db, new FakeEngineerProvider());
        var created = await coordinator.CreateRunAsync("greenfield");
        Assert.Equal(409, (await Assert.ThrowsAsync<WorkflowException>(() =>
            coordinator.ReplanAsync(created.Id, new ReplanRequest("x")))).StatusCode);

        var paused = await coordinator.StartAsync("greenfield");
        Assert.Equal(400, (await Assert.ThrowsAsync<WorkflowException>(() =>
            coordinator.ReplanAsync(paused.Id, new ReplanRequest("  ")))).StatusCode);
    }

    [Fact]
    public async Task Stop_SafeStopsAWaitingRun()
    {
        var coordinator = Harness.Create(db, new FakeEngineerProvider());
        var run = await coordinator.StartAsync("greenfield");
        run = await coordinator.StopAsync(run.Id, "operator", "Changed my mind.");
        Assert.Equal("safe_stopped", run.Status);
        Assert.Equal(409, (await Assert.ThrowsAsync<WorkflowException>(() =>
            coordinator.StopAsync(run.Id, "operator", null))).StatusCode);
    }

    [Fact]
    public async Task Stop_CancelsAnInFlightRun()
    {
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var agent = new FakeEngineerProvider { Gate = () => { started.TrySetResult(); return release.Task; } };
        var coordinator = Harness.Create(db, agent);
        var created = await coordinator.CreateRunAsync("greenfield");

        var running = coordinator.ContinueAsync(created.Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await coordinator.StopAsync(created.Id, "operator", "Abort.");
        var run = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("safe_stopped", run.Status);
        release.TrySetResult();
    }

    [Fact]
    public async Task Restart_RecoversWaitingRunsAndSafeStopsInterruptedOnes()
    {
        var first = Harness.Create(db, new FakeEngineerProvider());
        var waiting = await first.StartAsync("greenfield");

        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();
        var blocking = Harness.Create(db, new FakeEngineerProvider { Gate = () => { started.TrySetResult(); return release.Task; } });
        var inFlight = await blocking.CreateRunAsync("brownfield");
        _ = blocking.ContinueAsync(inFlight.Id);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var restarted = Harness.Create(db, new FakeEngineerProvider());
        await restarted.InitializeAsync();

        var recovered = restarted.Get(waiting.Id)!;
        Assert.Equal("waiting_approval", recovered.Status);
        var interrupted = restarted.Get(inFlight.Id)!;
        Assert.Equal("safe_stopped", interrupted.Status);

        var finished = await restarted.ApproveAsync(waiting.Id, Harness.Approve(recovered));
        Assert.Equal("waiting_approval", finished.Status);
        Assert.Equal("waiting_approval", finished.Nodes["release-approval"].Status);
        release.TrySetResult();
    }

    [Fact]
    public async Task AuditLedger_IsVerifiableAndDetectsTampering()
    {
        var coordinator = Harness.Create(db, new FakeEngineerProvider());
        var run = await coordinator.StartAsync("greenfield");
        await coordinator.ApproveAsync(run.Id, Harness.Approve(run));

        var (valid, broken, events) = await coordinator.VerifyAuditAsync(run.Id);
        Assert.True(valid);
        Assert.Null(broken);
        Assert.True(events > 10);

        await using (var context = await db.Factory.CreateDbContextAsync())
        {
            var target = await context.WorkflowAudit.Where(a => a.RunId == run.Id).OrderBy(a => a.Sequence).Skip(3).FirstAsync();
            target.Details = "{\"tampered\":true}";
            await context.SaveChangesAsync();
        }
        var tampered = await coordinator.VerifyAuditAsync(run.Id);
        Assert.False(tampered.Valid);
        Assert.NotNull(tampered.BrokenAtSequence);
    }

    [Fact]
    public async Task Evidence_RecordsModelMetadataArtifactsValidationsAndApprovalsWithoutSecrets()
    {
        var agent = new FakeEngineerProvider
        {
            Override = request => request.Stage == "requirements"
                ? new("Found key sk-abcdefghijklmnopqrstuvwxyz123456 in input.", [], [], [], [], [], [], [])
                : null
        };
        var coordinator = Harness.Create(db, agent);
        var run = await coordinator.StartAsync("greenfield");
        await coordinator.ApproveAsync(run.Id, Harness.Approve(run, "ok"));

        var repository = db.Repository();
        var calls = await repository.GetAgentCallsAsync(run.Id);
        Assert.NotEmpty(calls);
        Assert.All(calls, call => Assert.False(string.IsNullOrEmpty(call.TaskId)));
        Assert.Contains(calls, call => call.Model == "fake-model-1" && call.PlanVersion == 1);
        Assert.NotEmpty(await repository.GetArtifactsAsync(run.Id));
        Assert.NotEmpty(await repository.GetValidationsAsync(run.Id));
        Assert.Single(await repository.GetApprovalsAsync(run.Id));

        var everything = System.Text.Json.JsonSerializer.Serialize(new
        {
            run = coordinator.Get(run.Id),
            evidence = await coordinator.GetEvidenceAsync(run.Id),
            audit = await repository.GetAuditAsync(run.Id)
        });
        Assert.DoesNotContain("sk-abcdefghijklmnopqrstuvwxyz123456", everything);
    }

    [Fact]
    public async Task Metrics_AggregateAcrossRuns()
    {
        var coordinator = Harness.Create(db, new FakeEngineerProvider());
        var a = await coordinator.StartAsync("greenfield");
        await coordinator.StartAsync("brownfield");
        await coordinator.StopAsync(a.Id, "operator", null);

        var metrics = coordinator.GetMetrics();
        Assert.Equal(2, metrics.TotalRuns);
        Assert.Equal(1, metrics.SafeStopped);
        Assert.Equal(1, metrics.WaitingApproval);
        Assert.Equal(8, metrics.AgentCalls);
    }
}

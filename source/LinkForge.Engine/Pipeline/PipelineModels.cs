using System.Text.Json;

namespace LinkForge;

public sealed class WorkflowOptions
{
    public const string SectionName = "Workflow";

    /// <summary>Allows the forceTestFailure demo switch. Keep false outside demos.</summary>
    public bool AllowFaultInjection { get; set; }
}

public sealed class WorkflowNodeState(WorkflowNodeDefinition definition)
{
    public string Id { get; init; } = definition.Id;
    public string Stage { get; init; } = definition.Stage;
    public string[] DependsOn { get; init; } = [.. definition.DependsOn];
    public string EntryGate { get; init; } = definition.EntryGate;
    public string ExitGate { get; init; } = definition.ExitGate;
    public bool ApprovalRequired { get; init; } = definition.ApprovalRequired;
    public bool HighImpact { get; init; } = definition.HighImpact;
    public int RetryLimit { get; init; } = definition.RetryLimit;
    public string Status { get; set; } = "pending";
    public int Attempts { get; set; }
    public JsonElement? Output { get; set; }
    public string? Error { get; set; }
    public string? ErrorKind { get; set; }
    public string? TaskId { get; set; }
    public DateTimeOffset? FirstFailureAt { get; set; }
}

public sealed record WorkflowAuditEvent(
    DateTimeOffset Timestamp,
    string RunId,
    string ScenarioId,
    int RequirementVersion,
    string Event,
    JsonElement Details,
    int Sequence = 0,
    string PreviousHash = "",
    string Hash = "");

public sealed class WorkflowRun
{
    public required string Id { get; init; }
    public required string ScenarioId { get; init; }
    public required string ScenarioTitle { get; init; }
    public required string Requirements { get; set; }
    public required string[] Assumptions { get; init; }
    public required string[] AcceptanceCriteria { get; init; }
    public int RequirementVersion { get; set; } = 1;
    public string Status { get; set; } = "running";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
    public required Dictionary<string, WorkflowNodeState> Nodes { get; init; }
    public List<WorkflowAuditEvent> Audit { get; } = [];
    public List<JsonElement> DecisionLineage { get; } = [];
    public WorkflowMetrics Metrics { get; set; } = new();
}

public sealed class WorkflowMetrics
{
    public int Retries { get; set; }
    public int Rollbacks { get; set; }
    public int Recoveries { get; set; }
    public long RecoveryMsTotal { get; set; }
    public long DurationMs { get; set; }
    public long ActiveMs { get; set; }
    public int AgentCalls { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long EndToEndLatencyMs => DurationMs;
    public long MeanTimeToRecoveryMs => Recoveries == 0 ? 0 : RecoveryMsTotal / Recoveries;
}

public sealed record AggregateMetrics(
    int TotalRuns, int Succeeded, int SafeStopped, int WaitingApproval, int Running,
    double SuccessRate, double RetriesPerRun, double RollbacksPerRun,
    long MeanTimeToRecoveryMs, long MeanEndToEndLatencyMs, long MeanActiveMs,
    int AgentCalls, long InputTokens, long OutputTokens);

public sealed record ApprovalRequest(bool Approved, string Actor, string? Reason, string? ChangeSetDigest = null);
public sealed record ReplanRequest(string Requirements);

public sealed class WorkflowException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>A workflow exit gate or independent validation did not pass.</summary>
public sealed class GateFailedException(string message) : Exception(message);

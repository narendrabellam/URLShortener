namespace LinkForge;

public sealed record ShortUrl(
    string Code,
    string Url,
    DateTimeOffset CreatedAt,
    long Clicks,
    DateTimeOffset? LastAccessedAt,
    IReadOnlyDictionary<string, long> Referrers);

public sealed class LinkStoreException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class ShortUrlEntity
{
    public long Id { get; set; }
    public string Code { get; set; } = "";
    public string Destination { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public long Clicks { get; set; }
    public DateTimeOffset? LastAccessedAt { get; set; }
    public List<ShortUrlReferrerEntity> Referrers { get; set; } = [];
}

public sealed class ShortUrlReferrerEntity
{
    public long ShortUrlId { get; set; }
    public ShortUrlEntity ShortUrl { get; set; } = null!;
    public string Referrer { get; set; } = "";
    public long Clicks { get; set; }
}

public sealed class WorkflowRunEntity
{
    public string Id { get; set; } = "";
    public string ScenarioId { get; set; } = "";
    public string Status { get; set; } = "";
    public int RequirementVersion { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string Payload { get; set; } = "";
}

/// <summary>Append-only, hash-chained audit record. Hash covers the previous hash, so edits are detectable.</summary>
public sealed class WorkflowAuditEntity
{
    public long Id { get; set; }
    public string RunId { get; set; } = "";
    public int Sequence { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public int RequirementVersion { get; set; }
    public string Event { get; set; } = "";
    public string Details { get; set; } = "{}";
    public string PreviousHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

/// <summary>One logical model request (after provider-level retries). Never stores prompt text or secrets.</summary>
public sealed class AgentCallEntity
{
    public long Id { get; set; }
    public string RunId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public int PlanVersion { get; set; }
    public int NodeAttempt { get; set; }
    public string Provider { get; set; } = "";
    public string Model { get; set; } = "";
    public string? ResponseId { get; set; }
    public int ProviderAttempts { get; set; }
    public bool UsedFallbackModel { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long LatencyMs { get; set; }
    public string Outcome { get; set; } = "";
    public string? ErrorKind { get; set; }
    public DateTimeOffset StartedAt { get; set; }
}

public sealed class ArtifactEntity
{
    public long Id { get; set; }
    public string RunId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public int PlanVersion { get; set; }
    public string Kind { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ValidationEntity
{
    public long Id { get; set; }
    public string RunId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public int PlanVersion { get; set; }
    public string Check { get; set; } = "";
    public bool Passed { get; set; }
    public string Details { get; set; } = "";
    public long DurationMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ApprovalEntity
{
    public long Id { get; set; }
    public string RunId { get; set; } = "";
    public string NodeId { get; set; } = "";
    public int PlanVersion { get; set; }
    public bool Approved { get; set; }
    public string Actor { get; set; } = "";
    public string Reason { get; set; } = "";
    public string ChangeSetDigest { get; set; } = "";
    public DateTimeOffset DecidedAt { get; set; }
}

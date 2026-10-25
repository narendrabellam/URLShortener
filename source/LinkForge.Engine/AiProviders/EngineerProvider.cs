using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkForge;

public sealed record EngineerProviderRequest(
    string ScenarioId,
    string ScenarioTitle,
    string Stage,
    string Requirements,
    int RequirementVersion,
    string[] Assumptions,
    string[] AcceptanceCriteria,
    IReadOnlyDictionary<string, JsonElement> PriorOutputs,
    string? CodebaseContext = null,
    string? ModelOverride = null,
    string? RepairFeedback = null,
    ProposedFile[]? PreviousFiles = null,
    ProposedFile[]? SiblingFiles = null);

public sealed record ProposedFile(string Path, string Content);

public sealed record EngineeringArtifact(
    string Summary,
    string[] Artifacts,
    string[] Risks,
    string[] OpenQuestions,
    string[] ProposedChanges,
    string[] Tests,
    string[] Decisions,
    ProposedFile[]? ProposedFiles = null)
{
    [JsonIgnore]
    public ProposedFile[] Files => ProposedFiles ?? [];
}

/// <summary>Provider/model facts that are safe to persist. Never contains prompts, responses, or credentials.</summary>
public sealed record AgentCallMetadata(
    string Provider,
    string Model,
    string? ResponseId,
    long InputTokens,
    long OutputTokens,
    long LatencyMs,
    int ProviderAttempts = 1,
    bool UsedFallbackModel = false);

public sealed record AgentResult(EngineeringArtifact Artifact, AgentCallMetadata Metadata);

public interface IEngineerProvider
{
    Task<AgentResult> GenerateAsync(EngineerProviderRequest request, CancellationToken cancellationToken = default);
}

public enum AgentErrorKind
{
    /// <summary>No API key or model configured. Not retryable.</summary>
    NotConfigured,
    /// <summary>Credentials rejected (401/403). Not retryable.</summary>
    Authentication,
    /// <summary>Request rejected as malformed or too large (400/404/422). Not retryable.</summary>
    InvalidRequest,
    /// <summary>Account out of quota/billing. Not retryable.</summary>
    QuotaExceeded,
    /// <summary>429 rate limit. Retryable with backoff.</summary>
    RateLimited,
    /// <summary>5xx or network failure. Retryable with backoff.</summary>
    Transient,
    /// <summary>The per-request timeout elapsed. Retryable.</summary>
    Timeout,
    /// <summary>Model output was empty, oversized, or violated the artifact contract. Retried by the workflow node.</summary>
    InvalidResponse
}

public sealed class AgentException(AgentErrorKind kind, string message, TimeSpan? retryAfter = null, Exception? inner = null)
    : Exception(message, inner)
{
    public AgentErrorKind Kind { get; } = kind;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public bool IsProviderRetryable => Kind is AgentErrorKind.RateLimited or AgentErrorKind.Transient or AgentErrorKind.Timeout;

    /// <summary>Whether re-running the workflow node could plausibly succeed.</summary>
    public bool IsNodeRetryable => Kind is not (AgentErrorKind.NotConfigured or AgentErrorKind.Authentication
        or AgentErrorKind.InvalidRequest or AgentErrorKind.QuotaExceeded);
}

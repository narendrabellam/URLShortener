namespace LinkForge;

/// <summary>
/// OpenAI settings. The API key is intentionally NOT part of this type; it is read from the
/// OPENAI_API_KEY environment variable at call time and never logged or persisted.
/// </summary>
public sealed class OpenAiOptions
{
    public const string SectionName = "OpenAI";

    /// <summary>Primary model. Override with OpenAI__Model.</summary>
    public string Model { get; set; } = "gpt-4.1";

    /// <summary>Optional model tried once after provider retries are exhausted by a transient failure.</summary>
    public string? FallbackModel { get; set; }

    /// <summary>Optional endpoint override (for example, a gateway). Defaults to the official API.</summary>
    public string? Endpoint { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 180;
    public int MaxAttempts { get; set; } = 3;
    public int BaseDelayMilliseconds { get; set; } = 500;
    public int MaxDelaySeconds { get; set; } = 20;
    public int MaxOutputTokens { get; set; } = 16000;
    public int MaxPromptBytes { get; set; } = 96_000;
    public int MaxCompletionBytes { get; set; } = 120_000;

    /// <summary>Leave null for models that only support the default temperature.</summary>
    public float? Temperature { get; set; }
}

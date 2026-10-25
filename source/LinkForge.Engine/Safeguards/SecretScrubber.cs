using System.Text.RegularExpressions;

namespace LinkForge;

/// <summary>Removes credential-shaped strings before anything is persisted, logged or returned.</summary>
public static partial class SecretScrubber
{
    public const string Mask = "[REDACTED]";

    [GeneratedRegex(@"sk-[A-Za-z0-9_\-]{16,}")]
    private static partial Regex ApiKeyPattern();

    [GeneratedRegex(@"(?i)bearer\s+[A-Za-z0-9._\-]{16,}")]
    private static partial Regex BearerPattern();

    [GeneratedRegex(@"(?i)(api[_-]?key|secret|password|token)\s*[=:]\s*[""']?[A-Za-z0-9/+_\-]{12,}")]
    private static partial Regex AssignmentPattern();

    [GeneratedRegex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")]
    private static partial Regex PrivateKeyPattern();

    public static bool ContainsSecret(string? text) =>
        !string.IsNullOrEmpty(text) &&
        (ApiKeyPattern().IsMatch(text) || BearerPattern().IsMatch(text) ||
         AssignmentPattern().IsMatch(text) || PrivateKeyPattern().IsMatch(text));

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        text = ApiKeyPattern().Replace(text, Mask);
        text = BearerPattern().Replace(text, Mask);
        text = AssignmentPattern().Replace(text, match => match.Value[..match.Value.IndexOfAny(['=', ':'])] + "=" + Mask);
        return PrivateKeyPattern().Replace(text, Mask);
    }
}

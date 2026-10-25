using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LinkForge;

/// <summary>
/// Adds a per-attempt timeout, bounded exponential backoff with jitter (honouring Retry-After),
/// error classification, and a single optional fallback-model attempt around any provider.
/// Never retries non-transient errors and never retries indefinitely.
/// </summary>
public sealed class ResilientEngineerProvider(
    IEngineerProvider inner,
    IOptions<OpenAiOptions> options,
    ILogger<ResilientEngineerProvider> logger,
    TimeProvider? timeProvider = null) : IEngineerProvider
{
    private readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task<AgentResult> GenerateAsync(
        EngineerProviderRequest request, CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var maxAttempts = Math.Clamp(settings.MaxAttempts, 1, 6);
        var attempts = 0;
        AgentException? last = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            attempts++;
            var outcome = await TryOnceAsync(request, settings, cancellationToken);
            if (outcome.Result is { } result)
                return result with { Metadata = result.Metadata with { ProviderAttempts = attempts } };

            last = outcome.Error!;
            if (!last.IsProviderRetryable || attempt == maxAttempts) break;
            var delay = ComputeDelay(settings, attempt, last.RetryAfter);
            logger.LogWarning("Provider attempt {Attempt}/{Max} failed ({Kind}); retrying in {DelayMs} ms.",
                attempt, maxAttempts, last.Kind, (int)delay.TotalMilliseconds);
            await Task.Delay(delay, time, cancellationToken);
        }

        if (last is { IsProviderRetryable: true } && !string.IsNullOrWhiteSpace(settings.FallbackModel) &&
            !string.Equals(settings.FallbackModel, request.ModelOverride ?? settings.Model, StringComparison.Ordinal))
        {
            attempts++;
            logger.LogWarning("Primary model exhausted retries ({Kind}); trying fallback model once.", last.Kind);
            var outcome = await TryOnceAsync(request with { ModelOverride = settings.FallbackModel }, settings, cancellationToken);
            if (outcome.Result is { } fallback)
                return fallback with { Metadata = fallback.Metadata with { ProviderAttempts = attempts, UsedFallbackModel = true } };
            last = outcome.Error!;
        }
        throw last!;
    }

    private async Task<(AgentResult? Result, AgentException? Error)> TryOnceAsync(
        EngineerProviderRequest request, OpenAiOptions settings, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, settings.RequestTimeoutSeconds)));
        try
        {
            return (await inner.GenerateAsync(request, timeout.Token), null);
        }
        catch (AgentException error)
        {
            return (null, error);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, new AgentException(AgentErrorKind.Timeout,
                $"Model request exceeded the {settings.RequestTimeoutSeconds}s timeout."));
        }
    }

    private static TimeSpan ComputeDelay(OpenAiOptions settings, int attempt, TimeSpan? retryAfter)
    {
        var cap = TimeSpan.FromSeconds(Math.Max(0, settings.MaxDelaySeconds));
        if (retryAfter is { } requested) return requested > cap ? cap : requested;
        var exponential = TimeSpan.FromMilliseconds(settings.BaseDelayMilliseconds * Math.Pow(2, attempt - 1));
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * settings.BaseDelayMilliseconds * 0.5);
        var total = exponential + jitter;
        return total > cap ? cap : total;
    }
}

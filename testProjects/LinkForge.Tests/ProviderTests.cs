using System.ClientModel.Primitives;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace LinkForge.Tests;

public sealed class ResilientAgentTests
{
    private sealed class ScriptedAgent(params object[] script) : IEngineerProvider
    {
        private int index;
        public int Calls { get; private set; }
        public List<string?> Models { get; } = [];

        public Task<AgentResult> GenerateAsync(EngineerProviderRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            Models.Add(request.ModelOverride);
            var step = script[Math.Min(index++, script.Length - 1)];
            return step switch
            {
                Exception error => Task.FromException<AgentResult>(error),
                Func<CancellationToken, Task<AgentResult>> hang => hang(cancellationToken),
                _ => Task.FromResult(new AgentResult(FakeEngineerProvider.Default(Request()),
                    new AgentCallMetadata("fake", (string)step, "r1", 1, 1, 1)))
            };
        }
    }

    private static EngineerProviderRequest Request() =>
        new("greenfield", "t", "requirements", "r", 1, [], [], new Dictionary<string, System.Text.Json.JsonElement>());

    private static ResilientEngineerProvider Create(IEngineerProvider inner, Action<OpenAiOptions>? configure = null)
    {
        var options = new OpenAiOptions { MaxAttempts = 3, BaseDelayMilliseconds = 1, MaxDelaySeconds = 1, RequestTimeoutSeconds = 1, Model = "primary" };
        configure?.Invoke(options);
        return new ResilientEngineerProvider(inner, Options.Create(options), NullLogger<ResilientEngineerProvider>.Instance);
    }

    [Fact]
    public async Task RateLimitThenSuccess_RetriesAndRecordsAttempts()
    {
        var inner = new ScriptedAgent(new AgentException(AgentErrorKind.RateLimited, "429", TimeSpan.Zero), "primary");
        var result = await Create(inner).GenerateAsync(Request());
        Assert.Equal(2, inner.Calls);
        Assert.Equal(2, result.Metadata.ProviderAttempts);
    }

    [Theory]
    [InlineData(AgentErrorKind.Authentication)]
    [InlineData(AgentErrorKind.QuotaExceeded)]
    [InlineData(AgentErrorKind.InvalidRequest)]
    [InlineData(AgentErrorKind.NotConfigured)]
    [InlineData(AgentErrorKind.InvalidResponse)]
    public async Task NonTransientErrors_AreNotRetried(AgentErrorKind kind)
    {
        var inner = new ScriptedAgent(new AgentException(kind, "x"));
        var error = await Assert.ThrowsAsync<AgentException>(() => Create(inner, o => o.FallbackModel = "backup").GenerateAsync(Request()));
        Assert.Equal(kind, error.Kind);
        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task PersistentTransientFailure_IsBoundedByMaxAttempts()
    {
        var inner = new ScriptedAgent(new AgentException(AgentErrorKind.Transient, "503"));
        await Assert.ThrowsAsync<AgentException>(() => Create(inner, o => o.MaxAttempts = 4).GenerateAsync(Request()));
        Assert.Equal(4, inner.Calls);
    }

    [Fact]
    public async Task ExhaustedRetries_TryFallbackModelExactlyOnce()
    {
        var inner = new ScriptedAgent(new AgentException(AgentErrorKind.Transient, "503"), new AgentException(AgentErrorKind.Transient, "503"), "backup");
        var result = await Create(inner, o => { o.MaxAttempts = 2; o.FallbackModel = "backup"; }).GenerateAsync(Request());
        Assert.True(result.Metadata.UsedFallbackModel);
        Assert.Equal(3, inner.Calls);
        Assert.Equal("backup", inner.Models[^1]);
    }

    [Fact]
    public async Task HungProvider_TimesOutAsRetryableTimeout()
    {
        Func<CancellationToken, Task<AgentResult>> hang = async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException();
        };
        var inner = new ScriptedAgent(hang);
        var error = await Assert.ThrowsAsync<AgentException>(() => Create(inner, o => o.MaxAttempts = 2).GenerateAsync(Request()));
        Assert.Equal(AgentErrorKind.Timeout, error.Kind);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesWithoutRetry()
    {
        var inner = new ScriptedAgent(new AgentException(AgentErrorKind.Transient, "503"));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(inner).GenerateAsync(Request(), cancelled.Token));
        Assert.True(inner.Calls <= 1);
    }
}

public sealed class OpenAiProviderTests
{
    private const string Key = "sk-test-0123456789abcdefghijklmnop";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    private static string Completion(string content) => System.Text.Json.JsonSerializer.Serialize(new
    {
        id = "chatcmpl-test", @object = "chat.completion", created = 1, model = "gpt-stub-2025",
        choices = new[] { new { index = 0, message = new { role = "assistant", content }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 11, completion_tokens = 7, total_tokens = 18 }
    });

    private static OpenAiEngineerProvider Create(StubHandler handler, string? key = Key, Action<OpenAiOptions>? configure = null)
    {
        var options = new OpenAiOptions { Model = "configured-model" };
        configure?.Invoke(options);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(key is null ? [] : new Dictionary<string, string?> { ["OPENAI_API_KEY"] = key }).Build();
        return new OpenAiEngineerProvider(Options.Create(options), configuration, NullLogger<OpenAiEngineerProvider>.Instance,
            client => client.Transport = new HttpClientPipelineTransport(new HttpClient(handler)));
    }

    private static EngineerProviderRequest Request(string stage = "requirements") =>
        new("greenfield", "Title", stage, "Add link expiry.", 1, ["a"], ["c"], new Dictionary<string, System.Text.Json.JsonElement>());

    private const string ValidArtifact =
        "{\"summary\":\"ok\",\"artifacts\":[],\"risks\":[\"r\"],\"openQuestions\":[],\"proposedChanges\":[],\"testProjects\":[\"t\"],\"decisions\":[],\"proposedFiles\":[]}";

    [Fact]
    public async Task Success_ParsesArtifactAndCapturesSafeMetadata()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Completion(ValidArtifact)));
        var result = await Create(handler).GenerateAsync(Request());

        Assert.Equal("ok", result.Artifact.Summary);
        Assert.Equal("openai", result.Metadata.Provider);
        Assert.Equal("gpt-stub-2025", result.Metadata.Model);
        Assert.Equal("chatcmpl-test", result.Metadata.ResponseId);
        Assert.Equal(11, result.Metadata.InputTokens);
        Assert.Equal(7, result.Metadata.OutputTokens);

        var request = Assert.Single(handler.Requests);
        Assert.Contains("/chat/completions", request.RequestUri!.AbsolutePath);
        Assert.Contains("\"configured-model\"", handler.LastBody);
        Assert.Contains("json_object", handler.LastBody);
    }

    [Fact]
    public async Task ModelOverride_IsUsedForTheRequest()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Completion(ValidArtifact)));
        await Create(handler).GenerateAsync(Request() with { ModelOverride = "override-model" });
        Assert.Contains("\"override-model\"", handler.LastBody);
    }

    [Fact]
    public async Task MissingKey_FailsAsNotConfiguredWithoutAnyNetworkCall()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Completion(ValidArtifact)));
        var error = await Assert.ThrowsAsync<AgentException>(() => Create(handler, key: null).GenerateAsync(Request()));
        Assert.Equal(AgentErrorKind.NotConfigured, error.Kind);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(429, "{\"error\":{\"message\":\"Rate limit\",\"type\":\"rate_limit_exceeded\"}}", AgentErrorKind.RateLimited)]
    [InlineData(429, "{\"error\":{\"message\":\"You exceeded your quota\",\"code\":\"insufficient_quota\"}}", AgentErrorKind.QuotaExceeded)]
    [InlineData(401, "{\"error\":{\"message\":\"Incorrect API key\"}}", AgentErrorKind.Authentication)]
    [InlineData(403, "{\"error\":{\"message\":\"forbidden\"}}", AgentErrorKind.Authentication)]
    [InlineData(400, "{\"error\":{\"message\":\"bad\"}}", AgentErrorKind.InvalidRequest)]
    [InlineData(500, "{\"error\":{\"message\":\"oops\"}}", AgentErrorKind.Transient)]
    [InlineData(503, "{\"error\":{\"message\":\"unavailable\"}}", AgentErrorKind.Transient)]
    public async Task HttpFailures_AreClassifiedAndNeverRetriedByTheSdk(int status, string body, AgentErrorKind expected)
    {
        var handler = new StubHandler(_ => Json((HttpStatusCode)status, body));
        var error = await Assert.ThrowsAsync<AgentException>(() => Create(handler).GenerateAsync(Request()));

        Assert.Equal(expected, error.Kind);
        Assert.Single(handler.Requests);
        Assert.DoesNotContain(Key, error.ToString());
    }

    [Fact]
    public async Task RateLimit_HonoursRetryAfterHeader()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.TooManyRequests, "{\"error\":{\"message\":\"slow down\"}}", ("Retry-After", "7")));
        var error = await Assert.ThrowsAsync<AgentException>(() => Create(handler).GenerateAsync(Request()));
        Assert.Equal(TimeSpan.FromSeconds(7), error.RetryAfter);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"summary\":\"\"}")]
    [InlineData("")]
    public async Task MalformedModelOutput_IsInvalidResponse(string content)
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Completion(content)));
        var error = await Assert.ThrowsAsync<AgentException>(() => Create(handler).GenerateAsync(Request()));
        Assert.Equal(AgentErrorKind.InvalidResponse, error.Kind);
    }

    [Fact]
    public async Task OverlongProse_IsTrimmedButFileCountStaysStrict()
    {
        var longText = new string('x', 5000);
        var body = System.Text.Json.JsonSerializer.Serialize(new { summary = longText, artifacts = new[] { longText }, risks = Array.Empty<string>(), openQuestions = Array.Empty<string>(), proposedChanges = Array.Empty<string>(), tests = Array.Empty<string>(), decisions = Array.Empty<string>(), proposedFiles = Array.Empty<object>() });
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Completion(body)));
        var result = await Create(handler).GenerateAsync(Request());
        Assert.Equal(2000, result.Artifact.Summary.Length);
        Assert.Equal(1000, result.Artifact.Artifacts[0].Length);
    }

    [Fact]
    public async Task ApiKey_IsSentAsBearerButNeverInTheBodyOrErrors()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Completion(ValidArtifact)));
        await Create(handler).GenerateAsync(Request());
        Assert.Contains(Key, handler.Requests[0].Headers.Authorization!.Parameter);
        Assert.DoesNotContain(Key, handler.LastBody);
    }

    [Fact]
    public async Task OversizedPrompt_IsRejectedBeforeTheNetwork()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Completion(ValidArtifact)));
        var error = await Assert.ThrowsAsync<AgentException>(() =>
            Create(handler, configure: o => o.MaxPromptBytes = 100).GenerateAsync(Request()));
        Assert.Equal(AgentErrorKind.InvalidRequest, error.Kind);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task InsecureEndpoint_IsRejected()
    {
        var handler = new StubHandler(_ => Json(HttpStatusCode.OK, Completion(ValidArtifact)));
        var error = await Assert.ThrowsAsync<AgentException>(() =>
            Create(handler, configure: o => o.Endpoint = "http://example.com/v1").GenerateAsync(Request()));
        Assert.Equal(AgentErrorKind.NotConfigured, error.Kind);
    }
}

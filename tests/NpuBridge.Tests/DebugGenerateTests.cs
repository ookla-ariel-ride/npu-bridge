using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Backends.Fake;

namespace NpuBridge.Tests;

public class DebugGenerateTests
{
    [Fact]
    public async Task Generates_and_reports_metrics()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["Hello", " ", "world"] });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync("/debug/generate",
            new { prompt = "say hi", system = "be brief", temperature = 0.3, top_p = 0.8, top_k = 10 });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("Hello world", root.GetProperty("text").GetString());
        Assert.Equal("Complete", root.GetProperty("status").GetString());
        Assert.Equal(3, root.GetProperty("progress_callbacks").GetInt32());
        Assert.Equal(11, root.GetProperty("chars").GetInt32());
        Assert.Equal(6, root.GetProperty("prompt_chars").GetInt32());
        Assert.Equal(6, root.GetProperty("usable_prompt_chars").GetInt32());
        Assert.True(root.GetProperty("total_ms").GetDouble() >= 0);
        Assert.True(root.GetProperty("ttft_ms").GetDouble() >= 0);

        var call = Assert.Single(fake.Calls);
        Assert.Equal("be brief", call.SystemPrompt);
        Assert.Equal(0.3f, call.Sampling?.Temperature);
        Assert.Equal(0.8f, call.Sampling?.TopP);
        Assert.Equal(10, call.Sampling?.TopK);
        Assert.Equal(0, fake.ActiveContexts);
    }

    [Fact]
    public async Task Preflight_is_omitted_when_backend_lacks_it()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Capabilities = BackendCapabilities.None, Responder = _ => ["x"] });
        await using var host = await BridgeTestHost.StartAsync(fake);
        var response = await host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "p" });
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(doc.RootElement.TryGetProperty("usable_prompt_chars", out _));
    }

    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("::ffff:192.168.1.2")]
    public async Task Non_loopback_callers_get_403(string remote)
    {
        await using var host = await BridgeTestHost.StartAsync(remoteAddress: System.Net.IPAddress.Parse(remote));
        var response = await host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("loopback_only", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(host.Fake.Calls);
    }

    [Fact]
    public async Task Unknown_remote_address_is_rejected()
    {
        // TestServer supplies no address; the host normally simulates loopback. Passing IPAddress.None
        // exercises the fail-closed branch for "no usable address".
        await using var host = await BridgeTestHost.StartAsync(remoteAddress: System.Net.IPAddress.None);
        var response = await host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Ipv6_loopback_is_accepted()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["ok"] });
        await using var host = await BridgeTestHost.StartAsync(fake, remoteAddress: System.Net.IPAddress.IPv6Loopback);
        var response = await host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "x" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Missing_prompt_is_400()
    {
        await using var host = await BridgeTestHost.StartAsync();
        var response = await host.Client.PostAsJsonAsync("/debug/generate", new { system = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("missing_prompt", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Not_ready_backend_is_503()
    {
        var fake = new FakeBackend(new FakeBackendOptions { InitFailure = new InvalidOperationException("nope") });
        await using var host = await BridgeTestHost.StartAsync(fake);
        var response = await host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "x" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("model_unavailable", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Non_complete_status_is_reported_not_hidden()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["a"], FailAfterTokens = 1, FailureStatus = GenerationStatus.PromptLargerThanContext });
        await using var host = await BridgeTestHost.StartAsync(fake);
        var response = await host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "x" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("PromptLargerThanContext", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(0, fake.ActiveContexts);
    }

    [Fact]
    public async Task Backend_exception_is_502_and_context_is_disposed()
    {
        var fake = new FakeBackend(new FakeBackendOptions { Responder = _ => ["a"], FailAfterTokens = 0, FailureException = new InvalidOperationException("kaboom") });
        await using var host = await BridgeTestHost.StartAsync(fake);
        var response = await host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "x" });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(0, fake.ActiveContexts);
    }

    [Fact]
    public async Task Create_context_failure_is_502_not_500()
    {
        var backend = new UnavailableBackend("x", "X", "cannot create");
        await using var host = await BridgeTestHost.StartAsync(new ThrowingContextBackend());
        var response = await host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "x" });
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains("cannot create", doc.RootElement.GetProperty("error").GetProperty("message").GetString(), StringComparison.Ordinal);
        await backend.DisposeAsync();
    }

    [Fact]
    public async Task Foreign_cancellation_is_502_with_openai_error_envelope()
    {
        using var foreignCancellation = new CancellationTokenSource();
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["Hello", " world"],
            FailAfterTokens = 1,
            FailureException = new OperationCanceledException(
                "adapter let a foreign cancellation escape", foreignCancellation.Token),
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "x" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("error");
        Assert.Equal("server_error", error.GetProperty("type").GetString());
        Assert.Equal("backend_error", error.GetProperty("code").GetString());
        Assert.True(error.TryGetProperty("message", out _));
        Assert.True(error.TryGetProperty("param", out _));
        Assert.Equal(0, fake.ActiveContexts);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task A_queued_client_disconnect_returns_empty_without_an_error_log_or_context_leak()
    {
        var capture = new CapturingLoggerProvider();
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => ["ok"],
            StartGate = startGate,
        });
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        var running = host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "running" });
        await TestWait.UntilAsync(() => fake.Calls.Count == 1);

        using var cts = new CancellationTokenSource();
        var queued = host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "queued" }, cts.Token);
        await TestWait.UntilAsync(() => host.Scheduler.QueueDepth == 1);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        await TestWait.UntilAsync(() => host.Scheduler.QueueDepth == 0);

        startGate.SetResult();
        using var response = await running;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await TestWait.UntilAsync(() => fake.ActiveContexts == 0);
        Assert.Single(fake.Calls);
        Assert.DoesNotContain(capture.Records, r => r.Level >= LogLevel.Error);
        host.AssertNoLeak();
    }

    [Fact]
    public async Task Client_disconnect_cancels_and_disposes_the_context()
    {
        var capture = new CapturingLoggerProvider();
        var deltaGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Responder = _ => Enumerable.Repeat("tok ", 200),
            DeltaGate = deltaGate,
        });
        await using var host = await BridgeTestHost.StartAsync(fake, loggerProvider: capture);

        using var cts = new CancellationTokenSource();
        var post = host.Client.PostAsJsonAsync("/debug/generate", new { prompt = "x" }, cts.Token);

        await TestWait.UntilAsync(() => fake.DeltasEmitted == 1);
        await cts.CancelAsync();
        try
        {
            await TestWait.UntilAsync(() => fake.CancellationsObserved == 1);
        }
        finally
        {
            deltaGate.SetResult();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => post);

        await TestWait.UntilAsync(() => fake.ActiveContexts == 0);
        Assert.Equal(0, fake.ActiveContexts);
        Assert.Single(fake.Calls);
        Assert.DoesNotContain(capture.Records, r => r.Level >= LogLevel.Error);
        host.AssertNoLeak();
    }

    /// <summary>Initializes fine but every CreateContext throws, to exercise the endpoint's error path.</summary>
    private sealed class ThrowingContextBackend : ILanguageModelBackend
    {
        public string ModelId => "throwing";

        public string DisplayName => "Throwing";

        public BackendCapabilities Capabilities => BackendCapabilities.None;

        public NpuBridge.Tokenizers.ITokenCounter TokenCounter => NpuBridge.Tokenizers.CharEstimateTokenCounter.Instance;

        public IReadOnlyDictionary<string, object?> Diagnostics { get; } = new Dictionary<string, object?>();

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public IModelContext CreateContext(string? systemPrompt) => throw new InvalidOperationException("cannot create");

        public int? GetUsablePromptLength(IModelContext context, string prompt) => null;

        public Task<GenerationResult> GenerateAsync(IModelContext context, string prompt, SamplingOptions? sampling, Action<string> onDelta, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using System.Net;
using System.Text.Json;
using NpuBridge.Backends;
using NpuBridge.Backends.Fake;
using NpuBridge.Configuration;
using NpuBridge.Hosting;

namespace NpuBridge.Tests;

public class HealthzTests
{
    [Fact]
    public async Task Ready_backend_reports_200_with_expected_fields()
    {
        var fake = new FakeBackend(new FakeBackendOptions { ContextWindowTokens = 3_581 });
        await using var host = await BridgeTestHost.StartAsync(
            fake,
            options: new BridgeOptions { Backend = BackendKind.Fake, QueueCapacity = 7 },
            identity: new StaticProcessIdentity("NpuBridge_1.0.0.0_arm64__abc", "NpuBridge_abc"));

        var response = await host.Client.GetAsync("/healthz");
        var json = await ReadJson(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Equal("ready", json.GetProperty("status").GetString());
        Assert.Equal("fake", json.GetProperty("backend").GetString());
        Assert.Equal("fake", json.GetProperty("model").GetString());
        Assert.True(json.GetProperty("package_identity").GetBoolean());
        Assert.Equal("NpuBridge_abc", json.GetProperty("package_family_name").GetString());
        Assert.Equal(7, json.GetProperty("queue_capacity").GetInt32());
        Assert.Equal(0, json.GetProperty("queue_depth").GetInt32());
        Assert.Equal(0, json.GetProperty("contexts_cached").GetInt32());
        Assert.Equal(4, json.GetProperty("context_cache_capacity").GetInt32());
        Assert.Equal(0, json.GetProperty("context_cache_hits").GetInt64());
        Assert.Equal(0, json.GetProperty("context_cache_misses").GetInt64());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("last_generation").ValueKind);
        Assert.Equal(0, json.GetProperty("consecutive_backend_faults").GetInt32());
        Assert.Equal(3_581, json.GetProperty("context_window_tokens").GetInt32());
        AssertCapabilities(json, fake.Capabilities,
            "sampling_options", "system_prompt_context", "prompt_length_preflight", "cancellation");
        Assert.Equal(1000, json.GetProperty("first_keep_alive_ms").GetInt32());
        Assert.Equal(15000, json.GetProperty("keep_alive_interval_ms").GetInt32());
        Assert.False(json.GetProperty("first_run_compile_likely").GetBoolean());
        Assert.False(json.TryGetProperty("error", out _), "error should be omitted when null");
        Assert.True(json.GetProperty("diagnostics").GetProperty("fake").GetBoolean());
        Assert.False(string.IsNullOrEmpty(json.GetProperty("version").GetString()));
    }

    [Fact]
    public async Task Ready_backend_with_unknown_context_window_writes_null()
    {
        await using var host = await BridgeTestHost.StartAsync(new FakeBackend());

        var json = await ReadJson(await host.Client.GetAsync("/healthz"));

        Assert.True(json.TryGetProperty("context_window_tokens", out var contextWindowTokens));
        Assert.Equal(JsonValueKind.Null, contextWindowTokens.ValueKind);
    }

    [Fact]
    public async Task Ready_backend_with_reduced_capabilities_reports_only_supported_names()
    {
        var fake = new FakeBackend(new FakeBackendOptions
        {
            Capabilities = BackendCapabilities.SystemPromptContext | BackendCapabilities.Cancellation,
        });
        await using var host = await BridgeTestHost.StartAsync(fake);

        var json = await ReadJson(await host.Client.GetAsync("/healthz"));

        AssertCapabilities(json, fake.Capabilities, "system_prompt_context", "cancellation");
    }

    [Fact]
    public async Task Loading_backend_reports_503_then_200_once_initialized()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions { InitGate = gate });

        await using var host = await BridgeTestHost.StartAsync(fake, waitForReady: false);

        var loading = await host.Client.GetAsync("/healthz");
        var loadingJson = await ReadJson(loading);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, loading.StatusCode);
        Assert.Equal("loading", loadingJson.GetProperty("status").GetString());
        Assert.Equal("10", loading.Headers.RetryAfter?.ToString());
        Assert.False(loadingJson.GetProperty("first_run_compile_likely").GetBoolean());
        Assert.True(loadingJson.GetProperty("loading_seconds").GetDouble() >= 0);

        gate.SetResult();
        await host.Lifecycle.Initialization;

        var ready = await host.Client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("ready", (await ReadJson(ready)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Long_load_flags_first_run_compile()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeBackend(new FakeBackendOptions { InitGate = gate });
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero));

        await using var host = await BridgeTestHost.StartAsync(fake, time: clock, waitForReady: false);
        clock.Advance(TimeSpan.FromSeconds(90));

        var json = await ReadJson(await host.Client.GetAsync("/healthz"));
        Assert.Equal("loading", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("first_run_compile_likely").GetBoolean());
        Assert.Equal(90, json.GetProperty("loading_seconds").GetDouble());

        gate.SetResult();
        await host.Lifecycle.Initialization;
    }

    [Fact]
    public async Task Failed_backend_reports_503_with_reason()
    {
        var fake = new FakeBackend(new FakeBackendOptions { InitFailure = new InvalidOperationException("no NPU here") });

        await using var host = await BridgeTestHost.StartAsync(fake);

        var response = await host.Client.GetAsync("/healthz");
        var json = await ReadJson(response);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("failed", json.GetProperty("status").GetString());
        Assert.Equal("no NPU here", json.GetProperty("error").GetString());
        Assert.Null(response.Headers.RetryAfter);
    }

    [Fact]
    public async Task Unavailable_backend_explains_itself()
    {
        var backend = new UnavailableBackend("phi-silica", "Phi Silica", "not built yet");
        await using var host = await BridgeTestHost.StartAsync(backend, new BridgeOptions { Backend = BackendKind.PhiSilica });

        var json = await ReadJson(await host.Client.GetAsync("/healthz"));
        Assert.Equal("failed", json.GetProperty("status").GetString());
        Assert.Equal("phi-silica", json.GetProperty("backend").GetString());
        AssertCapabilities(json, backend.Capabilities);
        Assert.Equal("not built yet", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Without_identity_registration_health_reports_no_identity()
    {
        await using var host = await BridgeTestHost.StartAsync(identity: new StaticProcessIdentity(null, null));
        var json = await ReadJson(await host.Client.GetAsync("/healthz"));
        Assert.False(json.GetProperty("package_identity").GetBoolean());
        Assert.False(json.TryGetProperty("package_family_name", out _));
    }

    [Fact]
    public async Task Diagnostics_keys_are_emitted_verbatim()
    {
        var fake = new FakeBackend();
        ((Dictionary<string, object?>)fake.Diagnostics)["lafStatus"] = "AvailableWithoutToken";
        await using var host = await BridgeTestHost.StartAsync(fake);

        var json = await ReadJson(await host.Client.GetAsync("/healthz"));
        Assert.Equal("AvailableWithoutToken", json.GetProperty("diagnostics").GetProperty("lafStatus").GetString());
    }

    private static void AssertCapabilities(JsonElement json, BackendCapabilities capabilities, params string[] expected)
    {
        Assert.Equal(expected, capabilities.ToHealthzNames());
        Assert.Equal(expected, json.GetProperty("capabilities").EnumerateArray().Select(value => value.GetString()!));
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// The keep-alive timings are read from the registered <c>StreamingOptions</c>, not from the
    /// defaults: <c>scripts/smoke.ps1</c> reads them off the running server so its D52 margin cannot
    /// drift from the number the code uses, and that only holds if the endpoint reports what is live.
    /// </summary>
    [Fact]
    public async Task Keep_alive_timings_report_the_registered_streaming_options()
    {
        await using var host = await BridgeTestHost.StartAsync(
            keepAliveInterval: TimeSpan.FromMilliseconds(20),
            firstKeepAliveDelay: TimeSpan.FromMilliseconds(7));

        var json = await ReadJson(await host.Client.GetAsync("/healthz"));

        Assert.Equal(7, json.GetProperty("first_keep_alive_ms").GetInt32());
        Assert.Equal(20, json.GetProperty("keep_alive_interval_ms").GetInt32());
    }
}

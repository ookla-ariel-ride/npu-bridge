using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NpuBridge.Backends;
using NpuBridge.Configuration;
using NpuBridge.Hosting;

namespace NpuBridge.Api;

public static class BridgeEndpoints
{
    public static readonly string Version =
        typeof(BridgeEndpoints).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BridgeEndpoints).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    public static IEndpointRouteBuilder MapNpuBridge(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet("/healthz", HealthEndpoint.Get);
        app.MapGet("/v1/models", ModelsEndpoint.List);
        app.MapGet("/v1/models/{id}", ModelsEndpoint.Get);
        app.MapNpuBridgeChat();
        app.MapNpuBridgeCompletions();
        app.MapNpuBridgeDebug();

        // Anything else under /v1 gets an OpenAI-shaped 404 (or 405 for a known path) instead of an empty body.
        app.MapFallback("/v1/{**path}", (HttpContext http, string? path) => FallbackEndpoint.Handle(http, path));

        return app;
    }
}

internal static class FallbackEndpoint
{
    /// <summary>Known /v1 paths and the methods they accept, so a wrong method yields 405 rather than 404.</summary>
    private static readonly (string Prefix, string Allow)[] KnownPaths =
    [
        ("models", "GET"),
        ("chat/completions", "POST"),
        ("completions", "POST"),
    ];

    public static IResult Handle(HttpContext http, string? path)
    {
        path ??= string.Empty;
        foreach (var (prefix, allow) in KnownPaths)
        {
            var isKnownPath = path.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
            var methodAllowed = allow.Split(',').Any(m => m.Trim().Equals(http.Request.Method, StringComparison.OrdinalIgnoreCase));

            // A known path with an allowed method that still fell through is an unknown sub-route → 404.
            if (isKnownPath && !methodAllowed)
            {
                http.Response.Headers.Allow = allow;
                return OpenAiError.Result(StatusCodes.Status405MethodNotAllowed,
                    $"Method {http.Request.Method} is not allowed for /v1/{path}.", OpenAiError.InvalidRequest, code: "method_not_allowed");
            }
        }

        return OpenAiError.NotFoundResult($"Unknown endpoint /v1/{path}.", code: "unknown_endpoint");
    }
}

/// <summary>Wire shape of <c>GET /healthz</c>. Property names become snake_case on the wire.</summary>
/// <param name="QueueDepth">
/// <see cref="GenerationScheduler.QueueDepth"/> (chunk 8): callers still genuinely waiting for the
/// worker right now — dequeued-and-running excluded, and a job whose own request was already cancelled
/// while queued excluded too, from the instant its token fired rather than from whenever the worker
/// drains to it (fix round 1, Finding 3). That distinction is not cosmetic, which is why the
/// scheduler keeps a live counter rather than reading the channel's own count: a client that issues and
/// abandons requests against one long generation would otherwise fill every slot with jobs nobody is
/// waiting for, shedding load for the rest of that generation and inflating the <c>Retry-After</c>
/// computed off the same number. The channel slot itself is still held until the worker reaches it,
/// which no caller of the scheduler can observe.
/// </param>
public sealed record HealthResponse(
    string Status,
    string Backend,
    string Model,
    string Version,
    double? LoadingSeconds,
    bool FirstRunCompileLikely,
    bool PackageIdentity,
    string? PackageFamilyName,
    int QueueDepth,
    int QueueCapacity,
    int ContextsCached,
    int ContextCacheCapacity,
    long ContextCacheHits,
    long ContextCacheMisses,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] GenerationHealthSnapshot? LastGeneration,
    int ConsecutiveBackendFaults,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? ContextWindowTokens,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<string>? Capabilities,
    int FirstKeepAliveMs,
    int KeepAliveIntervalMs,
    string? Error,
    IReadOnlyDictionary<string, object?> Diagnostics);

internal static class HealthEndpoint
{
    public static IResult Get(
        HttpContext http,
        BackendLifecycle lifecycle,
        BridgeOptions options,
        IProcessIdentity identity,
        TimeProvider time,
        ContextCache cache,
        StreamingOptions streaming,
        GenerationScheduler scheduler,
        GenerationHealth generationHealth)
    {
        var snapshot = lifecycle.Snapshot;
        var now = time.GetUtcNow();
        var elapsed = snapshot.LoadingElapsed(now);

        var health = generationHealth.Snapshot;
        var lastGeneration = health.LastGeneration;
        var consecutiveBackendFaults = health.ConsecutiveBackendFaults;
        var degraded = snapshot.Kind == BackendStateKind.Ready && consecutiveBackendFaults >= 2;
        var status = degraded
            ? "degraded"
            : snapshot.Kind switch
            {
                BackendStateKind.Ready => "ready",
                BackendStateKind.Loading => "loading",
                BackendStateKind.Failed => "failed",
                _ => "not_started",
            };

        var body = new HealthResponse(
            Status: status,
            Backend: options.Backend.ToConfigName(),
            Model: lifecycle.Backend.ModelId,
            Version: BridgeEndpoints.Version,
            LoadingSeconds: elapsed is { } e ? Math.Round(e.TotalSeconds, 1) : null,
            FirstRunCompileLikely: snapshot.Kind == BackendStateKind.Loading && elapsed >= BackendLifecycle.FirstRunCompileThreshold,
            PackageIdentity: identity.HasPackageIdentity,
            PackageFamilyName: identity.PackageFamilyName,
            QueueDepth: scheduler.QueueDepth,
            QueueCapacity: options.QueueCapacity,
            ContextsCached: cache.Count,
            ContextCacheCapacity: cache.Capacity,
            ContextCacheHits: cache.Hits,
            ContextCacheMisses: cache.Misses,
            LastGeneration: lastGeneration,
            ConsecutiveBackendFaults: consecutiveBackendFaults,
            ContextWindowTokens: lifecycle.Backend.ContextWindowTokens,
            Capabilities: lifecycle.Backend.Capabilities.ToHealthzNames(),
            FirstKeepAliveMs: (int)streaming.FirstKeepAliveDelay.TotalMilliseconds,
            KeepAliveIntervalMs: (int)streaming.KeepAliveInterval.TotalMilliseconds,
            Error: degraded ? lastGeneration?.Error : snapshot.Error,
            Diagnostics: lifecycle.Backend.Diagnostics);

        http.Response.Headers.CacheControl = "no-store";
        var code = snapshot.Kind == BackendStateKind.Ready && !degraded
            ? StatusCodes.Status200OK
            : StatusCodes.Status503ServiceUnavailable;
        if (snapshot.Kind == BackendStateKind.Loading)
        {
            http.Response.Headers.RetryAfter = "10";
        }

        return Results.Json(body, JsonDefaults.Options, statusCode: code);
    }
}

public sealed record ModelObject(string Id, long Created, string OwnedBy)
{
    [JsonPropertyName("object")]
    public string ObjectType { get; init; } = "model";
}

public sealed record ModelList(IReadOnlyList<ModelObject> Data)
{
    [JsonPropertyName("object")]
    public string ObjectType { get; init; } = "list";
}

internal static class ModelsEndpoint
{
    // Fixed so /v1/models is stable across restarts; clients cache it.
    private static readonly long CreatedUnix = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    public static IResult List(BackendLifecycle lifecycle) =>
        Results.Json(new ModelList([Describe(lifecycle.Backend)]), JsonDefaults.Options);

    public static IResult Get(string id, BackendLifecycle lifecycle) =>
        string.Equals(id, lifecycle.Backend.ModelId, StringComparison.OrdinalIgnoreCase)
            ? Results.Json(Describe(lifecycle.Backend), JsonDefaults.Options)
            : OpenAiError.NotFoundResult($"The model '{id}' does not exist. This server exposes '{lifecycle.Backend.ModelId}'.",
                code: "model_not_found");

    private static ModelObject Describe(ILanguageModelBackend backend) => new(backend.ModelId, CreatedUnix, "npu-bridge");
}

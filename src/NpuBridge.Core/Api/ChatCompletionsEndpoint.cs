using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Configuration;

namespace NpuBridge.Api;

/// <summary>
/// Remembers which accepted-but-ignored request parameters have already been warned about, so the log
/// carries one line per parameter for the life of the process rather than one per request. A DI
/// singleton rather than a static field: the process has exactly one host, so it is still once per
/// process, and a test host gets its own instance instead of inheriting another test's state.
/// </summary>
public sealed class IgnoredParameterLog
{
    private readonly ConcurrentDictionary<string, byte> _warned = new(StringComparer.Ordinal);

    /// <summary>True the first time a given parameter name is seen, false forever after. Thread-safe.</summary>
    public bool ShouldWarn(string parameter) => _warned.TryAdd(parameter, 0);
}

public static class ChatCompletionsEndpoints
{
    public static IEndpointRouteBuilder MapNpuBridgeChat(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/v1/chat/completions", ChatCompletionsEndpoint.PostAsync);
        return app;
    }
}

/// <summary>
/// <c>POST /v1/chat/completions</c>. Phase one — body, validation, readiness, placement, rendering —
/// belongs to <see cref="ChatRequestPreparer"/> and is shared by both response shapes; this type then
/// either hands a <c>stream: true</c> request to <see cref="ChatCompletionsStreamEndpoint"/> or runs
/// the non-streaming phase two through <see cref="JsonPipeline"/>: a single generation on a context
/// from <see cref="ConversationSession"/> — cached when the transcript extends one the cache holds,
/// fresh otherwise — shaped into one OpenAI response. The context lookup, the preflight and the
/// generation are one scheduled unit behind <see cref="GenerationScheduler"/> (chunk 8), on both shapes
/// identically: all three are calls on the one shared model handle, so none of them may run while
/// another request's generation is live.
///
/// That phase is <see cref="JsonPipeline"/>'s rather than this type's because
/// <see cref="CompletionsEndpoint"/> ran a byte-identical copy of it (D91 extracted the streaming half
/// and left this one copied), which is the shape D56 and D57 drifted out of. What is left here is the
/// wire shape: the request DTO's preparation call, the stream branch, and the
/// <see cref="ChatCompletionResponse"/> built from the pipeline's <see cref="JsonReply"/>.
/// </summary>
internal sealed class ChatCompletionsEndpoint
{
    // Never instantiated: the type exists so the handler has an ILogger<T> category of its own, which a
    // static class cannot have (a static type is not a legal generic type argument).
    private ChatCompletionsEndpoint()
    {
    }

    public static async Task<IResult> PostAsync(
        HttpContext http,
        BackendLifecycle lifecycle,
        BridgeOptions options,
        StreamingOptions streaming,
        IgnoredParameterLog ignoredLog,
        ContextCache cache,
        GenerationScheduler scheduler,
        GenerationHealth generationHealth,
        TimeProvider time,
        ILogger<ChatCompletionsEndpoint> logger,
        ILogger<ChatCompletionsStreamEndpoint> streamLogger)
    {
        var preparation = await ChatRequestPreparer
            .PrepareAsync(http, lifecycle, options, ignoredLog, logger)
            .ConfigureAwait(false);

        // Preparation is shared, and it fails before a single byte is written — so a streamed request
        // that fails it still gets the ordinary JSON error with its ordinary status code. Only once the
        // stream's headers are committed does error handling have to move into the stream.
        if (preparation.IsFailed)
        {
            return preparation.Failure;
        }

        var prepared = preparation.Prepared;

        // The branch. Everything above ran identically for both shapes; everything below is the
        // single-JSON-object generation phase, whose SSE sibling lives in ChatCompletionsStreamEndpoint.
        // The streaming phase usually writes the response itself and leaves nothing to return; it
        // returns a result only when it failed before writing a byte, and then the status line is still
        // ours to set, so the client gets the ordinary error instead of a 200 stream that says "stop".
        if (prepared.Request.Stream == true)
        {
            return await ChatCompletionsStreamEndpoint
                .StreamAsync(http, prepared, options, streaming, cache, scheduler, generationHealth, time, streamLogger)
                .ConfigureAwait(false) ?? Results.Empty;
        }

        // Everything from here to the response body is JsonPipeline's, shared with /v1/completions:
        // the scheduled closure (Acquire, the lease publication, the cut race, the truncation retry),
        // the scheduler admission mapping, the cut, GenerationOutcome, the cache decision, the usage
        // estimate, the log line and both catch clauses. This endpoint's whole remaining job is the
        // wire shape it serves, which is the factory below.
        return await JsonPipeline.RunAsync(http, prepared, options, cache, scheduler, generationHealth, time, logger, "chat-json",
            reply => new ChatCompletionResponse(
                Id: reply.Id,
                Created: reply.Created,
                Model: reply.Model,
                Choices:
                [
                    new ChatCompletionChoice(
                        0,
                        new ChatCompletionResponseMessage("assistant", reply.Content) { ToolCalls = reply.ToolCalls },
                        reply.FinishReason),
                ],
                Usage: reply.Usage)).ConfigureAwait(false);
    }
}

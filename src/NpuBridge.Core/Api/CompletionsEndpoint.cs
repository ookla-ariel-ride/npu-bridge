using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Configuration;

namespace NpuBridge.Api;

public static class CompletionsEndpoints
{
    public static IEndpointRouteBuilder MapNpuBridgeCompletions(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.MapPost("/v1/completions", CompletionsEndpoint.PostAsync);
        return app;
    }
}

/// <summary>
/// <c>POST /v1/completions</c> (chunk 8 task 3, PLAN §2.2): the legacy text-completion shape.
/// <c>prompt</c> — a string, or a single-element array — is wrapped into one user message by
/// <see cref="ChatRequestPreparer.PrepareForCompletionAsync"/> and run through the identical pipeline
/// <c>/v1/chat/completions</c> uses from the model-id check onward: readiness, placement, rendering,
/// <see cref="ConversationSession"/>/<see cref="ContextCache"/>, the scheduler, the client-side cut,
/// <see cref="GenerationPipeline"/>, <see cref="GenerationOutcome"/> and
/// <see cref="ChatRequestMetrics.LogRequest"/> are all the exact same code the chat shape runs — a
/// second worker thread against the one shared model handle is exactly the bug chunk 8 exists to
/// close, wherever the request came from. <c>tools</c> do not exist on this wire shape at all, so
/// unlike <see cref="ChatCompletionsEndpoint"/> there is no tool-call branch to consider here.
///
/// "The exact same code" is now literally that: chunk 8 shipped it as a byte-identical copy of the
/// chat endpoint's scheduled closure, and <see cref="JsonPipeline"/> is that copy folded into one,
/// including the two controller rulings task 2 already paid for —
/// <see cref="ConversationSession.Acquire"/> runs inside the scheduled closure (never outside it, so it
/// cannot race a running generation for the one shared handle), and the lease is published to the
/// pipeline's own variable the instant <c>Acquire</c> hands it over, never carried back only on the
/// closure's return value (D85).
/// </summary>
internal sealed class CompletionsEndpoint
{
    // Never instantiated: the type exists so the handler has an ILogger<T> category of its own, which a
    // static class cannot have (a static type is not a legal generic type argument).
    private CompletionsEndpoint()
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
        ILogger<CompletionsEndpoint> logger,
        ILogger<CompletionsStreamEndpoint> streamLogger)
    {
        var preparation = await ChatRequestPreparer
            .PrepareForCompletionAsync(http, lifecycle, options, ignoredLog, logger)
            .ConfigureAwait(false);

        if (preparation.IsFailed)
        {
            return preparation.Failure;
        }

        var prepared = preparation.Prepared;

        if (prepared.Request.Stream == true)
        {
            return await CompletionsStreamEndpoint
                .StreamAsync(http, prepared, options, streaming, cache, scheduler, generationHealth, time, streamLogger)
                .ConfigureAwait(false) ?? Results.Empty;
        }

        // The generation phase is JsonPipeline's, shared byte for byte with /v1/chat/completions --
        // which is the point: a second copy of it is what D56 and D57 record drifting. This endpoint
        // keeps only the legacy wire shape, built below from the pipeline's JsonReply.
        return await JsonPipeline.RunAsync(http, prepared, options, cache, scheduler, generationHealth, time, logger, "completions-json",
            reply => new CompletionResponse(
                Id: reply.Id,
                Created: reply.Created,
                Model: reply.Model,
                // Content is null only where the reply was reshaped into tool_calls, and `tools` does
                // not exist on this wire shape at all -- the catalog phase one builds for it is always
                // null, so that branch cannot fire here.
                Choices: [new CompletionChoice(reply.Content!, 0, reply.FinishReason)],
                Usage: reply.Usage)).ConfigureAwait(false);
    }
}

using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Configuration;

namespace NpuBridge.Api;

/// <summary>
/// Streaming phase two of <c>POST /v1/completions</c>: the server-sent-event sibling of
/// <see cref="CompletionsEndpoint"/>'s second half, and the <c>text_completion</c> counterpart of
/// <see cref="ChatCompletionsStreamEndpoint"/>. Phase one is shared — this type is only ever reached
/// with a <see cref="PreparedChatRequest"/> built from a wrapped <c>prompt</c>, so it never validates
/// the request.
///
/// Every rule <see cref="ChatCompletionsStreamEndpoint"/> documents applies here unchanged: headers
/// commit on the first frame that has something to say, so a failure before it is the ordinary HTTP
/// status and body (D52); the exit is cancel, drain, settle, in that order, because the context is a
/// live handle the generation task may still be writing to (D51); the scheduled closure never touches
/// <c>http.Response</c>, so the request thread alone applies the truncated-turns header (chunk 8 fix
/// round 2); and the lease is published to this method's own <c>lease</c> variable the instant
/// <see cref="ConversationSession.Acquire"/> hands it over, never carried back only on the closure's
/// return value (D43 + D51).
///
/// It is simpler than the chat shape in exactly one way: <c>tools</c> do not exist on this wire shape,
/// so there is no buffer-the-whole-reply branch to consider, no role chunk (a text completion has no
/// role to open), and no tool-call parse. Every delta is written out as soon as the cutter releases it.
/// </summary>
internal sealed class CompletionsStreamEndpoint
{
    // Never instantiated: the type exists so the streaming phase has an ILogger<T> category of its own.
    private CompletionsStreamEndpoint()
    {
    }

    /// <summary>
    /// Writes the stream and returns null, or — when the request failed before a single byte was written
    /// — returns the ordinary JSON error result for the caller to return unchanged.
    /// </summary>
    public static async Task<IResult?> StreamAsync(
        HttpContext http,
        PreparedChatRequest prepared,
        BridgeOptions options,
        StreamingOptions streaming,
        ContextCache cache,
        GenerationScheduler scheduler,
        GenerationHealth generationHealth,
        TimeProvider time,
        ILogger logger)
    {
        var aborted = http.RequestAborted;

        var requestId = prepared.RequestId;
        var backendName = prepared.BackendName;
        var session = new ConversationSession(prepared, cache, options, logger);

        var model = prepared.Backend.ModelId;
        var created = time.GetUtcNow().ToUnixTimeSeconds();
        var includeUsage = prepared.Request.StreamOptions?.IncludeUsage == true;

        // The truncated-turns header, exactly as on the chat shape: applied from the request thread in
        // the last instant before the first frame commits the response, and only while no drop is in
        // progress -- the preflight loop inside Acquire, or the status-driven retry's own
        // TryDropOldestExchange below, both of which clear the flag before the count moves.
        var sse = new SseStream(http.Response, CreateBeforeHeadersHook(session, http.Response));
        var stopwatch = Stopwatch.StartNew();

        // The client-side cut. Runs on the single channel reader, never on the backend's callback
        // thread, exactly as on the chat shape.
        var cutter = new OutputCutter(prepared.Limits);

        var streamed = false;

        // Set when this handler cancels the generation because a limit fired while streaming, read
        // when the status comes back. See ChatCompletionsStreamEndpoint for why this is a fact recorded
        // at the cancel rather than inferred from the cutter afterwards (D62).
        var cancelledByCut = false;

        var queueWaitMs = 0.0;
        var backendCalls = new BackendCallTracker();
        var attemptDurationMs = 0.0;

        // Published from *inside* the scheduled closure -- see the type-level remarks and
        // ChatCompletionsStreamEndpoint's fuller account of why (D43 + D51).
        ContextLease? lease = null;

        // One channel for the whole request, not one per attempt (chunk 8 fix round 1): Acquire() runs
        // inside the scheduled closure alongside the generation, and a --truncate-history retry
        // continues on this same channel rather than re-entering the queue.
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        var sink = DeltaSink.ToChannel(stopwatch, channel.Writer);

        // The current attempt's own cancellation source; not `using` inside the closure, because the
        // caller's finally is what cancels through it on the way out (see ChatCompletionsStreamEndpoint).
        CancellationTokenSource? currentGenerationCts = null;

        // The scheduled attempt, not the generation itself -- unwrapped only where it is actually
        // needed, by ReportSchedulerOutcomeAsync below.
        Task<ScheduleResult<ChatAttemptResult>>? generation = null;

        try
        {
            generation = scheduler.ScheduleAsync(async ct =>
            {
                var attemptStopwatch = Stopwatch.StartNew();
                try
                {
                    while (true)
                {
                    var acquisition = session.Acquire(backendCalls);
                    if (acquisition.Failure is { } refused)
                    {
                        channel.Writer.TryComplete();
                        return ChatAttemptResult.Refused(refused);
                    }

                    var attemptLease = acquisition.Lease!;
                    lease = attemptLease;

                    try
                    {
                        var generationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        Interlocked.Exchange(ref currentGenerationCts, generationCts)?.Dispose();

                        var result = await backendCalls.AwaitAsync(backendCalls.Invoke(() => prepared.Backend.GenerateAsync(
                            attemptLease.Context,
                            attemptLease.Prompt,
                            prepared.Sampling,
                            sink.OnDelta,
                            generationCts.Token))).ConfigureAwait(false);

                        if (result.Status == GenerationStatus.PromptLargerThanContext && session.TryDropOldestExchange())
                        {
                            attemptLease.Dispose();
                            continue;
                        }

                        channel.Writer.TryComplete();
                        return ChatAttemptResult.Generated(result, cancelledByCut, 0, 0);
                    }
                    catch
                    {
                        channel.Writer.TryComplete();
                        attemptLease.Dispose();
                        throw;
                    }
                }
                }
                finally
                {
                    attemptDurationMs = attemptStopwatch.Elapsed.TotalMilliseconds;
                }
            }, aborted);

            // Backstop for a job dropped without ever running (shutdown mid-wait); a harmless no-op on
            // every other path, which completes the channel itself.
            _ = generation.ContinueWith(
                static (_, state) => ((ChannelWriter<string>)state!).TryComplete(),
                channel.Writer,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            // Rejection and a post-shutdown enqueue are both decided synchronously, before the
            // scheduler ever touches the channel (D52).
            if (generation.IsCompleted)
            {
                var immediateResult = await StreamingPipeline.ReportSchedulerOutcomeAsync(
                    generation, sse, http, logger, requestId, backendName, prepared, session, generationHealth, backendCalls, () => attemptDurationMs, aborted)
                    .ConfigureAwait(false);
                if (immediateResult.Handled)
                {
                    return immediateResult.Response;
                }
            }

            // Nothing has been written yet, on purpose: waiting here is what keeps the status line
            // available for a failure that arrives before the first token.
            streamed = await StreamingPipeline.WaitForFirstDeltaAsync(sse, channel.Reader, streaming, time, aborted)
                .ConfigureAwait(false);

            GenerationResult result;
            if (streamed)
            {
                // Deliberately not cancelled by `aborted`: the loop must end when the channel completes,
                // so that `generation` is always reached and always drained below.
                await foreach (var delta in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    var release = cutter.Accept(delta);
                    if (release.Length > 0)
                    {
                        await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage, release, finishReason: null), aborted).ConfigureAwait(false);
                    }

                    if (cutter.StopRequested && !cancelledByCut)
                    {
                        cancelledByCut = true;
                        await GenerationPipeline.CancelGuardedAsync(currentGenerationCts!, logger, requestId, "at the cut").ConfigureAwait(false);
                    }

                    if (cutter.IsCut)
                    {
                        break;
                    }
                }

                if (cancelledByCut)
                {
                    await GenerationPipeline.DrainWithWarningsAsync(
                        generation, options.DrainWarningSeconds, time, logger, requestId, "stream").ConfigureAwait(false);
                }

                var schedulerOutcome = await StreamingPipeline.ReportSchedulerOutcomeAsync(
                    generation, sse, http, logger, requestId, backendName, prepared, session, generationHealth, backendCalls, () => attemptDurationMs, aborted)
                    .ConfigureAwait(false);
                if (schedulerOutcome.Handled)
                {
                    return schedulerOutcome.Response;
                }

                result = schedulerOutcome.Attempt!.Result!;
                queueWaitMs = schedulerOutcome.QueueWaitMs;
            }
            else
            {
                var schedulerOutcome = await StreamingPipeline.ReportSchedulerOutcomeAsync(
                    generation, sse, http, logger, requestId, backendName, prepared, session, generationHealth, backendCalls, () => attemptDurationMs, aborted)
                    .ConfigureAwait(false);
                if (schedulerOutcome.Handled)
                {
                    return schedulerOutcome.Response;
                }

                result = schedulerOutcome.Attempt!.Result!;
                queueWaitMs = schedulerOutcome.QueueWaitMs;
            }

            // A generation was attempted on the truncated transcript, so the header says so, exactly as
            // on the chat shape's stream.
            session.ApplyTruncationHeader(http.Response);

            stopwatch.Stop();
            var cacheLabel = lease!.CacheHit ? "hit" : "miss";
            var tailTurns = lease.TailTurns;
            var promptChars = lease.PromptChars;
            var truncatedTurns = session.DroppedTurns;

            var totalMs = stopwatch.Elapsed.TotalMilliseconds;
            var ttftMs = sink.TtftMs(totalMs);

            GenerationPipeline.LogRawOutput(logger, options, requestId, result);

            if (aborted.IsCancellationRequested)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: 0,
                    cache: cacheLabel, tailTurns: tailTurns, truncatedTurns: truncatedTurns,
                    queueWaitMs: queueWaitMs);
                return null;
            }

            var outcome = GenerationOutcome.Classify(result, cancelledByCut, generationHealth, attemptDurationMs);
            if (outcome.Failure is { } failure)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-",
                    httpStatus: sse.Started ? StatusCodes.Status200OK : failure.StatusCode,
                    cache: cacheLabel, tailTurns: tailTurns, truncatedTurns: truncatedTurns,
                    queueWaitMs: queueWaitMs);
                return await StreamingPipeline.FailAsync(sse, failure, logger, requestId, aborted).ConfigureAwait(false);
            }

            // The held tail: the generation ended without a stop string forming, so characters withheld
            // in case they were its first half are ordinary output after all. Empty when a limit fired,
            // and empty as well when the runtime withheld the answer (never flushed then, exactly as on
            // the chat shape).
            var tail = outcome.Filtered ? string.Empty : cutter.Flush();

            // Read after the flush, never before -- Flush() can be the call that commits the cap (D57).
            var finishReason = outcome.FinishReason(cutter.FinishReason);

            // Back into the cache, by the shared rule. No tool calls exist on this endpoint.
            if (outcome.KeepsContext(cutter.FinishReason))
            {
                lease.Keep(result.Text);
            }

            if (tail.Length > 0)
            {
                await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage, tail, finishReason: null), aborted).ConfigureAwait(false);
            }

            // The last real chunk. Its text is empty; it exists to carry finish_reason.
            await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage, string.Empty, finishReason), aborted).ConfigureAwait(false);

            var promptTokens = lease.TranscriptTokens;
            var deliveredChars = cutter.ContentLength;
            var completionTokens = prepared.Backend.TokenCounter.TokensCovering(cutter.AllText, deliveredChars);

            if (includeUsage)
            {
                await sse.WriteChunkAsync(new CompletionChunk(
                    Id: requestId,
                    Created: created,
                    Model: model,
                    Choices: [],
                    Usage: CompletionUsage.For(promptTokens, completionTokens)),
                    aborted).ConfigureAwait(false);
            }

            await sse.WriteAsync(StreamingPipeline.DoneFrame, aborted).ConfigureAwait(false);

            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, completionTokens,
                result.Status.ToString(), finishReason, StatusCodes.Status200OK, totalMs,
                cache: cacheLabel, tailTurns: tailTurns, truncatedTurns: truncatedTurns,
                queueWaitMs: queueWaitMs);
            return null;
        }
        catch (Exception ex) when (aborted.IsCancellationRequested)
        {
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, lease?.PromptChars ?? prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: ex is OperationCanceledException ? nameof(GenerationStatus.Cancelled) : ex.GetType().Name,
                finish: "-", httpStatus: 0,
                cache: StreamingPipeline.CacheLabel(lease), tailTurns: lease?.TailTurns ?? 0, truncatedTurns: session.DroppedTurns,
                queueWaitMs: queueWaitMs);
            return null;
        }
        catch (Exception ex)
        {
            var failure = GenerationFailure.FromException(ex, backendCalls.Caught(ex) ? generationHealth : null, attemptDurationMs);
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, lease?.PromptChars ?? prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: ex.GetType().Name, finish: "-",
                httpStatus: sse.Started ? StatusCodes.Status200OK : failure.StatusCode,
                cache: StreamingPipeline.CacheLabel(lease), tailTurns: lease?.TailTurns ?? 0, truncatedTurns: session.DroppedTurns,
                queueWaitMs: queueWaitMs);
            return await StreamingPipeline.FailAsync(sse, failure, logger, requestId, aborted).ConfigureAwait(false);
        }
        finally
        {
            // Cancel, drain, settle -- in that order (D51). See ChatCompletionsStreamEndpoint for the
            // full account of why each step is where it is.
            var generationCts = Volatile.Read(ref currentGenerationCts);
            if (generationCts is not null)
            {
                await GenerationPipeline.CancelGuardedAsync(generationCts, logger, requestId, "on the way out").ConfigureAwait(false);
            }

            if (generation is not null)
            {
                try
                {
                    if (generationCts is null)
                    {
                        await generation.ConfigureAwait(false);
                    }
                    else
                    {
                        await GenerationPipeline.DrainWithWarningsAsync(
                            generation, options.DrainWarningSeconds, time, logger, requestId, "stream").ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "req={RequestId} generation ended with an exception; drained before disposing the context.",
                        requestId);
                }
            }

            lease?.Dispose();

            Volatile.Read(ref currentGenerationCts)?.Dispose();
        }
    }

    /// <summary>
    /// The one-time first-frame callback: it must run on the request thread while the response is still
    /// mutable, because the scheduler worker may be changing the truncation count concurrently.
    /// </summary>
    internal static Action CreateBeforeHeadersHook(ConversationSession session, HttpResponse response) =>
        () => session.ApplyTruncationHeaderIfSettled(response);

    /// <summary>
    /// One content-bearing chunk. With <paramref name="nullUsage"/> (the request asked for usage) it
    /// carries <c>"usage": null</c>, as every chunk before the usage chunk must.
    /// </summary>
    private static CompletionChunk Chunk(
        string id,
        long created,
        string model,
        bool nullUsage,
        string text,
        string? finishReason)
    {
        var chunk = new CompletionChunk(id, created, model, [new CompletionChunkChoice(text, 0, finishReason)]);
        return nullUsage ? chunk.WithNullUsage() : chunk;
    }
}

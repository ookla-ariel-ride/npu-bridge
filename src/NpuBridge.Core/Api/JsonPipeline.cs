using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Configuration;

namespace NpuBridge.Api;

/// <summary>
/// The single-JSON-object generation phase both non-streaming shapes share:
/// <see cref="ChatCompletionsEndpoint"/> for <c>POST /v1/chat/completions</c> and
/// <see cref="CompletionsEndpoint"/> for the legacy <c>POST /v1/completions</c>. Chunk 8 extracted the
/// streaming half into <see cref="StreamingPipeline"/> (D91) and left this half as two copies of one
/// roughly sixty-line closure — the <see cref="ConversationSession.Acquire"/> loop, the lease
/// publication (D85), the <see cref="CutWatcher"/> race and its guarded cancel, the
/// <c>--truncate-history</c> retry that keeps its scheduled slot (D86) — plus an identical admission
/// block and an identical pair of catch clauses. That is the shape D56 and D57 came out of: two copies
/// of the post-generation pipeline that drifted, one labelling a generation <c>stop</c> where the other
/// said <c>length</c>, one answering a backend <c>Error</c> with 502 where the other answered 200.
/// D81's rule is the answer — the shared steps are written once and a third caller uses them rather
/// than copying them — and this type is that rule applied to the JSON half.
///
/// What is left to each endpoint is exactly what differs on the wire: its request DTO and the
/// preparation call that reads it, its <c>stream: true</c> sibling, and the response DTO it builds from
/// the <see cref="JsonReply"/> this hands back. Everything between — the scheduled closure, the
/// scheduler admission mapping, <see cref="GenerationOutcome.Classify"/>, the client-side cut, the
/// tool-call reshaping (a no-op when the request offered no tools, which is every request on the legacy
/// shape), the cache <see cref="ContextLease.Keep"/>, the usage estimate, the per-request log line and
/// both catch clauses — happens here, once.
///
/// <see cref="GenerationPipeline"/> is the shape-agnostic core underneath both pipelines;
/// <see cref="SchedulerAdmission"/> is the third member of that family. Deliberately not folded
/// together with <see cref="StreamingPipeline"/>: the two differ in when a failure can still be an
/// ordinary HTTP status (D52), which is the whole of the streaming shape's error story and none of
/// this one's.
/// </summary>
internal static class JsonPipeline
{
    /// <summary>
    /// Runs one prepared request to a single JSON response. <paramref name="respond"/> is called once,
    /// on the success path only, to turn the shared <see cref="JsonReply"/> into whichever wire DTO the
    /// caller serves; every other path returns an error result, <c>Results.Empty</c> for a client that
    /// has gone, or the scheduler's own answer, none of which has a shape to choose.
    /// </summary>
    /// <typeparam name="TResponse">
    /// The caller's response DTO. Generic rather than <c>object</c> for the reason
    /// <see cref="SseStream.WriteChunkAsync{T}"/> is: inferred at the call site, so each endpoint's body
    /// is serialised as its own declared type, exactly as it was when each endpoint called
    /// <c>Results.Json</c> itself.
    /// </typeparam>
    public static async Task<IResult> RunAsync<TResponse>(
        HttpContext http,
        PreparedChatRequest prepared,
        BridgeOptions options,
        ContextCache cache,
        GenerationScheduler scheduler,
        GenerationHealth generationHealth,
        TimeProvider time,
        ILogger logger,
        Func<JsonReply, TResponse> respond)
    {
        var requestId = prepared.RequestId;
        var backendName = prepared.BackendName;
        var backend = prepared.Backend;

        var limits = prepared.Limits;
        var session = new ConversationSession(prepared, cache, options, logger);

        // The current attempt's lease. Assigned from *inside* the scheduled closure, the instant
        // Acquire hands one over, rather than from the value the closure returns: a lease the outer
        // scope learns about only from a returned value is a lease it does not have on any path where
        // that value never arrives, and the finally below is the one place a context is released (D43).
        // Reassigned on a --truncate-history retry, whose previous lease the closure has already
        // disposed -- Dispose is idempotent, so a stale reference here is a no-op rather than a double
        // release. Reading a disposed lease's own CacheHit/TailTurns/PromptChars afterwards is likewise
        // safe: they are plain fields set once in the constructor, and the catch clauses' log line needs
        // them to say cache=hit|miss rather than cache=-.
        ContextLease? lease = null;

        // How long the attempt that actually produced `result` waited behind the scheduler's one
        // worker. Stays 0 for every log line written before a generation was ever scheduled; reassigned
        // once the single ScheduleAsync call below settles. Declared outside the try so both catch
        // clauses, which run for a throw at any point including before scheduling, can still log the
        // best value they have.
        var queueWaitMs = 0.0;
        var backendCalls = new BackendCallTracker();
        var attemptDurationMs = 0.0;

        try
        {
            // Chunk 8 fix round 1 (controller ruling): the whole attempt -- Acquire's own preflight and
            // truncation loop, the outer retry-after-a-failed-generation loop that used to live here,
            // and the generation itself -- runs inside one scheduled closure, not just the generation.
            // CreateContext and GetUsablePromptLength are calls on the one shared model handle exactly
            // like GenerateAsync is; leaving them outside the queue let N concurrent requests make N
            // concurrent handle calls against a live generation, which is the gap docs/FUTURE.md:388
            // describes and the one PLAN §2.7 says chunk 8 closes ("a job cancelled while queued is
            // dropped without touching the model"). A retry (--truncate-history) continues on the same
            // scheduled slot rather than re-entering the queue (D86). ChatAttemptResult carries back
            // what the rest of this method needs: the result, whether this handler cancelled it for the
            // cut, and its own timing -- never the lease, which is published above instead (D85).
            var scheduled = await scheduler.ScheduleAsync(async ct =>
            {
                var stopwatch = Stopwatch.StartNew();
                try
                {
                    while (true)
                {
                    // 7. The context: checked out of the cache when the transcript extends a cached
                    // prefix, created fresh otherwise, and refused here -- before a token is generated --
                    // when a backend with a preflight says the prompt does not fit (D55).
                    var acquisition = session.Acquire(backendCalls);
                    if (acquisition.Failure is { } refused)
                    {
                        return ChatAttemptResult.Refused(refused);
                    }

                    var attemptLease = acquisition.Lease!;

                    // Published to the caller's scope before anything can throw, so the finally out
                    // there always has this context to settle however this method leaves -- including
                    // the paths where the value this closure returns never reaches its caller.
                    lease = attemptLease;

                    // Guarded all the same: a throw here would unwind through ScheduleAsync, and the
                    // caller's finally disposes exactly once whether this dispose ran first or not
                    // (Dispose is idempotent). Disposing here keeps the release as close to the failure
                    // as it was before chunk 8, when this loop ran in the caller's own try/finally.
                    try
                    {
                        // Once a generation is attempted on a truncated transcript the header says so,
                        // whatever that generation goes on to report -- the same moment the streaming
                        // path sets it. A refusal above carries none: no reply was produced for the
                        // dropped turns to describe. Re-applied on a retry, so a later drop updates the
                        // count. Mutating the response from the worker thread is safe: the caller is
                        // suspended on this same ScheduleAsync call and touches neither the response nor
                        // session again until it resumes (fix round 1, Finding 6: this used to run before
                        // scheduling, so a request that then hit a full queue or a shutdown could carry
                        // the header despite no generation ever having been attempted).
                        session.ApplyTruncationHeader(http.Response);

                        // Everything an attempt cancels with, or learns from its own deltas, belongs to
                        // that attempt. A retry after a cut used to inherit the cancelled token and the
                        // cut flag, so the retried generation returned Cancelled at once and the stale
                        // flag reported that as a successful cut: HTTP 200, empty content, finish_reason
                        // "stop". Cancelled either by the client going away or by the client-side cut
                        // deciding it has enough text; only the latter needs a source of the handler's
                        // own, the former arrives through ct, which is the scheduler's own token for this
                        // job (linked from http.RequestAborted by the ScheduleAsync call below).
                        using var generationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var cancelledByCut = false;

                        // Watches the text as it arrives purely to decide when to stop the generation
                        // early; the authoritative cut is applied below to the text the backend finally
                        // reports, with a second OutputCutter, so the answer does not depend on which
                        // deltas the watcher happened to see. Null when the request set no limits, which
                        // is the ordinary case and costs nothing. Fresh per attempt, like the sink beside
                        // it.
                        var watcher = limits.IsEmpty ? null : new CutWatcher(limits);
                        var sink = DeltaSink.ToWatcher(stopwatch, watcher);

                        var generation = backendCalls.Invoke(() => backend.GenerateAsync(
                            attemptLease.Context,
                            attemptLease.Prompt,
                            prepared.Sampling,
                            sink.OnDelta,
                            generationCts.Token));

                        // Whichever comes first. When the watcher signals because the cut fired or it
                        // faulted, cancel here -- on this thread, guarded -- and then wait for the
                        // generation to end as it would have anyway. The overshoot is a delta or two and
                        // costs nothing: the cut itself is applied to the final text below.
                        //
                        // A watcher fault is retained by the sink and rethrown after the cancelled
                        // generation drains. It must not count as a cut: only a normal watcher signal
                        // lets a Cancelled backend result map to the client-side cut outcome.
                        //
                        // Skipped when the request set no limits: there is no watcher, so nothing can
                        // ever complete the other half of the race, and awaiting the generation alone
                        // says the same.
                        var drainAfterCancellation = false;
                        if (watcher is not null)
                        {
                            await Task.WhenAny(generation, watcher.Signal).ConfigureAwait(false);
                            if (watcher.Signal.IsCompleted)
                            {
                                var watcherFaulted = sink.BridgeFault is not null;
                                cancelledByCut = !watcherFaulted;
                                await GenerationPipeline.CancelGuardedAsync(
                                    generationCts,
                                    logger,
                                    requestId,
                                    watcherFaulted ? "after a cutter fault" : "at the cut").ConfigureAwait(false);
                                drainAfterCancellation = true;
                            }
                        }

                        var result = drainAfterCancellation
                            ? await backendCalls.AwaitAsync(GenerationPipeline.DrainWithWarningsAsync(
                                generation, options.DrainWarningSeconds, time, logger, requestId, "json")).ConfigureAwait(false)
                            : await backendCalls.AwaitAsync(generation).ConfigureAwait(false);
                        if (sink.BridgeFault is { } bridgeFault)
                        {
                            throw bridgeFault;
                        }

                        // 7a. A backend without a preflight can only say "too long" by failing the
                        // generation. With --truncate-history that is not the end: drop the oldest
                        // exchange and go round again on a fresh context (this one ended in a
                        // non-Complete status and is disposed, D11). A backend with a preflight never
                        // reaches this -- Acquire refused or truncated already -- unless it reports the
                        // status the preflight did not predict, in which case the same loop handles it.
                        // This retry continues on the same scheduled slot rather than re-entering the
                        // queue (D86).
                        if (result.Status == GenerationStatus.PromptLargerThanContext && session.TryDropOldestExchange())
                        {
                            attemptLease.Dispose();
                            continue;
                        }

                        var totalMs = stopwatch.Elapsed.TotalMilliseconds;
                        var ttftMs = sink.TtftMs(totalMs);
                        return ChatAttemptResult.Generated(result, cancelledByCut, ttftMs, totalMs);
                    }
                    catch
                    {
                        attemptLease.Dispose();
                        throw;
                    }
                }
                }
                finally
                {
                    attemptDurationMs = stopwatch.Elapsed.TotalMilliseconds;
                }
            }, http.RequestAborted).ConfigureAwait(false);

            queueWaitMs = scheduled.QueueWait.TotalMilliseconds;

            var admission = SchedulerAdmission.Classify(scheduled, http.RequestAborted.IsCancellationRequested);
            if (admission == SchedulerOutcome.ClientGone)
            {
                // The client was already gone before its turn came, or the scheduler was already
                // shutting down at the same moment -- either way there is nobody to send a body to,
                // exactly like the same check further down for an attempt that did run.
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, prepared.PromptChars, ttftMs: 0, tokens: 0,
                    status: scheduled.Kind.ToString(), finish: "-", httpStatus: 0,
                    truncatedTurns: session.DroppedTurns, queueWaitMs: queueWaitMs);
                return Results.Empty;
            }

            if (admission != SchedulerOutcome.Completed)
            {
                // QueueFull (429), QueueShuttingDown (503, task-2-brief.md integration decision 4), or
                // BackendThrewCancellation (502, fix round 1 Finding 1) -- none of which ever reached the
                // closure above in the first two cases, so no context exists to dispose beyond what the
                // finally already handles (null).
                var schedulerFailure = SchedulerAdmission.FailureFor(
                    admission,
                    scheduled.RetryAfterSeconds,
                    backendCalls.Faulted ? generationHealth : null,
                    attemptDurationMs);
                SchedulerAdmission.ApplyRetryAfter(http.Response, admission, scheduled.RetryAfterSeconds);

                ChatRequestMetrics.LogRequest(logger, requestId, backendName, prepared.PromptChars, ttftMs: 0, tokens: 0,
                    status: admission.ToString(), finish: "-", httpStatus: schedulerFailure.StatusCode,
                    truncatedTurns: session.DroppedTurns, queueWaitMs: queueWaitMs);
                return schedulerFailure.ToResult();
            }

            var attempt = scheduled.Result!;

            if (attempt.Refusal is { } attemptRefused)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, prepared.PromptChars, ttftMs: 0, tokens: 0,
                    status: GenerationStatus.PromptLargerThanContext.ToString(), finish: "-", httpStatus: attemptRefused.StatusCode,
                    truncatedTurns: session.DroppedTurns, queueWaitMs: queueWaitMs);
                return attemptRefused.ToResult();
            }

            var result = attempt.Result!;
            var cancelledByCut = attempt.CancelledByCut;
            var ttftMs = attempt.TtftMs;
            var totalMs = attempt.TotalMs;
            var cacheLabel = GenerationPipeline.CacheLabel(lease);
            var promptChars = lease!.PromptChars;

            GenerationPipeline.LogRawOutput(logger, options, requestId, result);

            // 8. Status → response or error. The mapping itself lives in GenerationFailure, shared with
            // the streaming path so the two shapes cannot describe the same condition differently.
            if (result.Status == GenerationStatus.Cancelled && http.RequestAborted.IsCancellationRequested)
            {
                // The client is gone; there is nobody to send a body to and this is not an error.
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: 0,
                    cache: cacheLabel, tailTurns: lease.TailTurns, truncatedTurns: session.DroppedTurns,
                    queueWaitMs: queueWaitMs);
                return Results.Empty;
            }

            // 8a. The client-side cut, before the status mapping. A generation this handler cancelled
            // because the cap or a stop string was reached comes back Cancelled, which is a 502 for any
            // other reason; the cut is what tells the two apart, and it is decided by the text rather
            // than by the status so that a cut which landed on the last delta reads the same either way.
            // The whole text in one go, so the cut's verdict is legible immediately -- unlike the stream,
            // which must wait for its Flush (D57).
            var cut = limits.Cut(result.Text);

            // Error, filtered, or content: one classification, shared with the streaming path, so the two
            // shapes cannot describe the same generation differently. See GenerationOutcome for why the
            // cut is consulted as the flag recorded at the cancel rather than as the cutter's state.
            var outcome = GenerationOutcome.Classify(result, cancelledByCut, generationHealth, totalMs);

            if (outcome.Failure is { } failure)
            {
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: failure.StatusCode,
                    cache: cacheLabel, tailTurns: lease.TailTurns, truncatedTurns: session.DroppedTurns,
                    queueWaitMs: queueWaitMs);
                return failure.ToResult();
            }

            string? content = outcome.Filtered ? string.Empty : cut.Text;
            var finishReason = outcome.FinishReason(cut.FinishReason);

            // 8a-i. Tool calls (chunk 7). Only when the request offered tools, and only over text the
            // client would otherwise have been given: a filtered reply is not parsed, because parsing
            // it would be the one place withheld text came back as arguments. A reply that parses to
            // nothing is ordinary content, which is the common case and costs one scan. The legacy
            // shape has no `tools` field at all, so its catalog is always null and this whole branch is
            // the one null check it pays -- it cannot emit tool_calls by this path any more than it
            // could when it spelled the shaping out for itself.
            var toolCalls = ToolCallReply.From(prepared.Tools, outcome, content);

            // What the model produced for this client, whether it went out as content or was reshaped
            // into tool_calls. Read before the content is cleared: the JSON the model wrote cost the
            // tokens it cost, and a tool call reporting completion_tokens 0 would tell a client
            // budgeting its context that the call was free.
            var deliveredChars = content?.Length ?? 0;

            if (toolCalls is not null)
            {
                content = null;

                // The cut keeps its label, for the reason the streaming path gives: a budget that
                // fired produced this call out of a reply the model had not finished, and saying
                // "tool_calls" would tell a client that resumes on "length" there is nothing to
                // resume. It still gets the calls.
                finishReason = cut.FinishReason ?? "tool_calls";
            }

            // 8b. Back into the cache -- the rule is GenerationOutcome's, and the finally disposes every
            // context it refuses (D11, D43). A tool call is stored under the transcript the client will
            // send back, which is the array this reply emitted and not the fenced text the model wrote.
            if (outcome.KeepsContext(cut.FinishReason))
            {
                lease.Keep(result.Text, ToolCallReply.Carried(toolCalls));
            }

            // 9. Usage, in the backend's own count (D80): Phi-3 tokens on Phi Silica, chars/4 where the
            // tokenizer is unpublished; never the progress-callback count (Phi Silica batches several
            // tokens per callback, D44). The prompt side is the whole transcript the model holds, not the
            // tail sent on a cache hit: a client budgeting its context wants the former, and the number
            // must not change with a cache hit.
            var promptTokens = lease.TranscriptTokens;
            // The tokens the model produced to reach the cut, in the tokenization of its own text: a
            // stop-truncated prefix can count more on its own than the model spent on it (D80).
            var completionTokens = backend.TokenCounter.TokensCovering(result.Text, deliveredChars);

            var body = respond(new JsonReply(
                Id: requestId,
                Created: time.GetUtcNow().ToUnixTimeSeconds(),
                Model: backend.ModelId,
                Content: content,
                FinishReason: finishReason,
                ToolCalls: toolCalls,
                Usage: CompletionUsage.For(promptTokens, completionTokens)));

            ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, completionTokens,
                result.Status.ToString(), finishReason, StatusCodes.Status200OK, totalMs,
                cache: cacheLabel, tailTurns: lease.TailTurns, truncatedTurns: session.DroppedTurns,
                queueWaitMs: queueWaitMs);

            return Results.Json(body, JsonDefaults.Options);
        }
        catch (Exception ex) when (http.RequestAborted.IsCancellationRequested)
        {
            // The client is gone, so there is nobody to hand a body to and this is not an error --
            // the same answer the returned-Cancelled check above gives, for the thrown form. http=0
            // says so, as it does on the streaming path. The finally still disposes.
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, lease?.PromptChars ?? prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: ex is OperationCanceledException ? nameof(GenerationStatus.Cancelled) : ex.GetType().Name,
                finish: "-", httpStatus: 0,
                cache: GenerationPipeline.CacheLabel(lease), tailTurns: lease?.TailTurns ?? 0, truncatedTurns: session.DroppedTurns,
                queueWaitMs: queueWaitMs);
            return Results.Empty;
        }
        // Unfiltered, so that the two clauses together really are exhaustive -- the same pair, in the
        // same order, as the streaming path. Excluding OperationCanceledException here left the case
        // "cancelled, but not by the client" uncaught: an adapter that breaks the
        // ILanguageModelBackend rule about swallowing the runtime's cancellation lets one out of the
        // cut's own linked token, RequestAborted is not set, the filter above does not match, and the
        // request died as an unhandled exception -- HTTP 500 with no OpenAI envelope, where the stream
        // answered the identical event with a 502 and the ordinary error body.
        catch (Exception ex)
        {
            var failure = GenerationFailure.FromException(ex, backendCalls.Caught(ex) ? generationHealth : null, attemptDurationMs);
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, lease?.PromptChars ?? prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: ex.GetType().Name, finish: "-", httpStatus: failure.StatusCode,
                cache: GenerationPipeline.CacheLabel(lease), tailTurns: lease?.TailTurns ?? 0, truncatedTurns: session.DroppedTurns,
                queueWaitMs: queueWaitMs);
            return failure.ToResult();
        }
        finally
        {
            // Disposes the context unless Keep or ReturnUntouched already settled it: every path that
            // took a context out of the cache or created one ends here (D43).
            lease?.Dispose();
        }
    }
}

/// <summary>
/// One finished generation, in the terms every JSON wire shape needs and none of the terms only one of
/// them does. <see cref="JsonPipeline.RunAsync{TResponse}"/> hands this to the endpoint's own factory
/// as the last step before the log line, so the factory's whole job is naming fields on its own DTO.
/// </summary>
/// <param name="Content">
/// The text the client receives, already cut and already emptied when the runtime filtered the reply —
/// or null when it was reshaped into <see cref="ToolCalls"/>, which happens only on a shape that has a
/// <c>tools</c> field to have offered them with.
/// </param>
/// <param name="FinishReason"><c>stop</c>, <c>length</c>, <c>content_filter</c> or <c>tool_calls</c>, already decided (D53, D56, D57).</param>
/// <param name="ToolCalls">The calls the reply was reshaped into, or null on an ordinary reply.</param>
internal readonly record struct JsonReply(
    string Id,
    long Created,
    string Model,
    string? Content,
    string FinishReason,
    IReadOnlyList<ChatCompletionToolCall>? ToolCalls,
    CompletionUsage Usage);

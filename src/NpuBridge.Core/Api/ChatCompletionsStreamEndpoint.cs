using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Configuration;

namespace NpuBridge.Api;

/// <summary>
/// Streaming phase two of <c>POST /v1/chat/completions</c>: the server-sent-event sibling of
/// <see cref="ChatCompletionsEndpoint"/>'s second half. Phase one is shared — this type is only ever
/// reached with a <see cref="PreparedChatRequest"/>, so it never validates the request.
///
/// It does still decide a status code, and that is the one thing about it worth reading twice. The
/// headers are not written when the handler starts; they are written by the first frame that has
/// something to say. Until then the status line is still the server's to set, so a generation that
/// fails before it produces a single token — an over-length prompt, a backend that throws on the way in
/// — comes back as the ordinary HTTP status with the ordinary JSON body, exactly as it would without
/// <c>stream: true</c>. Only once a byte has gone out does an error have to travel inside the stream as
/// a <c>data: {"error":...}</c> event. <see cref="GenerationFailure"/> owns both forms so the two
/// cannot describe the same condition differently.
///
/// The other thing worth reading twice is the shape of the exit: cancel, drain, settle. The context is
/// a live handle the generation task may still be writing to, so its lease is settled -- stored back
/// in the cache or disposed -- only after that task has finished, on every path out of the method
/// including a client that vanished mid-frame.
/// </summary>
internal sealed class ChatCompletionsStreamEndpoint
{
    // Never instantiated: the type exists so the streaming phase has an ILogger<T> category of its
    // own. ChatRequestPreparer takes a plain ILogger and therefore adopts whatever category its caller
    // hands it, so the category has to be chosen on purpose somewhere; this is where.
    private ChatCompletionsStreamEndpoint()
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

        // Identity of the reply, fixed once and repeated on every chunk: a client that stitches the
        // chunks back together must see the same id/created/model a non-streamed reply would carry.
        // The served id, never the requested one: preparation already refused any other (D77).
        var model = prepared.Backend.ModelId;
        var created = time.GetUtcNow().ToUnixTimeSeconds();
        var includeUsage = prepared.Request.StreamOptions?.IncludeUsage == true;

        // The truncated-turns header is applied here, from the request thread, in the last instant
        // before the first frame commits the response -- see the note on the scheduled closure below for
        // why it cannot be applied where the turns are actually dropped. A truncation that has not
        // happened yet at this point simply finds nothing to report; the second call, once the outcome
        // is known, covers both the request that never wrote a frame at all and the one whose truncation
        // arrived too late to be sent (which then logs the warning it always did). The hook is the
        // IfSettled form: a keep-alive that lands while turns are still being dropped must not stamp
        // the count reached so far, because that number is locked in and the final one is not. Both
        // droppers clear the flag before the count moves -- the preflight loop inside Acquire, and the
        // status-driven retry's own TryDropOldestExchange below -- so the hook declines during either.
        var sse = new SseStream(http.Response, () => session.ApplyTruncationHeaderIfSettled(http.Response));
        var stopwatch = Stopwatch.StartNew();

        // The client-side cut. It runs here, on the single channel reader, and never on the backend's
        // callback thread: it decides what goes on the wire, so it belongs on the side of the hand-off
        // that owns the response. Holding text back is the whole difference from the JSON path — a
        // stop string can straddle two deltas, and a delta already written cannot be recalled.
        var cutter = new OutputCutter(prepared.Limits);

        var streamed = false;

        // Whether the assistant message has been opened, which is a different question: a keep-alive
        // comment starts the stream without opening it, and a reply with no deltas at all still needs
        // its role chunk before the finish chunk. D81 removed this flag because it was then always
        // equal to `streamed` at its only read; chunk 7 breaks that equality, because a buffered reply
        // sees deltas without writing anything and defers the role chunk to the tail.
        var roleSent = false;

        // With tools offered, nothing goes out until the reply is whole: only a finished reply can be
        // told from prose (PLAN §2.6 item 2). The cost is that the window in which a failure can still
        // be an ordinary HTTP status now spans the entire generation rather than only the wait for the
        // first token — which is why keep-alives have to cover the buffered drain too.
        var buffering = prepared.Tools is not null;

        // Set when this handler cancels the generation because a limit fired while streaming, and read
        // when the status comes back. A fact recorded at the cancel, not inferred from the cutter
        // afterwards: the JSON path used to infer it from a different cutter state, and the two shapes
        // answered a backend's unprompted Cancelled differently (D62). Written by the reader loop below
        // (on this thread) and read inside the scheduled closure once GenerateAsync returns for the
        // attempt the cancel targeted -- the cancellation token that return is causally downstream of
        // carries the happens-before this needs.
        var cancelledByCut = false;

        var queueWaitMs = 0.0;
        var backendCalls = new BackendCallTracker();
        var attemptDurationMs = 0.0;

        // The current attempt's lease, assigned from *inside* the scheduled closure the instant Acquire
        // hands one over rather than from the value that closure returns. The difference is the whole
        // reason this variable is written where it is: the reader loop below can unwind -- a client that
        // vanishes mid-frame is the ordinary case -- long before the scheduled task is ever unwrapped,
        // and a lease this scope only learns about from a returned value is a lease it does not have
        // when that value never arrives. The finally's Dispose then saw null and the context was never
        // released, which is both D43 and D51 gone at once. Reassigned on a --truncate-history retry,
        // whose previous lease the closure has already disposed; Dispose is idempotent, so a stale
        // reference here settles to a no-op rather than a double release.
        ContextLease? lease = null;

        // The one channel for the whole request, not one per attempt (chunk 8 fix round 1, controller
        // ruling): Acquire() -- the cache lookup and the preflight -- now runs inside the scheduled
        // closure alongside the generation, for the same reason the JSON shape's does (CreateContext
        // and GetUsablePromptLength are calls on the one shared model handle). A retry
        // (--truncate-history on a backend with no preflight, after a generation that produced zero
        // deltas) continues on this same scheduled slot and this same channel rather than re-entering
        // the queue: nothing is ever written to the channel by an attempt that fails with zero deltas,
        // so leaving it open across attempts costs nothing, and only the final attempt (success, or a
        // terminal failure) completes it.
        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        var sink = DeltaSink.ToChannel(stopwatch, channel.Writer);

        // The current attempt's own cancellation source, for the reader loop below to reach when the
        // cut fires. Reassigned by the closure at the start of each attempt (retries only reach a
        // second assignment; the ordinary case assigns once) and read by the reader loop only once a
        // delta has actually arrived on the channel -- which happens-after the sink above observed that
        // delta, which happens-after GenerateAsync was called with the token from *this* assignment, so
        // the channel's own synchronization is what makes the read safe without a lock.
        //
        // Disposed by this method's finally, after the drain, and deliberately not by a `using` inside
        // the closure: the finally cancels through this same reference on its way out, and a source the
        // closure had already disposed made CancelAsync throw on every ordinary request -- caught and
        // logged by CancelGuardedAsync, so the only visible damage was a Debug line per request claiming
        // the cancel had failed, which is exactly the kind of routine noise that trains a reader to
        // ignore the line that one day means something.
        CancellationTokenSource? currentGenerationCts = null;

        // The scheduled attempt, not the generation itself: chunk 8 serializes Acquire and the
        // generation together behind GenerationScheduler.ScheduleAsync, so what this method starts
        // (without awaiting) and drains in the finally is the schedule's own outcome. Unwrapped only
        // where it is actually needed, by ReportSchedulerOutcomeAsync below, which is also where a job
        // dropped without ever running gets this channel closed so a reader parked on it is not left
        // waiting forever.
        Task<ScheduleResult<ChatAttemptResult>>? generation = null;

        try
        {
            // See ChatCompletionsEndpoint (the JSON shape) for the fuller account of why Acquire moved
            // in here. Unlike that shape, this closure must not touch http.Response at all (fix round 2):
            // this call is started rather than awaited, so the request thread runs straight on into the
            // reader loop and writes keep-alive frames to that same response while this runs. The
            // truncated-turns header is therefore applied by the request thread alone -- once as
            // SseStream is about to commit the response, and once when the outcome is known -- because a
            // truncation can land on either side of that first frame, and because Kestrel's header
            // collection is neither thread-safe nor mutable after the response has started.
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

                    // Published to the caller's scope before anything can throw, so its finally always
                    // has this context to settle -- including on the paths where what this closure
                    // returns never reaches the code that would otherwise have read it.
                    lease = attemptLease;

                    try
                    {
                        // Not a `using`: the caller's finally owns this source's lifetime, because the
                        // caller's finally is what cancels through it on the way out.
                        var generationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        Interlocked.Exchange(ref currentGenerationCts, generationCts)?.Dispose();

                        var result = await backendCalls.AwaitAsync(backendCalls.Invoke(() => prepared.Backend.GenerateAsync(
                            attemptLease.Context,
                            attemptLease.Prompt,
                            prepared.Sampling,
                            sink.OnDelta,
                            generationCts.Token))).ConfigureAwait(false);

                        // Not one delta, and the backend says the prompt was too long. On a backend
                        // without a preflight this is the only way it can say so; with
                        // --truncate-history the answer is to drop the oldest exchange and go round
                        // again on a fresh context (this one ended in a non-Complete status and is
                        // disposed, D11) -- on the same scheduled slot, per the fix report. A backend
                        // with a preflight never reaches this unless it reports the status the preflight
                        // did not predict.
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
                        // Guarded, as on the JSON shape: nothing outside this closure exists yet to
                        // dispose attemptLease if anything above throws, and the channel must still be
                        // completed so the reader loop is not left waiting forever.
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

            // The channel is completed by the closure itself on every path it returns through, but not
            // if the job is dropped without ever running (shutdown mid-wait, most plausibly, since a
            // queued client abort is caught by the reader loop's own token instead): this is the
            // backstop for that one case, a harmless no-op everywhere else.
            _ = generation.ContinueWith(
                static (_, state) => ((ChannelWriter<string>)state!).TryComplete(),
                channel.Writer,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            // Rejection and a post-shutdown enqueue are both decided synchronously, before the
            // scheduler ever touches the channel -- so this checks the returned task without an await,
            // the one way left to answer with an ordinary HTTP status rather than opening the stream
            // (D52, integration decision 1). Since Acquire moved inside the closure there is now only
            // ever one ScheduleAsync call for the whole request (fix round 1, Finding 2's root cause --
            // a retry used to mean a second call, reachable after a keep-alive had already committed
            // the response), so this check and the one after the reader loop below are the only two
            // places a scheduler-level failure can be discovered, not one per retry.
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

            // Nothing has been written yet, on purpose. Waiting here — rather than opening with the
            // role chunk — is what keeps the status line available for a failure that arrives before
            // the first token. The first keep-alive comment is what ends that window, about a second in.
            streamed = await StreamingPipeline.WaitForFirstDeltaAsync(sse, channel.Reader, streaming, time, aborted)
                .ConfigureAwait(false);

            GenerationResult result;
            if (streamed && buffering)
            {
                // Buffered: every delta goes through the cutter and nothing goes out. The cutter
                // still decides the cut, so a tool-call reply is capped and stopped exactly as a
                // streamed one is; what it releases is accumulated rather than written, and read
                // from EmittedText in the tail.
                if (await DrainBufferedAsync(sse, channel.Reader, cutter, streaming, time,
                        () => currentGenerationCts!, logger, requestId, aborted).ConfigureAwait(false))
                {
                    cancelledByCut = true;
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
            else if (streamed)
            {
                // The role chunk. OpenAI clients rely on it to open the assistant message.
                roleSent = true;
                await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage,
                    new ChatCompletionDelta("assistant", string.Empty), finishReason: null), aborted).ConfigureAwait(false);

                // Deliberately not cancelled by `aborted`: the loop must end when the channel completes,
                // so that `generation` is always reached and always drained below.
                await foreach (var delta in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    // What the cutter releases, not the delta: with stop strings configured this lags
                    // the backend by up to Holdback characters, and on the delta that trips a limit it
                    // is the truncated prefix.
                    var release = cutter.Accept(delta);
                    if (release.Length > 0)
                    {
                        await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage,
                            new ChatCompletionDelta(null, release), finishReason: null), aborted).ConfigureAwait(false);
                    }

                    if (cutter.StopRequested && !cancelledByCut)
                    {
                        // Stop the model. On a settled cut nothing more will be emitted; on a token
                        // budget the cutter may know the budget is passed before it can place the cut
                        // (D80), and then the deltas already in flight keep coming through it so the
                        // flush below decides over everything the model produced.
                        cancelledByCut = true;
                        await GenerationPipeline.CancelGuardedAsync(currentGenerationCts!, logger, requestId, "at the cut").ConfigureAwait(false);
                    }

                    if (cutter.IsCut)
                    {
                        // Stop consuming. Whatever is still queued is discarded; the finally's
                        // cancel-drain-settle then runs unchanged, so the context is still disposed
                        // exactly once and only after the generation task has ended.
                        break;
                    }
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

            // A generation was attempted on the truncated transcript, so the header says so -- whatever
            // that generation goes on to report, exactly as on the JSON shape. The second and last of
            // the two moments the request thread owns the response for this purpose: the first, in
            // SseStream, is the only chance a request that does write frames ever gets, and this is the
            // only chance a request that writes none (an ordinary status-code failure, or a reply with
            // no deltas) ever gets. On a stream that has already started this writes nothing and logs
            // the warning instead. Deliberately after the three branches converge and not inside them:
            // a scheduler-level refusal and a preflight refusal both return from inside
            // ReportSchedulerOutcomeAsync without reaching here, and neither may carry the header --
            // no reply was produced for the dropped turns to describe.
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
                // The client is gone: no finish chunk, no error event, nothing. http=0 says so, as it
                // does on the JSON path. The finally still drains and disposes.
                ChatRequestMetrics.LogRequest(logger, requestId, backendName, promptChars, ttftMs, tokens: 0,
                    status: result.Status.ToString(), finish: "-", httpStatus: 0,
                    cache: cacheLabel, tailTurns: tailTurns, truncatedTurns: truncatedTurns,
                    queueWaitMs: queueWaitMs);
                return null;
            }

            // Error, filtered, or content: one classification, shared with the non-streaming path, in
            // the same order and for the same reasons. See GenerationOutcome for why a Cancelled the
            // handler asked for is not a failure while every other status still is, and why "the cut
            // caused it" is the flag set beside the CancelAsync above rather than the cutter's state.
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

            if (!roleSent)
            {
                // The role chunk has not gone out yet: no delta arrived at all (an empty reply, or a
                // filtered one), or this request buffered and nothing has been written. It still has
                // to: a client builds the assistant message from it, and it must precede the chunks
                // below.
                roleSent = true;
                await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage,
                    new ChatCompletionDelta("assistant", string.Empty), finishReason: null), aborted).ConfigureAwait(false);
            }

            // The held tail. The generation ended without a stop string forming, so the characters that
            // were withheld in case they were its first half are ordinary output after all and must go
            // out — losing them would silently truncate every reply whose last characters happened to
            // look like the start of a stop string. Empty when a limit fired: the text after a cut is
            // never sent, and empty as well when no stop string was configured, since nothing was held.
            //
            // Not flushed at all when the runtime withheld the answer. The deltas already on the wire
            // cannot be recalled, but these have not been written yet and the bridge now knows they were
            // withheld: writing them here would be the one place a filtered reply gained text.
            var tail = outcome.Filtered ? string.Empty : cutter.Flush();

            // Read after the flush, never before -- except for a filtered reply, which has no flush to
            // read after and whose label does not depend on the cut anyway. Flush() can be the call
            // that commits the cap: it is
            // deliberately deferred until the text runs Holdback past the budget, so a reply that ends
            // inside that window is only cut here. Reading FinishReason first labelled such a request
            // "stop" on this shape while the JSON path -- which reads it after its own flush -- called
            // the very same generation "length", and a client that resumes on "length" stopped instead
            // (D57). Hence the post-flush verdict is an argument to the shared classifier rather than
            // something it reads for itself.
            var finishReason = outcome.FinishReason(cutter.FinishReason);

            // Tool calls (chunk 7), over the whole buffered reply rather than the held tail: while
            // tools are present nothing has gone out, so the cutter holds everything the client is
            // owed and the parse sees the reply entire. A filtered reply is not parsed, for the reason
            // ToolCallReply gives. Decided before the cache is written, because what is stored depends
            // on it.
            var toolCalls = buffering
                ? ToolCallReply.From(prepared.Tools, outcome, outcome.Filtered ? null : cutter.EmittedText)
                : null;

            // Back into the cache, by the shared rule. The generation task has already ended (awaited
            // above), so the context is idle; the finally's drain finds nothing to wait for and its
            // Dispose finds the lease already settled (D51). A tool call is stored under the transcript
            // the client will send back — the array this reply emitted, not the fenced text the model
            // wrote — so the next turn of an agent loop can hit.
            if (outcome.KeepsContext(cutter.FinishReason))
            {
                lease.Keep(result.Text, ToolCallReply.Carried(toolCalls));
            }

            if (toolCalls is not null)
            {
                // One chunk carrying the whole array, arguments included, then the finish chunk — the
                // shape OpenAI produces when it sends arguments in one piece. There is nothing to
                // stream: the bridge cannot know a reply is a call until the model has stopped.
                //
                // The cut keeps its label. A budget that fired produced a call out of a reply the
                // model had not finished, and saying "tool_calls" would tell a client that resumes on
                // "length" that there was nothing left to resume. Both facts reach it: the calls are
                // sent, and the finish reason still says the text was truncated.
                finishReason = cutter.FinishReason ?? "tool_calls";
                await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage,
                    new ChatCompletionDelta(null, null, Indexed(toolCalls)), finishReason: null), aborted).ConfigureAwait(false);
            }
            else if (buffering)
            {
                // Ordinary content, held back until the parse could rule out a tool call. It goes out
                // as one chunk; a client that concatenates deltas sees exactly what the non-streamed
                // shape would have returned.
                var buffered = outcome.Filtered ? string.Empty : cutter.EmittedText;
                if (buffered.Length > 0)
                {
                    await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage,
                        new ChatCompletionDelta(null, buffered), finishReason: null), aborted).ConfigureAwait(false);
                }
            }
            else if (tail.Length > 0)
            {
                await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage,
                    new ChatCompletionDelta(null, tail), finishReason: null), aborted).ConfigureAwait(false);
            }

            // The last real chunk. Its delta is empty; it exists to carry finish_reason.
            await sse.WriteChunkAsync(Chunk(requestId, created, model, includeUsage,
                new ChatCompletionDelta(null, null), finishReason), aborted).ConfigureAwait(false);

            // Usage, in the backend's own count on both sides, as on the non-streaming path (D80; never
            // the progress-callback count, D44). Counted off the cutter rather than the backend's
            // returned text, always: after a cut that text runs past what was sent, after a filtered
            // reply it is empty while deltas did go out, and the cutter is the only thing that knows
            // exactly what reached the client. The prompt side is the whole transcript the model holds,
            // not the tail sent on a cache hit, as on the JSON path.
            var promptTokens = lease.TranscriptTokens;

            // The tokens the model produced to reach what was sent, in the tokenization of everything
            // the cutter saw (D80): a prefix counted on its own can tokenize differently.
            //
            // A buffered reply that was filtered is the one case where the cutter's length is not what
            // reached the client: nothing was written, because nothing is written until the parse
            // decides, and by then the answer was withheld. Counting it would report tokens for text
            // the client never saw, and the non-streaming shape reports none for the same generation.
            var deliveredChars = buffering && outcome.Filtered ? 0 : cutter.ContentLength;
            var completionTokens = prepared.Backend.TokenCounter.TokensCovering(cutter.AllText, deliveredChars);

            if (includeUsage)
            {
                await sse.WriteChunkAsync(new ChatCompletionChunk(
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
            // A write that failed because the client went away, or the cancellation that follows it.
            // There is nobody to report anything to, and it is not an error: swallow it here rather than
            // let it escape as an unhandled request exception. The finally still drains and disposes.
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, lease?.PromptChars ?? prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: ex is OperationCanceledException ? nameof(GenerationStatus.Cancelled) : ex.GetType().Name,
                finish: "-", httpStatus: 0,
                cache: StreamingPipeline.CacheLabel(lease), tailTurns: lease?.TailTurns ?? 0, truncatedTurns: session.DroppedTurns,
                queueWaitMs: queueWaitMs);
            return null;
        }
        // Unfiltered, so that the two clauses together really are exhaustive. Excluding
        // OperationCanceledException here left the case "cancelled, but not by the client" uncaught: an
        // adapter that breaks the ILanguageModelBackend rule about swallowing the runtime's cancellation
        // lets one out of the cut's own linked token, RequestAborted is not set, neither filter matches,
        // and the request dies as an unhandled exception mid-stream instead of emitting its finish chunk.
        // A cancellation that reaches here is a generation that failed, and is reported as one.
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
            // Cancel, drain, settle — in that order, and the order is the whole point. The context is a
            // live WinRT handle that the generation task may still be generating against; disposing it
            // while that task runs is a use-after-dispose, not merely an unobserved task. Cancelling
            // first is what keeps the wait short; awaiting is what makes the disposal safe. The lease's
            // Dispose is a no-op when Keep already stored the context, which only happens after the
            // generation was awaited above, so the cache never receives a context still in use.
            // Guarded because this was the one statement in the method outside a try, and it stands
            // between a failure and the disposal below: letting a throw escape would skip both the drain
            // and Dispose(), leaking exactly the handle D43 guarantees is released -- worse than the D51
            // defect, which disposed too early rather than never.
            //
            // currentGenerationCts is null only if the scheduled job never ran at all (Rejected, or
            // Cancelled before its turn), which skips the call entirely. Otherwise it is live and this
            // scope owns it: the closure deliberately does not `using` it, because a source disposed
            // there makes this cancel throw on every ordinary request and turns CancelGuardedAsync's
            // Debug line into noise on the happy path. Cancelling an already-finished generation's
            // source is a no-op; the case this exists to serve is the one in between, a write that
            // failed while an attempt was still genuinely in flight.
            var generationCts = Volatile.Read(ref currentGenerationCts);
            if (generationCts is not null)
            {
                await GenerationPipeline.CancelGuardedAsync(generationCts, logger, requestId, "on the way out").ConfigureAwait(false);
            }

            if (generation is not null)
            {
                try
                {
                    await generation.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Already reported above, or unreportable because the client is gone. Observed here
                    // only so that the drain completes and the task does not fault unobserved.
                    logger.LogDebug(ex, "req={RequestId} generation ended with an exception; drained before disposing the context.",
                        requestId);
                }
            }

            lease?.Dispose();

            // After the drain, never before: until the generation task has ended, the token this source
            // owns is still the one that task is generating under. Read again rather than reused, in
            // case a retry replaced it between the cancel above and here.
            Volatile.Read(ref currentGenerationCts)?.Dispose();
        }
    }

    /// <summary>
    /// The calls with their positions, which only the streaming shape carries: a client assembles the
    /// array across chunks by <c>index</c>, and OpenAI's non-streaming tool call has no such field.
    /// </summary>
    private static ChatCompletionToolCall[] Indexed(IReadOnlyList<ChatCompletionToolCall> calls)
    {
        var indexed = new ChatCompletionToolCall[calls.Count];
        for (var i = 0; i < calls.Count; i++)
        {
            indexed[i] = calls[i].AtIndex(i);
        }

        return indexed;
    }

    /// <summary>
    /// Consumes the whole reply without writing any of it, emitting <c>: keep-alive</c> comments while
    /// it waits. This is the buffering PLAN §2.6 item 2 requires: with tools offered, a reply cannot be
    /// told from a tool call until the model has stopped, and a delta already written cannot be
    /// recalled.
    ///
    /// Every delta still goes through the cutter, so <c>max_tokens</c> and <c>stop</c> behave exactly
    /// as they do on a streamed reply and the model is still stopped at the cut; the difference is only
    /// that what the cutter releases is accumulated in it rather than written out. The caller reads it
    /// back from <c>EmittedText</c> once the flush has run.
    /// </summary>
    /// <returns>True when this drain cancelled the generation because the cut fired.</returns>
    private static async Task<bool> DrainBufferedAsync(
        SseStream sse,
        ChannelReader<string> reader,
        OutputCutter cutter,
        StreamingOptions streaming,
        TimeProvider time,
        Func<CancellationTokenSource> currentGenerationCts,
        ILogger logger,
        string requestId,
        CancellationToken cancellationToken)
    {
        var cancelled = false;
        var more = true;

        // The keep-alive is due a fixed time after the last frame went out, not a fixed time after the
        // last delta arrived. Waiting a fresh interval on every lap is what a naive loop does, and it
        // starves: a model producing a delta a second with a fifteen-second interval completes every
        // wait before its timer, so no keep-alive is ever written and a buffered reply is a response
        // that says nothing for its entire length — exactly the silence buffering needs them for.
        var due = Stopwatch.GetTimestamp() + (long)(streaming.KeepAliveInterval.TotalSeconds * Stopwatch.Frequency);

        while (more)
        {
            while (reader.TryRead(out var delta))
            {
                cutter.Accept(delta);

                if (cutter.StopRequested && !cancelled)
                {
                    cancelled = true;
                    // A function, not a captured value (chunk 8 fix round 1): the scheduled closure may
                    // reassign its own cancellation source across a --truncate-history retry, so this
                    // reads whichever one is current at the moment the cut actually fires rather than
                    // whichever one existed when the drain started.
                    await GenerationPipeline.CancelGuardedAsync(currentGenerationCts(), logger, requestId, "at the cut")
                        .ConfigureAwait(false);
                }
            }

            if (cutter.IsCut)
            {
                // Settled: nothing further will ever be released, so there is nothing left to buffer.
                // The caller's cancel-drain-settle runs unchanged.
                break;
            }

            if (streaming.KeepAliveInterval <= TimeSpan.Zero)
            {
                more = await reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false);
                continue;
            }

            var remaining = TimeSpan.FromSeconds((due - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
            if (remaining <= TimeSpan.Zero)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await sse.WriteAsync(StreamingPipeline.KeepAliveFrame, cancellationToken).ConfigureAwait(false);
                due = Stopwatch.GetTimestamp() + (long)(streaming.KeepAliveInterval.TotalSeconds * Stopwatch.Frequency);
                continue;
            }

            // WaitForDeltaAsync writes its own keep-alive if this wait runs the whole way out, so the
            // deadline is reset whenever it does — hence the assignment on both branches.
            more = await StreamingPipeline.WaitForDeltaAsync(sse, reader, streaming, remaining, time, cancellationToken).ConfigureAwait(false);
            if (!more || Stopwatch.GetTimestamp() >= due)
            {
                due = Stopwatch.GetTimestamp() + (long)(streaming.KeepAliveInterval.TotalSeconds * Stopwatch.Frequency);
            }
        }

        return cancelled;
    }

    /// <summary>
    /// One content-bearing chunk. With <paramref name="nullUsage"/> (the request asked for usage) it
    /// carries <c>"usage": null</c>, as every chunk before the usage chunk must.
    /// </summary>
    private static ChatCompletionChunk Chunk(
        string id,
        long created,
        string model,
        bool nullUsage,
        ChatCompletionDelta delta,
        string? finishReason)
    {
        var chunk = new ChatCompletionChunk(id, created, model, [new ChatCompletionChunkChoice(0, delta, finishReason)]);
        return nullUsage ? chunk.WithNullUsage() : chunk;
    }
}

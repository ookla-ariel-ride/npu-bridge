using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Configuration;

namespace NpuBridge.Api;

/// <summary>
/// The SSE plumbing both streaming shapes share: <see cref="ChatCompletionsStreamEndpoint"/> for
/// <c>POST /v1/chat/completions</c> and <see cref="CompletionsStreamEndpoint"/> for the legacy
/// <c>POST /v1/completions</c>. Task 3 copied these five pieces rather than extracting them, because
/// <c>ChatCompletionsStreamEndpoint</c> was already merged and reviewed and re-touching it for an
/// unrelated task looked riskier than a copy the review had diffed byte-identical. That review found
/// exactly one difference between the copies -- <see cref="SseStream.WriteChunkAsync{T}"/>'s argument
/// type, <c>ChatCompletionChunk</c> on one side and <c>CompletionChunk</c> on the other -- and the
/// method only ever serialises that argument, so a generic parameter is the whole fix. Everything else
/// here is unchanged from either copy; the doc comments are the fuller (chat) copy's, since the
/// completions copy had reduced some of them to one-line pointers at the copy.
///
/// D81's rule is what this pays off: the post-generation pipeline is written once so a third caller
/// uses it rather than copying it. <see cref="GenerationPipeline"/> is the other half of that same
/// rule, for the parts of phase two that do not depend on the wire shape at all; this type is for the
/// parts that are still streaming-shaped -- the keep-alive wait, the scheduler-outcome report, the
/// header-commit-on-first-write stream, and the failure reporter that decides between an ordinary HTTP
/// status and an SSE error event -- but are just as chunk-DTO-agnostic as <see cref="GenerationPipeline"/>
/// once <see cref="SseStream.WriteChunkAsync{T}"/> stops caring which chunk type it is given.
/// <see cref="SchedulerAdmission"/> is the third member of this family, extracted two rounds ago after
/// its own three copies were found already divergent on day one (a missing <c>sse.Started</c> guard in
/// one of them).
/// </summary>
internal static class StreamingPipeline
{
    /// <summary>Terminator of an SSE stream, per the OpenAI protocol. Not JSON, deliberately.</summary>
    internal const string DoneFrame = "data: [DONE]\n\n";

    /// <summary>
    /// An SSE comment: a client's event parser drops the line, but a proxy counting idle seconds sees
    /// traffic. This is what stops a slow first token — a cold model load can be tens of seconds on this
    /// hardware — from being read as a dead connection.
    /// </summary>
    internal const string KeepAliveFrame = ": keep-alive\n\n";

    /// <summary>
    /// Waits until either the first delta is queued (true) or the generation ended without producing one
    /// (false), emitting <c>: keep-alive</c> comments meanwhile. The first comment is the first byte of
    /// the response and commits the headers, so its delay answers a different question from the ones
    /// after it: those keep a proxy from calling the connection idle, this one keeps a client from
    /// waiting on headers — and it is also the window in which a failure can still be a real HTTP status.
    /// Hence two intervals: about a second, then every fifteen.
    ///
    /// The channel is completed on every outcome of the generation, a cancelled one included, so this
    /// returns on its own and the caller always reaches the drain. A client that leaves mid-wait is the
    /// other way out: the wait ends as an <see cref="OperationCanceledException"/>, which the caller's
    /// client-gone clause catches and answers with the silent http=0 log line. Both routes reach the
    /// caller's finally, so the context is disposed whichever happens.
    /// </summary>
    public static Task<bool> WaitForFirstDeltaAsync(
        SseStream sse,
        ChannelReader<string> reader,
        StreamingOptions streaming,
        TimeProvider time,
        CancellationToken cancellationToken) =>
        WaitForDeltaAsync(
            sse,
            reader,
            streaming,
            streaming.FirstKeepAliveDelay > TimeSpan.Zero ? streaming.FirstKeepAliveDelay : streaming.KeepAliveInterval,
            time,
            cancellationToken);

    /// <summary>
    /// The same wait, with the first delay chosen by the caller. A buffered reply (chunk 7) waits here
    /// repeatedly rather than once, and every wait after the first is on the ordinary interval: the
    /// shorter first delay exists to get headers to a client quickly, and by then they are long gone.
    /// </summary>
    public static async Task<bool> WaitForDeltaAsync(
        SseStream sse,
        ChannelReader<string> reader,
        StreamingOptions streaming,
        TimeSpan firstDelay,
        TimeProvider time,
        CancellationToken cancellationToken)
    {
        var wait = reader.WaitToReadAsync(CancellationToken.None).AsTask();
        if (streaming.KeepAliveInterval <= TimeSpan.Zero)
        {
            return await wait.ConfigureAwait(false);
        }

        var next = firstDelay > TimeSpan.Zero ? firstDelay : streaming.KeepAliveInterval;

        while (true)
        {
            try
            {
                // WaitAsync returns a task of its own and leaves `wait` -- the channel's, awaited again
                // on the next lap -- to complete when it completes. It costs nothing to lap: its
                // internal promise unregisters itself from `wait` and releases its timer on the timeout
                // path as well as on completion, so neither continuations nor timers accumulate however
                // long the model takes to produce its first token.
                return await wait.WaitAsync(next, time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // No delta yet -- unless one landed in the very gap between the timer firing and this
                // thread being scheduled to hear about it. Then the timeout is stale, and acting on it
                // would write a keep-alive that commits 200 to a request whose real answer is still an
                // ordinary HTTP status: a backend without a preflight reports an over-length prompt by
                // completing the generation with no delta at all, so "the channel closed" and "the timer
                // expired" becoming true in the same instant turns a 400 into an SSE error event.
                // Task.WhenAny used to settle this by argument order, which put `wait` first; WaitAsync
                // settles it by which fired first, so the preference is spelled out here instead of
                // inherited from an overload's parameter order.
                if (wait.IsCompleted)
                {
                    return await wait.ConfigureAwait(false);
                }
            }

            // A cancel and a timeout that become ready together can be reported either way round, so the
            // timeout path has to check: writing to a connection whose client has gone throws, and this
            // is the write that would do it.
            cancellationToken.ThrowIfCancellationRequested();

            await sse.WriteAsync(KeepAliveFrame, cancellationToken).ConfigureAwait(false);

            // The headers are out now, so every later comment is only about proxy idle timeouts.
            next = streaming.KeepAliveInterval;
        }
    }

    /// <summary>
    /// Reports a failure the only way still available. Before the first byte that is the ordinary status
    /// and body, returned to the caller; after it, the status line is spent, so the same body goes out as
    /// an SSE event followed by the done marker — a stream that ends badly still ends.
    /// </summary>
    public static async Task<IResult?> FailAsync(
        SseStream sse,
        GenerationFailure failure,
        ILogger logger,
        string requestId,
        CancellationToken cancellationToken)
    {
        if (!sse.Started)
        {
            return failure.ToResult();
        }

        if (cancellationToken.IsCancellationRequested)
        {
            // The stream is open but the client is not there to read the bad news. Writing would only
            // throw, and an unhandled exception is a worse way to end than silence.
            return null;
        }

        try
        {
            await sse.WriteAsync(failure.ToEventFrame(), cancellationToken).ConfigureAwait(false);
            await sse.WriteAsync(DoneFrame, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // This is the last thing the handler does for the client, and it is already reporting a
            // failure. A connection that dies between the check above and the write here would otherwise
            // throw a second exception on the way out of a catch block, which reaches the host as an
            // unhandled request exception and says nothing useful. The drain and the disposal in the
            // caller's finally are unaffected either way.
            logger.LogDebug(ex, "req={RequestId} could not write the stream's error event; the client is gone.", requestId);
        }

        return null;
    }

    /// <summary>
    /// Kept as the streamed shapes' entry point, forwarding to the one implementation in
    /// <see cref="GenerationPipeline"/> now that the JSON shapes share it too: the label means the same
    /// thing on all four endpoints, so it cannot live in a type only two of them use.
    /// </summary>
    public static string CacheLabel(ContextLease? lease) => GenerationPipeline.CacheLabel(lease);

    /// <summary>
    /// Awaits the scheduled attempt and reports whatever it means for the client: a real
    /// <see cref="ChatAttemptResult"/> to keep going with (<see cref="SchedulerOutcomeReport.Handled"/>
    /// false), the ordinary silent "client is gone" outcome, or one of the scheduler-level failures
    /// (queue full, shutting down, or a backend that threw <see cref="OperationCanceledException"/>
    /// instead of reporting a status -- fix round 1, Finding 1) via
    /// <see cref="SchedulerAdmission"/>, so this shape and the JSON one cannot describe the same
    /// condition differently (Finding 4). Every failure here goes through <see cref="FailAsync"/>,
    /// which is what makes it safe to call this after the reader loop has already been running for a
    /// while: by then <paramref name="sse"/> may well have already started (a queue wait or a preflight
    /// refusal discovered only once the closure finally ran can each have sent a keep-alive first,
    /// task-2-brief.md's controller ruling accepts this explicitly), and <c>FailAsync</c> is what
    /// decides between an ordinary status and an SSE error event. Called once before the reader loop
    /// starts (a synchronous Rejected/Cancelled never reaches <see cref="ScheduleResultKind.Completed"/>
    /// so <c>Handled</c> is always true there in practice) and once after it ends -- never more than
    /// twice, since Acquire moving inside the closure means there is only ever one scheduled attempt
    /// per request now (Finding 2's fix).
    /// </summary>
    public static async Task<SchedulerOutcomeReport> ReportSchedulerOutcomeAsync(
        Task<ScheduleResult<ChatAttemptResult>> generation,
        SseStream sse,
        HttpContext http,
        ILogger logger,
        string requestId,
        string backendName,
        PreparedChatRequest prepared,
        ConversationSession session,
        GenerationHealth generationHealth,
        BackendCallTracker backendCalls,
        Func<double> attemptDuration,
        CancellationToken aborted)
    {
        var scheduled = await generation.ConfigureAwait(false);
        var queueWaitMs = scheduled.QueueWait.TotalMilliseconds;
        var admission = SchedulerAdmission.Classify(scheduled, aborted.IsCancellationRequested);

        if (admission == SchedulerOutcome.ClientGone)
        {
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: scheduled.Kind.ToString(), finish: "-", httpStatus: 0,
                truncatedTurns: session.DroppedTurns, queueWaitMs: queueWaitMs);
            return new SchedulerOutcomeReport(true, null, null, queueWaitMs);
        }

        if (admission != SchedulerOutcome.Completed)
        {
            var schedulerFailure = SchedulerAdmission.FailureFor(
                admission,
                scheduled.RetryAfterSeconds,
                backendCalls.Faulted ? generationHealth : null,
                attemptDuration());

            // Only meaningful (and only safe to set -- headers are read-only once the response has
            // started) before the first byte, which the shared helper is what knows; FailAsync below is
            // what still tells the client the queue was full once the stream has already started, via
            // the ordinary error envelope's message rather than the header.
            SchedulerAdmission.ApplyRetryAfter(http.Response, admission, scheduled.RetryAfterSeconds);

            ChatRequestMetrics.LogRequest(logger, requestId, backendName, prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: admission.ToString(), finish: "-",
                httpStatus: sse.Started ? StatusCodes.Status200OK : schedulerFailure.StatusCode,
                truncatedTurns: session.DroppedTurns, queueWaitMs: queueWaitMs);
            var response = await FailAsync(sse, schedulerFailure, logger, requestId, aborted).ConfigureAwait(false);
            return new SchedulerOutcomeReport(true, response, null, queueWaitMs);
        }

        var attempt = scheduled.Result!;
        if (attempt.Refusal is { } refused)
        {
            ChatRequestMetrics.LogRequest(logger, requestId, backendName, prepared.PromptChars, ttftMs: 0, tokens: 0,
                status: GenerationStatus.PromptLargerThanContext.ToString(), finish: "-",
                httpStatus: sse.Started ? StatusCodes.Status200OK : refused.StatusCode,
                truncatedTurns: session.DroppedTurns, queueWaitMs: queueWaitMs);
            var response = await FailAsync(sse, refused, logger, requestId, aborted).ConfigureAwait(false);
            return new SchedulerOutcomeReport(true, response, null, queueWaitMs);
        }

        return new SchedulerOutcomeReport(false, null, attempt, queueWaitMs);
    }
}

/// <summary>
/// What awaiting the scheduled attempt meant, and whether the caller already has its answer.
/// <see cref="Handled"/> true means the caller should <c>return</c> <see cref="Response"/> (possibly
/// null, for the ordinary silent "client is gone" outcome) without looking at
/// <see cref="Attempt"/> or <see cref="QueueWaitMs"/> any further; false means the schedule reached
/// <see cref="ScheduleResultKind.Completed"/> and <see cref="Attempt"/> is the real
/// <see cref="ChatAttemptResult"/> to keep going with.
/// </summary>
internal readonly record struct SchedulerOutcomeReport(bool Handled, IResult? Response, ChatAttemptResult? Attempt, double QueueWaitMs);

/// <summary>
/// The response, plus the SSE framing and the one-time header assignment. The headers go on with
/// the first write rather than up front, which is what makes <see cref="Started"/> the question
/// "is the status code still mine to choose?" — the boundary the whole error story turns on. The
/// answer is the response's, not this class's; only the "have the fields been assigned yet" book-
/// keeping lives here.
/// </summary>
internal sealed class SseStream
{
    private readonly HttpResponse _response;
    private readonly Action? _beforeHeaders;
    private bool _headersPrepared;

    /// <param name="beforeHeaders">
    /// Run once, on this thread, in the last instant the response is still mutable — after that the
    /// status line, the content type and every header are fixed. It is where a header whose value is
    /// only known late (the truncated-turns count, chunk 8) gets its last chance, and running it here
    /// rather than from whichever thread computed it is what keeps every mutation of the response on
    /// the one thread that owns it.
    /// </param>
    public SseStream(HttpResponse response, Action? beforeHeaders = null)
    {
        _response = response;
        _beforeHeaders = beforeHeaders;
    }

    /// <summary>
    /// True once the response has actually begun, i.e. once 200 and <c>text/event-stream</c> are
    /// the answer and nothing can change them. The question every caller asks it is "is the status
    /// code still mine to choose?", so it is the response's own <c>HasStarted</c> rather than a
    /// flag this class raises when it assigns the header fields — assigning them commits nothing.
    ///
    /// <c>HttpResponse.WriteAsync</c> calls <c>StartAsync</c> before it writes a byte, so by the
    /// time a body write or flush fails the response really has started, and the flag this
    /// replaced was right about that case. The two part only when starting the response is itself
    /// what fails — a failing response-starting callback, or an already-cancelled token, since
    /// <c>StartAsync</c> observes one — and there this answer is the right one.
    /// </summary>
    public bool Started => _response.HasStarted;

    /// <summary>
    /// Serialises and writes one chunk. Generic over the chunk's own DTO type rather than fixed to
    /// either wire shape's -- the only thing that ever made this class chat- or completions-shaped,
    /// since the method only ever serialises its argument immediately. <typeparamref name="T"/> is
    /// always inferred at the call site, so each caller still gets exactly the JSON its own chunk
    /// type would have produced serialised directly.
    /// </summary>
    public Task WriteChunkAsync<T>(T chunk, CancellationToken cancellationToken) =>
        WriteAsync($"data: {JsonSerializer.Serialize(chunk, JsonDefaults.Options)}\n\n", cancellationToken);

    /// <summary>
    /// One SSE frame, flushed immediately: without the flush the chunks sit in Kestrel's buffer and
    /// the client sees the whole reply at once, which is the one thing streaming exists to avoid.
    /// </summary>
    public async Task WriteAsync(string frame, CancellationToken cancellationToken)
    {
        if (!_headersPrepared)
        {
            // Anything else that still wants a header goes on first: after this block the response
            // is committed by the write below and nothing can be added.
            _beforeHeaders?.Invoke();

            // Assigned once and only while the response is still mutable. A failed first write
            // leaves them assigned on a response that never started, which is harmless: the
            // ordinary error result the caller returns instead overwrites the status and the
            // content type, no-cache is right on that reply too, and the X-Accel-Buffering that
            // survives means nothing to a proxy handling a JSON error. Since chunk 8 the hook above
            // may have added a truncated-turns header to that list, and it survives the same way;
            // it states something true about the transcript either way, and the only client that can
            // see the difference is one that has already gone, since a first write fails when it has.
            //
            // X-Accel-Buffering defeats nginx's response buffering, which would otherwise hold the
            // whole stream and deliver it as one lump.
            _response.StatusCode = StatusCodes.Status200OK;
            _response.ContentType = "text/event-stream";
            _response.Headers.CacheControl = "no-cache";
            _response.Headers["X-Accel-Buffering"] = "no";
            _headersPrepared = true;
        }

        await _response.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await _response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

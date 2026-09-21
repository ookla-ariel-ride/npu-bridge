using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NpuBridge.Backends;
using NpuBridge.Configuration;

namespace NpuBridge.Api;

/// <summary>
/// What one scheduled attempt at a chat generation produced (chunk 8 fix round 1, controller ruling):
/// either a generation — successful or not, <see cref="Result"/>'s status decides that — or a preflight
/// refusal issued before a token was generated, after every truncation round <c>--truncate-history</c>
/// allowed. <see cref="ConversationSession.Acquire"/>'s two calls on the one shared model handle
/// (<c>CreateContext</c>, <c>GetUsablePromptLength</c>) moved inside the scheduled closure alongside
/// <c>GenerateAsync</c> itself, so this is what both response shapes' closures return instead of a bare
/// <see cref="GenerationResult"/>.
///
/// Deliberately <em>not</em> carrying the <see cref="ContextLease"/>: a lease the caller learns about
/// only from a value the closure returns is a lease the caller does not have when the closure's value
/// never reaches it. That is exactly what happened on the streaming shape — a client that disconnected
/// mid-stream unwound the reader loop before the scheduled task was ever unwrapped, so the
/// <c>finally</c>'s <c>lease?.Dispose()</c> saw null and the context was never released (D43 and D51
/// both). Each shape therefore publishes the attempt's lease to its own outer variable the instant
/// <c>Acquire</c> hands it over, from inside the closure, and that variable is the single thing the
/// <c>finally</c> settles however the method leaves.
/// </summary>
internal sealed record ChatAttemptResult(
    GenerationResult? Result,
    GenerationFailure? Refusal,
    bool CancelledByCut,
    double TtftMs,
    double TotalMs)
{
    public static ChatAttemptResult Refused(GenerationFailure failure) => new(null, failure, false, 0, 0);

    public static ChatAttemptResult Generated(GenerationResult result, bool cancelledByCut, double ttftMs, double totalMs) =>
        new(result, null, cancelledByCut, ttftMs, totalMs);
}

/// <summary>
/// Identifies exceptions raised by an invocation of the model backend. Callers use it to distinguish a
/// backend fault from bridge work that happens to run after a backend call, such as cutting output or
/// tokenizing usage.
/// </summary>
internal sealed class BackendCallTracker
{
    private Exception? _exception;

    public bool Faulted => Volatile.Read(ref _exception) is not null;

    public T Invoke<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _exception, ex);
            throw;
        }
    }

    public async Task<T> AwaitAsync<T>(Task<T> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _exception, ex);
            throw;
        }
    }

    public bool Caught(Exception exception) => ReferenceEquals(Volatile.Read(ref _exception), exception);
}

/// <summary>
/// The parts of phase two that are the same whichever shape the reply takes. Both
/// <see cref="ChatCompletionsEndpoint"/> and <see cref="ChatCompletionsStreamEndpoint"/> drive one
/// generation, time its first delta, may cancel it early, and then report what came back; only the
/// writing differs. Those shared steps used to be written out twice, and D56 and D57 each record a
/// drift between exactly those two copies — the same generation labelled <c>stop</c> on one shape and
/// <c>length</c> on the other, a backend <c>Error</c> answered 502 on one and 200 on the other. Chunk
/// 7's buffer-the-whole-reply path would have been a third copy, so they live here instead.
/// </summary>
internal static class GenerationPipeline
{
    /// <summary>
    /// Cancels the generation without letting the cancel itself fail the request.
    /// <see cref="CancellationTokenSource.CancelAsync"/> faults when a registration on the token
    /// throws, and CsWinRT registers one that calls <c>IAsyncInfo.Cancel()</c> on the live WinRT
    /// operation — a COM call that can fail rather than no-op. Every place either shape cancels goes on
    /// to await the generation and dispose the context regardless, so a throw here is a Debug line and
    /// nothing more. Cancelling a source twice is a no-op, so calling this at the cut and again on the
    /// way out is fine.
    /// </summary>
    /// <param name="where">Where the cancel was made, for the log line: "at the cut", "on the way out".</param>
    public static async Task CancelGuardedAsync(
        CancellationTokenSource generationCts,
        ILogger logger,
        string requestId,
        string where)
    {
        try
        {
            await generationCts.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "req={RequestId} cancelling the generation {Where} threw; draining and disposing anyway.",
                requestId, where);
        }
    }

    /// <summary>
    /// Awaits a cancelled generation without ever bounding that wait. The context cannot be settled while
    /// its backend operation may still be using it (D51), so this only observes a slow drain; it never
    /// turns one into a timeout. Both response shapes use this at their cancel-drain-settle boundary.
    /// </summary>
    public static async Task<T> DrainWithWarningsAsync<T>(
        Task<T> generation,
        int warningSeconds,
        TimeProvider time,
        ILogger logger,
        string requestId,
        string shape)
    {
        var interval = TimeSpan.FromSeconds(warningSeconds);
        var started = time.GetTimestamp();
        var warned = false;

        using var stopWarningDelay = new CancellationTokenSource();
        try
        {
            while (!generation.IsCompleted)
            {
                var nextWarning = Task.Delay(interval, time, stopWarningDelay.Token);
                if (await Task.WhenAny(generation, nextWarning).ConfigureAwait(false) == generation || generation.IsCompleted)
                {
                    break;
                }

                warned = true;
                var waitedSeconds = (long)Math.Floor(time.GetElapsedTime(started).TotalSeconds);
                logger.LogWarning("req={RequestId} shape={Shape} generation drain is still waiting after {SecondsWaited}s.",
                    requestId, shape, waitedSeconds);
            }

            return await generation.ConfigureAwait(false);
        }
        finally
        {
            stopWarningDelay.Cancel();
            if (warned)
            {
                var waitedSeconds = (long)Math.Floor(time.GetElapsedTime(started).TotalSeconds);
                logger.LogInformation("req={RequestId} shape={Shape} generation drain completed after {SecondsWaited}s.",
                    requestId, shape, waitedSeconds);
            }
        }
    }

    /// <summary>
    /// The log line's <c>cache=</c> field: <c>hit</c>, <c>miss</c>, or <c>-</c> for a request that never
    /// got as far as a context. Four copies of this one expression existed — one per endpoint — until
    /// <see cref="StreamingPipeline"/> took two of them and <see cref="JsonPipeline"/> the other two;
    /// it lives down here because both pipelines can reach it and neither owns it.
    /// </summary>
    public static string CacheLabel(ContextLease? lease) => lease is null ? "-" : lease.CacheHit ? "hit" : "miss";

    /// <summary>
    /// The whole model output, verbatim, under <c>--verbose</c>. The one place the raw text is logged:
    /// what a client is shown has been through the cut and the status mapping, so this is how a
    /// question about the model rather than about the bridge gets answered.
    /// </summary>
    public static void LogRawOutput(ILogger logger, BridgeOptions options, string requestId, GenerationResult result)
    {
        if (!options.Verbose)
        {
            return;
        }

        logger.LogInformation("req={RequestId} raw model output ({Status}):\n---- output ----\n{Text}\n---- end ----",
            requestId, result.Status, result.Text);
    }
}

/// <summary>
/// The only thing the backend's callback thread can reach. It holds a stopwatch and exactly one
/// destination, either a <see cref="ChannelWriter{T}"/> or a <see cref="CutWatcher"/>: no
/// <see cref="Microsoft.AspNetCore.Http.HttpResponse"/>, no <c>HttpContext</c>, and no delegate that
/// could close over one. So "never write to the response from the callback" stays a property of what
/// is in scope rather than a rule someone has to remember. An <c>Action&lt;string&gt;</c> parameter here
/// would accept a closure over the response and give that property away, which is why both
/// destinations are named types and why the constructor is private.
///
/// The mutable fields are touched through interlocked operations because <see cref="OnDelta"/> and the
/// request's own task run at once; the destination and the stopwatch are readonly and need none.
/// </summary>
internal sealed class DeltaSink
{
    private readonly Stopwatch _stopwatch;
    private readonly ChannelWriter<string>? _writer;
    private readonly CutWatcher? _watcher;
    private Exception? _bridgeFault;
    private long _firstTokenTicks;
    private int _count;

    private DeltaSink(Stopwatch stopwatch, ChannelWriter<string>? writer, CutWatcher? watcher)
    {
        _stopwatch = stopwatch;
        _writer = writer;
        _watcher = watcher;
    }

    /// <summary>
    /// The streaming path's sink: deltas cross into the channel whose single reader owns the response.
    /// That reader runs the cut, so there is no watcher here.
    /// </summary>
    /// <param name="stopwatch">Started when the request's generation phase began; read at the first delta.</param>
    public static DeltaSink ToChannel(Stopwatch stopwatch, ChannelWriter<string> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return new DeltaSink(stopwatch, writer, watcher: null);
    }

    /// <summary>
    /// The non-streaming path's sink. There is no channel — the handler awaits the whole text — but the
    /// deltas are still watched as they go by, to decide when to stop the model early.
    /// </summary>
    /// <param name="watcher">Null when the request set no limits, which is the ordinary case.</param>
    public static DeltaSink ToWatcher(Stopwatch stopwatch, CutWatcher? watcher) =>
        new(stopwatch, writer: null, watcher);

    /// <summary>Callbacks seen. Not a token count: runtimes batch several tokens per callback (D44).</summary>
    private int Count => Volatile.Read(ref _count);

    /// <summary>Stopwatch ticks at the first callback; meaningless when <see cref="Count"/> is 0.</summary>
    private long FirstTokenTicks => Interlocked.Read(ref _firstTokenTicks);

    /// <summary>
    /// Time to first token, the one thing either handler asks this type for. A generation that produced
    /// no delta at all has no first token to time, and reporting 0 would read as an instant one, so the
    /// whole elapsed time is reported instead.
    /// </summary>
    public double TtftMs(double totalMs) => Count == 0 ? totalMs : FirstTokenTicks * 1000.0 / Stopwatch.Frequency;

    /// <summary>The first bridge-side failure raised by the non-streaming cut watcher.</summary>
    public Exception? BridgeFault => Volatile.Read(ref _bridgeFault);

    public void OnDelta(string delta)
    {
        if (Interlocked.Increment(ref _count) == 1)
        {
            Interlocked.Exchange(ref _firstTokenTicks, _stopwatch.ElapsedTicks);
        }

        // Exactly one of these is ever set, so their order here says nothing and must not be read as a
        // rule. A future caller that wants both -- a streamed request that also watches its own deltas
        // -- has to add a third factory and decide the order there, because the watcher seeing a delta
        // after the channel reader has already acted on it is a different cut from the one this path
        // makes. Unbounded channel: TryWrite only fails once the writer is completed, which happens
        // after GenerateAsync has returned and so after the last callback.
        _writer?.TryWrite(delta);
        try
        {
            _watcher?.Accept(delta);
        }
        catch (Exception ex)
        {
            Interlocked.CompareExchange(ref _bridgeFault, ex, null);
            _watcher?.SignalFault();
        }
    }
}

/// <summary>
/// The non-streaming path's early stop. It runs an <see cref="OutputCutter"/> over the deltas purely to
/// decide when the generation has produced enough; the authoritative cut is applied afterwards to the
/// text the backend finally reports, with a second cutter, so the answer does not depend on which
/// deltas this one happened to see.
///
/// It never cancels anything itself. <see cref="Accept"/> runs on the backend's callback thread, and
/// cancelling from there is wrong twice over: a straight <c>Cancel()</c> can complete the generation's
/// await inline and re-enter the adapter while it is still inside this callback (Phi Silica then spins
/// draining a callback that cannot finish until it returns), and <c>CancelAfter(0)</c> moves the cancel
/// onto a timer thread, where a throwing registration — CsWinRT's <c>IAsyncInfo.Cancel()</c> on the live
/// operation is one — is rethrown with nothing above it to catch it, and the process terminates. So this
/// completes <see cref="Signal"/> and the request's own task, awaiting it, cancels on its own thread
/// inside a try. <c>RunContinuationsAsynchronously</c> keeps that continuation off the callback thread too.
///
/// The streaming path needs none of this: it drives its cutter from the single channel reader, which is
/// already the thread that owns the response.
/// </summary>
internal sealed class CutWatcher
{
    private readonly OutputCutter _cutter;
    private readonly TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CutWatcher(OutputLimits limits) => _cutter = new OutputCutter(limits);

    /// <summary>
    /// Completes once the model should be stopped or the watcher faults, and never faults itself.
    /// Never completes if no limit fires and no watcher fault occurs.
    /// </summary>
    public Task Signal => _signal.Task;

    /// <summary>Signals that a watcher fault needs the model stopped before the request reports it.</summary>
    public void SignalFault() => _signal.TrySetResult();

    /// <summary>
    /// One delta, from the backend's thread. Locked because the cutter is not thread-safe and a runtime
    /// is free to raise the next callback before this one returns.
    /// </summary>
    public void Accept(string delta)
    {
        // StopRequested, not IsCut: with a token budget the cutter may know the budget is passed before
        // it can place the cut exactly (D80); either way the model stops and the authoritative cut runs
        // over the text the backend returns.
        bool stop;
        lock (_cutter)
        {
            if (_cutter.StopRequested)
            {
                return;
            }

            _cutter.Accept(delta);
            stop = _cutter.StopRequested;
        }

        if (stop)
        {
            _signal.TrySetResult();
        }
    }
}

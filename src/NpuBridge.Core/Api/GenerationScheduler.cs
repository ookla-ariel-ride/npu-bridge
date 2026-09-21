using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NpuBridge.Configuration;

namespace NpuBridge.Api;

/// <summary>
/// Serializes generation against the one NPU model handle (PLAN §2.7, chunk 8). One worker drains a
/// bounded queue of jobs strictly one at a time, in FIFO order, so two concurrent requests never each
/// grab their own context the way chunk 5 left them able to. A full queue rejects the caller outright
/// rather than blocking it: the caller is an HTTP request with a client waiting on the other end, and a
/// request that blocked inside <see cref="ScheduleAsync{TResult}"/> would hold its connection, its
/// cancellation token and, on the streaming shape, its not-yet-committed response hostage until a slot
/// opened. D52 needs the queue-full decision made before the first SSE frame, so rejection is
/// synchronous with the enqueue attempt — never awaited, never a wait with a timeout.
///
/// This class knows nothing about backends, contexts or HTTP: <see cref="ScheduleAsync{TResult}"/>
/// takes an arbitrary async operation, so both response shapes drive their own generation through it
/// without either duplicating the queueing logic.
/// </summary>
/// <remarks>
/// Invariants that must not regress:
/// <list type="bullet">
///   <item>The worker never starts a job's body until the previous job's body has completed, including
///   one still running after its own token was cancelled — D51 one level up. Awaiting a cancelled
///   operation to the end is the drain; only then does the loop move to the next job.</item>
///   <item>A job cancelled while still queued completes its caller as
///   <see cref="ScheduleResultKind.Cancelled"/> the instant its token fires, without ever invoking its
///   body: the model is never touched for work nobody is waiting for, and the caller does not wait for
///   the worker to drain to its position first. <see cref="QueueDepth"/> also stops counting it at that
///   same instant (fix round 1, Finding 3) rather than only once the worker drains to it; the channel
///   slot itself is still held until then, which is invisible to every caller of this class.</item>
///   <item>An enqueue attempt after the scheduler has started shutting down is
///   <see cref="ScheduleResultKind.Cancelled"/>, not <see cref="ScheduleResultKind.Rejected"/>: a
///   stopped scheduler is never coming back to honour a <c>Retry-After</c>.</item>
///   <item>A queue-full rejection carries a <c>Retry-After</c>, already computed as whole seconds:
///   queue depth times the mean duration of the last <see cref="GenerationWindow"/> generations,
///   floored at 1. With no generation yet completed the mean is 0, so a cold-start rejection floors to
///   1 second — PLAN §2.7 does not define this case; that floor is the decision (task-1-brief.md).</item>
///   <item>Shutdown stops accepting new work and drains whatever is left in the queue as cancelled
///   rather than running it, but still awaits a job already running to its natural end (same D51
///   invariant, not suspended for shutdown).</item>
///   <item><see cref="QueueDepth"/> stays correct only while every job written to the channel is
///   eventually dequeued. That holds today because the worker loop cannot fault and drains after
///   <c>TryComplete</c>.</item>
/// </list>
/// </remarks>
public sealed class GenerationScheduler : IHostedService, IAsyncDisposable
{
    /// <summary>
    /// How long <see cref="DisposeAsync"/> waits for the worker to drain before giving up and returning
    /// anyway. Mirrors <c>BackendLifecycle.DisposeGracePeriod</c>: disposal must have the same escape
    /// hatch <see cref="StopAsync"/> has, or the two disagree about whether shutdown can hang forever on
    /// a generation that ignores cancellation (fix-round-1 finding 1).
    /// </summary>
    public static readonly TimeSpan DisposeGracePeriod = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How many recent generations the <c>Retry-After</c> estimate averages over. A cumulative mean
    /// since process start was the first shape, and it never forgets: one cold-start generation that
    /// took two minutes still drags every estimate this process ever makes, and a model that has since
    /// warmed up cannot talk it down. Sixteen is long enough that one outlier cannot own the estimate
    /// and short enough that a queue-capacity's worth of recent work (four by default) dominates it
    /// within a minute of load.
    /// </summary>
    private const int GenerationWindow = 16;

    private readonly Channel<IQueuedJob> _queue;
    private readonly TimeProvider _time;
    private readonly ILogger<GenerationScheduler> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _statsGate = new();

    /// <summary>The last <see cref="GenerationWindow"/> generation durations, oldest overwritten. Guarded by <see cref="_statsGate"/>.</summary>
    private readonly double[] _recentGenerationSeconds = new double[GenerationWindow];

    /// <summary>Generations recorded since start; only its low bits (the ring slot) and its cap at the window size are ever read. Guarded by <see cref="_statsGate"/>.</summary>
    private long _recordedGenerations;
    private Task _worker = Task.CompletedTask;
    private bool _disposed;

    /// <summary>
    /// Jobs genuinely waiting on the worker right now, live (fix round 1, Finding 3): incremented once a
    /// job is actually written to <see cref="_queue"/>, decremented the instant it stops being something
    /// the worker still needs to get to -- either because its own caller cancelled it while it was still
    /// queued, or because the worker dequeued it. Deliberately not <c>_queue.Reader.Count</c>: that count
    /// only shrinks when the worker drains to a cancelled job's position, so a caller that enqueues,
    /// times out and retries several times against one long generation used to leave every one of those
    /// dead jobs occupying a slot for the generation's whole duration -- shedding load the bridge had
    /// already stopped waiting for, and inflating <see cref="ComputeRetryAfterSeconds"/> off the same
    /// stale number. See <see cref="QueuedJob{TResult}.LeaveQueueIfNeeded"/> for how exactly-once is
    /// guaranteed between those two triggers.
    /// </summary>
    private int _liveQueueDepth;

    public GenerationScheduler(BridgeOptions options, TimeProvider time, ILogger<GenerationScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(logger);
        _time = time;
        _logger = logger;

        // BridgeOptionsBinder validates QueueCapacity to 1..1000; the floor here is only for a
        // scheduler built directly (as every test does), never routed through the binder.
        var capacity = Math.Max(1, options.QueueCapacity);
        _queue = Channel.CreateBounded<IQueuedJob>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// Jobs waiting for the worker right now — dequeued and running does not count, and (fix round 1,
    /// Finding 3) neither does a job whose own caller has already cancelled it while it was still
    /// queued: this is <see cref="_liveQueueDepth"/>, not <c>_queue.Reader.Count</c>. Read by
    /// <c>/healthz</c> (Task 2) and by <see cref="ComputeRetryAfterSeconds"/>.
    /// </summary>
    public int QueueDepth => Volatile.Read(ref _liveQueueDepth);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The worker's own lifetime is governed by _shutdown (via StopAsync/DisposeAsync), not by the
        // token the host happens to pass to this call, so that is deliberate rather than forwarded.
        _worker = Task.Run(RunWorkerAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops accepting new work and waits for the worker to drain. A job already running is awaited to
    /// its natural end regardless of who asked to cancel it (D51); everything still only queued is
    /// completed as <see cref="ScheduleResultKind.Cancelled"/> instead of run, so shutdown never hangs
    /// on work nobody will collect.
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _queue.Writer.TryComplete();

        try
        {
            // WaitAsync rather than WhenAny against a never-completing Task.Delay: the delay's
            // registration on the host's token outlived every shutdown the worker won, which is every
            // ordinary one, and stayed there for as long as the caller's token source did. WaitAsync
            // disposes its own registration on both outcomes.
            await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Generation scheduler did not drain before the host's own shutdown deadline; a job may still be running.");
        }
        catch (Exception ex)
        {
            // Defensive: RunAsync funnels every exception a job body throws into that job's own
            // completion source, so the worker loop itself cannot fault today. If it ever does, the
            // WhenAny this replaced said nothing at all and the fault reached no log on any path.
            // Logged rather than rethrown: shutdown reporting a worker's fault as its own failure is a
            // behaviour change nothing asked for, and DisposeAsync still swallows it exactly as before.
            _logger.LogWarning(ex, "The generation scheduler's worker ended with an exception rather than draining.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _shutdown.CancelAsync().ConfigureAwait(false);
        _queue.Writer.TryComplete();

        // Same escape hatch as StopAsync, on its own bounded clock rather than the host's: the two
        // must not disagree about whether disposal can be made to wait forever behind a generation
        // that never observes cancellation (fix-round-1 finding 1). WaitAsync for the same reason
        // StopAsync uses it: the Task.Delay this replaced kept a timer alive for the whole grace
        // period every time the worker drained first.
        try
        {
            await _worker.WaitAsync(DisposeGracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Generation scheduler did not drain within {Grace}s of disposal; a job may still be running.",
                DisposeGracePeriod.TotalSeconds);
        }
        catch (Exception ex)
        {
            // A faulted worker (see StopAsync, which logs it as a Warning): disposal must not throw for
            // it, which is what the WhenAny this replaced achieved by never observing it at all.
            _logger.LogDebug(ex, "The generation scheduler's worker had already faulted when it was disposed.");
        }

        _shutdown.Dispose();
    }

    /// <summary>
    /// Enqueues one generation. Returns immediately — never blocks — with
    /// <see cref="ScheduleResultKind.Rejected"/> when the queue is already full, or with
    /// <see cref="ScheduleResultKind.Cancelled"/> when the scheduler is already shutting down (a
    /// stopped scheduler answers "this will never run" rather than "try again in N seconds", since
    /// nothing will be here to honour a <c>Retry-After</c>). Otherwise the returned task completes once
    /// the worker has run <paramref name="operation"/>, or has dropped it, uncalled, because
    /// <paramref name="cancellationToken"/> fired — either while the job was still queued (completed
    /// immediately, without waiting for the worker to drain to its position) or, if the operation itself
    /// throws <see cref="OperationCanceledException"/> for that same token, while it was running.
    /// <see cref="ScheduleResult{TResult}.QueueWait"/> on every non-rejected outcome is how long the job
    /// actually waited, for the caller's <c>queue_wait_ms</c> log field; a rejected or already-cancelled
    /// job never queued at all, so its <see cref="ScheduleResult{TResult}.RetryAfterSeconds"/> (rejected
    /// only) is what matters instead.
    /// </summary>
    /// <remarks>
    /// The returned task can also fault: <paramref name="operation"/> throwing anything other than an
    /// <see cref="OperationCanceledException"/> for its own token is not translated into a
    /// <see cref="ScheduleResultKind"/> — the caller sees that exception rethrown from its own
    /// <c>await</c>, exactly as if it had called <paramref name="operation"/> directly. Only
    /// cancellation and queue state are the scheduler's to interpret.
    /// </remarks>
    public Task<ScheduleResult<TResult>> ScheduleAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);

        var job = new QueuedJob<TResult>(operation, _time.GetUtcNow(), _time,
            onLeftQueue: () => Interlocked.Decrement(ref _liveQueueDepth), cancellationToken);

        // Controller ruling, fix-round-1 finding 3: complete the caller the moment its own token fires
        // rather than leaving it to wait for the worker to drain to this job's position in the queue.
        // Armed BEFORE TryWrite (fix-round-2 finding), not after: TryWrite publishes the job to the
        // worker thread immediately, and arming a moment later left a window where the worker could
        // dequeue and settle the job -- Drop, or a fast RunAsync -- before this thread reached the arm
        // call, so the registration assigned after that was never disposed by anyone and leaked for as
        // long as the caller's own token source lived. Arming first makes TryWrite's own handoff a real
        // happens-before, so every disposal path the worker can reach always sees the live field.
        job.ArmCancellationCompletion();

        if (_queue.Writer.TryWrite(job))
        {
            // Only from here on does this job count toward QueueDepth (Finding 3): before this line the
            // cancellation callback above could have already fired (a token already cancelled when
            // ScheduleAsync was called runs its UnsafeRegister callback inline) and found nothing to
            // decrement, correctly, since MarkEnteredQueue had not run yet.
            Interlocked.Increment(ref _liveQueueDepth);
            job.MarkEnteredQueue();
            return job.Completion.Task;
        }

        // The job never reached the queue at all, so neither Drop nor RunAsync will ever run to
        // dispose the registration this call just armed -- this is the one path that must do it
        // itself. Idempotent alongside the registration's own self-dispose: if the token fired in the
        // narrow window between arming and this failed TryWrite, Completion is already set to
        // Cancelled and this is a harmless second Dispose -- the Rejected/Cancelled result returned
        // below simply supersedes it, and nothing about that result is an exception nobody observes.
        job.DisposeCancellationRegistration();

        if (_shutdown.IsCancellationRequested)
        {
            // Controller ruling, review finding 8: the scheduler is going away, not merely busy, so
            // the true answer is Cancelled -- Task 2 turns this into a 503, not a 429 with a
            // Retry-After aimed at a process that will not be here to honour it.
            return Task.FromResult(ScheduleResult.Cancelled<TResult>(TimeSpan.Zero));
        }

        var retryAfter = ComputeRetryAfterSeconds();
        _logger.LogWarning("Generation queue is full (depth {Depth}); rejecting with Retry-After {RetryAfterSeconds}s.",
            QueueDepth, retryAfter);
        return Task.FromResult(ScheduleResult.Rejected<TResult>(retryAfter));
    }

    /// <summary>queue depth × the mean of the last <see cref="GenerationWindow"/> generations' seconds, floored at 1, in whole seconds (task-1-brief.md).</summary>
    private int ComputeRetryAfterSeconds()
    {
        var depth = QueueDepth;
        double average;
        lock (_statsGate)
        {
            // Summed on demand rather than carried as a running total: sixteen additions on the
            // rejection path only, and no accumulated floating-point drift over a long-lived process.
            var count = (int)Math.Min(_recordedGenerations, GenerationWindow);
            var total = 0.0;
            for (var i = 0; i < count; i++)
            {
                total += _recentGenerationSeconds[i];
            }

            average = count == 0 ? 0 : total / count;
        }

        var seconds = (int)Math.Ceiling(depth * average);
        return Math.Max(1, seconds);
    }

    private async Task RunWorkerAsync()
    {
        while (await _queue.Reader.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
        {
            while (_queue.Reader.TryRead(out var job))
            {
                // From here on this job is the worker's to decide about; ArmCancellationCompletion's
                // registration steps back once it sees this (fix-round-1 finding 3's carve-out), so a
                // cancel that arrives after this point is the running job's own to answer for -- D51 --
                // rather than something this loop preempts out from under it.
                job.MarkDequeued();

                var queueWait = _time.GetUtcNow() - job.EnqueuedAt;

                // Shutdown or the request's own cancellation: either way this job was never dequeued
                // while there was still someone to run it for, so its body never runs.
                if (_shutdown.IsCancellationRequested || job.IsCancellationRequested)
                {
                    job.Drop(queueWait);
                    continue;
                }

                var start = _time.GetUtcNow();
                await job.RunAsync(queueWait).ConfigureAwait(false);
                RecordGenerationDuration((_time.GetUtcNow() - start).TotalSeconds);
            }
        }
    }

    /// <summary>
    /// Records one job whose body actually ran (completed or cancelled mid-run) — never one dropped
    /// while only queued, which touched the model for zero seconds and would only drag the estimate
    /// down. Kept in a fixed <see cref="GenerationWindow"/>-slot ring, oldest overwritten, so
    /// <see cref="ComputeRetryAfterSeconds"/> describes what this bridge is doing now rather than
    /// everything it has ever done.
    /// </summary>
    private void RecordGenerationDuration(double seconds)
    {
        lock (_statsGate)
        {
            _recentGenerationSeconds[(int)(_recordedGenerations % GenerationWindow)] = seconds;
            _recordedGenerations++;
        }
    }

    /// <summary>The type-erased half of <see cref="QueuedJob{TResult}"/> the worker loop can hold in one channel regardless of what each caller's operation returns.</summary>
    internal interface IQueuedJob
    {
        DateTimeOffset EnqueuedAt { get; }

        bool IsCancellationRequested { get; }

        /// <summary>
        /// Marks that the worker now owns this job's fate. Called exactly once, the moment it is
        /// dequeued, whether it is about to be run or dropped -- from this point on, a cancellation of
        /// its own token is the worker's (via <see cref="Drop"/> or <see cref="RunAsync"/>) to answer,
        /// not the standing registration's.
        /// </summary>
        void MarkDequeued();

        /// <summary>Completes the job as cancelled without ever invoking its operation.</summary>
        void Drop(TimeSpan queueWait);

        /// <summary>Invokes the operation and completes the job with whatever it returned, threw, or was cancelled with.</summary>
        Task RunAsync(TimeSpan queueWait);
    }

    /// <summary>
    /// One scheduled operation and its queue-depth state. Internal so tests can drive the publish-before-arm
    /// gate without relying on a worker-thread race.
    /// </summary>
    internal sealed class QueuedJob<TResult> : IQueuedJob
    {
        private readonly Func<CancellationToken, Task<TResult>> _operation;
        private readonly TimeProvider _time;
        private readonly CancellationToken _cancellationToken;
        private readonly Action _onLeftQueue;
        private CancellationTokenRegistration _cancellationRegistration;

        // The arming, dequeue and cancellation signals can arrive in either order: the job is published
        // before MarkEnteredQueue runs, while cancellation is registered before publication. Once arming
        // records that this job was counted, either a prior dequeue or a prior queued cancellation retries
        // the leave. _queueDepthClaimed makes the eventual callback exactly-once.
        private int _queueDepthArmed;
        private int _queueDepthClaimed;
        private int _dequeued;
        private int _cancelledWhileQueued;

        public QueuedJob(Func<CancellationToken, Task<TResult>> operation, DateTimeOffset enqueuedAt, TimeProvider time, Action onLeftQueue, CancellationToken cancellationToken)
        {
            _operation = operation;
            EnqueuedAt = enqueuedAt;
            _time = time;
            _cancellationToken = cancellationToken;
            _onLeftQueue = onLeftQueue;
        }

        public TaskCompletionSource<ScheduleResult<TResult>> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public DateTimeOffset EnqueuedAt { get; }

        public bool IsCancellationRequested => _cancellationToken.IsCancellationRequested;

        /// <summary>
        /// Called once, right after a successful <c>TryWrite</c> -- arms <see cref="LeaveQueueIfNeeded"/>
        /// to actually decrement the live counter this job was just added to.
        ///
        /// Task 3b review fix round 1, Finding 1: the worker thread can call <see cref="MarkDequeued"/>
        /// -- itself calling <see cref="LeaveQueueIfNeeded"/> -- before this thread reaches this call at
        /// all, since <c>TryWrite</c> hands the job to the worker immediately and the worker can dequeue
        /// and even finish running it before the enqueuing thread gets here. When that happens
        /// <see cref="MarkDequeued"/>'s or the cancellation callback's own <see cref="LeaveQueueIfNeeded"/>
        /// call can see <see cref="_queueDepthArmed"/> still 0 and return without decrementing. After
        /// arming, this checks both signals and retries the leave itself; <see cref="_queueDepthClaimed"/>'s
        /// <c>CompareExchange</c> is what keeps the actual decrement exactly-once regardless of which
        /// signal gets there first.
        ///
        /// <see cref="Interlocked.Exchange(ref int, int)"/>, not <c>Volatile.Write</c>, on both this and
        /// <see cref="MarkDequeued"/>'s write to <see cref="_dequeued"/>: the two threads write one flag
        /// and read the other in mirrored order (a Dekker shape), and a plain release store paired with
        /// an acquire load on the same field does not by itself stop that store from being reordered
        /// past this thread's own later read of the *other* field on a weaker memory model -- this
        /// project's exe targets ARM64. The full fence each `Interlocked` call carries is what makes the
        /// ordering an actual guarantee instead of a bet that happened not to lose in this run.
        /// </summary>
        public void MarkEnteredQueue()
        {
            Interlocked.Exchange(ref _queueDepthArmed, 1);
            if (Volatile.Read(ref _dequeued) != 0 || Volatile.Read(ref _cancelledWhileQueued) != 0)
            {
                LeaveQueueIfNeeded();
            }
        }

        public void MarkDequeued()
        {
            Interlocked.Exchange(ref _dequeued, 1);
            LeaveQueueIfNeeded();
        }

        /// <summary>
        /// Decrements the scheduler's live queue-depth counter exactly once for this job, and only if it
        /// was ever counted at all (<see cref="_queueDepthArmed"/>) -- never for a job whose enqueue
        /// attempt failed (queue full), even if the cancellation callback below happened to fire in the
        /// narrow window before that failure was known.
        /// </summary>
        private void LeaveQueueIfNeeded()
        {
            if (Volatile.Read(ref _queueDepthArmed) == 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _queueDepthClaimed, 1, 0) == 0)
            {
                _onLeftQueue();
            }
        }

        /// <summary>
        /// Completes <see cref="Completion"/> as <see cref="ScheduleResultKind.Cancelled"/> the instant
        /// <see cref="_cancellationToken"/> fires, instead of leaving the caller to wait for the worker
        /// to drain to this job's position in the queue (fix-round-1 finding 3) -- but only while the
        /// job is still genuinely queued. Once <see cref="MarkDequeued"/> has run, a later cancel is the
        /// worker's own to answer: D51 requires an already-running operation be awaited to whatever end
        /// it reaches (which may be a normal <see cref="ScheduleResultKind.Completed"/>, if the
        /// operation itself chooses to ignore its token), not preempted by this callback the instant the
        /// token fires. Races harmlessly with <see cref="Drop"/> and <see cref="RunAsync"/> in every
        /// case: all three use <c>TrySetResult</c>, so only the first to arrive matters.
        /// <see cref="CancellationToken.UnsafeRegister"/> is used (not <c>Register</c>) because this
        /// callback captures no ambient context worth flowing, matching the reviewer's suggestion.
        /// </summary>
        /// <remarks>
        /// Must be called <em>before</em> the job is published to the worker (fix-round-2 finding): the
        /// channel handoff (<c>TryWrite</c>/dequeue) is the only real happens-before between the
        /// enqueuing thread and the worker thread, so arming first is what guarantees every disposal
        /// path the worker can reach (<see cref="Drop"/>, <see cref="RunAsync"/>'s <c>finally</c>) sees
        /// a fully-written <see cref="_cancellationRegistration"/> rather than racing its assignment.
        /// Arming after publication left a window where the worker could dequeue and settle the job
        /// before the enqueuing thread reached this call, so the registration written a moment later was
        /// never disposed by anyone and leaked for as long as the caller's own token source lived.
        /// </remarks>
        public void ArmCancellationCompletion()
        {
            _cancellationRegistration = _cancellationToken.UnsafeRegister(static state =>
            {
                var job = (QueuedJob<TResult>)state!;
                if (Volatile.Read(ref job._dequeued) != 0)
                {
                    // The worker already owns this job (running, or about to decide to drop it via its
                    // own IsCancellationRequested check); Drop/RunAsync's own completion and dispose is
                    // what settles it, not this callback.
                    return;
                }

                Interlocked.Exchange(ref job._cancelledWhileQueued, 1);

                var queueWait = job._time.GetUtcNow() - job.EnqueuedAt;
                job.Completion.TrySetResult(ScheduleResult.Cancelled<TResult>(queueWait));

                // Fix round 1, Finding 3: this job is no longer something the worker still needs to
                // reach, so it stops counting toward QueueDepth right now rather than whenever the
                // worker eventually drains to it.
                job.LeaveQueueIfNeeded();

                // Safe to dispose the registration from inside its own callback: it is already
                // running, so this only stops it from being disposed again later and releases the
                // token's reference to it.
                job._cancellationRegistration.Dispose();
            }, this);
        }

        /// <summary>
        /// For the one path where the job never reached the queue at all (<c>TryWrite</c> failed), so
        /// neither <see cref="Drop"/> nor <see cref="RunAsync"/> will ever run to dispose the
        /// registration <see cref="ArmCancellationCompletion"/> just created. Safe to call unconditionally:
        /// idempotent alongside the registration's own self-dispose in the narrow window where the token
        /// fired between arming and the failed <c>TryWrite</c>.
        /// </summary>
        public void DisposeCancellationRegistration() => _cancellationRegistration.Dispose();

        public void Drop(TimeSpan queueWait)
        {
            Completion.TrySetResult(ScheduleResult.Cancelled<TResult>(queueWait));
            _cancellationRegistration.Dispose();
        }

        public async Task RunAsync(TimeSpan queueWait)
        {
            try
            {
                var result = await _operation(_cancellationToken).ConfigureAwait(false);
                Completion.TrySetResult(ScheduleResult.Completed(result, queueWait));
            }
            catch (OperationCanceledException) when (_cancellationToken.IsCancellationRequested)
            {
                // Fix round 1, Finding 1: this operation was invoked -- RunAsync only ever runs after
                // the worker's own IsCancellationRequested check found the token still live at dequeue
                // (otherwise the job took the Drop path below instead) -- and it ended by throwing for
                // its own token rather than reporting a domain result. That is a real thing that
                // happened to a running generation, not "the scheduler never got to this job", and a
                // caller must be able to tell the two apart (Cancelled with Ran false is Drop's; Ran
                // true is this one) rather than reporting both as the same benign "dropped while
                // queued" -- which is what let an adapter that breaks the no-raw-cancellation contract
                // (D82) come back as an incorrectly cheerful "the queue is shutting down" instead of the
                // failure it is.
                Completion.TrySetResult(ScheduleResult.Cancelled<TResult>(queueWait, ran: true));
            }
            catch (Exception ex)
            {
                Completion.TrySetException(ex);
            }
            finally
            {
                _cancellationRegistration.Dispose();
            }
        }
    }
}

public enum ScheduleResultKind
{
    /// <summary>The operation ran to completion and returned a result.</summary>
    Completed,

    /// <summary>
    /// The job's cancellation token fired before the worker reached it (its body never ran), or the
    /// operation itself ended by throwing <see cref="OperationCanceledException"/> for that same token.
    /// </summary>
    Cancelled,

    /// <summary>The queue was already full; the operation never ran and never will for this call. <see cref="ScheduleResult{TResult}.RetryAfterSeconds"/> is set.</summary>
    Rejected,
}

/// <summary>
/// What <see cref="GenerationScheduler.ScheduleAsync{TResult}"/> hands back. <see cref="Result"/> is
/// meaningful only when <see cref="Kind"/> is <see cref="ScheduleResultKind.Completed"/>;
/// <see cref="RetryAfterSeconds"/> only when it is <see cref="ScheduleResultKind.Rejected"/>.
/// <see cref="QueueWait"/> is meaningful on every kind except <see cref="ScheduleResultKind.Rejected"/>,
/// which never queued at all.
/// </summary>
public sealed class ScheduleResult<TResult>
{
    internal ScheduleResult(ScheduleResultKind kind, TResult? result, TimeSpan queueWait, int retryAfterSeconds, bool ran)
    {
        Kind = kind;
        Result = result;
        QueueWait = queueWait;
        RetryAfterSeconds = retryAfterSeconds;
        Ran = ran;
    }

    public ScheduleResultKind Kind { get; }

    /// <summary>The operation's return value. Default when <see cref="Kind"/> is not <see cref="ScheduleResultKind.Completed"/>.</summary>
    public TResult? Result { get; }

    /// <summary>
    /// How long the job sat in the queue before the worker reached it — the caller's <c>queue_wait_ms</c>
    /// log field. <see cref="TimeSpan.Zero"/> on <see cref="ScheduleResultKind.Rejected"/>.
    /// </summary>
    public TimeSpan QueueWait { get; }

    /// <summary>Whole seconds for the <c>Retry-After</c> header. Zero when <see cref="Kind"/> is not <see cref="ScheduleResultKind.Rejected"/>.</summary>
    public int RetryAfterSeconds { get; }

    /// <summary>
    /// True once the worker actually invoked the caller's operation. Always true on
    /// <see cref="ScheduleResultKind.Completed"/>; on <see cref="ScheduleResultKind.Cancelled"/> it is
    /// what tells apart a job the worker never got to run at all (dropped while still queued, or a
    /// post-shutdown enqueue -- <c>false</c>, nothing touched the model) from one that ran and ended by
    /// throwing <see cref="OperationCanceledException"/> for its own token instead of reporting a
    /// domain result (<c>true</c> -- something real happened to a live generation, most plausibly a
    /// backend adapter letting the runtime's own cancellation escape rather than reporting a
    /// <c>Cancelled</c> status, which is exactly the contract violation D82 exists to answer). Always
    /// <c>false</c> on <see cref="ScheduleResultKind.Rejected"/>: the queue was full, and nothing ran.
    /// Added fix round 1, Finding 1, after both kinds of <c>Cancelled</c> looked identical to a caller
    /// and a job that ran and threw was reported as the queue merely being busy.
    /// </summary>
    public bool Ran { get; }
}

/// <summary>
/// Non-generic factories for <see cref="ScheduleResult{TResult}"/> (CA1000: a generic type may not
/// declare its own static members), so a caller writes <c>ScheduleResult.Completed(text, wait)</c> with
/// <typeparamref name="TResult"/> inferred rather than named.
/// </summary>
public static class ScheduleResult
{
    public static ScheduleResult<TResult> Completed<TResult>(TResult result, TimeSpan queueWait) =>
        new(ScheduleResultKind.Completed, result, queueWait, 0, ran: true);

    /// <param name="ran">
    /// True only from <see cref="GenerationScheduler"/>'s own worker, for a job whose operation was
    /// invoked and ended by throwing <see cref="OperationCanceledException"/> for its own token. Every
    /// other caller (a job dropped while still queued, or a post-shutdown enqueue) leaves this false.
    /// </param>
    public static ScheduleResult<TResult> Cancelled<TResult>(TimeSpan queueWait, bool ran = false) =>
        new(ScheduleResultKind.Cancelled, default, queueWait, 0, ran);

    public static ScheduleResult<TResult> Rejected<TResult>(int retryAfterSeconds) =>
        new(ScheduleResultKind.Rejected, default, TimeSpan.Zero, retryAfterSeconds, ran: false);
}

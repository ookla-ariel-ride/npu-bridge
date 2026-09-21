using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NpuBridge.Api;
using NpuBridge.Configuration;

namespace NpuBridge.Tests;

/// <summary>
/// The scheduler on its own, with no backend and no HTTP: <see cref="GenerationScheduler.ScheduleAsync{TResult}"/>
/// takes an arbitrary async operation, so every test drives it with hand-built gates the way
/// <c>FakeBackend</c>'s <c>StartGate</c>/<c>FirstTokenGate</c>/<c>CancellationGate</c> drive a
/// generation — released by the test, never by a clock (D54). Ordering is asserted structurally: the
/// worker is a single reader over a single channel, so "job B cannot have started" while job A's gate
/// is still open is a fact about the implementation, not a timing guess.
/// </summary>
public class GenerationSchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 12, 8, 0, 0, TimeSpan.Zero);

    private static GenerationScheduler NewScheduler(int queueCapacity = 4, TimeProvider? time = null) =>
        new(new BridgeOptions { QueueCapacity = queueCapacity }, time ?? TimeProvider.System, NullLogger<GenerationScheduler>.Instance);

    private static GenerationScheduler.QueuedJob<string> NewQueuedJob(Action onLeftQueue, CancellationToken cancellationToken) =>
        new(_ => Task.FromResult("unused"), T0, TimeProvider.System, onLeftQueue, cancellationToken);

    [Fact]
    public async Task Jobs_run_one_at_a_time()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var order = new List<int>();
            var gateA = new TaskCompletionSource();
            var startedA = new TaskCompletionSource();

            var taskA = scheduler.ScheduleAsync<string>(async ct =>
            {
                startedA.TrySetResult();
                await gateA.Task.WaitAsync(ct).ConfigureAwait(false);
                lock (order) { order.Add(1); }
                return "a";
            }, CancellationToken.None);

            await startedA.Task;

            var taskB = scheduler.ScheduleAsync<string>(ct =>
            {
                lock (order) { order.Add(2); }
                return Task.FromResult("b");
            }, CancellationToken.None);

            // Structural, not timing-based: the worker is a single reader awaiting A's body to
            // completion, and A's body is parked on gateA, so B cannot have been dequeued yet.
            lock (order)
            {
                Assert.DoesNotContain(2, order);
            }

            gateA.SetResult();
            var resultA = await taskA;
            var resultB = await taskB;

            Assert.Equal(new[] { 1, 2 }, order);
            Assert.Equal(ScheduleResultKind.Completed, resultA.Kind);
            Assert.Equal("a", resultA.Result);
            Assert.Equal(ScheduleResultKind.Completed, resultB.Kind);
            Assert.Equal("b", resultB.Result);
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    [Fact]
    public async Task Ordering_is_FIFO_across_more_than_two_jobs()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var order = new List<int>();
            var gate = new TaskCompletionSource();
            var startedFirst = new TaskCompletionSource();

            // The first job holds the worker so the other three are still sitting in the channel,
            // in the order they were enqueued, when the worker finally reaches them.
            var first = scheduler.ScheduleAsync<int>(async ct =>
            {
                startedFirst.TrySetResult();
                await gate.Task.WaitAsync(ct).ConfigureAwait(false);
                lock (order) { order.Add(0); }
                return 0;
            }, CancellationToken.None);
            await startedFirst.Task;

            var second = Enqueue(2);
            var third = Enqueue(3);
            var fourth = Enqueue(4);

            Task<ScheduleResult<int>> Enqueue(int n) => scheduler.ScheduleAsync<int>(ct =>
            {
                lock (order) { order.Add(n); }
                return Task.FromResult(n);
            }, CancellationToken.None);

            gate.SetResult();
            await first;
            await second;
            await third;
            await fourth;

            Assert.Equal(new[] { 0, 2, 3, 4 }, order);
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    [Fact]
    public async Task Enqueue_past_capacity_is_rejected_with_a_retry_after_of_at_least_one_second()
    {
        var scheduler = NewScheduler(queueCapacity: 1);
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var gateA = new TaskCompletionSource();
            var startedA = new TaskCompletionSource();
            var taskA = scheduler.ScheduleAsync<string>(async ct =>
            {
                startedA.TrySetResult();
                await gateA.Task.WaitAsync(ct).ConfigureAwait(false);
                return "a";
            }, CancellationToken.None);
            await startedA.Task; // A is running; the channel itself is empty.

            var gateB = new TaskCompletionSource();
            var taskB = scheduler.ScheduleAsync<string>(async ct =>
            {
                await gateB.Task.WaitAsync(ct).ConfigureAwait(false);
                return "b";
            }, CancellationToken.None);
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 1); // B fills the one slot.

            // With no generation ever completed the rolling average is 0, so the floor applies.
            var rejected = await scheduler.ScheduleAsync<string>(_ => Task.FromResult("c"), CancellationToken.None);

            Assert.Equal(ScheduleResultKind.Rejected, rejected.Kind);
            Assert.True(rejected.RetryAfterSeconds >= 1);
            Assert.Equal(TimeSpan.Zero, rejected.QueueWait);

            gateA.SetResult();
            gateB.SetResult();
            await taskA;
            await taskB;
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    [Fact]
    public async Task Retry_after_is_queue_depth_times_the_rolling_average_floored_at_one()
    {
        var time = new ManualTimeProvider(T0);
        var scheduler = NewScheduler(queueCapacity: 2, time: time);
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            // Two completed generations, 2s and 4s: rolling average settles at 3s, measured entirely
            // through the manual clock -- no real delay anywhere in this test.
            await RunOneJobAsync(scheduler, time, TimeSpan.FromSeconds(2));
            await RunOneJobAsync(scheduler, time, TimeSpan.FromSeconds(4));

            // Fill the queue to its capacity of 2: X is running, Y and Z sit in the channel.
            var gateX = new TaskCompletionSource();
            var startedX = new TaskCompletionSource();
            var taskX = scheduler.ScheduleAsync<string>(async ct =>
            {
                startedX.TrySetResult();
                await gateX.Task.WaitAsync(ct).ConfigureAwait(false);
                return "x";
            }, CancellationToken.None);
            await startedX.Task;

            var gateY = new TaskCompletionSource();
            var taskY = scheduler.ScheduleAsync<string>(async ct =>
            {
                await gateY.Task.WaitAsync(ct).ConfigureAwait(false);
                return "y";
            }, CancellationToken.None);
            var gateZ = new TaskCompletionSource();
            var taskZ = scheduler.ScheduleAsync<string>(async ct =>
            {
                await gateZ.Task.WaitAsync(ct).ConfigureAwait(false);
                return "z";
            }, CancellationToken.None);
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 2);

            var rejected = await scheduler.ScheduleAsync<string>(_ => Task.FromResult("w"), CancellationToken.None);

            Assert.Equal(ScheduleResultKind.Rejected, rejected.Kind);
            Assert.Equal(6, rejected.RetryAfterSeconds); // ceil(2 * 3.0)

            gateX.SetResult();
            gateY.SetResult();
            gateZ.SetResult();
            await taskX;
            await taskY;
            await taskZ;
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    /// <summary>Runs one job whose measured duration is exactly <paramref name="duration"/>, advancing only the manual clock -- never a real delay.</summary>
    private static async Task RunOneJobAsync(GenerationScheduler scheduler, ManualTimeProvider time, TimeSpan duration)
    {
        var started = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var task = scheduler.ScheduleAsync<string>(async ct =>
        {
            started.TrySetResult();
            await gate.Task.WaitAsync(ct).ConfigureAwait(false);
            return "ok";
        }, CancellationToken.None);

        await started.Task; // the worker has captured its start time by this point
        time.Advance(duration);
        gate.SetResult();
        await task;
    }

    /// <summary>
    /// Issue #26 item 4: the estimate averages the last sixteen attempts, not every attempt since the
    /// process started. A cumulative mean never forgets — one 160-second cold-start generation stayed
    /// in every <c>Retry-After</c> this process would ever compute, and a model that had since warmed
    /// up could not talk it back down. Here sixteen one-second generations follow it, which is exactly
    /// the window, so the outlier has been overwritten and the estimate is the warm one.
    ///
    /// Deterministic, with no wall clock anywhere (D54): every duration is the manual clock's, and the
    /// rejection is read while X, Y and Z are still parked on their gates, so none of them has recorded
    /// a duration of its own yet.
    /// </summary>
    [Fact]
    public async Task Retry_after_averages_only_the_last_sixteen_generations()
    {
        var time = new ManualTimeProvider(T0);
        var scheduler = NewScheduler(queueCapacity: 2, time: time);
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            await RunOneJobAsync(scheduler, time, TimeSpan.FromSeconds(160));
            for (var i = 0; i < GenerationWindow; i++)
            {
                await RunOneJobAsync(scheduler, time, TimeSpan.FromSeconds(1));
            }

            // Fill the queue to its capacity of 2: X is running, Y and Z sit in the channel.
            var gateX = new TaskCompletionSource();
            var startedX = new TaskCompletionSource();
            var taskX = scheduler.ScheduleAsync<string>(async ct =>
            {
                startedX.TrySetResult();
                await gateX.Task.WaitAsync(ct).ConfigureAwait(false);
                return "x";
            }, CancellationToken.None);
            await startedX.Task;

            var gateY = new TaskCompletionSource();
            var taskY = scheduler.ScheduleAsync<string>(async ct =>
            {
                await gateY.Task.WaitAsync(ct).ConfigureAwait(false);
                return "y";
            }, CancellationToken.None);
            var gateZ = new TaskCompletionSource();
            var taskZ = scheduler.ScheduleAsync<string>(async ct =>
            {
                await gateZ.Task.WaitAsync(ct).ConfigureAwait(false);
                return "z";
            }, CancellationToken.None);
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 2);

            var rejected = await scheduler.ScheduleAsync<string>(_ => Task.FromResult("w"), CancellationToken.None);

            Assert.Equal(ScheduleResultKind.Rejected, rejected.Kind);
            // ceil(2 x 1.0). A cumulative mean would still be carrying the 160s outlier:
            // (160 + 16 x 1) / 17 = 10.35s, so ceil(2 x 10.35) = 21.
            Assert.Equal(2, rejected.RetryAfterSeconds);

            gateX.SetResult();
            gateY.SetResult();
            gateZ.SetResult();
            await taskX;
            await taskY;
            await taskZ;
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    /// <summary>The scheduler's own window size, mirrored here so the test says why sixteen short generations is the number that clears one outlier.</summary>
    private const int GenerationWindow = 16;

    [Fact]
    public async Task A_job_cancelled_while_queued_never_runs_its_body()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var gateA = new TaskCompletionSource();
            var startedA = new TaskCompletionSource();
            var taskA = scheduler.ScheduleAsync<string>(async ct =>
            {
                startedA.TrySetResult();
                await gateA.Task.WaitAsync(ct).ConfigureAwait(false);
                return "a";
            }, CancellationToken.None);
            await startedA.Task; // A is running and holds the worker.

            using var ctsB = new CancellationTokenSource();
            var bodyRan = false;
            var taskB = scheduler.ScheduleAsync<string>(ct =>
            {
                bodyRan = true;
                return Task.FromResult("b");
            }, ctsB.Token);

            await TestWait.UntilAsync(() => scheduler.QueueDepth == 1); // B confirmed still queued, not dequeued.
            ctsB.Cancel();

            gateA.SetResult();
            var resultA = await taskA;
            var resultB = await taskB;

            Assert.Equal(ScheduleResultKind.Completed, resultA.Kind);
            Assert.Equal(ScheduleResultKind.Cancelled, resultB.Kind);
            Assert.False(bodyRan);
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    /// <summary>
    /// Fix-round-1 finding 3 (controller ruling: fix the caller half now, defer the slot half). Before
    /// the fix, a queued job's own token was only polled when the worker dequeued it, so its caller's
    /// task stayed pending for as long as whatever was running ahead of it took -- an aborted client
    /// behind a 60s generation waited the full 60s for nothing. This test would hang without the fix:
    /// A's gate is never released before <c>taskB</c> is awaited, so the only way this test can pass is
    /// if B's cancellation is observed independently of the worker ever reaching B.
    /// </summary>
    [Fact]
    public async Task A_job_cancelled_while_queued_completes_its_caller_immediately_not_after_the_running_job_drains()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var gateA = new TaskCompletionSource();
            var startedA = new TaskCompletionSource();
            var taskA = scheduler.ScheduleAsync<string>(async ct =>
            {
                startedA.TrySetResult();
                await gateA.Task.WaitAsync(ct).ConfigureAwait(false);
                return "a";
            }, CancellationToken.None);
            await startedA.Task; // A is running and will not finish until gateA is released -- which it is not, below.

            using var ctsB = new CancellationTokenSource();
            var taskB = scheduler.ScheduleAsync<string>(_ => Task.FromResult("b"), ctsB.Token);
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 1); // B confirmed queued behind A.

            ctsB.Cancel();

            // A is still running, gateA is still open: if B's caller had to wait for the worker to
            // drain to its position, this would never complete. Bounded (fix-round-2 controller
            // ruling) so a regression here fails the test instead of hanging the suite.
            var resultB = await taskB.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ScheduleResultKind.Cancelled, resultB.Kind);

            gateA.SetResult();
            var resultA = await taskA;
            Assert.Equal(ScheduleResultKind.Completed, resultA.Kind);
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    /// <summary>
    /// Fix round 1, Finding 3 (the "slot half" the comment above deferred): before this fix,
    /// <see cref="GenerationScheduler.QueueDepth"/> was <c>_queue.Reader.Count</c>, which only shrinks
    /// once the worker drains to a cancelled job's position -- so a caller that enqueued, gave up and
    /// retried several times against one long generation left every one of those dead jobs still
    /// occupying a slot for the whole generation, inflating both <c>/healthz</c>'s reported depth and
    /// the <c>Retry-After</c> computed off it. <see cref="ScheduleResultKind.Cancelled"/> already came
    /// back to B's own caller immediately (the test above); this pins that the *count* drops in that
    /// same instant too, well before A's gate is ever released.
    /// </summary>
    [Fact]
    public async Task Queue_depth_drops_the_instant_a_queued_jobs_own_token_cancels_not_when_the_worker_drains_to_it()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var gateA = new TaskCompletionSource();
            var startedA = new TaskCompletionSource();
            var taskA = scheduler.ScheduleAsync<string>(async ct =>
            {
                startedA.TrySetResult();
                await gateA.Task.WaitAsync(ct).ConfigureAwait(false);
                return "a";
            }, CancellationToken.None);
            await startedA.Task;

            using var ctsB = new CancellationTokenSource();
            var taskB = scheduler.ScheduleAsync<string>(_ => Task.FromResult("b"), ctsB.Token);
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 1);

            ctsB.Cancel();
            await taskB.WaitAsync(TimeSpan.FromSeconds(10)); // B's own caller is already done...

            // ...and the depth reflects that immediately: A is still running (gateA still open), so if
            // this were still Reader.Count it would still read 1 until the worker drained to B.
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 0);

            gateA.SetResult();
            await taskA;
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    /// <summary>
    /// Fix round 1, Finding 1: <see cref="ScheduleResult{TResult}.Ran"/> is what lets a caller tell "the
    /// scheduler never got to this job" apart from "the job ran and ended by throwing
    /// <see cref="OperationCanceledException"/> for its own token instead of reporting a domain result"
    /// -- both used to surface as an identical <see cref="ScheduleResultKind.Cancelled"/>. This is the
    /// second of those two: the operation is genuinely invoked (unlike a job dropped while queued) and
    /// throws for the very token it was handed.
    /// </summary>
    [Fact]
    public async Task Ran_is_true_when_a_running_operation_throws_for_its_own_token()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            using var cts = new CancellationTokenSource();
            var started = new TaskCompletionSource();
            var task = scheduler.ScheduleAsync<string>(async ct =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return "unreachable";
            }, cts.Token);

            await started.Task;
            cts.Cancel();

            var result = await task;
            Assert.Equal(ScheduleResultKind.Cancelled, result.Kind);
            Assert.True(result.Ran, "an operation that was actually invoked and threw for its own token should report Ran");
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    /// <summary>Counterpart to the test above: a job dropped without ever running reports <c>Ran: false</c>.</summary>
    [Fact]
    public async Task Ran_is_false_when_a_job_is_dropped_while_still_queued()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var gateA = new TaskCompletionSource();
            var startedA = new TaskCompletionSource();
            var taskA = scheduler.ScheduleAsync<string>(async ct =>
            {
                startedA.TrySetResult();
                await gateA.Task.WaitAsync(ct).ConfigureAwait(false);
                return "a";
            }, CancellationToken.None);
            await startedA.Task;

            using var ctsB = new CancellationTokenSource();
            var taskB = scheduler.ScheduleAsync<string>(_ => Task.FromResult("b"), ctsB.Token);
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 1);
            ctsB.Cancel();

            var resultB = await taskB.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ScheduleResultKind.Cancelled, resultB.Kind);
            Assert.False(resultB.Ran, "a job dropped while still queued never invoked its operation");

            gateA.SetResult();
            await taskA;
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_job_cancelled_while_running_is_awaited_to_completion_before_the_next_job_starts()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var order = new List<string>();
            var startedB = new TaskCompletionSource();
            var letBFinish = new TaskCompletionSource();
            using var ctsB = new CancellationTokenSource();

            // Models a runtime whose in-flight operation cannot be stopped on demand (the fake's
            // CancellationGate): the body ignores its own token entirely and only ends when the test
            // lets it, so "the next job waited" is a fact about the scheduler, not about how fast this
            // body happens to run.
            var taskB = scheduler.ScheduleAsync<string>(async ct =>
            {
                lock (order) { order.Add("b-start"); }
                startedB.TrySetResult();
                await letBFinish.Task.ConfigureAwait(false);
                lock (order) { order.Add("b-end"); }
                return "b";
            }, ctsB.Token);
            await startedB.Task;

            var taskC = scheduler.ScheduleAsync<string>(ct =>
            {
                lock (order) { order.Add("c"); }
                return Task.FromResult("c");
            }, CancellationToken.None);

            ctsB.Cancel(); // B's own token fires while B is still running.

            // Structural: C cannot have run, because the worker is still awaiting B's RunAsync task,
            // which is parked on letBFinish and does not observe ctsB at all.
            lock (order)
            {
                Assert.DoesNotContain("c", order);
            }

            letBFinish.SetResult();
            var resultB = await taskB;
            var resultC = await taskC;

            Assert.Equal(new[] { "b-start", "b-end", "c" }, order);
            Assert.Equal(ScheduleResultKind.Completed, resultB.Kind); // B's own body decided to finish normally despite the cancel.
            Assert.Equal(ScheduleResultKind.Completed, resultC.Kind);
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    [Fact]
    public async Task Shutdown_completes_still_queued_jobs_as_cancelled_without_running_them()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var gateA = new TaskCompletionSource();
            var startedA = new TaskCompletionSource();
            var taskA = scheduler.ScheduleAsync<string>(async ct =>
            {
                startedA.TrySetResult();
                await gateA.Task.WaitAsync(ct).ConfigureAwait(false);
                return "a";
            }, CancellationToken.None);
            await startedA.Task; // A is running and holds the worker.

            var bodyRanB = false;
            var taskB = scheduler.ScheduleAsync<string>(ct =>
            {
                bodyRanB = true;
                return Task.FromResult("b");
            }, CancellationToken.None);
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 1); // B confirmed still queued.

            // Shutdown is requested while B is provably still sitting in the channel and A is provably
            // still running (parked on gateA): the worker cannot have reached B yet.
            var stopTask = scheduler.StopAsync(CancellationToken.None);
            gateA.SetResult(); // let A finish so shutdown's drain can proceed; this is D51, not suspended for shutdown.
            var resultA = await taskA;
            await stopTask.WaitAsync(TimeSpan.FromSeconds(10)); // fails this named test instead of hanging the suite.

            var resultB = await taskB;
            Assert.Equal(ScheduleResultKind.Completed, resultA.Kind);
            Assert.Equal(ScheduleResultKind.Cancelled, resultB.Kind);
            Assert.False(bodyRanB);
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    [Fact]
    public async Task Queue_depth_reflects_what_is_waiting_not_the_job_running()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(0, scheduler.QueueDepth);

            var gateA = new TaskCompletionSource();
            var startedA = new TaskCompletionSource();
            var taskA = scheduler.ScheduleAsync<string>(async ct =>
            {
                startedA.TrySetResult();
                await gateA.Task.WaitAsync(ct).ConfigureAwait(false);
                return "a";
            }, CancellationToken.None);
            await startedA.Task;
            Assert.Equal(0, scheduler.QueueDepth); // A is running; nothing is waiting.

            var gateB = new TaskCompletionSource();
            var taskB = scheduler.ScheduleAsync<string>(async ct =>
            {
                await gateB.Task.WaitAsync(ct).ConfigureAwait(false);
                return "b";
            }, CancellationToken.None);
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 1);

            var gateC = new TaskCompletionSource();
            var taskC = scheduler.ScheduleAsync<string>(async ct =>
            {
                await gateC.Task.WaitAsync(ct).ConfigureAwait(false);
                return "c";
            }, CancellationToken.None);
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 2);

            gateA.SetResult();
            await taskA;
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 1); // B is now running; C still waits.

            gateB.SetResult();
            await taskB;
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 0); // C is now running.

            gateC.SetResult();
            await taskC;
            Assert.Equal(0, scheduler.QueueDepth);
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    [Fact]
    public async Task Completed_result_reports_how_long_the_job_waited_in_the_queue()
    {
        var time = new ManualTimeProvider(T0);
        var scheduler = NewScheduler(time: time);
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var gateA = new TaskCompletionSource();
            var startedA = new TaskCompletionSource();
            var taskA = scheduler.ScheduleAsync<string>(async ct =>
            {
                startedA.TrySetResult();
                await gateA.Task.WaitAsync(ct).ConfigureAwait(false);
                return "a";
            }, CancellationToken.None);
            await startedA.Task;

            var taskB = scheduler.ScheduleAsync<string>(_ => Task.FromResult("b"), CancellationToken.None);
            await TestWait.UntilAsync(() => scheduler.QueueDepth == 1);

            time.Advance(TimeSpan.FromSeconds(7));
            gateA.SetResult();
            await taskA;

            var resultB = await taskB;
            Assert.Equal(ScheduleResultKind.Completed, resultB.Kind);
            Assert.Equal(TimeSpan.FromSeconds(7), resultB.QueueWait);
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    /// <summary>
    /// Fix-round-1 finding 2: <c>catch (Exception ex) { Completion.TrySetException(ex); }</c> is the
    /// only thing standing between "a job body threw" and a faulted, unobserved worker task that wedges
    /// every subsequent <see cref="GenerationScheduler.ScheduleAsync{TResult}"/> forever. This pins that
    /// the exception is rethrown to the exact caller that is waiting for it, and that the worker survives
    /// to run the very next job.
    ///
    /// This test is also the one the task 3b review reproduced flaking (1 failure in 11 full-suite runs)
    /// on the <c>Assert.Equal(0, scheduler.QueueDepth)</c> below, from the publish-before-arm race
    /// Finding 1 describes: <c>taskA</c> is a job the worker can dequeue and run to completion before
    /// this thread even reaches its own <c>Interlocked.Increment</c>/<c>MarkEnteredQueue</c> call two
    /// lines after <c>TryWrite</c>. <see cref="Rapid_back_to_back_jobs_never_leave_the_depth_counter_stuck_above_zero"/>
    /// below is the dedicated regression test for that race; this test's own final assertion is left as
    /// it was rather than removed, since it is a real (if now much rarer to hit by chance) instance of
    /// the same invariant.
    /// </summary>
    [Fact]
    public async Task A_job_that_throws_faults_its_own_caller_without_wedging_the_worker()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            var boom = new InvalidOperationException("boom");
            var taskA = scheduler.ScheduleAsync<string>(_ => throw boom, CancellationToken.None);

            // Bounded (fix-round-2 controller ruling): this is the only regression guard on a
            // Critical-class failure mode (a wedged worker), so it must fail loudly rather than hang
            // the suite if the catch-all it pins is ever removed.
            var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => taskA).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(boom, observed);

            var taskB = scheduler.ScheduleAsync<string>(_ => Task.FromResult("b"), CancellationToken.None);
            var resultB = await taskB.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(ScheduleResultKind.Completed, resultB.Kind);
            Assert.Equal("b", resultB.Result);
            Assert.Equal(0, scheduler.QueueDepth);
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    [Fact]
    public void Queue_depth_gate_dequeued_before_entered_leaves_exactly_once()
    {
        using var cancellation = new CancellationTokenSource();
        var leftQueue = 0;
        var job = NewQueuedJob(() => leftQueue++, cancellation.Token);
        job.ArmCancellationCompletion();
        try
        {
            job.MarkDequeued();
            Assert.Equal(0, leftQueue); // The counter was not armed when the worker dequeued it.

            job.MarkEnteredQueue();
            Assert.Equal(1, leftQueue);
        }
        finally
        {
            job.DisposeCancellationRegistration();
        }
    }

    [Fact]
    public void Queue_depth_gate_cancelled_before_entered_leaves_exactly_once()
    {
        using var cancellation = new CancellationTokenSource();
        var leftQueue = 0;
        var job = NewQueuedJob(() => leftQueue++, cancellation.Token);
        job.ArmCancellationCompletion();
        try
        {
            cancellation.Cancel(); // Drives the registered cancellation callback synchronously.
            Assert.Equal(0, leftQueue); // Cancellation cannot remove a job not yet counted.

            job.MarkEnteredQueue();
            Assert.Equal(1, leftQueue);
        }
        finally
        {
            job.DisposeCancellationRegistration();
        }
    }

    [Fact]
    public void Queue_depth_gate_cancelled_after_dequeued_leaves_exactly_once()
    {
        using var cancellation = new CancellationTokenSource();
        var leftQueue = 0;
        var job = NewQueuedJob(() => leftQueue++, cancellation.Token);
        job.ArmCancellationCompletion();
        try
        {
            job.MarkDequeued();
            Assert.Equal(0, leftQueue); // Dequeue cannot leave before the job was counted.

            cancellation.Cancel();
            Assert.Equal(0, leftQueue); // Neither signal may decrement an unarmed depth.
            job.MarkEnteredQueue();
            Assert.Equal(1, leftQueue);
        }
        finally
        {
            job.DisposeCancellationRegistration();
        }
    }

    [Fact]
    public void Queue_depth_gate_entered_then_cancelled_then_dequeued_leaves_exactly_once()
    {
        using var cancellation = new CancellationTokenSource();
        var leftQueue = 0;
        var job = NewQueuedJob(() => leftQueue++, cancellation.Token);
        job.ArmCancellationCompletion();
        try
        {
            job.MarkEnteredQueue();
            Assert.Equal(0, leftQueue);

            cancellation.Cancel();
            Assert.Equal(1, leftQueue);
            job.MarkDequeued();
            Assert.Equal(1, leftQueue);
        }
        finally
        {
            job.DisposeCancellationRegistration();
        }
    }

    [Fact]
    public void Queue_depth_gate_dequeued_then_cancelled_leaves_exactly_once()
    {
        using var cancellation = new CancellationTokenSource();
        var leftQueue = 0;
        var job = NewQueuedJob(() => leftQueue++, cancellation.Token);
        job.ArmCancellationCompletion();
        try
        {
            job.MarkEnteredQueue();
            job.MarkDequeued();
            Assert.Equal(1, leftQueue);

            cancellation.Cancel();
            Assert.Equal(1, leftQueue);
        }
        finally
        {
            job.DisposeCancellationRegistration();
        }
    }

    /// <summary>
    /// Integration-level companion to the deterministic <c>Queue_depth_gate_*</c> tests above. Those
    /// tests pin every publish-before-arm ordering directly; this one retains the real worker/channel
    /// path as a guard that completed jobs leave <c>QueueDepth</c> at zero.
    /// </summary>
    [Fact]
    public async Task Rapid_back_to_back_jobs_never_leave_the_depth_counter_stuck_above_zero()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 500; i++)
            {
                var result = await scheduler.ScheduleAsync<string>(_ => Task.FromResult("x"), CancellationToken.None);
                Assert.Equal(ScheduleResultKind.Completed, result.Kind);

                // Checked every iteration, not only at the end: the bug leaves the counter one too high
                // per race it wins, so a single check after the loop could still pass by coincidence if
                // an unrelated regression instead left it transiently negative partway through.
                Assert.Equal(0, scheduler.QueueDepth);
            }
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    /// <summary>Controller ruling, review finding 8: a stopped scheduler is never coming back to honour a Retry-After, so the truer answer to a post-shutdown enqueue is Cancelled, not Rejected.</summary>
    [Fact]
    public async Task Enqueue_after_shutdown_is_cancelled_not_rejected()
    {
        var scheduler = NewScheduler();
        await scheduler.StartAsync(CancellationToken.None);
        await scheduler.StopAsync(CancellationToken.None); // nothing queued: drains immediately.

        var result = await scheduler.ScheduleAsync<string>(_ => Task.FromResult("x"), CancellationToken.None);

        Assert.Equal(ScheduleResultKind.Cancelled, result.Kind);
        Assert.Equal(TimeSpan.Zero, result.QueueWait);
        Assert.Equal(0, result.RetryAfterSeconds);

        await scheduler.DisposeAsync();
    }
}

/// <summary>
/// The one place a <see cref="ScheduleResult{TResult}"/> is turned into something an endpoint can act on
/// (chunk 8 fix round 1, Finding 4). It exists because all three callers — both
/// <c>/v1/chat/completions</c> shapes and <c>/debug/generate</c> — used to spell a full queue and a
/// scheduler shutdown out for themselves, and the two OpenAI shapes had already drifted apart on day
/// one over whether the answer goes out as a status line or an SSE event (Finding 2). Pinned here
/// rather than only through the endpoints so <c>/v1/completions</c> (task 3) inherits a mapping with a
/// test rather than a fourth copy.
/// </summary>
public class SchedulerAdmissionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromMilliseconds(5);

    [Fact]
    public void A_completed_schedule_is_completed_even_when_the_client_has_gone()
    {
        // Deliberately not ClientGone: the operation ran, so there is a lease to settle and a status to
        // log. Telling the client is the caller's own aborted check, further down its own method.
        Assert.Equal(SchedulerOutcome.Completed,
            SchedulerAdmission.Classify(ScheduleResult.Completed("x", Wait), clientAlreadyGone: true));
    }

    [Fact]
    public void A_full_queue_is_a_429_with_the_schedulers_own_retry_after()
    {
        var rejected = ScheduleResult.Rejected<string>(7);
        var outcome = SchedulerAdmission.Classify(rejected, clientAlreadyGone: false);

        Assert.Equal(SchedulerOutcome.QueueFull, outcome);
        var failure = SchedulerAdmission.FailureFor(outcome, rejected.RetryAfterSeconds);
        Assert.Equal(StatusCodes.Status429TooManyRequests, failure.StatusCode);
        Assert.Equal(OpenAiError.RateLimit, failure.Body.Error.Type);
        Assert.Equal("queue_full", failure.Body.Error.Code);
        Assert.Contains("7", failure.Body.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>Integration decision 4: a scheduler that is going away answers "this will never run", not "try again in N".</summary>
    [Fact]
    public void A_job_that_never_ran_is_a_503_queue_shutting_down()
    {
        var outcome = SchedulerAdmission.Classify(ScheduleResult.Cancelled<string>(Wait), clientAlreadyGone: false);

        Assert.Equal(SchedulerOutcome.QueueShuttingDown, outcome);
        var failure = SchedulerAdmission.FailureFor(outcome, 0);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, failure.StatusCode);
        Assert.Equal("queue_shutting_down", failure.Body.Error.Code);
    }

    /// <summary>
    /// Finding 1, and the whole reason <see cref="ScheduleResult{TResult}.Ran"/> exists: the same
    /// <see cref="ScheduleResultKind.Cancelled"/> means two unrelated things, and reporting the second
    /// as the first tells a client the queue is shutting down when what actually happened is that a
    /// live generation threw.
    /// </summary>
    [Fact]
    public void A_job_that_ran_and_threw_its_own_cancellation_is_a_502_backend_error()
    {
        var outcome = SchedulerAdmission.Classify(ScheduleResult.Cancelled<string>(Wait, ran: true), clientAlreadyGone: false);

        Assert.Equal(SchedulerOutcome.BackendThrewCancellation, outcome);
        var failure = SchedulerAdmission.FailureFor(outcome, 0);
        Assert.Equal(StatusCodes.Status502BadGateway, failure.StatusCode);
        Assert.Equal("backend_error", failure.Body.Error.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_aborted_client_outranks_every_reason_but_completion(bool ran)
    {
        Assert.Equal(SchedulerOutcome.ClientGone,
            SchedulerAdmission.Classify(ScheduleResult.Cancelled<string>(Wait, ran), clientAlreadyGone: true));
        Assert.Equal(SchedulerOutcome.ClientGone,
            SchedulerAdmission.Classify(ScheduleResult.Rejected<string>(3), clientAlreadyGone: true));
    }

    /// <summary>
    /// Neither has a failure to report — the first has a real result instead, the second nobody to send
    /// one to — so asking for one is the caller's bug rather than a silent default it would then send.
    /// (Not a <c>[Theory]</c> over the enum: <see cref="SchedulerOutcome"/> is internal, and a public
    /// test method may not take one as a parameter.)
    /// </summary>
    [Fact]
    public void There_is_no_failure_for_a_completed_or_client_gone_outcome()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SchedulerAdmission.FailureFor(SchedulerOutcome.Completed, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => SchedulerAdmission.FailureFor(SchedulerOutcome.ClientGone, 0));
    }

    /// <summary>The header goes on only for a full queue, and never onto a response whose headers are already spent (Finding 2).</summary>
    [Fact]
    public void Retry_after_is_written_for_a_full_queue_and_for_nothing_else()
    {
        var http = new DefaultHttpContext();

        SchedulerAdmission.ApplyRetryAfter(http.Response, SchedulerOutcome.QueueShuttingDown, 9);
        Assert.False(http.Response.Headers.ContainsKey("Retry-After"));

        SchedulerAdmission.ApplyRetryAfter(http.Response, SchedulerOutcome.QueueFull, 9);
        Assert.Equal("9", http.Response.Headers.RetryAfter.ToString());
    }
}

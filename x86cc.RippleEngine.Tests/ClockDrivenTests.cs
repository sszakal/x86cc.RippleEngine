using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using x86cc.RippleEngine.Core;
using x86cc.RippleEngine.Engine;
using x86cc.RippleEngine.Storage;

namespace x86cc.RippleEngine.Tests;

/// <summary>
/// The engine's time-driven behaviour, checked against a <see cref="FakeTimeProvider"/> instead of the wall clock:
/// the execution timeout, the retry backoff stamp, the recorded duration, the shutdown drain grace, the settlement
/// write retry and the loop cadence. Components are built directly with an in-memory splash store — no host, no Postgres — so time only
/// moves when the test says so and every timestamp is asserted EXACTLY. The real waits left here only let async
/// continuations run; nothing asserts on how long they took.
/// </summary>
/// <remarks>
/// A fake clock is a component-level tool, not a host-level one: a whole engine host on a fake clock would stall
/// (the dispatcher's poll backoff and every loop wait on time nobody advances), and the timestamps it writes are
/// compared with Postgres <c>now()</c>, which no fake clock moves.
/// </remarks>
public sealed class ClockDrivenTests
{
    private static readonly string TypeKey = RippleTypeKey.Compose(nameof(ClockWave), nameof(ClockRipple));

    private readonly CountingTimeProvider _time = new();
    private readonly ClockProbe _probe = new();
    private readonly RecordingSplashStore _store = new();

    [Fact]
    public async Task the_execution_timeout_cancels_the_handler_exactly_when_the_clock_reaches_it()
    {
        var pipeline = BuildPipeline(o => o.ExecutionTimeout = TimeSpan.FromSeconds(30));
        _probe.Behaviour = async (ctx, _) =>
        {
            _probe.Started.TrySetResult(ctx);
            await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
            return null;
        };

        pipeline.Post(Ripple(attempt: 1, maxAttempts: 3));
        var context = await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var startedAt = _time.GetUtcNow();

        // Timers fire synchronously inside Advance, so the token state is exact on both sides of the deadline.
        _time.Advance(TimeSpan.FromSeconds(30) - TimeSpan.FromTicks(1));
        context.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        _time.Advance(TimeSpan.FromTicks(1));
        context.CancellationToken.IsCancellationRequested.ShouldBeTrue();

        var failure = await SingleAsync(_store.Failed);
        failure.StartedAt.ShouldBe(startedAt);
        failure.EndedAt.ShouldBe(startedAt + TimeSpan.FromSeconds(30));
        failure.Terminal.ShouldBeFalse();
        failure.Report!.ShouldContain("TaskCanceledException");

        await StopAsync(pipeline);
    }

    /// <summary>The retry deadline is <c>endedAt + RetryBackoff · 2^(attempt-1)</c>, capped at MaxRetryBackoff, and
    /// the last allowed attempt is terminal with no retry.</summary>
    [Theory]
    [InlineData(1, 2)]  // 2s base
    [InlineData(2, 4)]  // doubled
    [InlineData(3, 5)]  // 8s, capped at 5s
    public async Task a_failed_attempt_is_requeued_at_exactly_its_backoff(int attempt, int expectedBackoffSeconds)
    {
        var pipeline = BuildPipeline(o =>
        {
            o.RetryBackoff = TimeSpan.FromSeconds(2);
            o.MaxRetryBackoff = TimeSpan.FromSeconds(5);
        });
        _probe.Behaviour = (_, _) => throw new InvalidOperationException("boom");

        pipeline.Post(Ripple(attempt, maxAttempts: 5));

        var failure = await SingleAsync(_store.Failed);
        failure.EndedAt.ShouldBe(_time.GetUtcNow()); // stamped from the injected clock, not the wall clock
        failure.Terminal.ShouldBeFalse();
        failure.NextAttemptAt.ShouldBe(failure.EndedAt + TimeSpan.FromSeconds(expectedBackoffSeconds));

        await StopAsync(pipeline);
    }

    [Fact]
    public async Task the_last_allowed_attempt_fails_terminally_with_no_retry()
    {
        var pipeline = BuildPipeline();
        _probe.Behaviour = (_, _) => throw new InvalidOperationException("boom");

        pipeline.Post(Ripple(attempt: 5, maxAttempts: 5));

        var failure = await SingleAsync(_store.Failed);
        failure.Terminal.ShouldBeTrue();
        failure.NextAttemptAt.ShouldBeNull();

        await StopAsync(pipeline);
    }

    /// <summary>The recorded duration is the handler's own time — 42 fake seconds, exactly, in no real time.</summary>
    [Fact]
    public async Task the_recorded_duration_is_exactly_the_handlers_time()
    {
        var pipeline = BuildPipeline();
        _probe.Behaviour = async (ctx, time) =>
        {
            var work = Task.Delay(TimeSpan.FromSeconds(42), time, ctx.CancellationToken);
            _probe.Started.TrySetResult(ctx); // signalled after the timer is registered, so Advance can't miss it
            await work;
            return null;
        };

        pipeline.Post(Ripple(attempt: 1, maxAttempts: 3));
        await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _time.Advance(TimeSpan.FromSeconds(42));

        var completion = await SingleAsync(_store.Completed);
        (completion.EndedAt - completion.StartedAt).ShouldBe(TimeSpan.FromSeconds(42));

        await StopAsync(pipeline);
    }

    /// <summary>Shutdown waits for an in-flight handler until ShutdownDrainGrace — not a tick sooner — then cancels it
    /// and still settles its outcome.</summary>
    [Fact]
    public async Task shutdown_drains_until_the_grace_expires_then_cancels_and_settles()
    {
        var pipeline = BuildPipeline(o => o.ShutdownDrainGrace = TimeSpan.FromSeconds(10));
        _probe.Behaviour = async (ctx, _) =>
        {
            _probe.Started.TrySetResult(ctx);
            await Task.Delay(Timeout.Infinite, ctx.CancellationToken);
            return null;
        };

        pipeline.Post(Ripple(attempt: 1, maxAttempts: 3));
        var context = await _probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var stop = pipeline.StopAsync(); // arms the drain deadline synchronously

        _time.Advance(TimeSpan.FromSeconds(10) - TimeSpan.FromTicks(1));
        context.CancellationToken.IsCancellationRequested.ShouldBeFalse("the handler must get the full drain grace");
        stop.IsCompleted.ShouldBeFalse();

        _time.Advance(TimeSpan.FromTicks(1));
        context.CancellationToken.IsCancellationRequested.ShouldBeTrue();
        await stop.WaitAsync(TimeSpan.FromSeconds(10));
        _store.Failed.Count.ShouldBe(1, "the cancelled attempt's outcome is still written");
    }

    /// <summary>A failed settlement write is retried on the clock — 250ms, then doubled — and the attempt keeps its
    /// in-flight slot (the dispatcher's back-pressure) until the write actually lands.</summary>
    [Fact]
    public async Task a_failed_settlement_retries_with_doubling_delay_and_holds_the_slot_until_it_lands()
    {
        var options = Options.Create(new RippleEngineOptions
        {
            InstanceId = "clock-test", SettlementRetryDelay = TimeSpan.FromMilliseconds(250)
        });
        var inFlight = new InFlightTracker();
        var settlement = BuildSettlement(options, inFlight);
        using var drain = new CancellationTokenSource();
        settlement.Start(drain.Token);
        _store.FailNextWrites = 2;

        var rippleId = Guid.NewGuid();
        inFlight.Track(rippleId, attempt: 1);
        settlement.Succeeded(new RippleCompletion(rippleId, Guid.NewGuid(), 1, _time.GetUtcNow(), null));

        await WaitUntilAsync(() => _time.TimersCreated == 1); // first write failed, backing off 250ms
        _time.Advance(TimeSpan.FromMilliseconds(250));
        await WaitUntilAsync(() => _time.TimersCreated == 2); // second write failed, backing off 500ms
        _time.Advance(TimeSpan.FromMilliseconds(500) - TimeSpan.FromTicks(1));
        _store.Completed.ShouldBeEmpty();
        inFlight.Count.ShouldBe(1, "an unwritten outcome keeps its slot");

        _time.Advance(TimeSpan.FromTicks(1));
        (await SingleAsync(_store.Completed)).RippleId.ShouldBe(rippleId);
        await WaitUntilAsync(() => inFlight.Count == 0);
        inFlight.RippleIds.ShouldBeEmpty();

        await settlement.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Past the drain deadline a failing write stops retrying, and the attempt stays owned — recovery, not
    /// a dropped outcome, is what settles it.</summary>
    [Fact]
    public async Task the_drain_deadline_stops_settlement_retries_and_leaves_the_attempt_owned()
    {
        var options = Options.Create(new RippleEngineOptions { InstanceId = "clock-test" });
        var inFlight = new InFlightTracker();
        var settlement = BuildSettlement(options, inFlight);
        using var drain = new CancellationTokenSource();
        settlement.Start(drain.Token);
        _store.FailNextWrites = int.MaxValue;

        var rippleId = Guid.NewGuid();
        inFlight.Track(rippleId, attempt: 1);
        settlement.Failed(new RippleFailure(rippleId, Guid.NewGuid(), 1, _time.GetUtcNow(), null, true, null));
        await WaitUntilAsync(() => _time.TimersCreated == 1);

        var complete = settlement.CompleteAsync();
        complete.IsCompleted.ShouldBeFalse("still retrying inside the drain grace");
        await drain.CancelAsync();
        await complete.WaitAsync(TimeSpan.FromSeconds(10));

        inFlight.Count.ShouldBe(1);
        inFlight.RippleIds.ShouldBe([rippleId]);
    }

    /// <summary>The pipeline is one-shot. A second Start used to replace the execute block (orphaning whatever was
    /// queued in the first) and start a second pair of flush loops; use before Start was a NullReferenceException.</summary>
    [Fact]
    public async Task the_pipeline_starts_once_stops_once_and_refuses_work_before_start()
    {
        var unstarted = BuildPipeline(start: false);
        Should.Throw<InvalidOperationException>(() => unstarted.Post(Ripple(attempt: 1, maxAttempts: 3)))
            .Message.ShouldContain("must be started");
        (await Should.ThrowAsync<InvalidOperationException>(unstarted.StopAsync)).Message.ShouldContain("must be started");
        unstarted.IsFaulted.ShouldBeFalse();

        var pipeline = BuildPipeline();
        Should.Throw<InvalidOperationException>(pipeline.Start).Message.ShouldContain("already been started");

        await StopAsync(pipeline);
        (await Should.ThrowAsync<InvalidOperationException>(pipeline.StopAsync)).Message.ShouldContain("already been stopped");
    }

    [Fact]
    public async Task settlement_starts_once_and_cannot_complete_unstarted()
    {
        var options = Options.Create(new RippleEngineOptions { InstanceId = "clock-test" });

        // Completing without loops would return as if every buffered outcome had been written.
        var unstarted = BuildSettlement(options, new InFlightTracker());
        await Should.ThrowAsync<InvalidOperationException>(unstarted.CompleteAsync);

        var settlement = BuildSettlement(options, new InFlightTracker());
        settlement.Start(CancellationToken.None);
        Should.Throw<InvalidOperationException>(() => settlement.Start(CancellationToken.None));
        await settlement.CompleteAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>A failed poll cycle (the DB down) retries after exactly <see cref="RippleEngineOptions.DispatchErrorDelay"/>,
    /// with the fallback heartbeat attempted in between.</summary>
    [Fact]
    public async Task a_failed_dispatch_cycle_retries_after_the_configured_error_delay()
    {
        var delay = TimeSpan.FromSeconds(7);
        var options = Options.Create(new RippleEngineOptions
        {
            InstanceId = "clock-test", MaxConcurrency = 1, DispatchErrorDelay = delay
        });
        var store = DownEngineStore.Create(_time);
        var registry = new RippleHandlerRegistry();
        var inFlight = new InFlightTracker();
        var pipeline = new ExecutionPipeline(options, registry, new ServiceCollection().BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>(), inFlight, new ExponentialRetryPolicy(options),
            BuildSettlement(options, inFlight), new RippleMetrics(), _time, NullLogger<ExecutionPipeline>.Instance);
        using var dispatcher = new Dispatcher(pipeline, registry, store, new RippleMetrics(), options,
            new NoopLifetime(), _time, NullLogger<Dispatcher>.Instance);
        var down = (DownEngineStore)(object)store;

        await dispatcher.StartAsync(CancellationToken.None);

        // The poll failed and the fallback beat was tried; wait until the error delay is actually armed.
        await WaitUntilAsync(() => down.Calls("BeatAsync") == 1);
        await WaitUntilAsync(() => _time.TimersCreated > down.TimersAtLastBeat);
        down.Calls("PollAsync").ShouldBe(1);

        _time.Advance(delay - TimeSpan.FromTicks(1));
        down.Calls("PollAsync").ShouldBe(1, "no retry before the configured delay");
        _time.Advance(TimeSpan.FromTicks(1));
        await WaitUntilAsync(() => down.Calls("PollAsync") == 2);

        await dispatcher.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task a_periodic_loop_waits_its_interval_and_keeps_going_after_a_failed_tick()
    {
        var interval = TimeSpan.FromMinutes(1);
        using var loop = new CountingLoop(interval, _time) { FailFirstTick = true };
        await loop.StartAsync(CancellationToken.None);

        // ExecuteAsync runs on a background thread, so wait until the loop has actually armed its delay — an
        // Advance that landed first would be missed and the timer would start from the advanced time.
        await WaitUntilAsync(() => _time.TimersCreated == 1);
        _time.Advance(interval - TimeSpan.FromTicks(1));
        loop.Ticks.ShouldBe(0);
        _time.Advance(TimeSpan.FromTicks(1));
        await WaitUntilAsync(() => loop.Ticks == 1);

        // The first tick threw; the loop must still be alive and re-arm a full interval from the tick.
        await WaitUntilAsync(() => _time.TimersCreated == 2);
        _time.Advance(interval - TimeSpan.FromTicks(1));
        loop.Ticks.ShouldBe(1);
        _time.Advance(TimeSpan.FromTicks(1));
        await WaitUntilAsync(() => loop.Ticks == 2);

        await loop.StopAsync(CancellationToken.None);
    }

    // ---- fixture ---------------------------------------------------------------------------------

    private ExecutionPipeline BuildPipeline(Action<RippleEngineOptions>? configure = null, bool start = true)
    {
        var options = new RippleEngineOptions { InstanceId = "clock-test", MaxConcurrency = 1 };
        configure?.Invoke(options);

        var services = new ServiceCollection()
            .AddSingleton(_probe)
            .AddSingleton<TimeProvider>(_time)
            .AddScoped<ClockHandler>()
            .BuildServiceProvider();
        var registry = new RippleHandlerRegistry();
        registry.Register<ClockHandler, ClockWave, ClockRipple>(batchSize: null, gapSeconds: 1, maxAttempts: null);

        var opts = Options.Create(options);
        var inFlight = new InFlightTracker();
        var pipeline = new ExecutionPipeline(opts, registry, services.GetRequiredService<IServiceScopeFactory>(),
            inFlight, new ExponentialRetryPolicy(opts), BuildSettlement(opts, inFlight), new RippleMetrics(), _time,
            NullLogger<ExecutionPipeline>.Instance);
        if (start)
        {
            pipeline.Start();
        }

        return pipeline;
    }

    private SettlementWriter BuildSettlement(IOptions<RippleEngineOptions> options, InFlightTracker inFlight)
        => new(options, _store, inFlight, _time, NullLogger<SettlementWriter>.Instance);

    private static PreparedRipple Ripple(int attempt, int maxAttempts)
        => new(Guid.NewGuid(), Guid.NewGuid(), attempt, maxAttempts, TypeKey, nameof(ClockRipple),
            JsonDocument.Parse($$"""{"id":"{{Guid.NewGuid()}}"}"""), JsonDocument.Parse("{}"));

    private static async Task StopAsync(ExecutionPipeline pipeline)
        => await pipeline.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

    private static async Task<T> SingleAsync<T>(ConcurrentQueue<T> queue)
    {
        await WaitUntilAsync(() => !queue.IsEmpty);
        queue.Count.ShouldBe(1);
        return queue.Single();
    }

    /// <summary>Real-time wait for async continuations to land — a liveness cap, never a timing assertion.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "condition never became true");
            await Task.Delay(10);
        }
    }

    /// <summary>A fake clock that also counts the timers armed on it, so a test can wait until a component is
    /// actually waiting before advancing past its deadline.</summary>
    internal sealed class CountingTimeProvider() : FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
    {
        private int _timersCreated;

        public int TimersCreated => Volatile.Read(ref _timersCreated);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Interlocked.Increment(ref _timersCreated);
            return base.CreateTimer(callback, state, dueTime, period);
        }
    }

    public sealed class ClockWave;

    public sealed class ClockRipple : IRippleTarget
    {
        public Guid Id { get; set; }

        [JsonIgnore]
        public IReadOnlyList<string> TargetIds => [Id.ToString()];
    }

    /// <summary>What the handler does, set per test; <see cref="Started"/> hands the test the live context.</summary>
    public sealed class ClockProbe
    {
        public Func<IRippleContext, TimeProvider, Task<SplashReport?>> Behaviour { get; set; } =
            (_, _) => Task.FromResult<SplashReport?>(null);

        public TaskCompletionSource<IRippleContext> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public sealed class ClockHandler(ClockProbe probe, TimeProvider time) : IRippleHandler<ClockWave, ClockRipple>
    {
        public Task<SplashReport?> Execute(ClockWave wave, ClockRipple ripple, IRippleContext context)
            => probe.Behaviour(context, time);
    }

    private sealed class RecordingSplashStore : ISplashStore
    {
        public ConcurrentQueue<RippleCompletion> Completed { get; } = new();
        public ConcurrentQueue<RippleFailure> Failed { get; } = new();

        /// <summary>How many of the next writes throw (a transient DB outage) before they start landing.</summary>
        public int FailNextWrites { get; set; }

        public Task CompleteRipplesAsync(IReadOnlyList<RippleCompletion> batch, string instanceId,
            CancellationToken ct = default)
        {
            ThrowIfFailing();
            foreach (var c in batch) Completed.Enqueue(c);
            return Task.CompletedTask;
        }

        public Task FailRipplesAsync(IReadOnlyList<RippleFailure> batch, string instanceId,
            CancellationToken ct = default)
        {
            ThrowIfFailing();
            foreach (var f in batch) Failed.Enqueue(f);
            return Task.CompletedTask;
        }

        private void ThrowIfFailing()
        {
            if (FailNextWrites > 0)
            {
                FailNextWrites--;
                throw new InvalidOperationException("transient settlement failure");
            }
        }
    }

    /// <summary>An <see cref="IEngineStore"/> for a database that is down: every call throws. A proxy rather than
    /// a hand-written fake, because the dispatcher only needs the calls to fail and be counted.</summary>
    public class DownEngineStore : DispatchProxy
    {
        private readonly ConcurrentDictionary<string, int> _calls = new();
        private CountingTimeProvider _time = default!;

        /// <summary>The fake clock's timer count when the last heartbeat was attempted: any timer after that
        /// is the error delay.</summary>
        public int TimersAtLastBeat { get; private set; }

        internal static IEngineStore Create(CountingTimeProvider time)
        {
            var store = Create<IEngineStore, DownEngineStore>();
            ((DownEngineStore)(object)store)._time = time;
            return store;
        }

        public int Calls(string method) => _calls.GetValueOrDefault(method);

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            if (name == "BeatAsync")
            {
                TimersAtLastBeat = _time.TimersCreated;
            }

            _calls.AddOrUpdate(name, 1, (_, n) => n + 1);
            throw new InvalidOperationException("database is down");
        }
    }

    private sealed class NoopLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class CountingLoop(TimeSpan interval, TimeProvider time)
        : PeriodicLoop(interval, time, NullLogger.Instance)
    {
        private int _ticks;

        public bool FailFirstTick { get; init; }

        public int Ticks => Volatile.Read(ref _ticks);

        protected override string FailureMessage => "counting loop tick failed";

        protected override Task TickAsync(CancellationToken stoppingToken)
            => Interlocked.Increment(ref _ticks) == 1 && FailFirstTick
                ? throw new InvalidOperationException("first tick fails")
                : Task.CompletedTask;
    }
}

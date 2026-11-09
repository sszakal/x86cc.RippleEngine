using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
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
/// the execution timeout, the retry backoff stamp, the recorded duration, the shutdown drain grace and the loop
/// cadence. Components are built directly with an in-memory splash store — no host, no Postgres — so time only
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

    private ExecutionPipeline BuildPipeline(Action<RippleEngineOptions>? configure = null)
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

        var pipeline = new ExecutionPipeline(Options.Create(options), registry,
            services.GetRequiredService<IServiceScopeFactory>(), _store, new RippleMetrics(), _time,
            NullLogger<ExecutionPipeline>.Instance);
        pipeline.Start();
        return pipeline;
    }

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
    private sealed class CountingTimeProvider() : FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))
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

        public Task CompleteRipplesAsync(IReadOnlyList<RippleCompletion> batch, string instanceId,
            CancellationToken ct = default)
        {
            foreach (var c in batch) Completed.Enqueue(c);
            return Task.CompletedTask;
        }

        public Task FailRipplesAsync(IReadOnlyList<RippleFailure> batch, string instanceId,
            CancellationToken ct = default)
        {
            foreach (var f in batch) Failed.Enqueue(f);
            return Task.CompletedTask;
        }
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

using System.Runtime.ExceptionServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using ResourceManager.App.Hosting;

namespace Resource_Manager_APP.Tests;

internal sealed class PresentFrameEtwTestHarness : IAsyncDisposable
{
    private readonly ServiceProvider services;

    private PresentFrameEtwTestHarness(
        ServiceProvider services,
        BusinessBlockController controller,
        PresentFrameEtwSource source,
        PresentFrameEtwTestSessionFactory sessions)
    {
        this.services = services;
        Controller = controller;
        Source = source;
        Sessions = sessions;
    }

    internal BusinessBlockController Controller { get; }
    internal PresentFrameEtwSource Source { get; }
    internal PresentFrameEtwTestSessionFactory Sessions { get; }

    internal static PresentFrameEtwTestHarness Create(
        ILogger<PresentFrameEtwSource>? logger = null,
        TimeProvider? timeProvider = null,
        IEtwConsumerThreadObserver? observer = null,
        BusinessBlockOptions? blockOptions = null,
        bool useNativeSession = false)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var sessions = new PresentFrameEtwTestSessionFactory();
        PresentFrameEtwSource? source = null;
        var registration = new BusinessBlockRegistration("present-etw", [], _ => source
            ?? throw new InvalidOperationException("The Present ETW test source has not been assigned."));
        var controller = new BusinessBlockController(services, [registration],
            Options.Create(blockOptions ?? new BusinessBlockOptions()), NullLogger<BusinessBlockController>.Instance);
        observer ??= new BusinessBlockEtwThreadObserver(controller);
        var sourceLogger = logger ?? NullLogger<PresentFrameEtwSource>.Instance;
        if (useNativeSession)
        {
            source = new PresentFrameEtwSource(sourceLogger, controller, observer);
        }
        else
        {
            source = new PresentFrameEtwSource(sourceLogger, controller, sessions.Create,
                timeProvider ?? new PresentFrameEtwManualTimeProvider(), observer);
        }

        return new(services, controller, source, sessions);
    }

    internal static Task ReadyAsync(IDisposable lease)
        => Dependency(lease).Ready;

    internal static ValueTask ReleaseAsync(IDisposable lease)
        => Dependency(lease).DisposeAsync();

    internal static IBusinessBlockDependency Dependency(IDisposable lease)
        => lease as IBusinessBlockDependency
            ?? throw new InvalidOperationException("A Present ETW lease must expose its controller dependency lifetime.");

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Controller.StopDependenciesAsync(new(BusinessBlockRequestKind.Dependency,
                "present-test-harness", "test-cleanup", Controller.CurrentPlanVersion));
        }
        finally
        {
            Source.Dispose();
            await Controller.DisposeAsync();
            await services.DisposeAsync();
        }
    }
}

internal sealed class PresentFrameEtwTestSessionFactory
{
    private readonly object sync = new();
    private readonly List<PresentFrameEtwRecordingSession> sessions = [];
    private int attempts;

    internal Action<PresentFrameEtwRecordingSession, int>? Configure { get; set; }
    internal int Attempts => Volatile.Read(ref attempts);
    internal PresentFrameEtwRecordingSession[] Snapshot()
    {
        lock (sync) return sessions.ToArray();
    }

    internal PresentFrameEtwRecordingSession Current
        => Snapshot().LastOrDefault() ?? throw new InvalidOperationException("No Present ETW session was created.");

    internal IPresentFrameEtwSession Create(string name)
    {
        var attempt = Interlocked.Increment(ref attempts);
        var session = new PresentFrameEtwRecordingSession(name);
        Configure?.Invoke(session, attempt);
        lock (sync) sessions.Add(session);
        return session;
    }
}

internal sealed record PresentEtwProviderOperation(
    string Kind,
    Guid Provider,
    TraceEventLevel Level = default,
    ulong Keywords = 0,
    int[]? EventIds = null);

internal sealed class PresentFrameEtwRecordingSession(string name) : IPresentFrameEtwSession
{
    private readonly object sync = new();
    private readonly TaskCompletionSource<Exception?> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> processExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource processStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<PresentEtwProviderOperation> operations = [];
    private readonly List<Guid> enabled = [];
    private Action<TraceEvent>? handler;
    private int stopped;
    private int eventsLost;
    private int eventsLostReads;
    private int flushes;

    internal string Name { get; } = name;
    internal Task ProcessExited => processExited.Task;
    internal Task ProcessStarted => processStarted.Task;
    internal Thread? ConsumerThread { get; private set; }
    internal int Stops => Volatile.Read(ref stopped);
    internal int EventsLostReads => Volatile.Read(ref eventsLostReads);
    internal int Flushes => Volatile.Read(ref flushes);
    internal int ReportedEventsLost
    {
        get => Volatile.Read(ref eventsLost);
        set => Volatile.Write(ref eventsLost, value);
    }
    internal (string Kind, Guid Provider)? FailTransition { get; set; }
    internal Action<Guid>? AfterEnable { get; set; }
    internal Exception? FlushFailure { get; set; }
    internal PresentEtwProviderOperation[] Operations
    {
        get { lock (sync) return operations.ToArray(); }
    }
    internal Guid[] Enabled
    {
        get { lock (sync) return enabled.ToArray(); }
    }

    public int EventsLost
    {
        get
        {
            Interlocked.Increment(ref eventsLostReads);
            return Volatile.Read(ref eventsLost);
        }
    }

    public void Bind(Action<TraceEvent> callback) => handler = callback;

    public void Process()
    {
        ConsumerThread = Thread.CurrentThread;
        processStarted.TrySetResult();
        try
        {
            var error = completed.Task.GetAwaiter().GetResult();
            if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        }
        finally { processExited.TrySetResult(true); }
    }

    public void EnableProvider(Guid provider, TraceEventLevel level, ulong keywords, int[] eventIds)
    {
        lock (sync) operations.Add(new("enable", provider, level, keywords, eventIds.ToArray()));
        ThrowIfRequested("enable", provider);
        lock (sync) enabled.Add(provider);
        AfterEnable?.Invoke(provider);
    }

    public void DisableProvider(Guid provider)
    {
        lock (sync) operations.Add(new("disable", provider));
        ThrowIfRequested("disable", provider);
        lock (sync) enabled.Remove(provider);
    }

    public void Flush()
    {
        Interlocked.Increment(ref flushes);
        if (FlushFailure is { } error) ExceptionDispatchInfo.Capture(error).Throw();
    }

    public void Complete(Exception? error = null) => completed.TrySetResult(error);

    public void Emit(TraceEvent data) => handler?.Invoke(data);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 0) completed.TrySetResult(null);
    }

    private void ThrowIfRequested(string kind, Guid provider)
    {
        if (FailTransition == (kind, provider))
            throw new InvalidOperationException("Recorded Present ETW provider transition failure.");
    }
}

internal sealed class PresentFrameEtwManualTimeProvider : TimeProvider
{
    private readonly object sync = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = DateTimeOffset.UnixEpoch.AddDays(1);

    public override DateTimeOffset GetUtcNow()
    {
        lock (sync) return now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (sync) timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    internal void Advance(TimeSpan amount)
    {
        if (amount < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(amount));
        ManualTimer[] pending;
        DateTimeOffset current;
        lock (sync)
        {
            now += amount;
            current = now;
            pending = timers.ToArray();
        }
        foreach (var timer in pending) timer.FireThrough(current);
    }

    private DateTimeOffset UtcNowUnsafe
    {
        get { lock (sync) return now; }
    }

    private sealed class ManualTimer(PresentFrameEtwManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private readonly object sync = new();
        private DateTimeOffset? dueAt;
        private TimeSpan period = Timeout.InfiniteTimeSpan;
        private bool disposed;

        public bool Change(TimeSpan dueTime, TimeSpan nextPeriod)
        {
            lock (sync)
            {
                if (disposed) return false;
                dueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.UtcNowUnsafe + dueTime;
                period = nextPeriod;
                return true;
            }
        }

        internal void FireThrough(DateTimeOffset now)
        {
            while (true)
            {
                lock (sync)
                {
                    if (disposed || dueAt is not { } scheduled || scheduled > now) return;
                    dueAt = period <= TimeSpan.Zero || period == Timeout.InfiniteTimeSpan
                        ? null
                        : scheduled + period;
                }
                callback(state);
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                disposed = true;
                dueAt = null;
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

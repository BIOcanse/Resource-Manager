using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Devices;
using ResourceManager.ReadWrite.Windows.Processes.Etw;
using ResourceManager.App.Hosting;

namespace Resource_Manager_APP.Tests;

internal sealed class VidMmBusinessBlockTestHarness : IAsyncDisposable
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);
    private readonly ServiceProvider services;

    private VidMmBusinessBlockTestHarness(
        ServiceProvider services,
        BusinessBlockController controller,
        DxgkrnlVidMmEtwTelemetryZone zone,
        VidMmEtwTestSessionFactory sessions,
        RecordingEtwConsumerThreadObserver observer,
        RecordingLogger<BusinessBlockController> controllerLogger,
        ProcessMetadataState processMetadata)
    {
        this.services = services;
        Controller = controller;
        Zone = zone;
        Sessions = sessions;
        Observer = observer;
        ControllerLogger = controllerLogger;
        this.processMetadata = processMetadata;
    }

    internal BusinessBlockController Controller { get; }
    internal DxgkrnlVidMmEtwTelemetryZone Zone { get; }
    internal VidMmEtwTestSessionFactory Sessions { get; }
    internal RecordingEtwConsumerThreadObserver Observer { get; }
    internal RecordingLogger<BusinessBlockController> ControllerLogger { get; }
    internal Func<uint, ulong?> CurrentProcessBirth
    {
        get => processMetadata.CurrentBirth;
        set => processMetadata.CurrentBirth = value;
    }
    internal int ProcessMetadataOpenCount => processMetadata.OpenCount;

    private readonly ProcessMetadataState processMetadata;

    internal static VidMmBusinessBlockTestHarness Create(BusinessBlockOptions? blockOptions = null)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var sessions = new VidMmEtwTestSessionFactory();
        var controllerLogger = new RecordingLogger<BusinessBlockController>();
        var processMetadata = new ProcessMetadataState();
        DxgkrnlVidMmEtwTelemetryZone? zone = null;
        var registration = new BusinessBlockRegistration(
            nameof(DxgkrnlVidMmEtwTelemetryZone),
            [],
            _ => zone ?? throw new InvalidOperationException("The VidMm test zone has not been assigned."));
        var controller = new BusinessBlockController(
            services,
            [registration],
            Options.Create(blockOptions ?? new BusinessBlockOptions()),
            controllerLogger);
        var observer = new RecordingEtwConsumerThreadObserver(new BusinessBlockEtwThreadObserver(controller));
        var harness = new VidMmBusinessBlockTestHarness(services, controller,
            zone = new DxgkrnlVidMmEtwTelemetryZone(
                NullLogger<DxgkrnlVidMmEtwTelemetryZone>.Instance,
                controller,
                processMetadata.Open,
                sessions.Create,
                observer),
            sessions,
            observer,
            controllerLogger,
            processMetadata);
        return harness;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Controller.StopDependenciesAsync(new(
                BusinessBlockRequestKind.Dependency,
                "vidmm-test-harness",
                "test-cleanup",
                Controller.CurrentPlanVersion)).WaitAsync(CleanupTimeout);
        }
        finally
        {
            await Controller.DisposeAsync();
            await services.DisposeAsync();
        }
    }

    private sealed class TestProcessLease(uint processId, ulong birth) : IWindowsNetworkEndpointProcessLease
    {
        public uint ProcessId { get; } = processId;

        public bool TryReadCreationFileTime(out ulong result)
        {
            result = birth;
            return true;
        }

        public void Dispose() { }
    }

    private sealed class ProcessMetadataState
    {
        private int openCount;

        internal Func<uint, ulong?> CurrentBirth { get; set; } = static _ => 100;
        internal int OpenCount => Volatile.Read(ref openCount);

        internal IWindowsNetworkEndpointProcessLease? Open(uint processId)
        {
            Interlocked.Increment(ref openCount);
            return CurrentBirth(processId) is { } birth ? new TestProcessLease(processId, birth) : null;
        }
    }
}

internal sealed class VidMmEtwTestSessionFactory
{
    private readonly object sync = new();
    private readonly List<VidMmEtwTestSession> sessions = [];
    private int attempts;

    internal int Attempts => Volatile.Read(ref attempts);

    internal VidMmEtwTestSession[] Snapshot()
    {
        lock (sync) return sessions.ToArray();
    }

    internal VidMmEtwTestSession Current
        => Snapshot().LastOrDefault() ?? throw new InvalidOperationException("No VidMm ETW session has been created.");

    internal IVidMmEtwSession Create(string sessionName)
    {
        Interlocked.Increment(ref attempts);
        var session = new VidMmEtwTestSession(sessionName);
        lock (sync) sessions.Add(session);
        return session;
    }
}

internal sealed class VidMmEtwTestSession(string sessionName) : IVidMmEtwSession
{
    private readonly TaskCompletionSource<Exception?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<TraceEvent>? handler;
    private Thread? consumerThread;
    private Thread? disposeThread;
    private int disposeCount;
    private bool consumerWasAliveAtDispose;

    internal string SessionName { get; } = sessionName;
    internal Task ProcessEntered => entered.Task;
    internal Task ProcessExited => exited.Task;
    internal Thread? ConsumerThread => Volatile.Read(ref consumerThread);
    internal Thread? DisposeThread => Volatile.Read(ref disposeThread);
    internal int DisposeCount => Volatile.Read(ref disposeCount);
    internal bool ConsumerWasAliveAtDispose => Volatile.Read(ref consumerWasAliveAtDispose);

    public void Bind(Action<TraceEvent> callback) => handler = callback;

    public void Process()
    {
        Volatile.Write(ref consumerThread, Thread.CurrentThread);
        entered.TrySetResult();
        try
        {
            var error = completion.Task.GetAwaiter().GetResult();
            if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        }
        finally
        {
            exited.TrySetResult();
        }
    }

    internal void Emit(TraceEvent data) => handler?.Invoke(data);

    internal void Complete(Exception? error = null) => completion.TrySetResult(error);

    public void Dispose()
    {
        if (Interlocked.Increment(ref disposeCount) != 1) return;
        Volatile.Write(ref disposeThread, Thread.CurrentThread);
        Volatile.Write(ref consumerWasAliveAtDispose, ConsumerThread?.IsAlive == true);
        completion.TrySetResult(null);
    }
}

internal sealed class RecordingEtwConsumerThreadObserver(IEtwConsumerThreadObserver? inner = null) : IEtwConsumerThreadObserver
{
    private readonly ConcurrentQueue<ThreadRegistration> registrations = new();
    private readonly ConcurrentQueue<(string BlockId, Exception Error)> failures = new();

    internal ThreadRegistration[] Registrations => registrations.ToArray();
    internal (string BlockId, Exception Error)[] Failures => failures.ToArray();

    public IDisposable RegisterCurrentThread(string blockId, string sessionName)
    {
        var registration = new ThreadRegistration(blockId, sessionName, Thread.CurrentThread,
            inner?.RegisterCurrentThread(blockId, sessionName));
        registrations.Enqueue(registration);
        return registration;
    }

    public void ReportFailure(string blockId, Exception error)
    {
        failures.Enqueue((blockId, error));
        inner?.ReportFailure(blockId, error);
    }

    internal sealed class ThreadRegistration(string blockId, string sessionName, Thread thread,
        IDisposable? innerRegistration = null) : IDisposable
    {
        private int disposed;

        internal string BlockId { get; } = blockId;
        internal string SessionName { get; } = sessionName;
        internal Thread Thread { get; } = thread;
        internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) innerRegistration?.Dispose();
        }
    }
}

internal sealed class RecordingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<CapturedLog> entries = new();
    internal IReadOnlyCollection<CapturedLog> Entries => entries.ToArray();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
            ? pairs.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);
        entries.Enqueue(new(logLevel, formatter(state, exception), values, exception));
    }

    internal sealed record CapturedLog(
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> State,
        Exception? Exception);
}

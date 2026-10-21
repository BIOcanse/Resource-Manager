using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Results.Explicit;
using ResourceManager.Subscription;
using ResourceManager.Subscription.Native;

internal static class WindowsRawInputSessionSubscriptionConsumer
{
    internal static async Task RunRecordedAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var owner = new WindowsExplicitResultOwner("recorded-input-subscription-host");
        var backend = owner.CreateBackend();
        var rate = new FieldReference(owner.ManualTestDeviceIdentity, "mouse_all_report_rate_measured");
        var jitter = new FieldReference(owner.ManualTestDeviceIdentity, "mouse_all_report_jitter_measured");
        var runtime = new RecordedRuntime(Device(0xA4));
        var first = Capture(owner, runtime, "input-subscription-baseline");
        Require(first.Values.MouseAllReportRateMeasured?.Value == 1000d
            && runtime.StartCaptureCount == 1 && runtime.DisposeCount == 1,
            "an explicit recorded session submits a baseline and releases its capture lease");
        var baselineRate = backend.ReadCached(rate);
        var baselineJitter = backend.ReadCached(jitter);
        Require(baselineRate.Status == FieldObservationStatus.Current && baselineRate.Value?.Number == 1000d
            && baselineJitter.Status == FieldObservationStatus.Current && baselineJitter.Value?.Number == 0d,
            "direct cached reads expose the already-submitted baseline without starting another capture");

        var failures = new ConcurrentQueue<SubscriptionWorkerFailure>();
        var recordingBorrow = new RecordingBorrow(owner.BorrowSubscriptionCapture());
        var configuration = Configuration();
        await using var composition = new NativeFieldSubscriptionComposition(recordingBorrow,
            new SubscriptionRoleSettings(1, 1000, 1, 3000, 1000, 1000), in configuration,
            failures.Enqueue, TimeSpan.FromMilliseconds(25));
        await composition.StartAsync(timeout.Token);
        using var demand = composition.Acquire("recorded-raw-input-demand",
            [rate, jitter], TimeSpan.FromMilliseconds(100));

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        var stream = composition.SubscribeAsync("recorded-raw-input-result",
            [rate, jitter], TimeSpan.FromSeconds(1), cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        var baselineCurrent = owner.ReadCurrent();
        var pending = stream.MoveNextAsync().AsTask();
        await Task.Yield();
        var committed = Capture(owner, runtime, "input-subscription-replacement");
        Require(await pending.WaitAsync(timeout.Token)
            && ReferenceEquals(committed, owner.ReadCurrent())
            && !ReferenceEquals(baselineCurrent, committed),
            "a future explicit input Stop publication wakes the role-2 subscriber and replaces the one current");
        Require(stream.Current.Length == 2
            && stream.Current.Single(item => item.Field == rate) == backend.ReadCached(rate)
            && stream.Current.Single(item => item.Field == jitter) == backend.ReadCached(jitter),
            "the actual role-2 composition and direct reads observe the same submitted Information values");
        Require(runtime.StartCaptureCount == 2 && runtime.DisposeCount == 2
            && runtime.ActiveCaptureCount == 0 && failures.IsEmpty,
            "subscription observation causes no extra Raw Input registration and the final role release is clean");

        var next = stream.MoveNextAsync().AsTask();
        cancellation.Cancel();
        try
        {
            _ = await next.WaitAsync(timeout.Token);
            throw new InvalidOperationException("A canceled Raw Input observer yielded another reading.");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        await stream.DisposeAsync();
        demand.Dispose();
        await composition.StopAsync();
        var afterLastRelease = recordingBorrow.CollectionCount;
        await Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token);
        Require(recordingBorrow.CollectionCount == afterLastRelease
            && owner.ReadCurrent() == committed && failures.IsEmpty,
            "last-demand release stops role-2 collection while the same Raw Input result remains readable");
        Console.WriteLine("SubscriptionCore Raw Input consumer passed: explicit recorded session, same-current role-2 observation, and last-demand release.");
    }

    private static WindowsExplicitResultCurrent Capture(WindowsExplicitResultOwner owner,
        RecordedRuntime runtime, string sessionId)
    {
        var device = WindowsRawInputDeviceCatalog.Enumerate(runtime).Single();
        using var session = WindowsRawInputCaptureSession.Start(owner, device,
            new("rawinput:mouse:a4", sessionId, SourceId: "recorded-rawinput-subscription",
                SourceVersion: "fixture-1", Workload: "recorded mouse report interval observation",
                MaximumRawInputBytes: 256, MaximumRawEvents: 16, MaximumSamples: 16), runtime);
        session.BeginEvidence(WindowsExplicitInputEvidenceKind.MouseReportObservation, "reports");
        runtime.Emit(new(device.DeviceHandle, WindowsRawInputPacketKind.Mouse, 48, 0));
        runtime.Emit(new(device.DeviceHandle, WindowsRawInputPacketKind.Mouse, 48, 0));
        session.EndEvidence(WindowsExplicitInputEvidenceKind.MouseReportObservation);
        var result = session.Stop();
        Require(result.Status == WindowsRawInputCaptureStopStatus.Committed && result.Current is not null,
            "a complete recorded input session commits into the original explicit owner");
        return result.Current!;
    }

    private static WindowsRawInputDeviceDescriptor Device(ulong handle)
        => new(handle, WindowsRawInputDeviceKind.Mouse, @"\\?\HID#RECORDED#MOUSE-A4",
            new(0, MouseId: 1, MouseButtonCount: 5, NativeReportedMouseSampleRate: 1000));

    private static NativeSamplingSubscriptionConfiguration Configuration() => new()
    {
        AbiVersion = NativeSamplingSubscriptionAbi.Version,
        StructSize = checked((uint)Unsafe.SizeOf<NativeSamplingSubscriptionConfiguration>()),
        Generation = 1,
        MaximumSourceCount = 8,
        MaximumItemCount = 8,
        MaximumMembershipCount = 16,
        MaximumDueItemCount = 8,
        MaximumSourceViewCount = 8,
        MaximumExpiredSourceCount = 8,
        DefaultIntervalMilliseconds = 1000,
        MinimumIntervalMilliseconds = 1,
        ActiveTtlMilliseconds = 3000,
        MaximumFutureSkewMilliseconds = 1000
    };

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class RecordedRuntime(WindowsRawInputDeviceDescriptor device)
        : IWindowsRawInputSessionRuntime
    {
        private long counter;
        private Action<WindowsRawInputNativeEvent>? onInput;
        private Action<WindowsRawInputCaptureFault>? onFault;
        private int activeCaptureCount;
        internal int StartCaptureCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal int ActiveCaptureCount => Volatile.Read(ref activeCaptureCount);

        public ImmutableArray<WindowsRawInputDeviceDescriptor> EnumerateDevices(
            int maximumDevices, int maximumDevicePathCharacters) => [device];

        public WindowsRawInputDeviceDescriptor QueryDevice(ulong deviceHandle, int maximumDevicePathCharacters)
            => deviceHandle == device.DeviceHandle ? device
                : throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                    "Recorded device is unavailable.");

        public long QueryPerformanceFrequency() => 1000;

        public bool TryQueryPerformanceCounter(out long value)
        {
            value = Interlocked.Increment(ref counter);
            return true;
        }

        public IDisposable StartCapture(WindowsRawInputDeviceKind kind, ulong selectedDeviceHandle,
            uint maximumRawInputBytes, Action<WindowsRawInputNativeEvent> input,
            Action<WindowsRawInputCaptureFault> fault)
        {
            StartCaptureCount++;
            Interlocked.Increment(ref activeCaptureCount);
            onInput = input;
            onFault = fault;
            return new Lease(this);
        }

        internal void Emit(WindowsRawInputNativeEvent packet) => onInput!(packet);

        private void Release()
        {
            onInput = null;
            onFault = null;
            Interlocked.Decrement(ref activeCaptureCount);
            DisposeCount++;
        }

        private sealed class Lease(RecordedRuntime owner) : IDisposable
        {
            private int disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Release();
            }
        }
    }

    private sealed class RecordingBorrow(IReadWriteCollectionBorrow inner) : IReadWriteCollectionBorrow
    {
        private long collectionCount;
        internal long CollectionCount => Interlocked.Read(ref collectionCount);
        public IReadWriteBackend Backend => inner.Backend;
        public ResourceManager.ReadWrite.Publications.SamplingPublicationSignal Publications => inner.Publications;
        public ImmutableArray<FieldCollectionBinding> Bind(ImmutableArray<FieldReference> fields) => inner.Bind(fields);
        public void SynchronizeDemand(ImmutableArray<FieldReference> activeFields) => inner.SynchronizeDemand(activeFields);
        public async ValueTask<FieldCollectionResult> CollectAsync(ImmutableArray<FieldReference> fields,
            ResourceManager.App.Infrastructure.Monitoring.IMetricPublicationPermit permit,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref collectionCount);
            return await inner.CollectAsync(fields, permit, cancellationToken).ConfigureAwait(false);
        }
        public void Dispose() => inner.Dispose();
    }
}

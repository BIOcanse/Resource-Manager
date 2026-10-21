using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Results.Explicit;

namespace Resource_Manager_APP.Tests;

public sealed class WindowsRawInputCaptureSessionTests
{
    [Fact]
    public async Task ExplicitRecordedSessionIsTheOnlyCaptureAndPublishesOneSharedCurrent()
    {
        using var owner = new WindowsExplicitResultOwner("recorded-raw-input-app-test");
        var runtime = new RecordedRuntime(Device(0x44));
        var backend = owner.CreateBackend();
        var field = new FieldReference(owner.ManualTestDeviceIdentity, "mouse_all_report_rate_measured");

        Assert.Equal(FieldObservationStatus.Unavailable, backend.ReadCached(field).Status);
        _ = await backend.CollectAsync([field], CancellationToken.None);
        Assert.Equal(0, runtime.StartCaptureCount);
        Assert.Equal(0, runtime.ActiveCaptureCount);

        var selected = Assert.Single(WindowsRawInputDeviceCatalog.Enumerate(runtime));
        Assert.Equal(0x44UL, selected.DeviceHandle);
        using var session = WindowsRawInputCaptureSession.Start(owner, selected,
            new("rawinput:app-test:mouse-44", "app-test-session",
                MaximumRawInputBytes: 256, MaximumRawEvents: 16, MaximumSamples: 16), runtime);
        Assert.Equal(1, runtime.StartCaptureCount);
        Assert.Equal(1, runtime.ActiveCaptureCount);

        session.BeginEvidence(WindowsExplicitInputEvidenceKind.MouseReportObservation, "reports");
        runtime.Emit(new(selected.DeviceHandle, WindowsRawInputPacketKind.Mouse, 48, 0,
            MouseFlags: 0, MouseButtonsUnion: 0, MouseButtonFlags: 0, MouseButtonData: 0,
            MouseRawButtons: 0, MouseX: 0, MouseY: 0, MouseExtraInformation: 0));
        runtime.Emit(new(selected.DeviceHandle, WindowsRawInputPacketKind.Mouse, 48, 0,
            MouseFlags: 0, MouseButtonsUnion: 0, MouseButtonFlags: 0, MouseButtonData: 0,
            MouseRawButtons: 0, MouseX: 5, MouseY: -2, MouseExtraInformation: 7));
        session.EndEvidence(WindowsExplicitInputEvidenceKind.MouseReportObservation);

        var stopped = session.Stop();
        Assert.Equal(WindowsRawInputCaptureStopStatus.Committed, stopped.Status);
        Assert.NotNull(stopped.Current);
        Assert.Same(owner.ReadCurrent(), stopped.Current);
        Assert.Equal(2, owner.ReadCurrent().InputSession!.RawEvents.Length);
        Assert.Equal(5, owner.ReadCurrent().InputSession!.RawEvents[1].MouseX);
        Assert.Equal(7u, owner.ReadCurrent().InputSession!.RawEvents[1].MouseExtraInformation);
        Assert.Equal(1000d, owner.ReadCurrent().Values.MouseAllReportRateMeasured?.Value);
        Assert.Equal(0, runtime.ActiveCaptureCount);
        Assert.Equal(1, runtime.DisposeCount);

        var cached = backend.ReadCached(field);
        var collected = await backend.CollectAsync([field], CancellationToken.None);
        Assert.Equal(FieldObservationStatus.Current, cached.Status);
        Assert.Equal(1000d, cached.Value?.Number);
        Assert.Equal(cached, Assert.Single(collected));
        Assert.Equal(1, runtime.StartCaptureCount);
    }

    [Fact]
    public void CancellationAndRecordedDeviceFailureLeaveCurrentUnchanged()
    {
        using var owner = new WindowsExplicitResultOwner("recorded-raw-input-failure-test");
        var canceledRuntime = new RecordedRuntime(Device(0x55));
        using var canceled = WindowsRawInputCaptureSession.Start(owner,
            Assert.Single(WindowsRawInputDeviceCatalog.Enumerate(canceledRuntime)),
            Options("cancelled-session"), canceledRuntime);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var canceledResult = canceled.Stop(cancellation.Token);

        Assert.Equal(WindowsRawInputCaptureStopStatus.Cancelled, canceledResult.Status);
        Assert.Equal(0UL, owner.ReadCurrent().Revision);
        Assert.Equal(0, canceledRuntime.ActiveCaptureCount);
        Assert.Equal(1, canceledRuntime.DisposeCount);

        var failedRuntime = new RecordedRuntime(Device(0x66));
        using var failed = WindowsRawInputCaptureSession.Start(owner,
            Assert.Single(WindowsRawInputDeviceCatalog.Enumerate(failedRuntime)),
            Options("failed-session"), failedRuntime);
        failed.BeginEvidence(WindowsExplicitInputEvidenceKind.MouseReportObservation, "reports");
        failedRuntime.Emit(new(0x66, WindowsRawInputPacketKind.Mouse, 48, 0));
        failed.EndEvidence(WindowsExplicitInputEvidenceKind.MouseReportObservation);
        failedRuntime.Remove(0x66);
        var failedResult = failed.Stop();

        Assert.Equal(WindowsRawInputCaptureStopStatus.Incomplete, failedResult.Status);
        Assert.Equal(WindowsRawInputCaptureFailureCode.DeviceRemoved, failedResult.FailureCode);
        Assert.Equal(0UL, owner.ReadCurrent().Revision);
        Assert.Null(owner.ReadCurrent().InputSession);
        Assert.Equal(0, failedRuntime.ActiveCaptureCount);
        Assert.Equal(1, failedRuntime.DisposeCount);
    }

    private static WindowsRawInputCaptureOptions Options(string sessionId)
        => new("rawinput:app-test:mouse", sessionId,
            MaximumRawInputBytes: 256, MaximumRawEvents: 16, MaximumSamples: 16);

    private static WindowsRawInputDeviceDescriptor Device(ulong handle)
        => new(handle, WindowsRawInputDeviceKind.Mouse, $@"\\?\HID#RECORDED#{handle:X}",
            new(0, MouseId: 1, MouseButtonCount: 3, NativeReportedMouseSampleRate: 1000));

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

        public WindowsRawInputDeviceDescriptor QueryDevice(ulong deviceHandle,
            int maximumDevicePathCharacters)
            => deviceHandle == device.DeviceHandle ? device
                : throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                    "The recorded device is unavailable.");

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
            if (kind != device.Kind || selectedDeviceHandle != device.DeviceHandle)
                throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                    "The recorded runtime received a different device selection.");
            StartCaptureCount++;
            Interlocked.Increment(ref activeCaptureCount);
            onInput = input;
            onFault = fault;
            return new Lease(this);
        }

        internal void Emit(WindowsRawInputNativeEvent packet) => onInput!(packet);

        internal void Remove(ulong deviceHandle)
            => onFault!(new(WindowsRawInputCaptureFailureCode.DeviceRemoved,
                "Recorded device removal.", DeviceHandle: deviceHandle));

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
}

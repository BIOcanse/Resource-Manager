using System.Collections.Immutable;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Windows.Results.Explicit;

internal static class WindowsRawInputCaptureSessionRecordedTests
{
    internal static async Task RunAsync()
    {
        await VerifyMouseRawInputAndSameCurrentAsync();
        VerifyKeyboardMakeBreakPrefixAndRepeatedMake();
        VerifyFailureOverflowAndCancellation();
    }

    private static async Task VerifyMouseRawInputAndSameCurrentAsync()
    {
        using var owner = new WindowsExplicitResultOwner("recorded-input-host",
            new WindowsExplicitResultLimits(MaximumInputSamples: 64));
        var runtime = new RecordedRuntime(Device(0x44, WindowsRawInputDeviceKind.Mouse));
        var backend = owner.CreateBackend();
        var rate = new FieldReference(owner.ManualTestDeviceIdentity, "mouse_all_report_rate_measured");

        Require(runtime.ActiveCaptureCount == 0 && backend.ReadCached(rate).Status == FieldObservationStatus.Unavailable,
            "owner construction and direct cached read do not start native capture");
        _ = await backend.CollectAsync([rate], CancellationToken.None);
        Require(runtime.StartCaptureCount == 0,
            "direct collection observes the one explicit-result current and does not register Raw Input");

        var selected = WindowsRawInputDeviceCatalog.Enumerate(runtime).Single();
        Require(selected.DeviceHandle == 0x44 && selected.Kind == WindowsRawInputDeviceKind.Mouse
            && selected.DeviceInterfacePath == @"\\?\HID#RECORDED#44",
            "device selection uses the typed exact hDevice and queried interface path");
        var session = WindowsRawInputCaptureSession.Start(owner, selected, Options("mouse-session",
            "rawinput:mouse:44", maximumEvents: 32, maximumSamples: 32), runtime);
        Require(runtime.ActiveCaptureCount == 1 && runtime.StartCaptureCount == 1,
            "only explicit Start acquires one recorded capture lease");

        session.BeginEvidence(WindowsExplicitInputEvidenceKind.MouseReportObservation, "reports");
        session.BeginEvidence(WindowsExplicitInputEvidenceKind.MouseTrajectoryTest, "trajectory");
        session.BeginEvidence(WindowsExplicitInputEvidenceKind.MouseButtonTransitionTest, "buttons");
        runtime.Emit(Mouse(0x45, 0, 900, 900));
        runtime.Emit(Mouse(0x44, 0, 0, 0));
        runtime.Emit(Mouse(0x44, 0, 0, 0));
        runtime.EndEvidenceForTest(session, WindowsExplicitInputEvidenceKind.MouseReportObservation);
        runtime.Emit(Mouse(0x44, 0, 12, -3));
        runtime.Emit(Mouse(0x44, 0x0001, 0, 0));
        runtime.Emit(Mouse(0x44, 0x0002, 0, 0));
        runtime.Emit(Mouse(0x44, 0x0001, 0, 0));
        session.EndEvidence(WindowsExplicitInputEvidenceKind.MouseTrajectoryTest);
        session.EndEvidence(WindowsExplicitInputEvidenceKind.MouseButtonTransitionTest);

        var stopped = session.Stop();
        Require(stopped.Status == WindowsRawInputCaptureStopStatus.Committed && stopped.Current is not null
            && ReferenceEquals(stopped.Current, owner.ReadCurrent()),
            "a complete explicit stop commits atomically into the supplied original owner");
        Require(runtime.ActiveCaptureCount == 0 && runtime.DisposeCount == 1,
            "Stop releases the final registration lease");
        var current = owner.ReadCurrent();
        var input = current.InputSession;
        Require(input is not null && input.RawEvents.Length == 6
            && input.RawEvents[0].MouseButtonsUnion == 0
            && input.RawEvents[2].MouseX == 12
            && input.RawEvents[3].MouseButtonFlags == 0x0001
            && input.RawEvents[4].MouseButtonFlags == 0x0002,
            "the committed input session retains the full typed RAWMOUSE payload and QPC records");
        Require(current.Values.MouseAllReportRateMeasured?.Value == 1000d
            && current.Values.MouseAllReportJitterMeasured?.Value == 0d
            && current.Values.MouseAllTrajectoryManualTest?.Value == 1
            && current.Values.MouseAllButtonTransitionManualTest?.Value == 3
            && current.Values.MouseAllButtonBounceManualTest?.Value == 1,
            "arrival intervals, relative trajectory, button transitions, and short alternations derive from recorded QPC packets");

        var cached = backend.ReadCached(rate);
        Require(cached.Status == FieldObservationStatus.Current && cached.Value?.Number == 1000d,
            "the public direct cache read observes the just-committed current");
        var collected = await backend.CollectAsync([rate], CancellationToken.None);
        Require(collected.Single() == cached && runtime.StartCaptureCount == 1,
            "direct collection reads the same committed current without starting another Raw Input session");
    }

    private static void VerifyKeyboardMakeBreakPrefixAndRepeatedMake()
    {
        using var owner = new WindowsExplicitResultOwner("recorded-keyboard-host",
            new WindowsExplicitResultLimits(MaximumInputSamples: 64));
        var runtime = new RecordedRuntime(Device(0x55, WindowsRawInputDeviceKind.Keyboard));
        var selected = WindowsRawInputDeviceCatalog.Enumerate(runtime).Single();
        using var session = WindowsRawInputCaptureSession.Start(owner, selected,
            Options("keyboard-session", "rawinput:keyboard:55", maximumEvents: 32, maximumSamples: 32), runtime);
        session.BeginEvidence(WindowsExplicitInputEvidenceKind.KeyboardTransitionTest, "transitions");
        session.BeginEvidence(WindowsExplicitInputEvidenceKind.KeyboardCombinationTest, "combination");
        runtime.Emit(Key(0x55, 0x1D, 0, true));
        runtime.Emit(Key(0x55, 0x1E, 0x0002, true));
        runtime.Emit(Key(0x55, 0x1E, 0x0002, true));
        runtime.Emit(Key(0x55, 0x1E, 0x0003, false));
        runtime.Emit(Key(0x55, 0x1D, 0x0001, false));
        session.EndEvidence(WindowsExplicitInputEvidenceKind.KeyboardTransitionTest);
        session.EndEvidence(WindowsExplicitInputEvidenceKind.KeyboardCombinationTest);

        var stopped = session.Stop();
        Require(stopped.Status == WindowsRawInputCaptureStopStatus.Committed,
            "the recorded keyboard action commits through the same owner");
        var input = owner.ReadCurrent().InputSession!;
        Require(input.RawEvents.Length == 5 && input.RawEvents[2].KeyboardFlags == 0x0002
            && input.RawEvents[3].KeyboardFlags == 0x0003 && input.RawEvents[3].VirtualKey == 0x41,
            "full RAWKEYBOARD make, break, extended-prefix, virtual-key, and repeated-make reports are retained");
        Require(owner.ReadCurrent().Values.KeyboardAllKeyTransitionManualTest?.Value == 4
            && owner.ReadCurrent().Values.KeyboardAllComboManualTest?.Value == 1
            && owner.ReadCurrent().Values.KeyboardAllScanCodeManualTest?.Value.SequenceEqual(
                new uint[] { 0x001D, 0xE01E, 0xE01E, 0x001D }) == true,
            "scan codes preserve E0 encoding; duplicate make reports remain raw but are not extra transitions");
    }

    private static void VerifyFailureOverflowAndCancellation()
    {
        using var owner = new WindowsExplicitResultOwner("recorded-failure-host",
            new WindowsExplicitResultLimits(MaximumInputSamples: 16));
        var runtime = new RecordedRuntime(Device(0x66, WindowsRawInputDeviceKind.Mouse));
        var selected = WindowsRawInputDeviceCatalog.Enumerate(runtime).Single();

        var cancelled = WindowsRawInputCaptureSession.Start(owner, selected,
            Options("cancelled-session", "rawinput:mouse:66", 8, 8), runtime);
        cancelled.BeginEvidence(WindowsExplicitInputEvidenceKind.MouseReportObservation, "reports");
        runtime.Emit(Mouse(0x66, 0, 0, 0));
        using (var token = new CancellationTokenSource())
        {
            token.Cancel();
            var result = cancelled.Stop(token.Token);
            Require(result.Status == WindowsRawInputCaptureStopStatus.Cancelled
                && owner.ReadCurrent().Revision == 0 && runtime.ActiveCaptureCount == 0,
                "cancellation releases resources and leaves the original current untouched");
        }

        var overflow = WindowsRawInputCaptureSession.Start(owner, selected,
            Options("overflow-session", "rawinput:mouse:66", 8, 2), runtime);
        overflow.BeginEvidence(WindowsExplicitInputEvidenceKind.MouseReportObservation, "reports");
        runtime.Emit(Mouse(0x66, 0, 0, 0));
        runtime.Emit(Mouse(0x66, 0, 0, 0));
        runtime.Emit(Mouse(0x66, 0, 0, 0));
        var overflowResult = overflow.Stop();
        Require(overflowResult.Status == WindowsRawInputCaptureStopStatus.Incomplete
            && overflowResult.FailureCode == WindowsRawInputCaptureFailureCode.SampleLimitExceeded
            && owner.ReadCurrent().Revision == 0 && runtime.ActiveCaptureCount == 0,
            "sample overflow reports an incomplete capture without submitting its prefix");

        var rawOverflow = WindowsRawInputCaptureSession.Start(owner, selected,
            Options("raw-overflow-session", "rawinput:mouse:66", 2, 8), runtime);
        rawOverflow.BeginEvidence(WindowsExplicitInputEvidenceKind.MouseReportObservation, "reports");
        runtime.Emit(Mouse(0x66, 0, 0, 0));
        runtime.Emit(Mouse(0x66, 0, 0, 0));
        runtime.Emit(Mouse(0x66, 0, 0, 0));
        var rawOverflowResult = rawOverflow.Stop();
        Require(rawOverflowResult.Status == WindowsRawInputCaptureStopStatus.Incomplete
            && rawOverflowResult.FailureCode == WindowsRawInputCaptureFailureCode.EventLimitExceeded
            && owner.ReadCurrent().Revision == 0 && runtime.ActiveCaptureCount == 0,
            "raw event overflow reports an incomplete capture without submitting its prefix");

        var packetBound = WindowsRawInputCaptureSession.Start(owner, selected,
            Options("packet-bound-session", "rawinput:mouse:66", 8, 8, maximumRawBytes: 40), runtime);
        packetBound.BeginEvidence(WindowsExplicitInputEvidenceKind.MouseReportObservation, "reports");
        runtime.Emit(Mouse(0x66, 0, 0, 0));
        var packetBoundResult = packetBound.Stop();
        Require(packetBoundResult.Status == WindowsRawInputCaptureStopStatus.Incomplete
            && packetBoundResult.FailureCode == WindowsRawInputCaptureFailureCode.PacketTooLarge
            && owner.ReadCurrent().Revision == 0 && runtime.ActiveCaptureCount == 0,
            "a selected packet above the configured byte ceiling is reported without a partial submit");

        var removed = WindowsRawInputCaptureSession.Start(owner, selected,
            Options("removed-session", "rawinput:mouse:66", 8, 8), runtime);
        removed.BeginEvidence(WindowsExplicitInputEvidenceKind.MouseReportObservation, "reports");
        runtime.Remove(0x66);
        var removedResult = removed.Stop();
        Require(removedResult.Status == WindowsRawInputCaptureStopStatus.Incomplete
            && removedResult.FailureCode == WindowsRawInputCaptureFailureCode.DeviceRemoved
            && owner.ReadCurrent().Revision == 0,
            "device removal is explicit and cannot be mistaken for a complete zero-input result");

        var backwardRuntime = new RecordedRuntime(Device(0x77, WindowsRawInputDeviceKind.Keyboard));
        var keyboard = WindowsRawInputDeviceCatalog.Enumerate(backwardRuntime).Single();
        var backward = WindowsRawInputCaptureSession.Start(owner, keyboard,
            Options("qpc-session", "rawinput:keyboard:77", 8, 8), backwardRuntime);
        backward.BeginEvidence(WindowsExplicitInputEvidenceKind.KeyboardTransitionTest, "keys");
        backwardRuntime.ForceNextCounter(0);
        backwardRuntime.Emit(Key(0x77, 0x1E, 0, true));
        var qpcResult = backward.Stop();
        Require(qpcResult.Status == WindowsRawInputCaptureStopStatus.Incomplete
            && qpcResult.FailureCode == WindowsRawInputCaptureFailureCode.QpcWentBackwards
            && owner.ReadCurrent().Revision == 0,
            "a regressing QPC fails the session without changing current");

        var conflictRuntime = new RecordedRuntime(Device(0x88, WindowsRawInputDeviceKind.Mouse))
        { FailRegistration = true };
        var conflictDevice = WindowsRawInputDeviceCatalog.Enumerate(conflictRuntime).Single();
        try
        {
            _ = WindowsRawInputCaptureSession.Start(owner, conflictDevice,
                Options("conflict-session", "rawinput:mouse:88", 8, 8), conflictRuntime);
            throw new InvalidOperationException("A registration conflict was accepted.");
        }
        catch (WindowsRawInputCaptureException exception)
        {
            Require(exception.Code == WindowsRawInputCaptureFailureCode.RegistrationConflict
                && owner.ReadCurrent().Revision == 0,
                "preexisting registration conflicts fail before any result submission");
        }
    }

    private static WindowsRawInputCaptureOptions Options(string sessionId, string scopeIdentity,
        int maximumEvents, int maximumSamples, uint maximumRawBytes = 256)
        => new(scopeIdentity, sessionId, SourceId: "recorded-rawinput", SourceVersion: "fixture-1",
            Workload: "deterministic recorded mouse/keyboard action", MouseBounceWindow: TimeSpan.FromMilliseconds(10),
            MaximumRawInputBytes: maximumRawBytes, MaximumRawEvents: maximumEvents, MaximumSamples: maximumSamples);

    private static WindowsRawInputDeviceDescriptor Device(ulong handle, WindowsRawInputDeviceKind kind)
        => new(handle, kind, $@"\\?\HID#RECORDED#{handle:X}",
            new(kind == WindowsRawInputDeviceKind.Mouse ? 0u : 1u,
                MouseId: kind == WindowsRawInputDeviceKind.Mouse ? 9u : 0u,
                MouseButtonCount: kind == WindowsRawInputDeviceKind.Mouse ? 5u : 0u,
                NativeReportedMouseSampleRate: kind == WindowsRawInputDeviceKind.Mouse ? 1000u : 0u,
                KeyboardTotalKeyCount: kind == WindowsRawInputDeviceKind.Keyboard ? 104u : 0u));

    private static WindowsRawInputNativeEvent Mouse(ulong handle, ushort buttonFlags, int x, int y)
        => new(handle, WindowsRawInputPacketKind.Mouse, 48, 0, MouseFlags: 0x0000,
            MouseButtonsUnion: buttonFlags, MouseButtonFlags: buttonFlags, MouseX: x, MouseY: y);

    private static WindowsRawInputNativeEvent Key(ulong handle, ushort makeCode, ushort flags, bool isDown)
        => new(handle, WindowsRawInputPacketKind.Keyboard, 40, 1,
            KeyboardMakeCode: makeCode, KeyboardFlags: isDown ? flags : (ushort)(flags | 0x0001),
            VirtualKey: 0x41, KeyboardMessage: isDown ? 0x0100u : 0x0101u,
            KeyboardExtraInformation: 0x1234);

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class RecordedRuntime(WindowsRawInputDeviceDescriptor descriptor)
        : IWindowsRawInputSessionRuntime
    {
        private long counter;
        private Action<WindowsRawInputNativeEvent>? onInput;
        private Action<WindowsRawInputCaptureFault>? onFault;
        private int activeCaptureCount;
        internal int StartCaptureCount { get; private set; }
        internal int DisposeCount { get; private set; }
        internal int ActiveCaptureCount => Volatile.Read(ref activeCaptureCount);
        internal bool FailRegistration { get; init; }

        public ImmutableArray<WindowsRawInputDeviceDescriptor> EnumerateDevices(
            int maximumDevices, int maximumDevicePathCharacters)
            => [descriptor];

        public WindowsRawInputDeviceDescriptor QueryDevice(ulong deviceHandle, int maximumDevicePathCharacters)
            => deviceHandle == descriptor.DeviceHandle ? descriptor
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
            if (FailRegistration)
                throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.RegistrationConflict,
                    "The recorded runtime simulates an existing owner.");
            StartCaptureCount++;
            Interlocked.Increment(ref activeCaptureCount);
            onInput = input;
            onFault = fault;
            return new Lease(this);
        }

        internal void Emit(WindowsRawInputNativeEvent packet) => onInput!(packet);

        internal void Remove(ulong deviceHandle)
            => onFault!(new(WindowsRawInputCaptureFailureCode.DeviceRemoved,
                "Recorded removal.", DeviceHandle: deviceHandle));

        internal void ForceNextCounter(long nextValue)
            => Interlocked.Exchange(ref counter, nextValue - 1);

        internal void EndEvidenceForTest(WindowsRawInputCaptureSession session,
            WindowsExplicitInputEvidenceKind kind) => session.EndEvidence(kind);

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

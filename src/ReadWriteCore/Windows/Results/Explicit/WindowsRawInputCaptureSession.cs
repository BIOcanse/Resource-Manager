using System.Collections.Immutable;
using System.Text.Json;

namespace ResourceManager.ReadWrite.Windows.Results.Explicit;

/// <summary>
/// One caller-started Raw Input observation. Start owns a native registration lease;
/// Stop commits the complete recorded session into the supplied original result owner.
/// Dispose cancels and releases resources without submitting.
/// </summary>
public sealed class WindowsRawInputCaptureSession : IDisposable
{
    public const string FormulaVersion = "resource-manager.rawinput-input-v1";
    private const ushort RiKeyBreak = 0x0001;
    private const ushort RiKeyE0 = 0x0002;
    private const ushort RiKeyE1 = 0x0004;
    private const ushort KeyboardOverrunMakeCode = 0x00FF;
    private const ushort MouseMoveAbsolute = 0x0001;
    private const ushort MouseLeftDown = 0x0001;
    private const ushort MouseLeftUp = 0x0002;
    private const ushort MouseRightDown = 0x0004;
    private const ushort MouseRightUp = 0x0008;
    private const ushort MouseMiddleDown = 0x0010;
    private const ushort MouseMiddleUp = 0x0020;
    private const ushort MouseX1Down = 0x0040;
    private const ushort MouseX1Up = 0x0080;
    private const ushort MouseX2Down = 0x0100;
    private const ushort MouseX2Up = 0x0200;
    private readonly object gate = new();
    private readonly WindowsExplicitResultOwner owner;
    private readonly WindowsRawInputDeviceDescriptor device;
    private readonly WindowsRawInputCaptureOptions options;
    private readonly IWindowsRawInputSessionRuntime runtime;
    private readonly TimeProvider timeProvider;
    private readonly long qpcFrequency;
    private readonly long startQpc;
    private readonly long bounceWindowQpc;
    private readonly List<WindowsExplicitInputEvidence> evidence = [];
    private readonly Dictionary<WindowsExplicitInputEvidenceKind, ActiveEvidence> activeEvidence = [];
    private readonly HashSet<string> actionIds = new(StringComparer.Ordinal);
    private readonly List<WindowsExplicitInputSample> samples = [];
    private readonly List<WindowsExplicitRawInputEvent> rawEvents = [];
    private readonly Dictionary<uint, bool> keyStates = [];
    private readonly Dictionary<uint, bool> buttonStates = [];
    private IDisposable? registration;
    private long lastQpc;
    private (int X, int Y)? lastAbsoluteMousePosition;
    private WindowsRawInputCaptureFault? failure;
    private WindowsRawInputCaptureStopResult? stopResult;
    private bool stopping;
    private bool finished;

    private WindowsRawInputCaptureSession(WindowsExplicitResultOwner owner,
        WindowsRawInputDeviceDescriptor device, WindowsRawInputCaptureOptions options,
        IWindowsRawInputSessionRuntime runtime, TimeProvider timeProvider,
        long qpcFrequency, long startQpc, long bounceWindowQpc)
    {
        this.owner = owner;
        this.device = device;
        this.options = options;
        this.runtime = runtime;
        this.timeProvider = timeProvider;
        this.qpcFrequency = qpcFrequency;
        this.startQpc = startQpc;
        this.lastQpc = startQpc;
        this.bounceWindowQpc = bounceWindowQpc;
    }

    public WindowsRawInputDeviceDescriptor Device => device;
    public bool IsActive { get { lock (gate) return !stopping && !finished; } }
    /// <summary>First recorded fault, if any; the caller must still Stop, Cancel, or Dispose to release capture.</summary>
    public WindowsRawInputCaptureFault? Failure { get { lock (gate) return failure; } }

    /// <summary>Explicitly starts one selected, freshly queried hDevice session.</summary>
    /// <remarks>
    /// With the native runtime, the caller must coordinate all same-process registrations
    /// for the selected mouse/keyboard usage. Win32 cannot atomically check and register:
    /// a concurrent registrant can be displaced without detection. Hosts sharing another
    /// receiver should supply their coordinated <see cref="IWindowsRawInputSessionRuntime"/>.
    /// </remarks>
    public static WindowsRawInputCaptureSession Start(WindowsExplicitResultOwner owner,
        WindowsRawInputDeviceDescriptor selectedDevice, WindowsRawInputCaptureOptions options,
        IWindowsRawInputSessionRuntime? runtime = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(selectedDevice);
        ArgumentNullException.ThrowIfNull(options);
        var validated = options.Validate(owner.Limits.MaximumInputSamples);
        runtime ??= WindowsRawInputNativeRuntime.Instance;
        timeProvider ??= TimeProvider.System;

        if (selectedDevice.DeviceHandle == 0 || string.IsNullOrWhiteSpace(selectedDevice.DeviceInterfacePath))
            throw new ArgumentException("Select a typed Raw Input device descriptor returned by the device catalog.", nameof(selectedDevice));
        if (!Enum.IsDefined(selectedDevice.Kind))
            throw new ArgumentException("The selected device kind is not one of the closed Raw Input kinds.", nameof(selectedDevice));
        if (selectedDevice.DeviceInterfacePath.Length > validated.MaximumDevicePathCharacters)
            throw new ArgumentException("The selected device path exceeds the configured character bound.", nameof(selectedDevice));

        var rechecked = runtime.QueryDevice(selectedDevice.DeviceHandle, validated.MaximumDevicePathCharacters);
        if (rechecked != selectedDevice)
            throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                "The selected hDevice or its exact queried device information changed before Start.");

        var expectedType = selectedDevice.Kind == WindowsRawInputDeviceKind.Mouse ? 0u : 1u;
        if (selectedDevice.Information.NativeType != expectedType)
            throw new ArgumentException("The selected device kind does not match RID_DEVICE_INFO.", nameof(selectedDevice));

        var frequency = runtime.QueryPerformanceFrequency();
        if (frequency <= 0)
            throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.QpcUnavailable,
                "QueryPerformanceFrequency returned an invalid frequency.");
        if (!runtime.TryQueryPerformanceCounter(out var start) || start < 0)
            throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.QpcUnavailable,
                "The starting QueryPerformanceCounter value is unavailable.");

        var bounceWindowQpc = selectedDevice.Kind == WindowsRawInputDeviceKind.Mouse
            ? ConvertBounceWindow(validated.MouseBounceWindow ?? TimeSpan.FromMilliseconds(50), frequency)
            : 0;
        var session = new WindowsRawInputCaptureSession(owner, selectedDevice, validated,
            runtime, timeProvider, frequency, start, bounceWindowQpc);
        session.registration = runtime.StartCapture(selectedDevice.Kind, selectedDevice.DeviceHandle,
            validated.MaximumRawInputBytes, session.OnNativeInput, session.OnNativeFault)
            ?? throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.RegistrationFailure,
                "The Raw Input runtime did not return a registration lease.");
        return session;
    }

    public void BeginEvidence(WindowsExplicitInputEvidenceKind kind, string actionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionId);
        lock (gate)
        {
            EnsureActive();
            if (!AllowedEvidence().Contains(kind))
                throw new ArgumentException("The evidence kind does not match the selected Raw Input device.", nameof(kind));
            if (activeEvidence.ContainsKey(kind) || evidence.Any(item => item.Kind == kind))
                throw new InvalidOperationException("Each Raw Input evidence kind may have one explicit action window per session.");
            if (!actionIds.Add(actionId))
                throw new ArgumentException("Raw Input action IDs must be unique within a session.", nameof(actionId));
            var qpc = ReadCounterLocked();
            activeEvidence.Add(kind, new(actionId, qpc));
        }
    }

    public void EndEvidence(WindowsExplicitInputEvidenceKind kind)
    {
        lock (gate)
        {
            EnsureActive();
            if (!activeEvidence.TryGetValue(kind, out var active))
                throw new InvalidOperationException("BeginEvidence must precede EndEvidence for this action.");
            var qpc = ReadCounterLocked();
            if (qpc <= active.StartQpc)
            {
                SetFailure(WindowsRawInputCaptureFailureCode.QpcWentBackwards,
                    "The QPC action window did not advance.");
                throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.QpcWentBackwards,
                    "The QPC action window did not advance.");
            }
            evidence.Add(new(kind, active.ActionId, active.StartQpc, qpc));
            activeEvidence.Remove(kind);
        }
    }

    /// <summary>Stops input capture and atomically submits a complete result, or leaves current unchanged.</summary>
    public WindowsRawInputCaptureStopResult Stop(CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Cancel();
        lock (gate)
        {
            if (stopResult is not null) return stopResult;
            if (stopping || finished) return Result(WindowsRawInputCaptureStopStatus.Incomplete,
                null, WindowsRawInputCaptureFailureCode.EvidenceIncomplete, "The capture session is already stopping.");
            stopping = true;
        }

        try { registration?.Dispose(); }
        catch (WindowsRawInputCaptureException exception)
        {
            lock (gate) SetFailure(exception.Code, exception.Message, exception.NativeError);
        }
        catch (Exception exception)
        {
            lock (gate) SetFailure(WindowsRawInputCaptureFailureCode.RegistrationFailure, exception.Message);
        }
        registration = null;

        ImmutableArray<WindowsExplicitInputEvidence> completedEvidence;
        ImmutableArray<WindowsExplicitInputSample> completedSamples;
        ImmutableArray<WindowsExplicitRawInputEvent> completedRawEvents;
        long endQpc;
        lock (gate)
        {
            try { endQpc = ReadCounterLocked(); }
            catch (WindowsRawInputCaptureException) { endQpc = lastQpc; }
            if (activeEvidence.Count != 0)
                SetFailure(WindowsRawInputCaptureFailureCode.EvidenceIncomplete,
                    "Every explicit test action must be ended before Stop.");
            if (endQpc <= startQpc)
                SetFailure(WindowsRawInputCaptureFailureCode.QpcWentBackwards,
                    "The final QPC value did not advance beyond session start.");
            completedEvidence = evidence.OrderBy(static item => item.StartQpc).ToImmutableArray();
            completedSamples = samples.ToImmutableArray();
            completedRawEvents = rawEvents.ToImmutableArray();
        }

        var eventCount = completedRawEvents.Length;
        var sampleCount = completedSamples.Length;
        if (cancellationToken.IsCancellationRequested)
            return Finish(WindowsRawInputCaptureStopStatus.Cancelled, null, null, null, eventCount, sampleCount);
        if (failure is { } capturedFailure)
            return Finish(WindowsRawInputCaptureStopStatus.Incomplete, null,
                capturedFailure.Code, capturedFailure.Message, eventCount, sampleCount);
        if (completedEvidence.IsEmpty)
            return Finish(WindowsRawInputCaptureStopStatus.Incomplete, null,
                WindowsRawInputCaptureFailureCode.EvidenceRequired,
                "At least one explicit evidence action window is required.", eventCount, sampleCount);

        var inputSession = new WindowsExplicitInputSession(options.SessionId, options.ScopeIdentity,
            device.DeviceHandle, qpcFrequency, startQpc, endQpc, FormulaVersion, completedEvidence,
            bounceWindowQpc, completedSamples, completedRawEvents, options.MaximumRawInputBytes);
        var metadata = new WindowsExplicitResultMetadata(owner.DeviceIdentity,
            WindowsExplicitResultScope.RawInputDevice, options.ScopeIdentity, options.SessionId,
            options.SourceId, options.SourceVersion, timeProvider.GetUtcNow(), FormulaVersion,
            options.Workload, BuildSourceMetadata());
        var kind = device.Kind == WindowsRawInputDeviceKind.Mouse
            ? WindowsExplicitResultKind.MouseInputManual : WindowsExplicitResultKind.KeyboardInputManual;
        try
        {
            var current = owner.Submit(new(WindowsExplicitResultOwner.SupportedSchemaVersion, kind,
                metadata, new(), inputSession), cancellationToken);
            return Finish(WindowsRawInputCaptureStopStatus.Committed, current, null, null, eventCount, sampleCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Finish(WindowsRawInputCaptureStopStatus.Cancelled, null, null, null, eventCount, sampleCount);
        }
        catch (Exception exception)
        {
            return Finish(WindowsRawInputCaptureStopStatus.Incomplete, null,
                WindowsRawInputCaptureFailureCode.SubmissionRejected,
                $"The complete Raw Input session was rejected atomically: {exception.Message}",
                eventCount, sampleCount);
        }
    }

    /// <summary>Cancels without submitting, then releases the registration/window lease.</summary>
    public WindowsRawInputCaptureStopResult Cancel()
    {
        lock (gate)
        {
            if (stopResult is not null) return stopResult;
            if (finished) return Result(WindowsRawInputCaptureStopStatus.Cancelled, null, null, null);
            if (stopping)
                return Result(WindowsRawInputCaptureStopStatus.Incomplete, null,
                    WindowsRawInputCaptureFailureCode.EvidenceIncomplete,
                    "Stop is already in progress; cancel it through its cancellation token.");
            stopping = true;
        }

        WindowsRawInputCaptureFault? cleanupFailure = null;
        try { registration?.Dispose(); }
        catch (WindowsRawInputCaptureException exception)
        {
            cleanupFailure = new(exception.Code, exception.Message, exception.NativeError);
        }
        catch (Exception exception)
        {
            cleanupFailure = new(WindowsRawInputCaptureFailureCode.RegistrationFailure, exception.Message);
        }
        registration = null;

        lock (gate)
        {
            finished = true;
            var rawCount = rawEvents.Count;
            var sampleCount = samples.Count;
            var failureValue = cleanupFailure ?? failure;
            samples.Clear();
            rawEvents.Clear();
            activeEvidence.Clear();
            return Finish(WindowsRawInputCaptureStopStatus.Cancelled, null,
                failureValue?.Code, failureValue?.Message, rawCount, sampleCount);
        }
    }

    public void Dispose() => _ = Cancel();

    private void OnNativeInput(WindowsRawInputNativeEvent packet)
    {
        lock (gate)
        {
            if (stopping || finished || packet.DeviceHandle != device.DeviceHandle) return;
            if (failure is not null) return;
            long qpc;
            try { qpc = ReadCounterLocked(); }
            catch (WindowsRawInputCaptureException) { return; }

            if (packet.InputSizeBytes > options.MaximumRawInputBytes)
            {
                SetFailure(WindowsRawInputCaptureFailureCode.PacketTooLarge,
                    "The selected WM_INPUT packet exceeded the configured raw-byte bound.");
                return;
            }
            if (packet.Kind != (device.Kind == WindowsRawInputDeviceKind.Mouse
                    ? WindowsRawInputPacketKind.Mouse : WindowsRawInputPacketKind.Keyboard)
                || packet.InputSizeBytes == 0 || packet.MessageInputCode > 1)
            {
                SetFailure(WindowsRawInputCaptureFailureCode.InvalidRawPacket,
                    "The selected WM_INPUT packet did not match the expected bounded Raw Input shape.");
                return;
            }
            if (rawEvents.Count >= options.MaximumRawEvents)
            {
                SetFailure(WindowsRawInputCaptureFailureCode.EventLimitExceeded,
                    "The explicit Raw Input event limit was reached; the session is incomplete.");
                return;
            }

            var purposes = ActivePurposes();
            rawEvents.Add(ToRawEvent(packet, qpc, purposes));
            if (packet.Kind == WindowsRawInputPacketKind.Mouse)
                RecordMouseSamples(packet, qpc, purposes);
            else
                RecordKeyboardSample(packet, qpc, purposes);
        }
    }

    private void RecordMouseSamples(WindowsRawInputNativeEvent packet, long qpc,
        ImmutableArray<WindowsExplicitInputEvidenceKind> purposes)
    {
        var additions = new List<WindowsExplicitInputSample>();
        if (activeEvidence.ContainsKey(WindowsExplicitInputEvidenceKind.MouseReportObservation))
            additions.Add(new(device.DeviceHandle, qpc, WindowsExplicitInputSampleKind.MouseReport,
                [WindowsExplicitInputEvidenceKind.MouseReportObservation], 0, 0, 0, null));

        if (activeEvidence.ContainsKey(WindowsExplicitInputEvidenceKind.MouseTrajectoryTest))
        {
            var deltaX = packet.MouseX;
            var deltaY = packet.MouseY;
            if ((packet.MouseFlags & MouseMoveAbsolute) != 0)
            {
                if (lastAbsoluteMousePosition is { } previous)
                {
                    deltaX = checked(packet.MouseX - previous.X);
                    deltaY = checked(packet.MouseY - previous.Y);
                }
                else
                {
                    deltaX = 0;
                    deltaY = 0;
                }
                lastAbsoluteMousePosition = (packet.MouseX, packet.MouseY);
            }
            if (deltaX != 0 || deltaY != 0)
                additions.Add(new(device.DeviceHandle, qpc, WindowsExplicitInputSampleKind.MouseMovement,
                    [WindowsExplicitInputEvidenceKind.MouseTrajectoryTest], 0, deltaX, deltaY, null));
        }

        if (activeEvidence.ContainsKey(WindowsExplicitInputEvidenceKind.MouseButtonTransitionTest))
        {
            foreach (var transition in MouseTransitions(packet.MouseButtonFlags))
            {
                if (buttonStates.TryGetValue(transition.Code, out var previous) && previous == transition.IsDown)
                    continue;
                buttonStates[transition.Code] = transition.IsDown;
                additions.Add(new(device.DeviceHandle, qpc, WindowsExplicitInputSampleKind.MouseButtonTransition,
                    [WindowsExplicitInputEvidenceKind.MouseButtonTransitionTest],
                    transition.Code, 0, 0, transition.IsDown));
            }
        }
        AddSamples(additions);
    }

    private void RecordKeyboardSample(WindowsRawInputNativeEvent packet, long qpc,
        ImmutableArray<WindowsExplicitInputEvidenceKind> purposes)
    {
        if (packet.KeyboardReserved != 0)
        {
            SetFailure(WindowsRawInputCaptureFailureCode.InvalidRawPacket,
                "RAWKEYBOARD.Reserved was nonzero.");
            return;
        }
        if (packet.KeyboardMakeCode == KeyboardOverrunMakeCode)
        {
            SetFailure(WindowsRawInputCaptureFailureCode.KeyboardInputOverrun,
                "Windows reported KEYBOARD_OVERRUN_MAKE_CODE; this session is incomplete.");
            return;
        }
        var extendedFlags = (ushort)(packet.KeyboardFlags & (RiKeyE0 | RiKeyE1));
        if (extendedFlags == (RiKeyE0 | RiKeyE1) || packet.KeyboardMakeCode > byte.MaxValue)
        {
            SetFailure(WindowsRawInputCaptureFailureCode.InvalidRawPacket,
                "RAWKEYBOARD carried an unsupported scan-code prefix combination.");
            return;
        }
        if (packet.KeyboardMakeCode == 0) return;

        var prefix = (extendedFlags & RiKeyE0) != 0 ? 0xE0u
            : (extendedFlags & RiKeyE1) != 0 ? 0xE1u : 0u;
        var scanCode = ((uint)packet.KeyboardMakeCode & 0xFF) | (prefix << 8);
        var isDown = (packet.KeyboardFlags & RiKeyBreak) == 0;
        if (keyStates.TryGetValue(scanCode, out var previous) && previous == isDown) return;
        keyStates[scanCode] = isDown;

        var keyPurposes = purposes.Where(static purpose => purpose is
            WindowsExplicitInputEvidenceKind.KeyboardTransitionTest
            or WindowsExplicitInputEvidenceKind.KeyboardCombinationTest).ToImmutableArray();
        if (keyPurposes.IsEmpty) return;
        AddSamples([new(device.DeviceHandle, qpc, WindowsExplicitInputSampleKind.KeyTransition,
            keyPurposes, scanCode, 0, 0, isDown)]);
    }

    private void AddSamples(List<WindowsExplicitInputSample> additions)
    {
        if (additions.Count == 0) return;
        if (samples.Count + additions.Count > options.MaximumSamples)
        {
            SetFailure(WindowsRawInputCaptureFailureCode.SampleLimitExceeded,
                "The explicit derived sample limit was reached; the session is incomplete.");
            return;
        }
        samples.AddRange(additions);
    }

    private static IEnumerable<(uint Code, bool IsDown)> MouseTransitions(ushort flags)
    {
        if ((flags & MouseLeftDown) != 0) yield return (1, true);
        if ((flags & MouseLeftUp) != 0) yield return (1, false);
        if ((flags & MouseRightDown) != 0) yield return (2, true);
        if ((flags & MouseRightUp) != 0) yield return (2, false);
        if ((flags & MouseMiddleDown) != 0) yield return (3, true);
        if ((flags & MouseMiddleUp) != 0) yield return (3, false);
        if ((flags & MouseX1Down) != 0) yield return (4, true);
        if ((flags & MouseX1Up) != 0) yield return (4, false);
        if ((flags & MouseX2Down) != 0) yield return (5, true);
        if ((flags & MouseX2Up) != 0) yield return (5, false);
    }

    private static WindowsExplicitRawInputEvent ToRawEvent(WindowsRawInputNativeEvent packet,
        long qpc, ImmutableArray<WindowsExplicitInputEvidenceKind> purposes)
        => new(packet.DeviceHandle, qpc,
            packet.Kind == WindowsRawInputPacketKind.Mouse
                ? WindowsExplicitRawInputPacketKind.Mouse : WindowsExplicitRawInputPacketKind.Keyboard,
            packet.InputSizeBytes, packet.MessageInputCode, purposes,
            packet.MouseFlags, packet.MouseButtonsUnion, packet.MouseButtonFlags,
            packet.MouseButtonData, packet.MouseRawButtons, packet.MouseX, packet.MouseY,
            packet.MouseExtraInformation, packet.KeyboardMakeCode, packet.KeyboardFlags,
            packet.KeyboardReserved, packet.VirtualKey, packet.KeyboardMessage,
            packet.KeyboardExtraInformation);

    private ImmutableArray<WindowsExplicitInputEvidenceKind> ActivePurposes()
        => activeEvidence.Keys.Order().ToImmutableArray();

    private ImmutableArray<WindowsExplicitInputEvidenceKind> AllowedEvidence()
        => device.Kind == WindowsRawInputDeviceKind.Mouse
            ? [WindowsExplicitInputEvidenceKind.MouseReportObservation,
                WindowsExplicitInputEvidenceKind.MouseTrajectoryTest,
                WindowsExplicitInputEvidenceKind.MouseButtonTransitionTest]
            : [WindowsExplicitInputEvidenceKind.KeyboardTransitionTest,
                WindowsExplicitInputEvidenceKind.KeyboardCombinationTest];

    private long ReadCounterLocked()
    {
        if (!runtime.TryQueryPerformanceCounter(out var counter) || counter < 0)
        {
            SetFailure(WindowsRawInputCaptureFailureCode.QpcUnavailable,
                "QueryPerformanceCounter failed during the explicit input session.");
            throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.QpcUnavailable,
                "QueryPerformanceCounter failed during the explicit input session.");
        }
        if (counter < lastQpc)
        {
            SetFailure(WindowsRawInputCaptureFailureCode.QpcWentBackwards,
                "QueryPerformanceCounter moved backwards during the explicit input session.");
            throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.QpcWentBackwards,
                "QueryPerformanceCounter moved backwards during the explicit input session.");
        }
        lastQpc = counter;
        return counter;
    }

    private void OnNativeFault(WindowsRawInputCaptureFault fault)
    {
        if (fault.DeviceHandle != 0 && fault.DeviceHandle != device.DeviceHandle) return;
        lock (gate)
        {
            if (finished) return;
            SetFailure(fault.Code, fault.Message, fault.NativeError);
        }
    }

    private void SetFailure(WindowsRawInputCaptureFailureCode code, string message, int nativeError = 0)
        => failure ??= new(code, message, nativeError, device.DeviceHandle);

    private void EnsureActive()
    {
        if (finished || stopping)
            throw new InvalidOperationException("The Raw Input session is not active.");
        if (failure is { } fault)
            throw new WindowsRawInputCaptureException(fault.Code, fault.Message, fault.NativeError);
    }

    private JsonElement BuildSourceMetadata()
        => JsonSerializer.SerializeToElement(new
        {
            runtimeKind = runtime is WindowsRawInputNativeRuntime
                ? "win32-native-raw-input" : "injected-recorded-or-custom-runtime",
            nativeApi = runtime is WindowsRawInputNativeRuntime
                ? "RegisterRawInputDevices/WM_INPUT/GetRawInputData/GetRawInputDeviceInfoW"
                : "caller-supplied IWindowsRawInputSessionRuntime",
            deviceInterfacePath = device.DeviceInterfacePath,
            deviceHandleScope = "process-local hDevice; valid only for this recorded session",
            deviceInformation = device.Information,
            eventTimebase = runtime is WindowsRawInputNativeRuntime
                ? "QueryPerformanceCounter read on the message thread after the selected WM_INPUT packet is retrieved"
                : "runtime-provided QueryPerformanceCounter read when the recorded/custom event is delivered",
            mouseReportRateAndJitter = "host-delivery arrival observations including OS/USB scheduling; not hardware scan rate",
            keyboardScanCodeEncoding = "MakeCode low byte | E0 prefix 0xE000 | E1 prefix 0xE100",
            repeatedKeyMake = "retained in rawEvents; not counted as another state transition",
            undetectedLoss = "Raw Input supplies no per-device sequence number here; undetected OS/driver packet loss cannot be ruled out",
            actionWindowPolicy = "only samples within explicitly bracketed evidence windows feed measurements",
            maximumRawEvents = options.MaximumRawEvents,
            maximumSamples = options.MaximumSamples,
            maximumRawInputBytes = options.MaximumRawInputBytes
        });

    private static long ConvertBounceWindow(TimeSpan duration, long frequency)
    {
        var ticks = duration.TotalSeconds * frequency;
        if (!double.IsFinite(ticks) || ticks <= 0 || ticks > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(duration));
        return checked((long)Math.Ceiling(ticks));
    }

    private WindowsRawInputCaptureStopResult Finish(WindowsRawInputCaptureStopStatus status,
        WindowsExplicitResultCurrent? current, WindowsRawInputCaptureFailureCode? failureCode,
        string? failureMessage, int eventCount, int sampleCount)
    {
        lock (gate)
        {
            samples.Clear();
            rawEvents.Clear();
            activeEvidence.Clear();
            finished = true;
            stopping = false;
            stopResult = new(status, current, failureCode, failureMessage, eventCount, sampleCount);
            return stopResult;
        }
    }

    private WindowsRawInputCaptureStopResult Result(WindowsRawInputCaptureStopStatus status,
        WindowsExplicitResultCurrent? current, WindowsRawInputCaptureFailureCode? failureCode,
        string? failureMessage)
        => stopResult ?? new(status, current, failureCode, failureMessage, rawEvents.Count, samples.Count);

    private sealed record ActiveEvidence(string ActionId, long StartQpc);
}

using System.Collections.Immutable;

namespace ResourceManager.ReadWrite.Windows.Results.Explicit;

public enum WindowsRawInputDeviceKind
{
    Mouse,
    Keyboard
}

/// <summary>Exact RID_DEVICE_INFO payload for a raw input mouse or keyboard.</summary>
public sealed record WindowsRawInputDeviceInformation(
    uint NativeType,
    uint MouseId = 0,
    uint MouseButtonCount = 0,
    uint NativeReportedMouseSampleRate = 0,
    bool MouseHasHorizontalWheel = false,
    uint KeyboardType = 0,
    uint KeyboardSubtype = 0,
    uint KeyboardMode = 0,
    uint KeyboardFunctionKeyCount = 0,
    uint KeyboardIndicatorCount = 0,
    uint KeyboardTotalKeyCount = 0,
    uint HidVendorId = 0,
    uint HidProductId = 0,
    uint HidVersionNumber = 0,
    ushort HidUsagePage = 0,
    ushort HidUsage = 0);

/// <summary>
/// A device descriptor returned by GetRawInputDeviceList and exact GetRawInputDeviceInfoW
/// queries. DeviceHandle is process/session-local and must be revalidated at Start.
/// </summary>
public sealed record WindowsRawInputDeviceDescriptor(
    ulong DeviceHandle,
    WindowsRawInputDeviceKind Kind,
    string DeviceInterfacePath,
    WindowsRawInputDeviceInformation Information);

public static class WindowsRawInputDeviceCatalog
{
    public static ImmutableArray<WindowsRawInputDeviceDescriptor> Enumerate(
        IWindowsRawInputSessionRuntime? runtime = null,
        int maximumDevices = 512,
        int maximumDevicePathCharacters = 2048)
    {
        if (maximumDevices <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDevices));
        if (maximumDevicePathCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(maximumDevicePathCharacters));
        var devices = (runtime ?? WindowsRawInputNativeRuntime.Instance)
            .EnumerateDevices(maximumDevices, maximumDevicePathCharacters);
        if (devices.IsDefault || devices.Length > maximumDevices)
            throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                "Raw Input enumeration returned an uninitialized or over-limit device list.");

        var handles = new HashSet<ulong>();
        foreach (var device in devices)
        {
            if (device is null || device.Information is null
                || !Enum.IsDefined(device.Kind) || device.DeviceHandle == 0
                || string.IsNullOrWhiteSpace(device.DeviceInterfacePath)
                || device.DeviceInterfacePath.Length > maximumDevicePathCharacters
                || !handles.Add(device.DeviceHandle))
                throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                    "Raw Input enumeration returned a duplicate, malformed, or over-limit device descriptor.");
            var expectedType = device.Kind == WindowsRawInputDeviceKind.Mouse ? 0u : 1u;
            if (device.Information.NativeType != expectedType)
                throw new WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode.DeviceUnavailable,
                    "Raw Input device kind does not match its RID_DEVICE_INFO type.");
        }
        return devices;
    }
}

/// <summary>Finite per-session limits; callers can replace defaults before explicit Start.</summary>
public sealed record WindowsRawInputCaptureOptions(
    string ScopeIdentity,
    string SessionId,
    string SourceId = "resource-manager.win32-raw-input",
    string SourceVersion = "1",
    string Workload = "explicit user raw input observation",
    TimeSpan? MouseBounceWindow = null,
    int MaximumDeviceCount = 512,
    int MaximumDevicePathCharacters = 2048,
    uint MaximumRawInputBytes = 4096,
    int MaximumRawEvents = 4096,
    int MaximumSamples = 4096)
{
    internal WindowsRawInputCaptureOptions Validate(int ownerSampleLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ScopeIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(SourceVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(Workload);
        if (MaximumDeviceCount <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumDeviceCount));
        if (MaximumDevicePathCharacters <= 0) throw new ArgumentOutOfRangeException(nameof(MaximumDevicePathCharacters));
        if (MaximumRawInputBytes is < 40 or > 1_048_576) throw new ArgumentOutOfRangeException(nameof(MaximumRawInputBytes));
        if (MaximumRawEvents <= 0 || MaximumRawEvents > ownerSampleLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumRawEvents), "Raw event count must be positive and within the existing explicit owner sample limit.");
        if (MaximumSamples <= 0 || MaximumSamples > ownerSampleLimit)
            throw new ArgumentOutOfRangeException(nameof(MaximumSamples), "Derived sample count must be positive and within the existing explicit owner sample limit.");
        if (MouseBounceWindow is { } bounce && (bounce <= TimeSpan.Zero || bounce > TimeSpan.FromMinutes(1)))
            throw new ArgumentOutOfRangeException(nameof(MouseBounceWindow));
        return this;
    }
}

public enum WindowsRawInputCaptureFailureCode
{
    RegistrationConflict,
    RegistrationFailure,
    RegistrationOwnershipLost,
    DeviceUnavailable,
    DeviceRemoved,
    NativeReadFailure,
    PacketTooLarge,
    EventLimitExceeded,
    SampleLimitExceeded,
    QpcUnavailable,
    QpcWentBackwards,
    KeyboardInputOverrun,
    InvalidRawPacket,
    EvidenceIncomplete,
    EvidenceRequired,
    SubmissionRejected
}

public sealed record WindowsRawInputCaptureFault(
    WindowsRawInputCaptureFailureCode Code,
    string Message,
    int NativeError = 0,
    ulong DeviceHandle = 0);

public enum WindowsRawInputPacketKind
{
    Mouse,
    Keyboard,
    Other
}

/// <summary>Decoded exact RAWMOUSE/RAWKEYBOARD fields copied from one WM_INPUT packet.</summary>
public sealed record WindowsRawInputNativeEvent(
    ulong DeviceHandle,
    WindowsRawInputPacketKind Kind,
    uint InputSizeBytes,
    uint MessageInputCode,
    ushort MouseFlags = 0,
    uint MouseButtonsUnion = 0,
    ushort MouseButtonFlags = 0,
    ushort MouseButtonData = 0,
    uint MouseRawButtons = 0,
    int MouseX = 0,
    int MouseY = 0,
    uint MouseExtraInformation = 0,
    ushort KeyboardMakeCode = 0,
    ushort KeyboardFlags = 0,
    ushort KeyboardReserved = 0,
    ushort VirtualKey = 0,
    uint KeyboardMessage = 0,
    uint KeyboardExtraInformation = 0);

/// <summary>
/// Injectable session runtime. The production implementation performs Win32 Raw Input;
/// recorded implementations exist for deterministic tests and package consumers.
/// </summary>
public interface IWindowsRawInputSessionRuntime
{
    ImmutableArray<WindowsRawInputDeviceDescriptor> EnumerateDevices(
        int maximumDevices, int maximumDevicePathCharacters);
    WindowsRawInputDeviceDescriptor QueryDevice(ulong deviceHandle, int maximumDevicePathCharacters);
    long QueryPerformanceFrequency();
    bool TryQueryPerformanceCounter(out long counter);
    /// <summary>
    /// Starts capture for one explicitly selected device. Disposing the returned lease is
    /// a callback barrier: after Dispose returns, neither callback may be invoked again.
    /// </summary>
    /// <remarks>The native implementation requires caller coordination with every
    /// same-process registrant for this usage class. Win32 has no atomic check-and-register
    /// operation; observed ownership checks cannot prove that no concurrent receiver was displaced.</remarks>
    IDisposable StartCapture(WindowsRawInputDeviceKind kind, ulong selectedDeviceHandle,
        uint maximumRawInputBytes, Action<WindowsRawInputNativeEvent> onInput,
        Action<WindowsRawInputCaptureFault> onFault);
}

public sealed class WindowsRawInputCaptureException : InvalidOperationException
{
    public WindowsRawInputCaptureFailureCode Code { get; }
    public int NativeError { get; }

    public WindowsRawInputCaptureException(WindowsRawInputCaptureFailureCode code,
        string message, int nativeError = 0, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        NativeError = nativeError;
    }
}

public enum WindowsRawInputCaptureStopStatus
{
    Committed,
    Cancelled,
    Incomplete
}

public sealed record WindowsRawInputCaptureStopResult(
    WindowsRawInputCaptureStopStatus Status,
    ResourceManager.ReadWrite.Windows.Results.Explicit.WindowsExplicitResultCurrent? Current,
    WindowsRawInputCaptureFailureCode? FailureCode,
    string? FailureMessage,
    int ObservedRawEventCount,
    int RecordedSampleCount);

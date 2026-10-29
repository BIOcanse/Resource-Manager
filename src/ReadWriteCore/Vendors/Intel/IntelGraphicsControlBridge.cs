namespace ResourceManager.ReadWrite.Vendors.Intel;

/// <summary>Finite Intel integrated GPU control operations over the shared System32 IGCL loader.</summary>
public sealed class IntelGraphicsControlBridge : IIntelGraphicsControlHardware
{
    // Control keeps API 1.1 and requests Level Zero (flags 1), as Intel's overclock sample does.
    private readonly Func<IntelGclNativeSession?> open;
    private readonly Func<bool> driverAvailable;
    internal const string ReleaseFailureKey = "IntelGclReleaseFailure";
    public IntelGraphicsControlBridge() : this(() => IntelGclNativeSession.Create(1, (1U << 16) | 1U),
        () => IntelGclNativeSession.IsDriverAvailable) { }
    internal IntelGraphicsControlBridge(IntelGclNativeFunctions functions)
        : this(() => IntelGclNativeSession.Open(functions, 1, (1U << 16) | 1U), () => true) { }
    internal IntelGraphicsControlBridge(Func<IntelGclNativeSession?> open, Func<bool> driverAvailable)
        => (this.open, this.driverAvailable) = (open, driverAvailable);

    public bool IsDriverAvailable => driverAvailable();

    public IntelGraphicsControlProbeResult Probe(ulong? adapterKey, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (adapterKey is null or 0) return new(IntelGraphicsControlProbe.TargetMissing);
        var session = open();
        if (session is null) return new(IntelGraphicsControlProbe.DriverMissing);
        return Complete(session, () => new IntelGraphicsControlProbeResult(
            Find(session, adapterKey.Value, token) is null ? IntelGraphicsControlProbe.TargetMissing
            : !session.SupportsControl ? IntelGraphicsControlProbe.NotSupported : IntelGraphicsControlProbe.Ready),
            (result, release) => result with { ReleaseErrorCode = release });
    }

    public IntelGraphicsControlRead ReadFrequencyOffsetMhz(ulong? adapterKey, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (adapterKey is null or 0) return new(null, IntelGraphicsControlUnreadable.TargetMissing);
        var session = open();
        if (session is null) return new(null, IntelGraphicsControlUnreadable.DriverMissing);
        return Complete<IntelGraphicsControlRead>(session, () =>
        {
            if (Find(session, adapterKey.Value, token) is not { } device) return new IntelGraphicsControlRead(null, IntelGraphicsControlUnreadable.TargetMissing);
            token.ThrowIfCancellationRequested();
            return session.ReadFrequencyOffset(device) is { } value ? new(value) : new(null, IntelGraphicsControlUnreadable.ReadingMissing);
        }, (result, release) => result with { ReleaseErrorCode = release });
    }

    public IntelGraphicsControlWrite WriteFrequencyOffsetMhz(ulong? adapterKey, double offsetMhz, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (adapterKey is null or 0) return new(-1, null, IntelGraphicsControlWriteFailure.TargetMissing);
        var session = open();
        if (session is null) return new(-1, null, IntelGraphicsControlWriteFailure.DriverMissing);
        return Complete(session, () => Write(session, adapterKey.Value, offsetMhz, token),
            (result, release) => result with { ReleaseErrorCode = release });
    }

    private static T Complete<T>(IntelGclNativeSession session, Func<T> work, Func<T, int?, T> receipt)
    {
        T result;
        try { result = work(); }
        catch (Exception error)
        {
            if (Close(session) is { } release)
                error.Data[ReleaseFailureKey] = new System.Runtime.InteropServices.ExternalException("IGCL could not close the API session.", release);
            throw;
        }
        return receipt(result, Close(session));
    }

    private static int? Close(IntelGclNativeSession session)
    {
        try { return session.Close(); }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return error.HResult; }
    }

    private static IntelGraphicsControlWrite Write(IntelGclNativeSession session, ulong adapterKey, double offsetMhz, CancellationToken token)
    {
        if (Find(session, adapterKey, token) is not { } device) return new(-1, null, IntelGraphicsControlWriteFailure.TargetMissing);
        token.ThrowIfCancellationRequested();
        var waiver = session.AcceptOverclockWaiver(device);
        if (waiver != 0) return new(waiver, null, IntelGraphicsControlWriteFailure.WaiverRejected);
        token.ThrowIfCancellationRequested();
        var result = session.WriteFrequencyOffset(device, offsetMhz);
        // Once set has run, complete the receipt even if the caller cancels.
        return result != 0 ? new(result, null, IntelGraphicsControlWriteFailure.DriverRejected)
            : new(result, session.ReadFrequencyOffset(device));
    }

    private static IntPtr? Find(IntelGclNativeSession session, ulong adapterKey, CancellationToken token)
        => session.TryGetDevices(out var devices, token)
            ? SelectIntegratedDevice(adapterKey, devices.Select(device => { token.ThrowIfCancellationRequested(); return session.ReadControlIdentity(device); }))
            : null;

    internal static IntPtr? SelectIntegratedDevice(ulong adapterKey, IEnumerable<DeviceIdentity?> identities)
    {
        if (adapterKey == 0) return null;
        IntPtr? selected = null;
        foreach (var identity in identities)
        {
            if (identity is not { Handle: var handle, AdapterKey: var key, VendorId: 0x8086,
                    DeviceType: 1, Properties: var properties }
                || handle == IntPtr.Zero || key != adapterKey || (properties & 1) == 0) continue;
            if (selected is not null) return null;
            selected = handle;
        }
        return selected;
    }

    internal readonly record struct DeviceIdentity(IntPtr Handle, ulong AdapterKey, uint VendorId, int DeviceType, uint Properties);
}

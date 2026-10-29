namespace ResourceManager.ReadWrite.Vendors.Intel;

/// <summary>Finite Intel integrated GPU control operations over the shared System32 IGCL loader.</summary>
public sealed class IntelGraphicsControlBridge : IIntelGraphicsControlHardware
{
    // Control keeps API 1.1 and requests Level Zero (flags 1), as Intel's overclock sample does.
    private readonly Func<IntelGclNativeSession?> open;
    public IntelGraphicsControlBridge() : this(() => IntelGclNativeSession.Create(1, (1U << 16) | 1U)) { }
    internal IntelGraphicsControlBridge(IntelGclNativeFunctions functions)
        : this(() => IntelGclNativeSession.Open(functions, 1, (1U << 16) | 1U)) { }
    private IntelGraphicsControlBridge(Func<IntelGclNativeSession?> open) => this.open = open;

    public IntelGraphicsControlProbe Probe(ulong? adapterKey, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (adapterKey is null or 0) return IntelGraphicsControlProbe.TargetMissing;
        using var session = open();
        if (session is null) return IntelGraphicsControlProbe.DriverMissing;
        return Find(session, adapterKey.Value, token) is null ? IntelGraphicsControlProbe.TargetMissing : IntelGraphicsControlProbe.Ready;
    }

    public IntelGraphicsControlRead ReadFrequencyOffsetMhz(ulong? adapterKey, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (adapterKey is null or 0) return new(null, IntelGraphicsControlUnreadable.TargetMissing);
        using var session = open();
        if (session is null) return new(null, IntelGraphicsControlUnreadable.DriverMissing);
        if (Find(session, adapterKey.Value, token) is not { } device) return new(null, IntelGraphicsControlUnreadable.TargetMissing);
        token.ThrowIfCancellationRequested();
        return session.ReadFrequencyOffset(device) is { } value ? new(value) : new(null, IntelGraphicsControlUnreadable.ReadingMissing);
    }

    public IntelGraphicsControlWrite WriteFrequencyOffsetMhz(ulong? adapterKey, double offsetMhz, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (adapterKey is null or 0) return new(-1, null, IntelGraphicsControlWriteFailure.TargetMissing);
        var session = open();
        if (session is null) return new(-1, null, IntelGraphicsControlWriteFailure.DriverMissing);
        try
        {
            var result = Write(session, adapterKey.Value, offsetMhz, token);
            return result with { ReleaseErrorCode = session.Close() };
        }
        finally
        {
            // Completed work already closed the session. If work threw, retain the normal
            // exceptional cleanup path. Neither path attempts ctlClose more than once.
            session.Dispose();
        }
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

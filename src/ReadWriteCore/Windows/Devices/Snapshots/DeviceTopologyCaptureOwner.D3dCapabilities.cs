using System.Runtime.Versioning;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
    /// <summary>Creates an explicitly scoped D3D capability view. The native reader is inert until
    /// discovery or selected collection is requested.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsD3dCapabilityAccess CreateD3dCapabilityInformation(WindowsD3dCapabilityScope scope)
        => CreateD3dCapabilityInformation(scope, new WindowsD3dCapabilityNativeReader());

    /// <summary>Creates a D3D view over this owner's existing current and capture gate using an
    /// externally supplied local capability provider. No read occurs until discovery or collection.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsD3dCapabilityAccess CreateD3dCapabilityInformation(
        WindowsD3dCapabilityScope scope, IWindowsD3dCapabilityReader reader)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reader);
        return new WindowsD3dCapabilityAccess(this, scope, reader);
    }

    internal async ValueTask<DeviceInformationSnapshot> CaptureD3dCapabilitiesAsync(
        WindowsD3dCapabilityScope scope, WindowsD3dCapabilityFields selectedFields,
        IWindowsD3dCapabilityReader reader, DeviceTopologyCapturePermission? permission,
        CancellationToken cancellationToken, Action? sourceQueryStarted = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if ((selectedFields & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(selectedFields));
        if (selectedFields == WindowsD3dCapabilityFields.None)
            return WindowsInformationFieldProjection.D3dCurrent(ReadState().InformationFields, scope);

        await sampleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (permission is not null && (!permission.IsActive() || !permission.HasCurrentDemand()))
                return WindowsInformationFieldProjection.D3dCurrent(ReadState().InformationFields, scope);

            WindowsD3dCapabilityReadResult read;
            try
            {
                sourceQueryStarted?.Invoke();
                read = reader.Read(scope, selectedFields, cancellationToken)
                    ?? WindowsD3dCapabilityReadResult.Unavailable(scope, selectedFields);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                read = WindowsD3dCapabilityReadResult.Unavailable(scope, selectedFields);
            }
            cancellationToken.ThrowIfCancellationRequested();
            var observedAt = DateTimeOffset.UtcNow;
            if (!WindowsInformationFieldProjection.IsValidD3dRead(scope, selectedFields, read, observedAt))
                read = WindowsD3dCapabilityReadResult.Unavailable(scope, selectedFields);
            _ = TryPublish(permission, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = ReadState();
                var nextFields = WindowsInformationFieldProjection.MergeD3d(
                    current.InformationFields, scope, selectedFields, read, observedAt);
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref state, current with
                {
                    StateRevision = Interlocked.Increment(ref stateRevision),
                    InformationFields = nextFields
                });
                InformationPublications.Publish([InformationCollectionItemId]);
            });
            return WindowsInformationFieldProjection.D3dCurrent(ReadState().InformationFields, scope);
        }
        finally { sampleGate.Release(); }
    }
}

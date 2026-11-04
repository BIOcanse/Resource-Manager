using System.Runtime.Versioning;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Bluetooth;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
    [SupportedOSPlatform("windows")]
    public WindowsBluetoothClassicInformationAccess CreateBluetoothClassicInformation(
        WindowsBluetoothClassicInformationScope scope)
        => CreateBluetoothClassicInformation(scope, new WindowsBluetoothClassicInformationNativeReader());

    /// <summary>Creates a passive scope over this owner's current. The caller's typed reader is
    /// invoked only by explicit discovery, selected collection, or the existing role-2 borrow.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsBluetoothClassicInformationAccess CreateBluetoothClassicInformation(
        WindowsBluetoothClassicInformationScope scope, IWindowsBluetoothClassicInformationReader reader)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reader);
        return new WindowsBluetoothClassicInformationAccess(this, scope, reader);
    }

    internal async ValueTask<DeviceInformationSnapshot> CaptureBluetoothInformationAsync(
        WindowsBluetoothClassicInformationScope scope,
        WindowsBluetoothClassicInformationFields selectedFields,
        IWindowsBluetoothClassicInformationReader reader,
        DeviceTopologyCapturePermission? permission,
        bool requireSuccessfulDirectories,
        CancellationToken cancellationToken,
        Action? sourceQueryStarted = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if ((selectedFields & ~scope.Fields) != 0) throw new ArgumentOutOfRangeException(nameof(selectedFields));
        if (selectedFields == WindowsBluetoothClassicInformationFields.None)
            return WindowsInformationFieldProjection.BluetoothCurrent(ReadState().InformationFields, scope);

        await sampleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (permission is not null && (!permission.IsActive() || !permission.HasCurrentDemand()))
                return WindowsInformationFieldProjection.BluetoothCurrent(ReadState().InformationFields, scope);

            sourceQueryStarted?.Invoke();
            var read = reader.Read(scope, selectedFields, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var observedAt = DateTimeOffset.UtcNow;
            var committed = TryPublish(permission, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = ReadState();
                var nextFields = WindowsInformationFieldProjection.MergeBluetooth(current.InformationFields,
                    scope, selectedFields, read, observedAt, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref state, current with
                {
                    StateRevision = Interlocked.Increment(ref stateRevision),
                    InformationFields = nextFields
                });
                InformationPublications.Publish([InformationCollectionItemId]);
            });
            if (requireSuccessfulDirectories && committed)
            {
                var failed = FailedBluetoothDirectories(selectedFields, read);
                if (failed.Length > 0)
                    throw new InvalidOperationException("Bluetooth discovery failed for requested directory: "
                        + string.Join("; ", failed));
            }
            return WindowsInformationFieldProjection.BluetoothCurrent(ReadState().InformationFields, scope);
        }
        finally { sampleGate.Release(); }
    }

    private static string[] FailedBluetoothDirectories(WindowsBluetoothClassicInformationFields selectedFields,
        WindowsBluetoothClassicInformationReadResult read)
    {
        var failures = new List<string>(2);
        if ((selectedFields & WindowsBluetoothClassicInformationFields.ControllerId) != 0
            && read.Controllers?.DirectorySucceeded != true)
            failures.Add("windows-bluetooth-controller|: " + (read.Controllers?.FailureReason ?? "source omitted the requested directory"));
        if ((selectedFields & WindowsBluetoothClassicInformationFields.DeviceState) != 0
            && read.Devices?.DirectorySucceeded != true)
            failures.Add("windows-bluetooth-device|: " + (read.Devices?.FailureReason ?? "source omitted the requested directory"));
        return [.. failures];
    }
}

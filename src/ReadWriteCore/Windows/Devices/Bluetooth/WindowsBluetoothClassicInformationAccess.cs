using System.Runtime.Versioning;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace ResourceManager.ReadWrite.Windows.Devices.Bluetooth;

/// <summary>A scope-bound view over the original topology owner's current, gate, and publication.
/// It owns no worker, scheduler, native lifetime, or current-value cache.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsBluetoothClassicInformationAccess
{
    private readonly DeviceTopologyCaptureOwner owner;
    private readonly WindowsBluetoothClassicInformationScope scope;
    private readonly IWindowsBluetoothClassicInformationReader reader;

    internal WindowsBluetoothClassicInformationAccess(DeviceTopologyCaptureOwner owner,
        WindowsBluetoothClassicInformationScope scope, IWindowsBluetoothClassicInformationReader reader)
    {
        this.owner = owner;
        this.scope = scope;
        this.reader = reader;
        Backend = new InformationReadWriteBackend(
            () => WindowsInformationFieldProjection.BluetoothCurrent(owner.ReadState().InformationFields, scope),
            token => owner.CaptureBluetoothInformationAsync(scope, scope.Fields, reader,
                permission: null, requireSuccessfulDirectories: true, cancellationToken: token),
            static _ => new(true, null, []),
            (selection, token) => owner.CaptureBluetoothInformationAsync(scope, scope.Select(selection), reader,
                permission: null, requireSuccessfulDirectories: false, cancellationToken: token));
    }

    public InformationReadWriteBackend Backend { get; }

    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
        => new InformationCollectionBorrow(Backend, owner.InformationPublications,
            DeviceTopologyCaptureOwner.InformationCollectionItemId,
            async (selection, permit, token) =>
            {
                var committed = false;
                var sourceRead = false;
                var permission = new DeviceTopologyCapturePermission(() => permit.IsActive,
                    commit => { committed = permit.TryPublish(commit); return committed; }, static () => true);
                var snapshot = await owner.CaptureBluetoothInformationAsync(scope, scope.Select(selection), reader,
                    permission, requireSuccessfulDirectories: false, cancellationToken: token,
                    sourceQueryStarted: () => sourceRead = true);
                return new(InformationReadWriteBackend.Read(selection, snapshot), committed,
                    InformationCollectionBorrow.QueriedFieldsAfterSourceRead(selection, sourceRead));
            }, FieldCollectionSourceKind.SourceQuery);
}

using System.Runtime.Versioning;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

/// <summary>A scoped view over the original topology owner's current and publication.
/// Reads are explicit; this access creates no worker or independent cache.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsD3dCapabilityAccess
{
    private readonly DeviceTopologyCaptureOwner owner;
    private readonly WindowsD3dCapabilityScope scope;
    private readonly IWindowsD3dCapabilityReader reader;

    internal WindowsD3dCapabilityAccess(DeviceTopologyCaptureOwner owner,
        WindowsD3dCapabilityScope scope, IWindowsD3dCapabilityReader reader)
    {
        this.owner = owner;
        this.scope = scope;
        this.reader = reader;
        Backend = new InformationReadWriteBackend(
            () => WindowsInformationFieldProjection.D3dCurrent(owner.ReadState().InformationFields, scope),
            token => owner.CaptureD3dCapabilitiesAsync(scope, scope.Fields, reader, null, token),
            static _ => new(true, null, []),
            (selection, token) => owner.CaptureD3dCapabilitiesAsync(scope, scope.Select(selection), reader, null, token));
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
                var snapshot = await owner.CaptureD3dCapabilitiesAsync(scope, scope.Select(selection), reader,
                    permission, token, sourceQueryStarted: () => sourceRead = true);
                return new(InformationReadWriteBackend.Read(selection, snapshot), committed,
                    InformationCollectionBorrow.QueriedFieldsAfterSourceRead(selection, sourceRead));
            }, FieldCollectionSourceKind.SourceQuery);
}

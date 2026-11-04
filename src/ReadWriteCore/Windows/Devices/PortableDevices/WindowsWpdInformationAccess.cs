using System.Collections.Immutable;
using System.Runtime.Versioning;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace ResourceManager.ReadWrite.Windows.Devices.PortableDevices;

/// <summary>A scope-bound view over the original topology owner's current, gate,
/// and publication. It owns no worker, scheduler, native session, or second current.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsWpdInformationAccess
{
    private readonly DeviceTopologyCaptureOwner owner;
    private readonly WindowsWpdInformationScope scope;
    private readonly IWindowsWpdInformationReader reader;

    internal WindowsWpdInformationAccess(DeviceTopologyCaptureOwner owner,
        WindowsWpdInformationScope scope, IWindowsWpdInformationReader reader)
    {
        this.owner = owner;
        this.scope = scope;
        this.reader = reader;
        Backend = new InformationReadWriteBackend(
            () => WindowsInformationFieldProjection.WpdCurrent(owner.ReadState().InformationFields, scope),
            token => owner.CaptureWpdInformationAsync(scope, scope.DiscoveryRequest(), reader,
                permission: null, requireSuccessfulRead: true, cancellationToken: token),
            static _ => new(true, null, []),
            (selection, token) => selection.IsEmpty
                ? ValueTask.FromResult(WindowsInformationFieldProjection.WpdCurrent(owner.ReadState().InformationFields, scope))
                : owner.CaptureWpdInformationAsync(scope, scope.Select(selection), reader,
                    permission: null, requireSuccessfulRead: false, cancellationToken: token));
    }

    public InformationReadWriteBackend Backend { get; }

    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
        => new InformationCollectionBorrow(Backend, owner.InformationPublications,
            DeviceTopologyCaptureOwner.InformationCollectionItemId,
            async (selection, permit, token) =>
            {
                if (selection.IsEmpty) return new([], false);
                var committed = false;
                var sourceRead = false;
                var permission = new DeviceTopologyCapturePermission(() => permit.IsActive,
                    commit => { committed = permit.TryPublish(commit); return committed; }, static () => true);
                var snapshot = await owner.CaptureWpdInformationAsync(scope, scope.Select(selection), reader,
                    permission, requireSuccessfulRead: false, cancellationToken: token,
                    sourceQueryStarted: () => sourceRead = true);
                return new(InformationReadWriteBackend.Read(selection, snapshot), committed,
                    InformationCollectionBorrow.QueriedFieldsAfterSourceRead(selection, sourceRead));
            }, FieldCollectionSourceKind.SourceQuery);
}

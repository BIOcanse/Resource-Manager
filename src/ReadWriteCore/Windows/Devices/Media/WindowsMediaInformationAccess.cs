using System.Collections.Immutable;
using System.Runtime.Versioning;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace ResourceManager.ReadWrite.Windows.Devices.Media;

/// <summary>A scope-bound view over the original topology owner's current and publication.
/// It creates no worker, scheduler, native lifetime, or current-value cache.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsMediaInformationAccess
{
    private readonly DeviceTopologyCaptureOwner owner;
    private readonly WindowsMediaInformationScope scope;
    private readonly IWindowsMediaInformationReader reader;

    internal WindowsMediaInformationAccess(DeviceTopologyCaptureOwner owner,
        WindowsMediaInformationScope scope, IWindowsMediaInformationReader reader)
    {
        this.owner = owner;
        this.scope = scope;
        this.reader = reader;
        Backend = new InformationReadWriteBackend(
            () => WindowsInformationFieldProjection.MediaCurrent(owner.ReadState().InformationFields, scope),
            token => owner.CaptureMediaInformationAsync(scope, scope.Fields, reader,
                permission: null, requireSuccessfulDirectories: true, cancellationToken: token),
            ReadSupport,
            (selection, token) => owner.CaptureMediaInformationAsync(scope, scope.Select(selection), reader,
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
                    commit => { committed = permit.TryPublish(commit); return committed; }, () => true);
                var snapshot = await owner.CaptureMediaInformationAsync(scope, scope.Select(selection), reader,
                    permission, requireSuccessfulDirectories: false, cancellationToken: token,
                    sourceQueryStarted: () => sourceRead = true);
                return new(InformationReadWriteBackend.Read(selection, snapshot), committed,
                    InformationCollectionBorrow.QueriedFieldsAfterSourceRead(selection, sourceRead));
            }, FieldCollectionSourceKind.SourceQuery);

    private static OperationSupport ReadSupport(DeviceInformationDefinition definition)
        => new(true, null, []);
}

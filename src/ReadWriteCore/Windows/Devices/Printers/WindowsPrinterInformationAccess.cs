using System.Collections.Immutable;
using System.Runtime.Versioning;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace ResourceManager.ReadWrite.Windows.Devices.Printers;

/// <summary>A scope-bound view over the original topology owner's current and
/// publication. It creates no worker, scheduler, native cache, or second current.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPrinterInformationAccess
{
    private readonly DeviceTopologyCaptureOwner owner;
    private readonly WindowsPrinterInformationScope scope;
    private readonly IWindowsPrinterInformationReader reader;

    internal WindowsPrinterInformationAccess(DeviceTopologyCaptureOwner owner,
        WindowsPrinterInformationScope scope, IWindowsPrinterInformationReader reader)
    {
        this.owner = owner;
        this.scope = scope;
        this.reader = reader;
        Backend = new InformationReadWriteBackend(
            () => WindowsInformationFieldProjection.PrinterCurrent(owner.ReadState().InformationFields, scope),
            token => owner.CapturePrinterInformationAsync(scope, scope.Fields, reader,
                permission: null, requireSuccessfulRead: true, cancellationToken: token),
            static _ => new(true, null, []),
            (selection, token) => owner.CapturePrinterInformationAsync(scope, scope.Select(selection), reader,
                permission: null, requireSuccessfulRead: false, cancellationToken: token));
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
                var snapshot = await owner.CapturePrinterInformationAsync(scope, scope.Select(selection), reader,
                    permission, requireSuccessfulRead: false, cancellationToken: token,
                    sourceQueryStarted: () => sourceRead = true);
                return new(InformationReadWriteBackend.Read(selection, snapshot), committed,
                    InformationCollectionBorrow.QueriedFieldsAfterSourceRead(selection, sourceRead));
            }, FieldCollectionSourceKind.SourceQuery);
}

using System.Collections.Immutable;
using System.Runtime.Versioning;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace ResourceManager.ReadWrite.Windows.Devices.Storage;

/// <summary>Typed, externally owned source. Returning a partial data or threshold page is represented
/// by null only in the affected selected field; source exceptions make the selected fields unavailable.</summary>
public delegate DeviceInformationSnapshot AtaSmartAttributeInformationSource(
    AtaSmartAttributeInformationRequest request, CancellationToken cancellationToken);

/// <summary>A scope-bound backend over the original topology owner's current and publication.
/// It owns no worker, native source, or second current-value cache.</summary>
[SupportedOSPlatform("windows")]
public sealed class AtaSmartInformationAccess
{
    private readonly DeviceTopologyCaptureOwner owner;
    private readonly AtaSmartAttributeInformationRequest scope;
    private readonly AtaSmartAttributeInformationSource source;

    internal AtaSmartInformationAccess(DeviceTopologyCaptureOwner owner,
        AtaSmartAttributeInformationRequest request, AtaSmartAttributeInformationSource source)
    {
        this.owner = owner;
        scope = WindowsInformationFieldProjection.CopyAtaSmartAccessRequest(request);
        this.source = source;
        Backend = new InformationReadWriteBackend(
            () => WindowsInformationFieldProjection.AtaSmartAccessCurrent(owner.ReadState().InformationFields, scope),
            token => owner.CaptureAtaSmartInformationAsync(scope, scope, source, null,
                requireSuccessfulRead: true, token),
            static _ => new(true, null, []),
            (selection, token) => owner.CaptureAtaSmartInformationAsync(scope,
                WindowsInformationFieldProjection.SelectAtaSmartAccessRequest(scope, selection), source, null,
                requireSuccessfulRead: false, token));
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
                var selected = WindowsInformationFieldProjection.SelectAtaSmartAccessRequest(scope, selection);
                var snapshot = await owner.CaptureAtaSmartInformationAsync(scope, selected, source, permission,
                    requireSuccessfulRead: false, token, sourceQueryStarted: () => sourceRead = true);
                return new(InformationReadWriteBackend.Read(selection, snapshot), committed,
                    InformationCollectionBorrow.QueriedFieldsAfterSourceRead(selection, sourceRead));
            }, FieldCollectionSourceKind.SourceQuery);
}

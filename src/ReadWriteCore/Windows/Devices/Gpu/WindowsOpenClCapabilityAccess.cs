using System.Runtime.Versioning;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

/// <summary>A scoped OpenCL view over the original topology owner's current and publication.
/// It creates no independent worker, cache, or publication signal.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsOpenClCapabilityAccess
{
    private readonly DeviceTopologyCaptureOwner owner;
    private readonly WindowsOpenClCapabilityScope scope;
    private readonly IWindowsOpenClCapabilityReader reader;

    internal WindowsOpenClCapabilityAccess(DeviceTopologyCaptureOwner owner,
        WindowsOpenClCapabilityScope scope, IWindowsOpenClCapabilityReader reader)
    {
        this.owner = owner;
        this.scope = scope;
        this.reader = reader;
        Backend = new InformationReadWriteBackend(
            () => WindowsInformationFieldProjection.OpenClCurrent(owner.ReadState().InformationFields, scope),
            token => owner.CaptureOpenClCapabilitiesAsync(scope, scope.Fields, reader, null, token,
                requireSuccessfulDirectory: true),
            static _ => new(true, null, []),
            (selection, token) => owner.CaptureOpenClCapabilitiesAsync(
                scope, scope.Select(selection), reader, null, token));
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
                var snapshot = await owner.CaptureOpenClCapabilitiesAsync(scope, scope.Select(selection), reader,
                    permission, token, sourceQueryStarted: () => sourceRead = true);
                return new(InformationReadWriteBackend.Read(selection, snapshot), committed,
                    InformationCollectionBorrow.QueriedFieldsAfterSourceRead(selection, sourceRead));
            }, FieldCollectionSourceKind.SourceQuery);
}

using System.Runtime.Versioning;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace ResourceManager.ReadWrite.Windows.Devices.Gpu;

/// <summary>A scoped OpenGL view over the original topology owner's current state, publication, and gate.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsOpenGlCapabilityAccess
{
    private readonly DeviceTopologyCaptureOwner owner;
    private readonly WindowsOpenGlCapabilityScope scope;
    private readonly IWindowsOpenGlCapabilityReader reader;

    internal WindowsOpenGlCapabilityAccess(DeviceTopologyCaptureOwner owner,
        WindowsOpenGlCapabilityScope scope, IWindowsOpenGlCapabilityReader reader)
    {
        this.owner = owner;
        this.scope = scope;
        this.reader = reader;
        Backend = new InformationReadWriteBackend(
            () => WindowsInformationFieldProjection.OpenGlCurrent(owner.ReadState().InformationFields, scope),
            token => owner.CaptureOpenGlCapabilitiesAsync(scope, scope.Fields, reader, null, token),
            static _ => new(true, null, []),
            (selection, token) => owner.CaptureOpenGlCapabilitiesAsync(
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
                var snapshot = await owner.CaptureOpenGlCapabilitiesAsync(scope, scope.Select(selection), reader,
                    permission, token, sourceQueryStarted: () => sourceRead = true);
                return new(InformationReadWriteBackend.Read(selection, snapshot), committed,
                    InformationCollectionBorrow.QueriedFieldsAfterSourceRead(selection, sourceRead));
            }, FieldCollectionSourceKind.SourceQuery);
}

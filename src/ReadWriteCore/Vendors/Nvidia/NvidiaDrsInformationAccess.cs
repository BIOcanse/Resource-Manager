using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;
using ResourceManager.ReadWrite.Windows.Devices.Snapshots;

namespace ResourceManager.ReadWrite.Vendors.Nvidia;

/// <summary>Scope-bound view of the original topology Current; owns neither source nor worker.</summary>
public sealed class NvidiaDrsInformationAccess
{
    private readonly DeviceTopologyCaptureOwner owner;
    private readonly NvidiaDrsReadSource source;
    private readonly DeviceTopologyCapturePermission? boundPermission;
    public NvidiaDrsInformationRequest Scope { get; }
    public InformationReadWriteBackend Backend { get; }

    internal NvidiaDrsInformationAccess(DeviceTopologyCaptureOwner owner, NvidiaDrsInformationRequest scope, NvidiaDrsReadSource source,
        DeviceTopologyCapturePermission? permission)
    {
        this.owner = owner; this.source = source; Scope = scope; boundPermission = permission;
        Backend = new(() => WindowsInformationFieldProjection.NvidiaDrsCurrent(owner.ReadState().InformationFields, scope),
            token => owner.CaptureNvidiaDrsInformationAsync(scope, scope, source, CombinePermission(null), token),
            static _ => new(true, null, []),
            (selection, token) => owner.CaptureNvidiaDrsInformationAsync(scope, scope.Select(selection), source, CombinePermission(null), token));
    }

    internal ValueTask<DeviceInformationSnapshot> CaptureAsync(System.Collections.Immutable.ImmutableArray<FieldReference> fields,
        DeviceTopologyCapturePermission permission, CancellationToken token, Action? sourceQueryStarted = null)
        => owner.CaptureNvidiaDrsInformationAsync(Scope, Scope.Select(Backend.Select(fields)), source, CombinePermission(permission), token, sourceQueryStarted);

    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
        => new InformationCollectionBorrow(Backend, owner.InformationPublications, DeviceTopologyCaptureOwner.InformationCollectionItemId,
            async (selection, permit, token) =>
            {
                var committed = false; var queried = false;
                var combined = CombinePermission(new(() => permit.IsActive, permit.TryPublish, () => true))!;
                var permission = new DeviceTopologyCapturePermission(combined.IsActive,
                    publish => { committed = combined.TryPublish(publish); return committed; }, combined.HasCurrentDemand);
                var snapshot = await owner.CaptureNvidiaDrsInformationAsync(Scope, Scope.Select(selection), source,
                    permission, token, () => queried = true);
                return new(InformationReadWriteBackend.Read(selection, snapshot), committed,
                    InformationCollectionBorrow.QueriedFieldsAfterSourceRead(selection, queried));
            }, FieldCollectionSourceKind.SourceQuery);

    // Outer hosted/publication permit is entered before the bound zone mode gate.
    // Never infer publication from an outer gate returning true without invoking its callback.
    private DeviceTopologyCapturePermission? CombinePermission(DeviceTopologyCapturePermission? outer)
    {
        if (boundPermission is null) return outer;
        var bound = boundPermission;
        return new(() => (outer?.IsActive() ?? true) && bound.IsActive(), publish =>
        {
            var committed = false;
            var boundAccepted = false;
            void PublishBound() => boundAccepted = bound.TryPublish(() => { publish(); committed = true; });
            var outerAccepted = outer is null;
            if (outer is null) PublishBound();
            else outerAccepted = outer.TryPublish(PublishBound);
            return outerAccepted && boundAccepted && committed;
        }, () => (outer?.HasCurrentDemand() ?? true) && bound.HasCurrentDemand());
    }
}

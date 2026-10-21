using System.Runtime.Versioning;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Storage;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
    /// <summary>Creates an inert ATA SMART information view over this owner's current and capture gate.
    /// The supplied source is called only by explicit discovery, selected collection, or role-2 demand.</summary>
    [SupportedOSPlatform("windows")]
    public AtaSmartInformationAccess CreateAtaSmartAttributeInformation(
        AtaSmartAttributeInformationRequest request, AtaSmartAttributeInformationSource source)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (topology is null) throw new InvalidOperationException("No topology policy is bound to this capture owner.");
        ArgumentNullException.ThrowIfNull(source);
        return new(this, request, source);
    }

    internal async ValueTask<DeviceInformationSnapshot> CaptureAtaSmartInformationAsync(
        AtaSmartAttributeInformationRequest scope,
        AtaSmartAttributeInformationRequest selected,
        AtaSmartAttributeInformationSource source,
        DeviceTopologyCapturePermission? permission,
        bool requireSuccessfulRead,
        CancellationToken cancellationToken,
        Action? sourceQueryStarted = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (selected.Devices.Any(pair => !scope.Devices.TryGetValue(pair.Key, out var configured)
                || (pair.Value & ~configured) != 0)
            || selected.Devices.Count > scope.MaximumDeviceCount)
            throw new ArgumentOutOfRangeException(nameof(selected));
        selected = WindowsInformationFieldProjection.ActiveAtaSmartAccessRequest(selected);
        if (!selected.Devices.Any(static pair => pair.Value != AtaSmartAttributeInformationFields.None))
            return WindowsInformationFieldProjection.AtaSmartAccessCurrent(ReadState().InformationFields, scope);

        await sampleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (permission is not null && (!permission.IsActive() || !permission.HasCurrentDemand()))
                return WindowsInformationFieldProjection.AtaSmartAccessCurrent(ReadState().InformationFields, scope);

            DeviceInformationSnapshot read;
            var readSucceeded = true;
            try
            {
                sourceQueryStarted?.Invoke();
                read = source(selected, cancellationToken)
                    ?? throw new InvalidDataException("ATA source returned no snapshot.");
                _ = WindowsInformationFieldProjection.MergeAtaSmartAccess(
                    DeviceInformationSnapshot.Empty, selected, read, DateTimeOffset.UtcNow, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                read = DeviceInformationSnapshot.Empty;
                readSucceeded = false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var observedAt = DateTimeOffset.UtcNow;
            var committed = TryPublish(permission, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = ReadState();
                var nextFields = readSucceeded
                    ? WindowsInformationFieldProjection.MergeAtaSmartAccess(current.InformationFields, selected, read,
                        observedAt, cancellationToken)
                    : WindowsInformationFieldProjection.AtaSmartAccessUnavailable(current.InformationFields, selected,
                        cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref state, current with
                {
                    StateRevision = Interlocked.Increment(ref stateRevision),
                    InformationFields = nextFields
                });
                InformationPublications.Publish([InformationCollectionItemId]);
            });

            if (requireSuccessfulRead && committed && !readSucceeded)
                throw new InvalidOperationException("ATA SMART discovery failed for the explicitly selected physical-disk scope.");
            return WindowsInformationFieldProjection.AtaSmartAccessCurrent(ReadState().InformationFields, scope);
        }
        finally { sampleGate.Release(); }
    }
}

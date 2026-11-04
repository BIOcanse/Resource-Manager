using System.Runtime.Versioning;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
    /// <summary>Creates an explicitly scoped OpenGL information view using only a caller-owned current context borrow.
    /// No context or window is created. Native loading is inert until discovery or selected collection.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsOpenGlCapabilityAccess CreateOpenGlCapabilityInformation(
        WindowsOpenGlCapabilityScope scope, IWindowsOpenGlCurrentContextExecutor contextExecutor)
        => CreateOpenGlCapabilityInformation(scope, new WindowsOpenGlCapabilityNativeReader(contextExecutor));

    /// <summary>Uses a caller-supplied OpenGL information reader over this owner's existing current, publication,
    /// and capture gate. Provider calls occur only during explicit discovery or selected collection.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsOpenGlCapabilityAccess CreateOpenGlCapabilityInformation(
        WindowsOpenGlCapabilityScope scope, IWindowsOpenGlCapabilityReader reader)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reader);
        return new WindowsOpenGlCapabilityAccess(this, scope, reader);
    }

    internal async ValueTask<DeviceInformationSnapshot> CaptureOpenGlCapabilitiesAsync(
        WindowsOpenGlCapabilityScope scope, WindowsOpenGlCapabilityFields selectedFields,
        IWindowsOpenGlCapabilityReader reader, DeviceTopologyCapturePermission? permission,
        CancellationToken cancellationToken, Action? sourceQueryStarted = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if ((selectedFields & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(selectedFields));
        if (selectedFields == WindowsOpenGlCapabilityFields.None)
            return WindowsInformationFieldProjection.OpenGlCurrent(ReadState().InformationFields, scope);

        await sampleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (permission is not null && (!permission.IsActive() || !permission.HasCurrentDemand()))
                return WindowsInformationFieldProjection.OpenGlCurrent(ReadState().InformationFields, scope);

            WindowsOpenGlCapabilityReadResult read;
            try
            {
                sourceQueryStarted?.Invoke();
                read = reader.Read(scope, selectedFields, cancellationToken)
                    ?? WindowsOpenGlCapabilityReadResult.Unavailable;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                read = WindowsOpenGlCapabilityReadResult.Unavailable;
            }
            cancellationToken.ThrowIfCancellationRequested();

            var observedAt = DateTimeOffset.UtcNow;
            if (!WindowsInformationFieldProjection.IsValidOpenGlRead(scope, selectedFields, read, observedAt))
                read = WindowsOpenGlCapabilityReadResult.Unavailable;

            _ = TryPublish(permission, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = ReadState();
                var nextFields = WindowsInformationFieldProjection.MergeOpenGl(
                    current.InformationFields, scope, selectedFields, read, observedAt);
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref state, current with
                {
                    StateRevision = Interlocked.Increment(ref stateRevision),
                    InformationFields = nextFields
                });
                InformationPublications.Publish([InformationCollectionItemId]);
            });
            return WindowsInformationFieldProjection.OpenGlCurrent(ReadState().InformationFields, scope);
        }
        finally { sampleGate.Release(); }
    }
}

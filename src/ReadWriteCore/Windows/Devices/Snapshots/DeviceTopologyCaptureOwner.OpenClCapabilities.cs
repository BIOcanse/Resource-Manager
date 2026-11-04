using System.Runtime.Versioning;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
    /// <summary>Creates an explicitly scoped OpenCL device-information view. The native reader is inert until
    /// discovery or selected collection is requested.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsOpenClCapabilityAccess CreateOpenClCapabilityInformation(WindowsOpenClCapabilityScope scope)
        => CreateOpenClCapabilityInformation(scope, new WindowsOpenClCapabilityNativeReader());

    /// <summary>Uses a caller-supplied OpenCL information provider over this owner's existing current, publication,
    /// and capture gate. Provider calls occur only during explicit discovery or selected collection.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsOpenClCapabilityAccess CreateOpenClCapabilityInformation(
        WindowsOpenClCapabilityScope scope, IWindowsOpenClCapabilityReader reader)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reader);
        return new WindowsOpenClCapabilityAccess(this, scope, reader);
    }

    internal async ValueTask<DeviceInformationSnapshot> CaptureOpenClCapabilitiesAsync(
        WindowsOpenClCapabilityScope scope, WindowsOpenClCapabilityFields selectedFields,
        IWindowsOpenClCapabilityReader reader, DeviceTopologyCapturePermission? permission,
        CancellationToken cancellationToken, bool requireSuccessfulDirectory = false,
        Action? sourceQueryStarted = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if ((selectedFields & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(selectedFields));
        if (selectedFields == WindowsOpenClCapabilityFields.None)
            return WindowsInformationFieldProjection.OpenClCurrent(ReadState().InformationFields, scope);

        await sampleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (permission is not null && (!permission.IsActive() || !permission.HasCurrentDemand()))
                return WindowsInformationFieldProjection.OpenClCurrent(ReadState().InformationFields, scope);

            WindowsOpenClCapabilityReadResult read;
            try
            {
                sourceQueryStarted?.Invoke();
                read = reader.Read(scope, selectedFields, cancellationToken)
                    ?? WindowsOpenClCapabilityReadResult.Unavailable;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                read = WindowsOpenClCapabilityReadResult.Unavailable;
            }
            cancellationToken.ThrowIfCancellationRequested();

            var observedAt = DateTimeOffset.UtcNow;
            if (!WindowsInformationFieldProjection.IsValidOpenClRead(
                ReadState().InformationFields, scope, selectedFields, read, observedAt))
                read = WindowsOpenClCapabilityReadResult.Unavailable;

            _ = TryPublish(permission, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = ReadState();
                var nextFields = WindowsInformationFieldProjection.MergeOpenCl(
                    current.InformationFields, scope, selectedFields, read, observedAt);
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref state, current with
                {
                    StateRevision = Interlocked.Increment(ref stateRevision),
                    InformationFields = nextFields
                });
                InformationPublications.Publish([InformationCollectionItemId]);
            });
            if (requireSuccessfulDirectory && !read.DirectorySucceeded)
                throw new InvalidOperationException("The requested OpenCL GPU directory could not be completely enumerated and identity-checked.");
            return WindowsInformationFieldProjection.OpenClCurrent(ReadState().InformationFields, scope);
        }
        finally { sampleGate.Release(); }
    }
}

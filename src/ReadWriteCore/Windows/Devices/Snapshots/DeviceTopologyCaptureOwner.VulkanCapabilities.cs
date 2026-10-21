using System.Runtime.Versioning;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Gpu;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
    /// <summary>Creates an explicitly scoped Vulkan information view. Native loading is inert until discovery or collection.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsVulkanCapabilityAccess CreateVulkanCapabilityInformation(WindowsVulkanCapabilityScope scope)
        => CreateVulkanCapabilityInformation(scope, new WindowsVulkanCapabilityNativeReader());

    /// <summary>Uses a caller-supplied Vulkan information provider over this owner's existing current,
    /// publication, and capture gate. Provider calls occur only during explicit discovery or selected collection.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsVulkanCapabilityAccess CreateVulkanCapabilityInformation(
        WindowsVulkanCapabilityScope scope, IWindowsVulkanCapabilityReader reader)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reader);
        return new WindowsVulkanCapabilityAccess(this, scope, reader);
    }

    internal async ValueTask<DeviceInformationSnapshot> CaptureVulkanCapabilitiesAsync(
        WindowsVulkanCapabilityScope scope, WindowsVulkanCapabilitySelection selection,
        IWindowsVulkanCapabilityReader reader, DeviceTopologyCapturePermission? permission,
        CancellationToken cancellationToken, Action? sourceQueryStarted = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if ((selection.QueryFields & ~scope.Fields) != 0 || selection.Definitions.IsDefault
            || selection.ExactFields.IsDefault || selection.Definitions.Any(definition =>
                !scope.Definitions.Any(candidate => ReferenceEquals(candidate, definition))))
            throw new ArgumentOutOfRangeException(nameof(selection));
        if (selection.QueryFields == WindowsVulkanCapabilityFields.None)
            return WindowsInformationFieldProjection.VulkanCurrent(ReadState().InformationFields, scope);

        await sampleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (permission is not null && (!permission.IsActive() || !permission.HasCurrentDemand()))
                return WindowsInformationFieldProjection.VulkanCurrent(ReadState().InformationFields, scope);

            WindowsVulkanCapabilityReadResult read;
            try
            {
                sourceQueryStarted?.Invoke();
                read = reader.Read(scope, selection.QueryFields, cancellationToken)
                    ?? WindowsVulkanCapabilityReadResult.Unavailable;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                read = WindowsVulkanCapabilityReadResult.Unavailable;
            }
            cancellationToken.ThrowIfCancellationRequested();

            var observedAt = DateTimeOffset.UtcNow;
            if (!WindowsInformationFieldProjection.IsValidVulkanRead(scope, selection, read, observedAt))
                read = WindowsVulkanCapabilityReadResult.Unavailable;

            _ = TryPublish(permission, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = ReadState();
                var nextFields = WindowsInformationFieldProjection.MergeVulkan(
                    current.InformationFields, scope, selection, read, observedAt);
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref state, current with
                {
                    StateRevision = Interlocked.Increment(ref stateRevision),
                    InformationFields = nextFields
                });
                InformationPublications.Publish([InformationCollectionItemId]);
            });
            return WindowsInformationFieldProjection.VulkanCurrent(ReadState().InformationFields, scope);
        }
        finally { sampleGate.Release(); }
    }
}

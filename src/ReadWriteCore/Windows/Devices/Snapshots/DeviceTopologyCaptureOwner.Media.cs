using System.Runtime.Versioning;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Windows.Devices.Media;
using ResourceManager.ReadWrite.Windows.Devices.Models;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
    [SupportedOSPlatform("windows")]
    public WindowsMediaInformationAccess CreateAudioCodecInformation(WindowsMediaInformationScope scope)
        => CreateAudioCodecInformation(scope, new WindowsMediaInformationNativeReader());

    /// <summary>Creates a scoped audio/codec view over this owner's existing current and capture gate,
    /// using the caller's local information provider. No read occurs until discovery or collection.</summary>
    [SupportedOSPlatform("windows")]
    public WindowsMediaInformationAccess CreateAudioCodecInformation(
        WindowsMediaInformationScope scope, IWindowsMediaInformationReader reader)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(reader);
        return new WindowsMediaInformationAccess(this, scope, reader);
    }

    internal async ValueTask<DeviceInformationSnapshot> CaptureMediaInformationAsync(
        WindowsMediaInformationScope scope,
        WindowsMediaInformationFields selectedFields,
        IWindowsMediaInformationReader reader,
        DeviceTopologyCapturePermission? permission,
        bool requireSuccessfulDirectories,
        CancellationToken cancellationToken,
        Action? sourceQueryStarted = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if ((selectedFields & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(selectedFields));
        if (selectedFields == WindowsMediaInformationFields.None)
            return WindowsInformationFieldProjection.MediaCurrent(ReadState().InformationFields, scope);

        await sampleGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (permission is not null && (!permission.IsActive() || !permission.HasCurrentDemand()))
                return WindowsInformationFieldProjection.MediaCurrent(ReadState().InformationFields, scope);

            sourceQueryStarted?.Invoke();
            var read = reader.Read(scope, selectedFields, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var observedAt = DateTimeOffset.UtcNow;
            var committed = TryPublish(permission, () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var current = ReadState();
                var nextFields = WindowsInformationFieldProjection.MergeMedia(current.InformationFields,
                    scope, selectedFields, read, observedAt);
                cancellationToken.ThrowIfCancellationRequested();
                Volatile.Write(ref state, current with
                {
                    StateRevision = Interlocked.Increment(ref stateRevision),
                    InformationFields = nextFields
                });
                InformationPublications.Publish([InformationCollectionItemId]);
            });
            if (requireSuccessfulDirectories && committed)
            {
                var failed = FailedDirectoryPrefixes(scope, selectedFields, read);
                if (failed.Length > 0)
                    throw new InvalidOperationException("Audio/codec discovery failed for requested directory prefix(es): "
                        + string.Join(", ", failed));
            }
            return WindowsInformationFieldProjection.MediaCurrent(ReadState().InformationFields, scope);
        }
        finally { sampleGate.Release(); }
    }

    private static string[] FailedDirectoryPrefixes(WindowsMediaInformationScope scope,
        WindowsMediaInformationFields selectedFields, WindowsMediaInformationReadResult read)
    {
        var expected = new HashSet<string>(StringComparer.Ordinal);
        if ((selectedFields & WindowsMediaInformationScope.AudioEndpointFields) != 0)
            foreach (var direction in scope.AudioDirections)
                expected.Add(WindowsInformationFieldProjection.AudioIdentityPrefix(direction));
        if ((selectedFields & WindowsMediaInformationFields.AudioDriverVersion) != 0)
            expected.Add(WindowsInformationFieldProjection.AudioDeviceIdentityPrefix);
        if ((selectedFields & WindowsMediaInformationFields.Codec) != 0)
        {
            foreach (var category in scope.CodecCategories)
                expected.Add(WindowsInformationFieldProjection.MftIdentityPrefix(category));
            foreach (var category in scope.DirectShowCategories)
                expected.Add(WindowsInformationFieldProjection.DirectShowIdentityPrefix(category));
        }

        var directories = read.Directories.ToDictionary(static directory => directory.IdentityPrefix,
            StringComparer.Ordinal);
        return [.. expected.Where(prefix => !directories.TryGetValue(prefix, out var directory)
                || !directory.DirectorySucceeded)
            .Order(StringComparer.Ordinal)];
    }
}

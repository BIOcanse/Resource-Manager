using System.Collections.Immutable;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Vendors.Nvidia;

namespace ResourceManager.ReadWrite.Windows.Devices.Snapshots;

public sealed partial class DeviceTopologyCaptureOwner
{
    public NvidiaDrsInformationAccess CreateNvidiaDrsInformation(NvidiaDrsInformationRequest scope, NvidiaDrsReadSource source,
        DeviceTopologyCapturePermission? permission = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(scope); ArgumentNullException.ThrowIfNull(source);
        return new(this, scope, source, permission);
    }

    internal async ValueTask<DeviceInformationSnapshot> CaptureNvidiaDrsInformationAsync(
        NvidiaDrsInformationRequest scope, NvidiaDrsInformationRequest selected, NvidiaDrsReadSource source,
        DeviceTopologyCapturePermission? permission, CancellationToken token, Action? sourceQueryStarted = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var allowedFields = scope.Fields.ToHashSet();
        if (selected.Fields.Any(field => !allowedFields.Contains(field)))
            throw new ArgumentException("DRS capture is outside the configured scope.", nameof(selected));
        token.ThrowIfCancellationRequested();
        if (selected.Fields.IsEmpty) return WindowsInformationFieldProjection.NvidiaDrsCurrent(ReadState().InformationFields, scope);
        await sampleGate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            token.ThrowIfCancellationRequested();
            if (permission is not null && (!permission.IsActive() || !permission.HasCurrentDemand()))
                return WindowsInformationFieldProjection.NvidiaDrsCurrent(ReadState().InformationFields, scope);
            DeviceInformationSnapshot? read = null;
            try
            {
                sourceQueryStarted?.Invoke();
                var typed = source(selected.Profiles, selected.Limits, token);
                if (typed is not null) read = WindowsInformationFieldProjection.ProjectNvidiaDrs(selected, typed, DateTimeOffset.UtcNow, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { read = null; }
            token.ThrowIfCancellationRequested();
            _ = TryPublish(permission, () =>
            {
                token.ThrowIfCancellationRequested();
                var current = ReadState();
                var fields = read is null
                    ? WindowsInformationFieldProjection.NvidiaDrsUnavailable(current.InformationFields, selected, token)
                    : WindowsInformationFieldProjection.MergeNvidiaDrs(current.InformationFields, selected, read, token);
                token.ThrowIfCancellationRequested();
                Volatile.Write(ref state, current with { StateRevision = Interlocked.Increment(ref stateRevision), InformationFields = fields });
                InformationPublications.Publish([InformationCollectionItemId]);
            });
            return WindowsInformationFieldProjection.NvidiaDrsCurrent(ReadState().InformationFields, scope);
        }
        finally { sampleGate.Release(); }
    }

    /// <summary>Explicit full-session directory discovery. Zero budget or unavailable source is null,
    /// never a successful unobserved empty directory. No application associations are queried.</summary>
    public async ValueTask<DeviceInformationSnapshot?> DiscoverNvidiaDrsDirectoryAsync(NvidiaDrsReadLimits limits,
        NvidiaDrsDirectorySource source, CancellationToken token = default, DeviceTopologyCapturePermission? permission = null)
    {
        ArgumentNullException.ThrowIfNull(limits); ArgumentNullException.ThrowIfNull(source);
        NvidiaNvapiReader.ValidateDrsLimits(limits);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        token.ThrowIfCancellationRequested();
        if (limits.MaximumProfileCount == 0) return null;
        await sampleGate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            token.ThrowIfCancellationRequested();
            if (permission is not null && (!permission.IsActive() || !permission.HasCurrentDemand())) return null;
            DeviceInformationSnapshot? observed = null;
            NvidiaDrsInformationRequest? observedScope = null;
            try
            {
                var typed = source(limits, token);
                if (typed is not null)
                {
                    observedScope = DirectoryScope(typed, limits, token);
                    observed = WindowsInformationFieldProjection.ProjectNvidiaDrs(observedScope,
                        new([.. typed.Profiles.Where(profile => !profile.Settings.IsEmpty)]), DateTimeOffset.UtcNow, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { observed = null; }
            token.ThrowIfCancellationRequested();
            if (observed is null || observedScope is null) return null;
            var committed = false;
            var accepted = TryPublish(permission, () =>
            {
                token.ThrowIfCancellationRequested();
                if (!observedScope.Fields.IsEmpty)
                {
                    var current = ReadState();
                    var fields = WindowsInformationFieldProjection.MergeNvidiaDrs(current.InformationFields, observedScope, observed, token);
                    token.ThrowIfCancellationRequested();
                    Volatile.Write(ref state, current with { StateRevision = Interlocked.Increment(ref stateRevision), InformationFields = fields });
                    InformationPublications.Publish([InformationCollectionItemId]);
                }
                committed = true;
            });
            if (!accepted || !committed) return null;
            return observed;
        }
        finally { sampleGate.Release(); }
    }

    private static NvidiaDrsInformationRequest DirectoryScope(NvidiaDrsSnapshot read, NvidiaDrsReadLimits limits, CancellationToken token)
    {
        if (read.Profiles.IsDefault || read.Profiles.Length > limits.MaximumProfileCount)
            throw new InvalidDataException("DRS directory exceeds its configured profile budget.");
        var profiles = ImmutableArray.CreateBuilder<NvidiaDrsProfileRequest>();
        ulong total = 0, bytes = checked((ulong)read.Profiles.Length * 4116);
        foreach (var profile in read.Profiles)
        {
            token.ThrowIfCancellationRequested();
            if (profile is null || profile.Settings.IsDefault || profile.Settings.Length > limits.MaximumSettingsPerProfile
                || profile.SettingCountReported != (uint)profile.Settings.Length)
                throw new InvalidDataException("DRS profile does not prove a complete bounded setting directory.");
            total = checked(total + (uint)profile.Settings.Length); bytes = checked(bytes + (ulong)profile.Settings.Length * 12320);
            if (total > limits.MaximumTotalSettingCount || bytes > limits.MaximumPayloadBytes)
                throw new InvalidDataException("DRS directory exceeds the full result budget.");
            profiles.Add(new(profile.ProfileName, profile.Settings.Select(item => item.SettingId).ToImmutableArray()));
        }
        if (bytes > limits.MaximumPayloadBytes) throw new InvalidDataException("DRS directory exceeds its payload budget.");
        return new(profiles.ToImmutable(), limits);
    }
}

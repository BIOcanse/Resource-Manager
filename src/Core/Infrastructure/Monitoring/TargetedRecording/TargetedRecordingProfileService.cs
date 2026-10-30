using System.Text.Json;
using ResourceManager.App.Application.RuntimeSpecialization;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Infrastructure.Monitoring.TargetedRecording;

/// <summary>The saved recording profiles, as the recorder reads them at start.</summary>
public interface ITargetedRecordingProfileReader
{
    Task<IReadOnlyList<TargetedRecordingProfile>> ReadProfilesAsync(CancellationToken cancellationToken);
}

public static class TargetedRecordingProfileErrors
{
    public const string ProfileNotFound = "targeted-recording-profile-not-found";
    public const string LastProfile = "targeted-recording-last-profile";
    public const string NameRequired = "targeted-recording-profile-invalid-name";
    public const string SettingsConflict = "settings-revision-conflict";
}

public sealed record TargetedRecordingProfileCreateRequest(
    string? Name,
    string? CopyFromId = null,
    int? DefaultIntervalMs = null,
    IReadOnlyList<TargetedRecordingItem>? Items = null);

public sealed record TargetedRecordingProfileUpdateRequest(
    string? Name = null,
    int? DefaultIntervalMs = null,
    IReadOnlyList<TargetedRecordingItem>? Items = null);

/// <summary>Outcome of a profile change. Profile is the created or updated profile; Error is a wire reason
/// with StatusCode the HTTP status the endpoint returns.</summary>
public sealed record TargetedRecordingProfileResult(int StatusCode, TargetedRecordingProfile? Profile, string? Error);

/// <summary>Owns profile create, copy, rename, edit and delete. Profiles live in user settings
/// (targetedRecording.profiles) and every change is saved through the settings patch path, so the settings
/// revision, validation and persistence are the same as any other settings edit.</summary>
public sealed class TargetedRecordingProfileService(
    IAppSettingsStore settingsStore,
    IRuntimeSpecializationCoordinator coordinator,
    ITargetedRecordingItemCatalog catalog) : ITargetedRecordingProfileReader
{
    private const int MaximumConflictRetries = 3;
    private static readonly JsonSerializerOptions PatchJson = new AppSettingsJsonContext(new(JsonSerializerDefaults.Web)).Options;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<IReadOnlyList<TargetedRecordingProfile>> ReadProfilesAsync(CancellationToken cancellationToken)
    {
        var loaded = await settingsStore.LoadReadOnlyAsync(cancellationToken);
        return Profiles(loaded.Settings);
    }

    public async Task<TargetedRecordingProfileResult> CreateAsync(TargetedRecordingProfileCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name)) return Fail(400, TargetedRecordingProfileErrors.NameRequired);
        return await ChangeAsync(async (profiles, token) =>
        {
            TargetedRecordingProfile? source = null;
            if (request.CopyFromId is not null)
            {
                source = profiles.FirstOrDefault(item => item.Id == request.CopyFromId);
                if (source is null) return (null, Fail(404, TargetedRecordingProfileErrors.ProfileNotFound));
            }
            var created = new TargetedRecordingProfile(Guid.NewGuid().ToString("N"), request.Name!.Trim(),
                request.DefaultIntervalMs ?? source?.DefaultIntervalMs ?? TargetedRecordingProfileDefaults.DefaultIntervalMs,
                request.Items ?? source?.Items ?? TargetedRecordingProfileDefaults.CreateDefaultProfile().Items);
            if (await ValidateAsync(created, source, token) is { } reason) return (null, Fail(400, reason));
            var next = profiles.ToList();
            next.Add(created);
            return (next, new TargetedRecordingProfileResult(201, created, null));
        }, cancellationToken);
    }

    public Task<TargetedRecordingProfileResult> UpdateAsync(string id, TargetedRecordingProfileUpdateRequest request,
        CancellationToken cancellationToken)
        => ChangeAsync(async (profiles, token) =>
        {
            var index = profiles.FindIndex(item => item.Id == id);
            if (index < 0) return (null, Fail(404, TargetedRecordingProfileErrors.ProfileNotFound));
            var previous = profiles[index];
            var updated = previous with
            {
                Name = request.Name?.Trim() ?? previous.Name,
                DefaultIntervalMs = request.DefaultIntervalMs ?? previous.DefaultIntervalMs,
                Items = request.Items ?? previous.Items
            };
            if (await ValidateAsync(updated, previous, token) is { } reason) return (null, Fail(400, reason));
            var next = profiles.ToList();
            next[index] = updated;
            return (next, new TargetedRecordingProfileResult(200, updated, null));
        }, cancellationToken);

    public Task<TargetedRecordingProfileResult> DeleteAsync(string id, CancellationToken cancellationToken)
        => ChangeAsync((profiles, _) =>
        {
            var index = profiles.FindIndex(item => item.Id == id);
            if (index < 0) return Task.FromResult<(List<TargetedRecordingProfile>?, TargetedRecordingProfileResult)>(
                (null, Fail(404, TargetedRecordingProfileErrors.ProfileNotFound)));
            if (profiles.Count == 1) return Task.FromResult<(List<TargetedRecordingProfile>?, TargetedRecordingProfileResult)>(
                (null, Fail(409, TargetedRecordingProfileErrors.LastProfile)));
            var next = profiles.ToList();
            next.RemoveAt(index);
            return Task.FromResult<(List<TargetedRecordingProfile>?, TargetedRecordingProfileResult)>(
                (next, new TargetedRecordingProfileResult(204, null, null)));
        }, cancellationToken);

    private static IReadOnlyList<TargetedRecordingProfile> Profiles(AppSettings settings)
        => settings.TargetedRecording?.Profiles is { Count: > 0 } profiles
            ? profiles : TargetedRecordingProfileDefaults.Create().Profiles;

    /// <summary>Shape first; then only the items that differ from the stored profile are checked against the
    /// live catalog, so a saved item whose device is absent today does not block a rename.</summary>
    private async Task<string?> ValidateAsync(TargetedRecordingProfile profile, TargetedRecordingProfile? stored,
        CancellationToken cancellationToken)
    {
        if (TargetedRecordingProfileShape.ValidateProfile(profile) is { } shape) return shape;
        var unchanged = stored?.Items.ToHashSet() ?? [];
        var changed = profile.Items.Where(item => !unchanged.Contains(item)).ToArray();
        if (changed.Length == 0) return null;
        var live = await catalog.ReadAsync(cancellationToken);
        foreach (var item in changed)
        {
            if (TargetedRecordingItemCatalogRules.Validate(item, live) is { } reason) return reason;
        }
        return null;
    }

    private async Task<TargetedRecordingProfileResult> ChangeAsync(
        Func<List<TargetedRecordingProfile>, CancellationToken, Task<(List<TargetedRecordingProfile>? Next, TargetedRecordingProfileResult Result)>> change,
        CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var loaded = await settingsStore.LoadAsync(cancellationToken);
                var (next, result) = await change(Profiles(loaded.Settings).ToList(), cancellationToken);
                if (next is null) return result;
                var section = JsonSerializer.Serialize(new AppTargetedRecordingSettings(next), PatchJson);
                using var patch = JsonDocument.Parse("{\"targetedRecording\":" + section + "}");
                var changes = patch.RootElement.Clone();
                var saved = await coordinator.ApplyAppSettingsPatchAsync(
                    new AppSettingsPatchRequest(loaded.Revision, changes), "targeted-recording-profiles", cancellationToken);
                if (saved.RuntimeApplicationDisposition is "committedAndApplied" or "committedWithCapabilityConstraints"
                    or "committedWithDeliveryFailures" or "savedNotApplied")
                    return result;
                if (saved.RuntimeFailureCode == TargetedRecordingProfileErrors.SettingsConflict && attempt < MaximumConflictRetries)
                    continue;
                return Fail(409, saved.RuntimeFailureCode ?? saved.RuntimeApplicationDisposition);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static TargetedRecordingProfileResult Fail(int statusCode, string error) => new(statusCode, null, error);
}

using System.Collections.Immutable;
using System.Text;

namespace ResourceManager.ReadWrite.Vendors.Nvidia;

public sealed partial class NvidiaNvapiReader
{
    private const int DrsEndEnumeration = -7;
    private const ulong DrsProfileBytes = 4116;
    private const ulong DrsSettingBytes = 12320;
    private static readonly UnicodeEncoding DrsUnicode = new(false, false, true);

    /// <summary>Explicit full-session, bounded database enumeration of profiles/settings actually returned by DRS.
    /// Null is unavailable/incomplete, including zero enumeration budget; only an observed empty database is successful empty.
    /// Does not enumerate application associations or fabricate inherited defaults.</summary>
    public NvidiaDrsSnapshot? DiscoverDrs(NvidiaDrsReadLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ValidateDrsLimits(limits);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (limits.MaximumProfileCount == 0) return null;
            return WithDrsSession((api, session) => EnumerateDrs(api, session, limits, cancellationToken), cancellationToken);
        }
    }

    /// <summary>Reads only selected named profiles/setting IDs (no database enumeration).
    /// Each call loads a new session. Current values are database declarations, not active GPU state.</summary>
    public NvidiaDrsSnapshot? ReadDrs(IReadOnlyList<NvidiaDrsProfileRequest> requests,
        NvidiaDrsReadLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(limits);
        ValidateDrsLimits(limits);
        // Snapshot the caller's selection before entering any native operation.
        var copy = CopyDrsRequests(requests, limits);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (copy.Length == 0) return new([]);
            return WithDrsSession((api, session) => ReadSelectedDrs(api, session, copy, cancellationToken), cancellationToken);
        }
    }

    private NvidiaDrsSnapshot? WithDrsSession(Func<INvidiaNvapiDrsApi, nint, NvidiaDrsSnapshot?> read,
        CancellationToken token)
    {
        NvidiaDrsSnapshot? result = null;
        var createdThisCall = false;
        try
        {
            // EnsureInitialized first resolves any single outstanding Destroy obligation.
            if (!EnsureInitialized() || drsApi is null || !drsApi.IsSupported) return null;
            token.ThrowIfCancellationRequested();
            nint session = 0;
            int status;
            try { status = drsApi.CreateSession(out session); }
            finally
            {
                // Even a failing API returning a nonzero output cannot cause us to lose that handle.
                if (session != 0) { pendingSession = session; createdThisCall = true; }
            }
            if (status != NvapiOk || session == 0) return null;
            token.ThrowIfCancellationRequested();
            if (drsApi.LoadSettings(session) != NvapiOk) return null;
            token.ThrowIfCancellationRequested();
            result = read(drsApi, session);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { result = null; }
        finally
        {
            // Failure retains the same handle/module for next-operation cleanup; never reuse the session for reads.
            if (createdThisCall && !TryCleanupPendingSession()) result = null;
        }
        return result;
    }

    internal static void ValidateDrsLimits(NvidiaDrsReadLimits limits)
    {
        if (limits.MaximumProfileCount == 0) return;
        if (limits.PageSize == 0 || limits.PageSize > (ulong)int.MaxValue / DrsSettingBytes)
            throw new ArgumentOutOfRangeException(nameof(limits), "PageSize must fit an allocated ABI page.");
        if (limits.MaximumProfileCount > int.MaxValue || limits.MaximumTotalSettingCount > int.MaxValue
            || limits.MaximumSettingsPerProfile > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(limits), "Requested counts must fit managed collections.");
    }

    internal static ImmutableArray<NvidiaDrsProfileRequest> CopyDrsRequests(
        IReadOnlyList<NvidiaDrsProfileRequest> requests, NvidiaDrsReadLimits limits)
    {
        var profileCount = requests.Count;
        if (profileCount < 0 || profileCount > limits.MaximumProfileCount)
            throw new ArgumentOutOfRangeException(nameof(requests));
        var names = new HashSet<string>(StringComparer.Ordinal);
        var result = ImmutableArray.CreateBuilder<NvidiaDrsProfileRequest>();
        ulong total = 0, bytes = 0;
        for (var profileIndex = 0; profileIndex < profileCount; profileIndex++)
        {
            var request = requests[profileIndex];
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.SettingIds);
            ValidateDrsProfileName(request.ProfileName);
            if (!names.Add(request.ProfileName)) throw new ArgumentException("Duplicate profile name.", nameof(requests));
            var settingCount = request.SettingIds.Count;
            if (settingCount < 0 || settingCount > limits.MaximumSettingsPerProfile)
                throw new ArgumentOutOfRangeException(nameof(requests));
            if (settingCount != 0)
            {
                // Prove all budgets before allocating/copying this caller-controlled list.
                total = checked(total + (uint)settingCount);
                bytes = checked(bytes + DrsProfileBytes + (ulong)settingCount * DrsSettingBytes);
                if (total > limits.MaximumTotalSettingCount || bytes > limits.MaximumPayloadBytes)
                    throw new ArgumentOutOfRangeException(nameof(requests));
                var ids = ImmutableArray.CreateBuilder<uint>(settingCount);
                var uniqueIds = new HashSet<uint>();
                for (var settingIndex = 0; settingIndex < settingCount; settingIndex++)
                {
                    var id = request.SettingIds[settingIndex];
                    if (!uniqueIds.Add(id)) throw new ArgumentException("Duplicate setting ID.", nameof(requests));
                    ids.Add(id);
                }
                result.Add(new(request.ProfileName, ids.MoveToImmutable()));
            }
            if (request.SettingIds.Count != settingCount)
                throw new ArgumentException("Setting count changed during request copy.", nameof(requests));
        }
        if (requests.Count != profileCount)
            throw new ArgumentException("Profile count changed during request copy.", nameof(requests));
        return result.ToImmutable();
    }

    private static void ValidateDrsProfileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name.Length >= 2048 || name.Contains('\0'))
            throw new ArgumentException("Profile lookup name must fit the NVAPI Unicode string.", nameof(name));
        _ = DrsUnicode.GetByteCount(name); // Reject malformed UTF-16 rather than replacement-key collisions.
    }

    private static unsafe NvidiaDrsSnapshot? EnumerateDrs(INvidiaNvapiDrsApi api, nint session,
        NvidiaDrsReadLimits limits, CancellationToken token)
    {
        if (api.GetNumProfiles(session, out var count) != NvapiOk || count > limits.MaximumProfileCount) return null;
        var profiles = ImmutableArray.CreateBuilder<NvidiaDrsProfile>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var handles = new HashSet<nint>();
        ulong totalSettings = 0, bytes = checked((ulong)count * DrsProfileBytes);
        if (bytes > limits.MaximumPayloadBytes) return null;
        for (uint index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            if (api.EnumProfiles(session, index, out var handle) != NvapiOk || handle == 0 || !handles.Add(handle)) return null;
            var info = NvDrsProfile.Create();
            if (api.GetProfileInfo(session, handle, ref info) != NvapiOk || info.Version != 0x00011014) return null;
            var name = ReadDrsUnicode(info.Name);
            if (name is null || name.Length == 0 || !names.Add(name)) return null;
            if (info.SettingCount > limits.MaximumSettingsPerProfile) return null;
            totalSettings = checked(totalSettings + info.SettingCount);
            bytes = checked(bytes + (ulong)info.SettingCount * DrsSettingBytes);
            if (totalSettings > limits.MaximumTotalSettingCount || bytes > limits.MaximumPayloadBytes) return null;

            // The announced count and full result charge are proved before page allocation.
            var page = new NvDrsSetting[checked((int)Math.Min(limits.PageSize, info.SettingCount))];
            var settings = ImmutableArray.CreateBuilder<NvidiaDrsSetting>();
            var ids = new HashSet<uint>();
            uint start = 0;
            while (start < info.SettingCount)
            {
                token.ThrowIfCancellationRequested();
                var capacity = Math.Min((uint)page.Length, info.SettingCount - start);
                if (capacity == 0) return null;
                for (var i = 0; i < capacity; i++) page[i] = NvDrsSetting.Create();
                var returned = capacity;
                int status;
                fixed (NvDrsSetting* pointer = page)
                    status = api.EnumSettings(session, handle, start, ref returned, (nint)pointer);
                if (status != NvapiOk || returned == 0 || returned > capacity) return null;
                for (var i = 0; i < returned; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (page[i].Version != 0x00013020 || !ids.Add(page[i].Id)) return null;
                    settings.Add(DecodeDrsSetting(ref page[i]));
                }
                start = checked(start + returned);
            }
            // A separately initialized one-element probe proves no hidden tail was truncated.
            var end = NvDrsSetting.Create();
            uint endCount = 1;
            token.ThrowIfCancellationRequested();
            var endStatus = api.EnumSettings(session, handle, start, ref endCount, (nint)(&end));
            if (endStatus != DrsEndEnumeration && !(endStatus == NvapiOk && endCount == 0)) return null;
            profiles.Add(new(name, info.GpuSupport, info.IsPredefined,
                info.ApplicationCount, info.SettingCount, settings.ToImmutable()));
        }
        token.ThrowIfCancellationRequested();
        if (api.EnumProfiles(session, count, out _) != DrsEndEnumeration) return null;
        return new(profiles.ToImmutable());
    }

    private static unsafe NvidiaDrsSnapshot ReadSelectedDrs(INvidiaNvapiDrsApi api, nint session,
        ImmutableArray<NvidiaDrsProfileRequest> requests, CancellationToken token)
    {
        var profiles = ImmutableArray.CreateBuilder<NvidiaDrsProfile>();
        foreach (var request in requests)
        {
            token.ThrowIfCancellationRequested();
            var info = NvDrsProfile.Create();
            var found = api.FindProfileByName(session, request.ProfileName, out var handle) == NvapiOk && handle != 0;
            if (found)
                found = api.GetProfileInfo(session, handle, ref info) == NvapiOk
                    && info.Version == 0x00011014
                    && StringComparer.Ordinal.Equals(ReadDrsUnicode(info.Name), request.ProfileName);
            var settings = ImmutableArray.CreateBuilder<NvidiaDrsSetting>();
            foreach (var id in request.SettingIds)
            {
                token.ThrowIfCancellationRequested();
                var setting = NvDrsSetting.Create();
                if (found && api.GetSetting(session, handle, id, ref setting) == NvapiOk
                    && setting.Version == 0x00013020 && setting.Id == id)
                    settings.Add(DecodeDrsSetting(ref setting));
                else settings.Add(new(id, null, null, null, null, null, null));
            }
            profiles.Add(new(request.ProfileName, found ? info.GpuSupport : null,
                found ? info.IsPredefined : null, found ? info.ApplicationCount : null,
                found ? info.SettingCount : null, settings.ToImmutable()));
        }
        return new(profiles.ToImmutable());
    }

    private static unsafe NvidiaDrsSetting DecodeDrsSetting(ref NvDrsSetting setting)
    {
        string? name;
        NvidiaDrsCurrentValue? current = null;
        fixed (ushort* pointer = setting.Name) name = ReadDrsUnicode(pointer);
        switch (setting.Type)
        {
            case 0: current = new NvidiaDrsDwordValue(setting.Current.Dword); break;
            case 1:
                if (setting.Current.Binary.Length <= 4096)
                {
                    fixed (byte* pointer = setting.Current.Binary.Data)
                        current = new NvidiaDrsBinaryValue(new ReadOnlySpan<byte>(pointer,
                            checked((int)setting.Current.Binary.Length)).ToArray().ToImmutableArray());
                }
                break;
            case 3:
                string? text;
                fixed (ushort* pointer = setting.Current.Unicode) text = ReadDrsUnicode(pointer);
                if (text is not null) current = new NvidiaDrsUnicodeValue(text);
                break;
            case 4: current = new NvidiaDrsQwordValue(setting.Current.Qword); break;
            // STRING_TYPE(2) has no separately proved encoding here; never guess ANSI or reinterpret as WSTRING.
        }
        return new(setting.Id, name, setting.Type, setting.Location,
            setting.IsCurrentPredefined, setting.IsPredefinedValid, current);
    }

    private static unsafe string? ReadDrsUnicode(ushort* pointer)
    {
        var length = 0;
        while (length < 2048 && pointer[length] != 0) length++;
        if (length == 2048) return null;
        try { return DrsUnicode.GetString(new ReadOnlySpan<byte>(pointer, checked(length * 2))); }
        catch (DecoderFallbackException) { return null; }
    }
}

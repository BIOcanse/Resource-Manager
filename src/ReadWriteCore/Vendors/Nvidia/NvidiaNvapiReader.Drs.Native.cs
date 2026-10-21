using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Vendors.Nvidia;

// NVIDIA/nvapi 70d337db9186e968eab622f7e786de7e437faf3d, nvapi.h.
// No Set/Save/CreateProfile/Delete/Restore/file-import entry points are bound.
internal interface INvidiaNvapiDrsApi
{
    bool IsSupported { get; }
    int Initialize();
    int Unload();
    void FreeLibrary();
    int CreateSession(out nint session);
    int DestroySession(nint session);
    int LoadSettings(nint session);
    int GetNumProfiles(nint session, out uint count);
    int EnumProfiles(nint session, uint index, out nint profile);
    int GetProfileInfo(nint session, nint profile, ref NvDrsProfile info);
    int FindProfileByName(nint session, string name, out nint profile);
    int EnumSettings(nint session, nint profile, uint start, ref uint count, nint settings);
    int GetSetting(nint session, nint profile, uint id, ref NvDrsSetting setting);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct NvDrsBinary
{
    internal uint Length;
    internal fixed byte Data[4096];
}

[StructLayout(LayoutKind.Explicit, Pack = 4, Size = 4100)]
internal unsafe struct NvDrsValue
{
    [FieldOffset(0)] internal uint Dword;
    [FieldOffset(0)] internal ulong Qword;
    [FieldOffset(0)] internal NvDrsBinary Binary;
    [FieldOffset(0)] internal fixed ushort Unicode[2048];
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct NvDrsSetting
{
    internal uint Version;
    internal fixed ushort Name[2048];
    internal uint Id;
    internal uint Type;
    internal uint Location;
    internal uint IsCurrentPredefined;
    internal uint IsPredefinedValid;
    internal NvDrsValue Predefined;
    internal NvDrsValue Current;

    internal static NvDrsSetting Create()
    {
        var value = new NvDrsSetting { Version = 0x00013020 };
        value.Predefined.Binary.Length = 4096;
        value.Current.Binary.Length = 4096;
        return value;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 8)]
internal unsafe struct NvDrsProfile
{
    internal uint Version;
    internal fixed ushort Name[2048];
    internal uint GpuSupport;
    internal uint IsPredefined;
    internal uint ApplicationCount;
    internal uint SettingCount;
    internal static NvDrsProfile Create() => new() { Version = 0x00011014 };
}

public sealed partial class NvidiaNvapiReader
{
    private readonly record struct DrsBindings(CreateSessionDelegate? CreateSession,
        SessionDelegate? DestroySession, SessionDelegate? LoadSettings,
        GetNumProfilesDelegate? GetNumProfiles, EnumProfilesDelegate? EnumProfiles,
        GetProfileInfoDelegate? GetProfileInfo, FindProfileDelegate? FindProfile,
        EnumSettingsDelegate? EnumSettings, GetSettingDelegate? GetSetting)
    {
        internal bool IsSupported => CreateSession is not null && DestroySession is not null
            && LoadSettings is not null && GetNumProfiles is not null && EnumProfiles is not null
            && GetProfileInfo is not null && FindProfile is not null && EnumSettings is not null && GetSetting is not null;
        internal static DrsBindings Load(NvapiQueryInterfaceDelegate query) => new(
            GetDelegate<CreateSessionDelegate>(query, 0x0694D52E),
            GetDelegate<SessionDelegate>(query, 0xDAD9CFF8),
            GetDelegate<SessionDelegate>(query, 0x375DBD6B),
            GetDelegate<GetNumProfilesDelegate>(query, 0x1DAE4FBC),
            GetDelegate<EnumProfilesDelegate>(query, 0xBC371EE0),
            GetDelegate<GetProfileInfoDelegate>(query, 0x61CD6FD6),
            GetDelegate<FindProfileDelegate>(query, 0x7E4A9A0B),
            GetDelegate<EnumSettingsDelegate>(query, 0xAE3039DA),
            GetDelegate<GetSettingDelegate>(query, 0x73BF8338));
    }

    private sealed class NativeDrsApi(NativeBindings native) : INvidiaNvapiDrsApi
    {
        public bool IsSupported => native.Drs.IsSupported;
        public int Initialize() => native.Initialize?.Invoke() ?? -3;
        public int Unload() => native.Unload?.Invoke() ?? -3;
        public void FreeLibrary() => NativeLibrary.Free(native.LibraryHandle);
        public int CreateSession(out nint session) => native.Drs.CreateSession!(out session);
        public int DestroySession(nint session) => native.Drs.DestroySession!(session);
        public int LoadSettings(nint session) => native.Drs.LoadSettings!(session);
        public int GetNumProfiles(nint session, out uint count) => native.Drs.GetNumProfiles!(session, out count);
        public int EnumProfiles(nint session, uint index, out nint profile) => native.Drs.EnumProfiles!(session, index, out profile);
        public int GetProfileInfo(nint session, nint profile, ref NvDrsProfile info) => native.Drs.GetProfileInfo!(session, profile, ref info);
        public int FindProfileByName(nint session, string name, out nint profile) => native.Drs.FindProfile!(session, name, out profile);
        public int EnumSettings(nint session, nint profile, uint start, ref uint count, nint settings) => native.Drs.EnumSettings!(session, profile, start, ref count, settings);
        public int GetSetting(nint session, nint profile, uint id, ref NvDrsSetting setting) => native.Drs.GetSetting!(session, profile, id, ref setting);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateSessionDelegate(out nint session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SessionDelegate(nint session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetNumProfilesDelegate(nint session, out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumProfilesDelegate(nint session, uint index, out nint profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetProfileInfoDelegate(nint session, nint profile, ref NvDrsProfile info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FindProfileDelegate(nint session, [MarshalAs(UnmanagedType.LPWStr)] string name, out nint profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int EnumSettingsDelegate(nint session, nint profile, uint start, ref uint count, nint settings);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetSettingDelegate(nint session, nint profile, uint id, ref NvDrsSetting setting);
}

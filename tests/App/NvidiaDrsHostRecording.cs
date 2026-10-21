using System.Runtime.InteropServices;
using System.Buffers.Binary;
using ResourceManager.ReadWrite.Vendors.Nvidia;

namespace Resource_Manager_APP.Tests;

// Actual lowlevel reader with a recorded native API; never opens the driver.
internal sealed class NvidiaDrsHostRecording : INvidiaNvapiDrsApi
{
    internal string ProfileName = "Game";
    internal ulong Value = ulong.MaxValue;
    internal int Creates, Destroys, Loads, Reads, Unloads, Frees;
    internal bool FailSetting, FailDestroy, FailUnload, Live;
    internal Action? OnSetting;
    public bool IsSupported => true;
    public int Initialize() => 0;
    public int Unload() { Assert.False(Live); Unloads++; return FailUnload ? -1 : 0; }
    public void FreeLibrary() { Assert.False(Live); Frees++; }
    public int CreateSession(out nint session) { Assert.False(Live); Live = true; session = ++Creates; return 0; }
    public int DestroySession(nint session) { Destroys++; if (FailDestroy) return -1; Assert.True(Live); Live = false; return 0; }
    public int LoadSettings(nint session) { Loads++; return 0; }
    public int GetNumProfiles(nint session, out uint count) { count = 1; return 0; }
    public int EnumProfiles(nint session, uint index, out nint profile) { profile = index == 0 ? 1 : 0; return index == 0 ? 0 : -7; }
    public int FindProfileByName(nint session, string name, out nint profile)
    { profile = name == ProfileName ? 1 : 0; return profile == 0 ? -163 : 0; }
    public int GetProfileInfo(nint session, nint profile, ref NvDrsProfile info)
    {
        var bytes = new byte[4116]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x00011014);
        for (var i = 0; i < ProfileName.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4 + i * 2), ProfileName[i]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4100), 0x80000007);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4112), 1);
        info = Structure<NvDrsProfile>(bytes); return 0;
    }
    public int GetSetting(nint session, nint profile, uint id, ref NvDrsSetting setting)
    {
        Reads++; OnSetting?.Invoke(); if (FailSetting) return -160;
        var bytes = new byte[12320]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 0x00013020);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4100), id);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4104), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4108), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8220), Value);
        setting = Structure<NvDrsSetting>(bytes); return 0;
    }
    private static T Structure<T>(byte[] bytes) where T : struct
    {
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { return Marshal.PtrToStructure<T>(pinned.AddrOfPinnedObject()); }
        finally { pinned.Free(); }
    }
    public int EnumSettings(nint session, nint profile, uint start, ref uint count, nint settings)
    {
        if (start != 0) { count = 0; return -7; }
        count = 1; var setting = NvDrsSetting.Create(); GetSetting(session, profile, 7, ref setting);
        Marshal.StructureToPtr(setting, settings, false); return 0;
    }
}

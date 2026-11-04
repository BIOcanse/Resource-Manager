using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Devices;

/// <summary>A synchronous read owns one INetFwPolicy2 reference and its COM initialization balance.</summary>
internal sealed class WindowsFirewallPolicyReadSession : IWindowsFirewallPolicyRead
{
    internal static readonly Guid ClassId = new("E2B3C97F-6AE1-41AC-817A-F6F92166D7DD");
    internal static readonly Guid InterfaceId = new("98325047-C671-4174-8D81-DEFCD3F03186");
    internal const int ReleaseSlot = 2, CurrentProfilesSlot = 7, EnabledSlot = 8;
    internal const int ChangedApartment = unchecked((int)0x80010106);
    private readonly WindowsFirewallComFunctions functions;
    private readonly bool uninitialize;
    private readonly FirewallRelease release;
    private readonly FirewallGetProfiles profiles;
    private readonly FirewallGetEnabled enabled;
    private IntPtr handle;

    private WindowsFirewallPolicyReadSession(IntPtr handle, bool uninitialize, WindowsFirewallComFunctions functions)
    {
        this.handle = handle; this.uninitialize = uninitialize; this.functions = functions;
        var table = Marshal.ReadIntPtr(handle);
        release = Bind<FirewallRelease>(table, ReleaseSlot);
        try { profiles = Bind<FirewallGetProfiles>(table, CurrentProfilesSlot); enabled = Bind<FirewallGetEnabled>(table, EnabledSlot); }
        catch { release(handle); this.handle = IntPtr.Zero; throw; }
    }
    internal static WindowsFirewallPolicyReadSession? Open() => Open(new(
        () => CoInitializeEx(IntPtr.Zero, 0),
        (Guid classId, Guid interfaceId, out IntPtr pointer) => CoCreateInstance(in classId, IntPtr.Zero, 1, in interfaceId, out pointer),
        CoUninitialize));
    internal static WindowsFirewallPolicyReadSession? Open(WindowsFirewallComFunctions functions)
    {
        var initialized = functions.Initialize();
        if (initialized is not (0 or 1 or ChangedApartment)) return null;
        var uninitialize = initialized is 0 or 1;
        var transferred = false;
        try
        {
            if (functions.Create(ClassId, InterfaceId, out var pointer) != 0 || pointer == IntPtr.Zero)
                return null;
            var session = new WindowsFirewallPolicyReadSession(pointer, uninitialize, functions);
            transferred = true;
            return session;
        }
        finally { if (!transferred && uninitialize) functions.Uninitialize(); }
    }
    public int GetCurrentProfiles(out uint value)
    { ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this); return profiles(handle, out value); }
    public int GetEnabled(uint profile, out short value)
    {
        ObjectDisposedException.ThrowIf(handle == IntPtr.Zero, this);
        if (profile is not (1 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(profile));
        return enabled(handle, profile, out value);
    }
    public void Dispose()
    {
        var pointer = Interlocked.Exchange(ref handle, IntPtr.Zero); if (pointer == IntPtr.Zero) return;
        try { release(pointer); } finally { if (uninitialize) functions.Uninitialize(); }
    }
    private static T Bind<T>(IntPtr table, int slot) where T : Delegate
        => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(table, slot * IntPtr.Size));

    [DllImport("ole32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CoInitializeEx(IntPtr reserved, uint model);
    [DllImport("ole32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void CoUninitialize();
    [DllImport("ole32.dll", ExactSpelling = true)] [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CoCreateInstance(in Guid classId, IntPtr outer, uint context, in Guid interfaceId, out IntPtr pointer);
}

internal delegate int FirewallCreate(Guid classId, Guid interfaceId, out IntPtr pointer);
internal sealed record WindowsFirewallComFunctions(Func<int> Initialize, FirewallCreate Create, Action Uninitialize);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate uint FirewallRelease(IntPtr self);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int FirewallGetProfiles(IntPtr self, out uint profiles);
[UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int FirewallGetEnabled(IntPtr self, uint profile, out short enabled);

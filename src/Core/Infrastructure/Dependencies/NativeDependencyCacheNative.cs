using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.Dependencies;

// ABI widths/layout only. Cache policy is implemented by dependencies/cache/state.zig.
internal static unsafe partial class NativeDependencyCacheNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Result
    {
        internal ulong Incarnation, Generation, Projection, Released, Required, StatusProbes, ProviderProbes;
    }

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_dependency_cache_initialize")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial uint Initialize(IntPtr region, nuint length, ulong incarnation, ulong statuses, ulong providers);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_dependency_cache_execute")]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static partial uint ExecuteRaw(IntPtr region, nuint length, uint operation, uint kind, uint unicodeProfile,
        char* key, uint keyLength, ulong source, ulong incarnation, ulong generation, ulong projection, out Result result);

    internal static uint Execute(IntPtr region, ulong length, uint operation, uint kind,
        string key, ulong source, ulong incarnation, ulong generation, ulong projection, out Result result)
    {
        fixed (char* text = key)
            return ExecuteRaw(region, checked((nuint)length), operation, kind,
                NativeDependencyProbeUnicodeProfile.Current, text, checked((uint)key.Length),
                source, incarnation, generation, projection, out result);
    }
}

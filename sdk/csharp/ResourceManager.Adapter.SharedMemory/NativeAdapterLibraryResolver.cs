using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.Adapter.SharedMemory;

internal static class NativeAdapterLibraryResolver
{
    internal const string LibraryName = "ResourceManager.Adapter.Native";
    internal const string FileName = "ResourceManager.Adapter.Native.dll";

#pragma warning disable CA2255 // The adapter package must bind its native image before any interop type is used.
    [ModuleInitializer]
    internal static void Register()
        => NativeLibrary.SetDllImportResolver(
            typeof(NativeAdapterLibraryResolver).Assembly,
            Resolve);
#pragma warning restore CA2255

    internal static string GetCanonicalPath()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, FileName));

    private static IntPtr Resolve(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
        => string.Equals(libraryName, LibraryName, StringComparison.Ordinal)
            ? NativeLibrary.Load(GetCanonicalPath())
            : IntPtr.Zero;
}

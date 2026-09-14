using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal static class NativeCoreLibraryResolver
{
    internal const string LibraryName = "ResourceManager.NativeCore";
    internal const string FileName = "ResourceManager.NativeCore.dll";

    [ModuleInitializer]
    internal static void Register()
        => NativeLibrary.SetDllImportResolver(
            typeof(NativeCoreLibraryResolver).Assembly,
            Resolve);

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

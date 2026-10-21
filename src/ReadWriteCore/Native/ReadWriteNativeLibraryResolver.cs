using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native;

internal static class ReadWriteNativeLibraryResolver
{
    static ReadWriteNativeLibraryResolver()
    {
        NativeLibrary.SetDllImportResolver(typeof(ReadWriteNativeLibraryResolver).Assembly,
            static (name, assembly, searchPath) =>
            {
                var rootFile = Path.Combine(AppContext.BaseDirectory, "ResourceManager.NativeCore.dll");
                return name == "ResourceManager.NativeCore" && File.Exists(rootFile)
                    ? NativeLibrary.Load(rootFile) : IntPtr.Zero;
            });
    }
    internal static void EnsureRegistered() { }
}

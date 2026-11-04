using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Native;

internal static class ReadWriteNativeLibraryResolver
{
    private const string NativeCoreName = "ResourceManager.NativeCore";
    private static readonly string NativeCorePath = Path.Combine(AppContext.BaseDirectory, NativeCoreName + ".dll");
    private static readonly object ResolutionGate = new();
    // Resolved P/Invoke entry points require this module for the default
    // assembly's entire process lifetime. Never unload it between calls.
    private static IntPtr nativeCore;

    static ReadWriteNativeLibraryResolver()
    {
        NativeLibrary.SetDllImportResolver(typeof(ReadWriteNativeLibraryResolver).Assembly,
            static (name, assembly, searchPath) => Resolve(name));
    }

    internal static IntPtr Resolve(string name)
    {
        if (name != NativeCoreName) return IntPtr.Zero;
        var loaded = Volatile.Read(ref nativeCore);
        if (loaded != IntPtr.Zero) return loaded;
        lock (ResolutionGate)
        {
            loaded = nativeCore;
            if (loaded == IntPtr.Zero && File.Exists(NativeCorePath))
            {
                loaded = NativeLibrary.Load(NativeCorePath);
                Volatile.Write(ref nativeCore, loaded);
            }
        }
        // Absence still falls through to the runtime's normal lookup. Only a
        // successful load is cached, so a missing file does not poison retries.
        return loaded;
    }

    internal static void EnsureRegistered() { }
}

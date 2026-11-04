using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

// Scoped source/platform ports only. Zig owns restore/mutate/save/commit/publish
// ordering. The remaining CLR source projection/file codec exits at Z-codec/P-file,
// and this serialized service binding exits with H3.
internal static unsafe partial class NativeKernelHistoryCommand
{
    private sealed class Scope(Action restore, Func<int> mutate, Action publish)
    {
        internal readonly Action Restore = restore, Publish = publish;
        internal readonly Func<int> Mutate = mutate;
        internal ExceptionDispatchInfo? Failure;
    }
    internal static int Invoke(Action restore, Func<int> mutate, Action publish)
    {
        var scope = new Scope(restore, mutate, publish);
        var root = GCHandle.Alloc(scope);
        try
        {
            var owner = new Owner { Context = GCHandle.ToIntPtr(root), Restore = &Restore, Mutate = &Mutate, Publish = &Publish };
            var code = InvokeNative(in owner, out var count);
            scope.Failure?.Throw();
            if (code != 0) throw new InvalidDataException($"Native kernel history command failed ({code}).");
            return count;
        }
        finally { root.Free(); }
    }
    private static Scope Get(nint context) => (Scope)GCHandle.FromIntPtr(context).Target!;
    private static int Run(nint context, Action<Scope> action)
    {
        var scope = Get(context);
        try { action(scope); return 0; }
        catch (Exception error) { scope.Failure = ExceptionDispatchInfo.Capture(error); return 1; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Restore(nint context) => Run(context, static s => s.Restore());
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Publish(nint context) => Run(context, static s => s.Publish());
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Mutate(nint context, int* count)
    {
        var scope = Get(context);
        try { *count = scope.Mutate(); return 0; }
        catch (Exception error) { scope.Failure = ExceptionDispatchInfo.Capture(error); return 1; }
    }
    private sealed class SaveScope(Action save)
    {
        internal readonly Action Save = save;
        internal ExceptionDispatchInfo? Failure;
    }
    internal static int Clear(nint store, Action save)
    {
        var scope = new SaveScope(save);
        var root = GCHandle.Alloc(scope);
        try
        {
            var code = ClearNative(store, GCHandle.ToIntPtr(root), &Save, out var count);
            scope.Failure?.Throw();
            if (code != 0) throw new InvalidDataException($"Native kernel history mutation failed ({code}).");
            return count;
        }
        finally { root.Free(); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Save(nint context)
    {
        var scope = (SaveScope)GCHandle.FromIntPtr(context).Target!;
        try { scope.Save(); return 0; }
        catch (Exception error) { scope.Failure = ExceptionDispatchInfo.Capture(error); return 1; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Owner
    {
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, int> Restore;
        internal delegate* unmanaged[Cdecl]<nint, int*, int> Mutate;
        internal delegate* unmanaged[Cdecl]<nint, int> Publish;
    }
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_history_invoke")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int InvokeNative(in Owner owner, out int count);
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_history_clear")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int ClearNative(nint store, nint context, delegate* unmanaged[Cdecl]<nint, int> save, out int count);
}

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.KernelLeak;

namespace ResourceManager.App.Endpoints.Transport;

// H-cutover removes the Kestrel binding. Source work remains the single kernel
// owner; these fields hold request completion only, never report data or leases.
internal sealed unsafe partial class NativeKernelHistoryFamily : INativeHttpFamilyBinding
{
    private readonly Func<int> execute;
    public NativeKernelHistoryFamily(KernelLeakObserver owner) : this(owner.ClearHistory) { }
    internal NativeKernelHistoryFamily(Func<int> execute) { this.execute = execute; }
    public int RequestBodyBytes => 0;
    public void BorrowRoute(Action<nint> consume) => Borrow(null, consume);
    public INativeHttpFamilyInvocation OpenInvocation() => new Invocation(execute);

    private sealed class Invocation(Func<int> execute) : INativeHttpFamilyInvocation
    {
        private readonly object gate = new();
        private bool started, ready, released;
        private int count;
        private Exception? failure;
        public void Borrow(Action<nint> consume) => NativeKernelHistoryFamily.Borrow(this, consume);
        internal int Read(uint phase, nint wake, nint post, out int result)
        {
            lock (gate)
            {
                result = 0;
                if (phase == 3)
                {
                    if (!ready || released) throw new InvalidOperationException("Kernel command released before drain or twice.");
                    released = true;
                    return 1;
                }
                if (released) throw new ObjectDisposedException(nameof(Invocation));
                if (phase == 0)
                {
                    if (started) throw new InvalidOperationException("Kernel command accepted twice.");
                    started = true;
                    _ = Task.Run(() =>
                    {
                        int completed = 0;
                        Exception? error = null;
                        try { completed = execute(); }
                        catch (Exception caught) { error = caught; }
                        lock (gate)
                        {
                            count = completed; failure = error; ready = true;
                            // Serialize poll/release with this final post. Native
                            // wake only queues work; no pointer survives release.
                            PostCompletion(post, wake);
                        }
                    });
                }
                // Accepted clear is not rolled back on disconnect, as before.
                // Cancellation waits for persistence/publication to finish.
                if (!ready) return 0;
                result = count;
                return failure is null ? 1 : -1;
            }
        }
        public void Dispose()
        {
            lock (gate)
                if (started && !released) throw new InvalidOperationException("Kernel invocation disposed before native release.");
        }
    }
    // EventDriven: invoked exactly on original command completion; it only
    // posts the current request's dispatcher wake, never samples domain data.
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven,
        "Kernel history command completed")]
    private static void PostCompletion(nint post, nint wake)
        => ((delegate* unmanaged[Cdecl]<nint, void>)post)(wake);
    private sealed class BorrowScope(Invocation? source, Action<nint> consume)
    {
        internal readonly Invocation? Source = source;
        internal readonly Action<nint> Consume = consume;
        internal ExceptionDispatchInfo? Failure;
    }
    private static void Borrow(Invocation? invocation, Action<nint> consume)
    {
        var scope = new BorrowScope(invocation, consume);
        var root = GCHandle.Alloc(scope);
        try
        {
            var source = new Source { Context = GCHandle.ToIntPtr(root), Read = &Read };
            var code = BorrowNative(invocation is null ? null : &source, GCHandle.ToIntPtr(root), &Consume);
            scope.Failure?.Throw();
            if (code != 0) throw new InvalidDataException($"Kernel HTTP descriptor failed ({code}).");
        }
        finally { root.Free(); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Consume(nint context, nint descriptor)
    {
        var scope = (BorrowScope)GCHandle.FromIntPtr(context).Target!;
        try { scope.Consume(descriptor); return 0; }
        catch (Exception error) { scope.Failure = ExceptionDispatchInfo.Capture(error); return 1; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Read(nint context, uint phase, nint wake, delegate* unmanaged[Cdecl]<nint, void> post, int* count)
    {
        var scope = (BorrowScope)GCHandle.FromIntPtr(context).Target!;
        try { return scope.Source!.Read(phase, wake, (nint)post, out *count); }
        catch (Exception error) { scope.Failure = ExceptionDispatchInfo.Capture(error); return -1; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Source
    {
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, uint, nint, delegate* unmanaged[Cdecl]<nint, void>, int*, int> Read;
    }
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_http_kernel_history_borrow")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int BorrowNative(Source* source, nint context, delegate* unmanaged[Cdecl]<nint, nint, int> consume);
}

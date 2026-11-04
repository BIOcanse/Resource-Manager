using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Application.BrowserRuntimes;
using ResourceManager.App.Domain.BrowserRuntimes;
using ResourceManager.App.Infrastructure.ProcessIdentity;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using ResourceManager.Shared.Runtime;

namespace ResourceManager.App.Endpoints.Transport;

// H1-runtime's source binding only. Binding, permission, result selection and
// JSON traversal are Zig. The original catalog remains the sole discovery/cache
// and inventory source (R-browser); original identity/platform facts retire with
// R-identity/P-process/H3. H-cutover removes this managed transport binding.
internal sealed partial class NativeRuntimeHttpBinding(
    uint kind, Func<NativeRuntimeHttpSources> sources, nuint outputWindow = 0) : INativeHttpFamilyBinding
{
    private readonly nuint window = outputWindow == 0 ? NativeMethods.MinimumWindow() : outputWindow;
    public int RequestBodyBytes => 0;
    public void BorrowRoute(Action<nint> consume) => Borrow(null, consume);
    public INativeHttpFamilyInvocation OpenInvocation() => new Invocation(this, sources());
    internal static unsafe string PathFor(uint kind)
    {
        var pointer = NativeMethods.Path(kind, out var length);
        if (pointer == null) throw new ArgumentOutOfRangeException(nameof(kind));
        return System.Text.Encoding.UTF8.GetString(pointer, checked((int)length));
    }

    private sealed class Consumer(Action<nint> action)
    {
        internal Action<nint> Action => action;
        internal ExceptionDispatchInfo? Failure;
    }

    private unsafe void Borrow(Invocation? invocation, Action<nint> consume)
    {
        var consumer = new Consumer(consume);
        var handle = GCHandle.Alloc(consumer);
        var owner = invocation is null ? default : GCHandle.Alloc(invocation);
        try
        {
            var source = new Source
            {
                Context = invocation is null ? 0 : GCHandle.ToIntPtr(owner),
                Read = &Read, Scalar = &Scalar, Text = &Text, Date = &Date
            };
            var code = NativeMethods.Borrow(kind, invocation is null ? null : &source,
                window, GCHandle.ToIntPtr(handle), &Consume);
            consumer.Failure?.Throw();
            if (code != 0) throw new InvalidOperationException($"Runtime HTTP binding failed ({code}).");
        }
        finally { if (owner.IsAllocated) owner.Free(); handle.Free(); }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Consume(nint context, nint route)
    {
        var consumer = (Consumer)GCHandle.FromIntPtr(context).Target!;
        try { consumer.Action(route); return 0; }
        catch (Exception error) { consumer.Failure = ExceptionDispatchInfo.Capture(error); return 1; }
    }
    private static Invocation Owner(nint context) => (Invocation)GCHandle.FromIntPtr(context).Target!;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Read(nint context, uint phase, uint operation, uint refresh, nint wake,
        delegate* unmanaged[Cdecl]<nint, void> post)
    {
        try { return Owner(context).Read(phase, operation, refresh != 0, wake, (nint)post); }
        catch { return -1; } // Native owns the original empty 500 outcome.
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Scalar(nint context, uint group, nuint index, uint field, ulong* result)
    {
        try { *result = Owner(context).Scalar(group, checked((int)index), field); return 0; }
        catch { return 1; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    private static unsafe int Text(nint context, uint group, nuint index, uint field, nint sink,
        delegate* unmanaged[Cdecl]<nint, ushort*, nuint, int> visitor)
    {
        try
        {
            var text = Owner(context).Text(group, checked((int)index), field);
            fixed (char* pointer = text)
                return visitor(sink, (ushort*)pointer, checked((nuint)(text?.Length ?? 0)));
        }
        catch { return 1; }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Date(nint context, DateParts* result)
    {
        try
        {
            var date = Owner(context).Snapshot!.CapturedAt;
            *result = new() { Year = date.Year, Month = date.Month, Day = date.Day,
                Hour = date.Hour, Minute = date.Minute, Second = date.Second,
                Fraction = checked((int)(date.Ticks % TimeSpan.TicksPerSecond)),
                OffsetMinutes = checked((int)date.Offset.TotalMinutes) };
            return 0;
        }
        catch { return 1; }
    }

    // Request-scoped accepted task, cancellation and original immutable result.
    // No source pin/GCHandle or Ledger lease is retained across pending/send.
    // Completion publication and wake are joined under gate before release.
    private sealed class Invocation(NativeRuntimeHttpBinding binding, NativeRuntimeHttpSources sources) : INativeHttpFamilyInvocation
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource cancellation = new();
        private Task<BrowserRuntimeSnapshot>? task;
        private bool ready, released;
        private Exception? failure;
        private nint wakeContext, wakePost;
        internal BrowserRuntimeSnapshot? Snapshot;
        private NativeRuntimeIdentityFacts? identity;
        public void Borrow(Action<nint> consume) => binding.Borrow(this, consume);
        internal unsafe int Read(uint phase, uint operation, bool refresh, nint wake, nint post)
        {
            lock (gate)
            {
                if (phase == 3)
                {
                    if (task is not null && !ready) throw new InvalidOperationException("Runtime source work was not drained.");
                    released = true; wakePost = 0; wakeContext = 0;
                    Snapshot = null; identity = null;
                    return 1;
                }
                wakeContext = wake; wakePost = post;
                if (phase == 0)
                {
                    if (operation == 1)
                    {
                        identity = sources.ReadIdentity(); ready = true;
                    }
                    else
                    {
                        try { task = sources.Catalog!.GetSnapshotAsync(refresh, cancellation.Token); }
                        catch (Exception error) { failure = error; ready = true; }
                        if (task is { IsCompleted: true }) Finish(task, notify: false);
                        else if (task is not null) _ = task.ContinueWith(completed => Finish(completed, notify: true), CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    }
                }
                if (phase == 2) cancellation.Cancel();
                if (!ready) return 0;
                return failure is OperationCanceledException ? 2 : failure is null ? 1 : -1;
            }
        }
        [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven,
            "The original runtime catalog request Task completed or acknowledged cancellation")]
        private unsafe void Finish(Task<BrowserRuntimeSnapshot> completed, bool notify)
        {
            lock (gate)
            {
                try { Snapshot = completed.GetAwaiter().GetResult(); }
                catch (Exception error) { failure = error; }
                ready = true;
                if (notify && !released && wakePost != 0)
                    ((delegate* unmanaged[Cdecl]<nint, void>)wakePost)(wakeContext);
            }
        }
        private BrowserRuntimeEntry? Entry(uint group, int index) => group switch
        { 0 => Snapshot!.SharedRuntime, 1 => Snapshot!.BrowserFallback, 2 => Snapshot!.Candidates[index], _ => throw new ArgumentOutOfRangeException(nameof(group)) };
        internal ulong Scalar(uint group, int index, uint field)
        {
            if (group == 3) return field switch
            { 1 => BackendRuntimeIdentityContract.ProtocolVersion, 3 => sources.Identity!.InstanceId,
                4 => checked((ulong)sources.Identity!.ProcessId), 5 => checked((ulong)sources.Identity!.ProcessCreatedUtcTicks), _ => throw new ArgumentOutOfRangeException(nameof(field)) };
            if (group == 4 && field == 2) return checked((ulong)Snapshot!.Candidates.Count);
            var entry = Entry(group, index);
            return field switch { 0 => entry is null ? 0UL : 1UL,
                1 => entry!.NativeWebView2Compatible ? 1UL : 0UL, 2 => entry!.Selected ? 1UL : 0UL,
                _ => throw new ArgumentOutOfRangeException(nameof(field)) };
        }
        internal string? Text(uint group, int index, uint field)
        {
            if (group == 3) return field switch
            { 0 => BackendRuntimeIdentityContract.ProductId, 2 => identity!.BuildVersion, 6 => identity!.ExecutablePath, _ => throw new ArgumentOutOfRangeException(nameof(field)) };
            if (group == 4) return Snapshot!.InstallComponentId;
            var entry = Entry(group, index)!;
            return field switch { 0 => entry.Id, 1 => entry.Name, 2 => entry.Kind, 3 => entry.Version,
                4 => entry.ExecutablePath, 5 => entry.RuntimeDirectory, 6 => entry.Source, _ => throw new ArgumentOutOfRangeException(nameof(field)) };
        }
        public void Dispose() => cancellation.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DateParts { internal int Year, Month, Day, Hour, Minute, Second, Fraction, OffsetMinutes; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Source
    {
        internal nint Context;
        internal delegate* unmanaged[Cdecl]<nint, uint, uint, uint, nint, delegate* unmanaged[Cdecl]<nint, void>, int> Read;
        internal delegate* unmanaged[Cdecl]<nint, uint, nuint, uint, ulong*, int> Scalar;
        internal delegate* unmanaged[Cdecl]<nint, uint, nuint, uint, nint, delegate* unmanaged[Cdecl]<nint, ushort*, nuint, int>, int> Text;
        internal delegate* unmanaged[Cdecl]<nint, DateParts*, int> Date;
    }
    private static partial class NativeMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_http_runtime_minimum_window")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial nuint MinimumWindow();
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_http_runtime_path")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial byte* Path(uint kind, out nuint length);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_http_runtime_borrow")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Borrow(uint kind, Source* source, nuint window, nint context,
            delegate* unmanaged[Cdecl]<nint, nint, int> consume);
    }
}

internal sealed record NativeRuntimeIdentityFacts(string BuildVersion, string ExecutablePath);
internal sealed record NativeRuntimeHttpSources(IBrowserRuntimeCatalog? Catalog, HostManagerRuntimeIdentity? Identity,
    Func<NativeRuntimeIdentityFacts>? IdentityReader = null)
{
    internal NativeRuntimeIdentityFacts ReadIdentity()
    {
        if (IdentityReader is not null) return IdentityReader();
        var assembly = Assembly.GetEntryAssembly() ?? typeof(NativeRuntimeHttpBinding).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString() ?? "unknown";
        var path = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(path)) return new(version, Path.GetFullPath(path));
        using var process = Process.GetCurrentProcess();
        return new(version, Path.GetFullPath(WindowsProcessImagePathReader.TryRead(process)
            ?? throw new InvalidOperationException("The backend executable path is unavailable.")));
    }
}

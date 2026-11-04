using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Settings;

namespace ResourceManager.App.Endpoints.Transport;

// Z2 source bridge: managed original fields, synchronously borrowed until encoding
// completes. Resolution may be published parameters or an unpublished settings
// preview. Sending it does not resolve, save, publish or deploy anything.
// S-tier removes Resolve and source-native migration removes these field callbacks.
// CLR GC reads remain while their processes exist: W4/H3 remove each real target,
// then GC-retire removes the last consumer's capability/settings/frontend/wire.
// ASP.NET and final output plumbing retire in H1.
internal static partial class NativeMemoryCpuTierResponse
{
    internal static bool UsePublished(bool hasValue) => NativeMethods.UsePublished(hasValue ? 1U : 0U) != 0;

    internal static Task Send(HttpContext context, MemoryCpuTierResolution resolution, RuntimeConfigState? runtime)
    {
        Encode(context, resolution, runtime);
        return FlushAsync(context.Response.BodyWriter, context.RequestAborted);
    }

    private static async Task FlushAsync(PipeWriter output, CancellationToken cancellationToken)
    {
        var result = await output.FlushAsync(cancellationToken);
        if (result.IsCanceled) throw new OperationCanceledException(cancellationToken);
    }

    private static unsafe void Encode(HttpContext context, MemoryCpuTierResolution resolution, RuntimeConfigState? runtime)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        using var borrowed = new BorrowedSnapshot(resolution, runtime, context.Response.BodyWriter);
        var handle = GCHandle.Alloc(borrowed);
        try
        {
            var callbacks = new Callbacks { Scalar = &ReadScalar, Text = &VisitText };
            var output = new OutputCallbacks { Reserve = &Reserve, Commit = &Commit };
            var code = NativeMethods.EncodeStream(GCHandle.ToIntPtr(handle), in callbacks, in output);
            borrowed.Failure?.Throw();
            if (code == 5) throw new InvalidOperationException($"Memory/CPU tier option '{resolution.Options[borrowed.LastOptionIndex].Name}' has no runtime value.");
            Check(code);
        }
        finally
        {
            handle.Free();
            GC.KeepAlive(resolution);
            GC.KeepAlive(runtime);
        }
    }

    private static void Check(int code)
    {
        if (code != 0) throw new InvalidOperationException($"Native memory/CPU tier encoding failed ({code}).");
    }

    private static BorrowedSnapshot Borrow(nint context) => (BorrowedSnapshot)GCHandle.FromIntPtr(context).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int ReadScalar(nint context, nuint index, nuint child, uint field, long* value)
    {
        var borrowed = Borrow(context);
        try { *value = borrowed.ReadScalar(index, child, field); return 0; }
        catch (Exception error) { borrowed.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    // The callback consumes text from this call's immutable borrowed snapshot.
    // Its identity ends with this synchronous visit; no runtime sampling occurs.
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    private static unsafe int VisitText(nint context, nuint index, nuint child, uint field, nint sink,
        delegate* unmanaged[Cdecl]<nint, ushort*, nuint, int> visitor)
    {
        var borrowed = Borrow(context);
        try
        {
            var text = borrowed.ReadText(index, child, field);
            fixed (char* pointer = text)
                return visitor(sink, (ushort*)pointer, checked((nuint)(text?.Length ?? 0)));
        }
        catch (Exception error) { borrowed.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Reserve(nint context, nuint completed, byte** pointer, nuint* capacity)
    {
        var borrowed = Borrow(context);
        try
        {
            borrowed.Commit(completed);
            var memory = borrowed.Output.GetMemory();
            borrowed.Pin = memory.Pin();
            borrowed.Pinned = true;
            borrowed.Capacity = memory.Length;
            *pointer = (byte*)borrowed.Pin.Pointer;
            *capacity = checked((nuint)memory.Length);
            return 0;
        }
        catch (Exception error) { borrowed.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Commit(nint context, nuint completed)
    {
        var borrowed = Borrow(context);
        try { borrowed.Commit(completed); return 0; }
        catch (Exception error) { borrowed.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    // A call-scoped owner of a single output-segment pin. No Ledger lease or
    // native pointer is retained by an endpoint, queue or asynchronous closure.
    private sealed class BorrowedSnapshot(MemoryCpuTierResolution resolution, RuntimeConfigState? runtime, PipeWriter output) : IDisposable
    {
        internal PipeWriter Output => output;
        internal MemoryHandle Pin;
        internal bool Pinned;
        internal int Capacity;
        internal ExceptionDispatchInfo? Failure;

        internal void Commit(nuint completed)
        {
            if (completed > checked((nuint)Capacity)) throw new InvalidOperationException("Native segment extent exceeds its borrow.");
            if (Pinned)
            {
                // Release the address before Advance lets PipeWriter reuse it.
                Pin.Dispose();
                Pinned = false;
                Capacity = 0;
                output.Advance(checked((int)completed));
            }
            else if (completed != 0) throw new InvalidOperationException("No output segment is borrowed.");
        }

        internal int LastOptionIndex;
        private MemoryCpuTierOptionResolution Option(nuint index) => resolution.Options[checked((int)index)];
        private long? RunningGen0 => runtime is { } state ? state.Running.Gen0MaxBudget : RuntimeConfigWriter.ReadInteger(RuntimeConfigWriter.Gen0MaxBudgetKey);
        private long? RunningConserve => runtime is { } state ? state.Running.ConserveMemory : RuntimeConfigWriter.ReadInteger(RuntimeConfigWriter.ConserveMemoryKey);

        internal long ReadScalar(nuint index, nuint child, uint field) => field switch
        {
            0 => resolution.Tier,
            1 => resolution.Options.Length,
            2 => resolution.Ignored.Length,
            3 => runtime is null ? 0 : 1,
            4 => runtime!.Writable ? 1 : 0,
            5 => runtime!.Files.Count,
            6 => RunningGen0.HasValue ? 1 : 0,
            7 => RunningGen0!.Value,
            8 => RunningConserve.HasValue ? 1 : 0,
            9 => RunningConserve!.Value,
            10 => runtime!.NextStart.Gen0MaxBudget.HasValue ? 1 : 0,
            11 => runtime!.NextStart.Gen0MaxBudget!.Value,
            12 => runtime!.NextStart.ConserveMemory,
            13 => Option(index).Value.Integer.HasValue ? 1 : 0,
            14 => Option(index).Value.Integer!.Value,
            15 => Option(index).Tiers.Length,
            16 => Option(index).Tiers[checked((int)child)].Value.Integer.HasValue ? 1 : 0,
            17 => Option(index).Tiers[checked((int)child)].Value.Integer!.Value,
            18 => Option(index).Range.Minimum,
            19 => Option(index).Range.Maximum,
            20 => Option(index).Range.Unset ? 1 : 0,
            21 => Option(index).Range.Choices.Length,
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };

        internal string? ReadText(nuint index, nuint child, uint field)
        {
            if (field == 0) LastOptionIndex = checked((int)index);
            return field switch
            {
                0 => Option(index).Name,
                1 => Option(index).Unit,
                2 => Option(index).Range.Kind,
                3 => Option(index).Category,
                4 => Option(index).Source,
                5 => Option(index).Apply,
                6 => Option(index).Value.Choice,
                7 => Option(index).Tiers[checked((int)child)].Value.Choice,
                8 => Option(index).Tiers[checked((int)child)].Category,
                9 => Option(index).Range.Choices[checked((int)child)],
                10 => runtime!.Reason,
                11 => runtime!.LastFailure,
                12 => RuntimeConfigWriter.NotInstalledReason,
                13 => RuntimeConfigWriter.NativeUiRunningUnavailable,
                14 => runtime!.Files[checked((int)index)].Process,
                15 => runtime!.Files[checked((int)index)].Path,
                16 => resolution.Ignored[checked((int)index)].Code,
                17 => resolution.Ignored[checked((int)index)].Path,
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };
        }

        public void Dispose()
        {
            if (Pinned) { Pin.Dispose(); Pinned = false; }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Callbacks
    {
        internal delegate* unmanaged[Cdecl]<nint, nuint, nuint, uint, long*, int> Scalar;
        internal delegate* unmanaged[Cdecl]<nint, nuint, nuint, uint, nint, delegate* unmanaged[Cdecl]<nint, ushort*, nuint, int>, int> Text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct OutputCallbacks
    {
        internal delegate* unmanaged[Cdecl]<nint, nuint, byte**, nuint*, int> Reserve;
        internal delegate* unmanaged[Cdecl]<nint, nuint, int> Commit;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_memory_cpu_tier_use_published")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint UsePublished(uint hasValue);

        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_memory_cpu_tier_encode_stream")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int EncodeStream(nint context, in Callbacks callbacks, in OutputCallbacks output);
    }
}

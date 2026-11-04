using System.Buffers;
using System.Collections.Immutable;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Optimization.Reports;

namespace ResourceManager.App.Endpoints.Transport;

// H1 removes HTTP/output plumbing; S-param/report-rules removes the original
// plan-field bridge. The core presentation helper remains its sole owner.
internal static partial class NativeSettingsReportRulesResponse
{
    internal static async Task Send(HttpContext context, ImmutableArray<CompiledHostManagerReportRuleSetting> rules)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        Encode(context.Response.BodyWriter, rules);
        var flushed = await context.Response.BodyWriter.FlushAsync(context.RequestAborted);
        if (flushed.IsCanceled) throw new OperationCanceledException(context.RequestAborted);
    }

    internal static unsafe void Encode(PipeWriter output, ImmutableArray<CompiledHostManagerReportRuleSetting> rules)
    {
        using var borrowed = new Borrowed(rules, output);
        var handle = GCHandle.Alloc(borrowed);
        try
        {
            var callbacks = new Callbacks { Count = &Count, Value = &Value };
            var sink = new Output { Reserve = &Reserve, Commit = &Commit };
            var code = Methods.Encode(GCHandle.ToIntPtr(handle), in callbacks, in sink);
            borrowed.Failure?.Throw();
            if (code != 0) throw new InvalidOperationException($"Native settings report rules failed ({code}).");
        }
        finally { handle.Free(); }
    }

    private static Borrowed Borrow(nint context) => (Borrowed)GCHandle.FromIntPtr(context).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Count(nint context, nuint index, uint field, long* value)
    {
        var b = Borrow(context);
        try
        {
            *value = field switch
            {
                0 => b.Rules.Length,
                1 => b.Rules[checked((int)index)].EditableFields.Length,
                2 => b.Rules[checked((int)index)].Defaults.ExecutableNames?.Count ?? -1,
                3 => b.Rules[checked((int)index)].Comparison,
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };
            return 0;
        }
        catch (Exception error) { b.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Value(nint context, nuint index, nuint child, uint field, nint writer)
    {
        var b = Borrow(context);
        try
        {
            var r = b.Rules[checked((int)index)];
            return field switch
            {
                0 => String(writer, r.Name),
                1 => String(writer, HostManagerReportPresentation.ReportType(r.FactKind)),
                2 => String(writer, r.DisplayUnit),
                3 => Number(writer, r.Scale),
                4 => Number(writer, r.Step),
                5 => Number(writer, r.ThresholdMinimum),
                6 => Number(writer, r.ThresholdMaximum),
                7 => Methods.Unsigned(writer, r.CountMaximum),
                8 => Number(writer, r.ZeroActivityMaximum),
                9 => String(writer, r.EditableFields[checked((int)child)]),
                10 => Number(writer, r.Defaults.ActivationThreshold),
                11 => Number(writer, r.Defaults.ClearThreshold),
                12 => Unsigned(writer, r.Defaults.RequiredConsecutiveHits),
                13 => Unsigned(writer, r.Defaults.RequiredConsecutiveMisses),
                14 => Number(writer, r.Defaults.ZeroActivityThreshold),
                15 => String(writer, r.Defaults.ExecutableNames![checked((int)child)]),
                _ => throw new ArgumentOutOfRangeException(nameof(field))
            };
        }
        catch (Exception error) { b.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    private static int Number(nint writer, double? value) => value is { } n ? Methods.Number(writer, n) : Methods.Null(writer);
    private static int Unsigned(nint writer, uint? value) => value is { } n ? Methods.Unsigned(writer, n) : Methods.Null(writer);
    private static unsafe int String(nint writer, string text)
    {
        fixed (char* value = text) return Methods.String(writer, (ushort*)value, checked((nuint)text.Length));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe int Reserve(nint context, nuint completed, byte** pointer, nuint* capacity)
    {
        var b = Borrow(context);
        try
        {
            b.Commit(completed);
            var memory = b.Output.GetMemory();
            b.Pin = memory.Pin(); b.Pinned = true; b.Capacity = memory.Length;
            *pointer = (byte*)b.Pin.Pointer; *capacity = checked((nuint)memory.Length);
            return 0;
        }
        catch (Exception error) { b.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Commit(nint context, nuint completed)
    {
        var b = Borrow(context);
        try { b.Commit(completed); return 0; }
        catch (Exception error) { b.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    private sealed class Borrowed(ImmutableArray<CompiledHostManagerReportRuleSetting> rules, PipeWriter output) : IDisposable
    {
        internal ImmutableArray<CompiledHostManagerReportRuleSetting> Rules => rules;
        internal PipeWriter Output => output;
        internal MemoryHandle Pin;
        internal bool Pinned;
        internal int Capacity;
        internal ExceptionDispatchInfo? Failure;
        internal void Commit(nuint length)
        {
            if (length > checked((nuint)Capacity)) throw new InvalidOperationException("Invalid settings output extent.");
            if (Pinned) { Pin.Dispose(); Pinned = false; Capacity = 0; output.Advance(checked((int)length)); }
            else if (length != 0) throw new InvalidOperationException("No settings output borrow.");
        }
        public void Dispose() { if (Pinned) { Pin.Dispose(); Pinned = false; } }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Callbacks
    {
        internal delegate* unmanaged[Cdecl]<nint, nuint, uint, long*, int> Count;
        internal delegate* unmanaged[Cdecl]<nint, nuint, nuint, uint, nint, int> Value;
    }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct Output
    {
        internal delegate* unmanaged[Cdecl]<nint, nuint, byte**, nuint*, int> Reserve;
        internal delegate* unmanaged[Cdecl]<nint, nuint, int> Commit;
    }
    private static partial class Methods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_settings_report_rules_encode")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Encode(nint context, in Callbacks callbacks, in Output output);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_hook_wait_string")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int String(nint writer, ushort* text, nuint length);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_hook_wait_f64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Number(nint writer, double value);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_hook_wait_u64")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Unsigned(nint writer, ulong value);
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_hook_wait_null")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Null(nint writer);
    }
}

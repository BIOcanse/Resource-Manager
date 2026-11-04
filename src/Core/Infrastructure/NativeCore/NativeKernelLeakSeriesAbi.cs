using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Application.KernelLeak;
using ResourceManager.App.Domain.KernelLeak;

namespace ResourceManager.App.Infrastructure.NativeCore;

// Synchronous reference-only ABI. Input/output spans belong to the caller's
// Ledger region for the whole call; native state never stores their addresses.
internal static unsafe partial class NativeKernelLeakSeriesAbi
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Options
    {
        internal uint SeriesCapacity, MaximumHolders, MinimumSamples, MinimumSeconds;
        internal ulong HandleFloor, MinimumGrowth;
        internal uint EdgePercentage;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Sample
    {
        internal int Pid;
        internal uint Kind;
        internal ulong Birth, Value;
        internal fixed byte Text[20];
        internal uint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Suspect
    {
        internal int Pid;
        internal uint SampleCount;
        internal ulong Birth, Current, Growth;
        internal double Seconds, Rate;
        internal fixed byte Text[20];
        internal uint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Result
    {
        internal nuint Suspects, Judged;
    }

    internal static Options ToOptions(KernelLeakOptions value) => new()
    {
        SeriesCapacity = checked((uint)value.SeriesCapacity),
        MaximumHolders = checked((uint)value.MaximumHolders),
        MinimumSamples = checked((uint)value.MinimumSamples),
        MinimumSeconds = checked((uint)value.MinimumObservationSeconds),
        HandleFloor = checked((ulong)value.HandleFloor),
        MinimumGrowth = checked((ulong)value.MinimumGrowth),
        EdgePercentage = checked((uint)value.EdgePercentage)
    };

    internal static Sample ToSample(KernelLeakSample value)
    {
        var result = new Sample { Pid = value.ProcessId, Kind = (uint)value.ResourceKind, Value = value.Value };
        // Excluded kinds still count against the frame bound, but their identity
        // is deliberately not interpreted (the old series had the same rule).
        if (value.ResourceKind != KernelLeakSampleKind.ProcessHandles) return result;
        var key = ToKey(value.ProcessId, value.StartKey);
        if (key.Length == 0) throw new InvalidDataException("Invalid kernel leak holder identity.");
        result.Birth = key.Birth;
        result.Length = key.Length;
        new ReadOnlySpan<byte>(key.Text, checked((int)key.Length)).CopyTo(new Span<byte>(result.Text, 20));
        return result;
    }

    // A null start key is the established unavailable-PID wildcard. Raw key
    // bytes preserve distinct spellings such as 42 and 042, as the old owner did.
    internal static NativeKernelLeakItems.Key ToKey(int pid, string? start)
    {
        var result = new NativeKernelLeakItems.Key { Pid = pid };
        if (start is null) return result;
        if (pid <= 0 || start.Length > 20 || !ulong.TryParse(start, out var birth) || birth == 0)
            throw new InvalidDataException("Invalid kernel leak holder identity.");
        var size = Encoding.UTF8.GetByteCount(start);
        if (size > 20) throw new InvalidDataException("Invalid kernel leak holder identity.");
        result.Birth = birth;
        result.Length = checked((uint)size);
        Encoding.UTF8.GetBytes(start, new Span<byte>(result.Text, 20));
        return result;
    }

    internal static string KeyText(in NativeKernelLeakItems.Key value)
    {
        if (value.Length > 20) throw new InvalidDataException("Invalid native kernel leak key length.");
        fixed (byte* text = value.Text) return Encoding.UTF8.GetString(text, checked((int)value.Length));
    }

    internal static string SuspectText(in Suspect value)
    {
        if (value.Length > 20) throw new InvalidDataException("Invalid native kernel leak key length.");
        fixed (byte* text = value.Text) return Encoding.UTF8.GetString(text, checked((int)value.Length));
    }

    internal static KernelLeakSuspect FromSuspect(in Suspect value) => new(
        KernelLeakResourceKind.ProcessHandles, value.Current, value.Rate, value.Seconds,
        checked((int)value.SampleCount), value.Growth, KernelLeakHolder.Unknown(value.Pid, SuspectText(in value)));

    internal static void Check(int code)
    {
        if (code == 2) throw new OverflowException("Kernel leak holder capacity exceeded.");
        if (code != 0) throw new InvalidDataException("Invalid kernel leak observation.");
    }

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_series_size")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial nuint Size(in Options options);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_series_init")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Initialize(nint region, nuint length, in Options options);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_series_observe")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial int Observe(nint region, long ticks, Sample* samples, nuint count,
        Suspect* suspects, NativeKernelLeakItems.Key* judged, nuint capacity, out Result result);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_series_clear")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Clear(nint region);

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_leak_series_metrics")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    internal static partial void Metrics(nint region, ulong* output);
}

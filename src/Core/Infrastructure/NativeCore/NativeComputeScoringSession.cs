using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativeComputeScoringSession : IDisposable
{
    private readonly object sync = new();
    private IntPtr handle;
    private NativeComputeScoringCapacity capacity;

    public unsafe NativeComputeScoringSession(in NativeComputeScoringConfiguration configuration, ReadOnlySpan<double> cpuWeights)
    {
        var actualVersion = NativeMethods.GetAbiVersion();
        if (actualVersion != NativeComputeScoringAbi.Version)
        {
            throw new InvalidOperationException(
                $"Native compute scoring ABI version mismatch: expected 0x{NativeComputeScoringAbi.Version:X8}, actual 0x{actualVersion:X8}.");
        }

        NativeComputeScoringStatus status;
        fixed (double* weights = cpuWeights)
        {
            status = (NativeComputeScoringStatus)NativeMethods.Create(in configuration, weights,
                checked((uint)cpuWeights.Length), out handle);
        }
        if (status != NativeComputeScoringStatus.Ok || handle == IntPtr.Zero)
        {
            handle = IntPtr.Zero;
            throw new InvalidOperationException(
                $"Native compute scoring session creation failed with {status}.");
        }

        try
        {
            capacity = QueryAndValidateCapacity(handle, in configuration);
        }
        catch
        {
            NativeMethods.Destroy(Interlocked.Exchange(ref handle, IntPtr.Zero));
            throw;
        }
    }

    ~NativeComputeScoringSession() => Dispose(false);

    public NativeComputeScoringCapacity Capacity
    {
        get
        {
            lock (sync)
            {
                _ = RequireHandle();
                return capacity;
            }
        }
    }

    public static uint GetAbiVersion() => NativeMethods.GetAbiVersion();

    public unsafe NativeComputeScoringStatus Reconfigure(
        in NativeComputeScoringConfiguration configuration, ReadOnlySpan<double> cpuWeights)
    {
        lock (sync)
        {
            var current = RequireHandle();
            NativeComputeScoringStatus status;
            fixed (double* weights = cpuWeights)
            {
                status = (NativeComputeScoringStatus)NativeMethods.Reconfigure(
                    current, in configuration, weights, checked((uint)cpuWeights.Length));
            }
            if (status == NativeComputeScoringStatus.Ok)
            {
                capacity = QueryAndValidateCapacity(current, in configuration);
            }
            return status;
        }
    }

    public unsafe NativeComputeScoringStatus CalculateWeightedCpuUse(
        ReadOnlySpan<NativeComputeScoringCpuCoreInput> rows, Span<double> output)
    {
        lock (sync)
        {
            fixed (NativeComputeScoringCpuCoreInput* rowPointer = rows)
            fixed (double* outputPointer = output)
            {
                return (NativeComputeScoringStatus)NativeMethods.WeightedCpuUse(RequireHandle(),
                    rowPointer, checked((uint)rows.Length), outputPointer, checked((uint)output.Length));
            }
        }
    }

    public unsafe NativeComputeScoringStatus Score(
        in NativeComputeScoringGenerationEnvelope envelope,
        ReadOnlySpan<NativeComputeScoringProcessInput> processes,
        ReadOnlySpan<NativeComputeScoringGpuInput> gpus,
        Span<NativeComputeScoringOutput> outputs,
        ref NativeComputeScoringSnapshot snapshot)
    {
        lock (sync)
        {
            fixed (NativeComputeScoringProcessInput* processPointer = processes)
            fixed (NativeComputeScoringGpuInput* gpuPointer = gpus)
            fixed (NativeComputeScoringOutput* outputPointer = outputs)
            {
                return (NativeComputeScoringStatus)NativeMethods.Score(
                    RequireHandle(),
                    in envelope,
                    processPointer,
                    checked((uint)processes.Length),
                    gpuPointer,
                    checked((uint)gpus.Length),
                    outputPointer,
                    checked((uint)outputs.Length),
                    ref snapshot);
            }
        }
    }

    public unsafe double CalculateSoftwareBaseMean(ReadOnlySpan<double> values)
    {
        lock (sync)
        {
            fixed (double* pointer = values)
            {
                var status = (NativeComputeScoringStatus)NativeMethods.SoftwareBaseMean(
                    RequireHandle(), pointer, checked((uint)values.Length), out var mean);
                if (status != NativeComputeScoringStatus.Ok)
                    throw new InvalidDataException($"Native software base mean failed with {status}.");
                return mean;
            }
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private void Dispose(bool disposing)
    {
        _ = disposing;
        lock (sync)
        {
            var current = Interlocked.Exchange(ref handle, IntPtr.Zero);
            if (current != IntPtr.Zero)
            {
                NativeMethods.Destroy(current);
            }
        }
    }

    private IntPtr RequireHandle()
    {
        var current = Volatile.Read(ref handle);
        return current != IntPtr.Zero
            ? current
            : throw new ObjectDisposedException(nameof(NativeComputeScoringSession));
    }

    private static NativeComputeScoringCapacity QueryAndValidateCapacity(
        IntPtr session,
        in NativeComputeScoringConfiguration configuration)
    {
        var status = (NativeComputeScoringStatus)NativeMethods.QueryCapacity(
            session,
            out var result,
            SizeOf<NativeComputeScoringCapacity>());
        if (status != NativeComputeScoringStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native compute scoring capacity query failed with {status}.");
        }

        ValidateCapacity(in result, in configuration);
        return result;
    }

    private static unsafe void ValidateCapacity(
        in NativeComputeScoringCapacity result,
        in NativeComputeScoringConfiguration configuration)
    {
        if (result.AbiVersion != NativeComputeScoringAbi.Version ||
            result.StructSize != SizeOf<NativeComputeScoringCapacity>() ||
            result.ConfigurationGeneration != configuration.Generation ||
            result.ProcessInputStructSize != SizeOf<NativeComputeScoringProcessInput>() ||
            result.GpuInputStructSize != SizeOf<NativeComputeScoringGpuInput>() ||
            result.OutputStructSize != SizeOf<NativeComputeScoringOutput>() ||
            result.ProcessCapacity != configuration.MaximumProcessCount ||
            result.GpuCapacity != configuration.MaximumGpuRowCount ||
            result.OutputCapacity != configuration.MaximumOutputCount ||
            result.ProcessCapacity == 0 ||
            result.GpuCapacity == 0 ||
            result.OutputCapacity == 0 ||
            result.Reserved[0] != 0 ||
            result.Reserved[1] != 0 ||
            result.Reserved[2] != 0 ||
            result.Reserved[3] != 0)
        {
            throw new InvalidOperationException("Native compute scoring capacity contract mismatch.");
        }
    }

    private static partial class NativeMethods
    {
        private const string LibraryName = "ResourceManager.NativeCore";

        [LibraryImport(LibraryName, EntryPoint = "rm_compute_scoring_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "rm_compute_scoring_create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Create(
            in NativeComputeScoringConfiguration configuration,
            double* weights,
            uint weightCount,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_compute_scoring_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_compute_scoring_reconfigure")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Reconfigure(
            IntPtr handle,
            in NativeComputeScoringConfiguration configuration,
            double* weights,
            uint weightCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_compute_scoring_weighted_cpu_use")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int WeightedCpuUse(IntPtr handle,
            NativeComputeScoringCpuCoreInput* rows, uint rowCount, double* output, uint processCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_compute_scoring_query_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryCapacity(
            IntPtr handle,
            out NativeComputeScoringCapacity capacity,
            uint capacitySize);

        [LibraryImport(LibraryName, EntryPoint = "rm_compute_scoring_software_base_mean")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int SoftwareBaseMean(
            IntPtr handle, double* values, uint count, out double mean);

        [LibraryImport(LibraryName, EntryPoint = "rm_compute_scoring_score")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Score(
            IntPtr handle,
            in NativeComputeScoringGenerationEnvelope envelope,
            NativeComputeScoringProcessInput* processes,
            uint processCapacity,
            NativeComputeScoringGpuInput* gpus,
            uint gpuCapacity,
            NativeComputeScoringOutput* outputs,
            uint outputCapacity,
            ref NativeComputeScoringSnapshot snapshot);
    }
}

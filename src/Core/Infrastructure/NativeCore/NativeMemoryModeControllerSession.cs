using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativeMemoryModeControllerSession : IDisposable
{
    private readonly object sync = new();
    private IntPtr handle;
    private NativeMemoryModeCapacity capacity;

    public NativeMemoryModeControllerSession(in NativeMemoryModeConfiguration configuration)
    {
        var actualVersion = NativeMethods.GetAbiVersion();
        if (actualVersion != NativeMemoryModeControllerAbi.Version)
        {
            throw new InvalidOperationException(
                $"Native memory mode controller ABI version mismatch: expected 0x{NativeMemoryModeControllerAbi.Version:X8}, actual 0x{actualVersion:X8}.");
        }

        var status = (NativeMemoryModeControllerStatus)NativeMethods.Create(
            in configuration,
            out handle);
        if (status != NativeMemoryModeControllerStatus.Ok || handle == IntPtr.Zero)
        {
            handle = IntPtr.Zero;
            throw new InvalidOperationException(
                $"Native memory mode controller creation failed with {status}.");
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

    ~NativeMemoryModeControllerSession() => Dispose(false);

    public NativeMemoryModeCapacity Capacity
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

    public NativeMemoryModeControllerStatus Reconfigure(
        in NativeMemoryModeConfiguration configuration)
    {
        lock (sync)
        {
            var current = RequireHandle();
            var status = (NativeMemoryModeControllerStatus)NativeMethods.Reconfigure(
                current,
                in configuration);
            if (status == NativeMemoryModeControllerStatus.Ok)
            {
                capacity = QueryAndValidateCapacity(current, in configuration);
            }
            return status;
        }
    }

    public unsafe NativeMemoryModeControllerStatus Plan(
        in NativeMemoryModeGenerationEnvelope envelope,
        ReadOnlySpan<NativeMemoryModeSoftwareInput> software,
        Span<NativeMemoryModeDesiredSoftwareOutput> outputs,
        ref NativeMemoryModeSnapshot snapshot)
    {
        lock (sync)
        {
            fixed (NativeMemoryModeSoftwareInput* softwarePointer = software)
            fixed (NativeMemoryModeDesiredSoftwareOutput* outputPointer = outputs)
            {
                return (NativeMemoryModeControllerStatus)NativeMethods.Plan(
                    RequireHandle(),
                    in envelope,
                    softwarePointer,
                    checked((uint)software.Length),
                    outputPointer,
                    checked((uint)outputs.Length),
                    ref snapshot);
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
            : throw new ObjectDisposedException(nameof(NativeMemoryModeControllerSession));
    }

    private static NativeMemoryModeCapacity QueryAndValidateCapacity(
        IntPtr session,
        in NativeMemoryModeConfiguration configuration)
    {
        var status = (NativeMemoryModeControllerStatus)NativeMethods.QueryCapacity(
            session,
            out var result,
            SizeOf<NativeMemoryModeCapacity>());
        if (status != NativeMemoryModeControllerStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native memory mode capacity query failed with {status}.");
        }
        ValidateCapacity(in result, in configuration);
        return result;
    }

    private static unsafe void ValidateCapacity(
        in NativeMemoryModeCapacity value,
        in NativeMemoryModeConfiguration configuration)
    {
        if (value.AbiVersion != NativeMemoryModeControllerAbi.Version ||
            value.StructSize != SizeOf<NativeMemoryModeCapacity>() ||
            value.ConfigurationGeneration != configuration.Generation ||
            value.SoftwareInputStructSize != SizeOf<NativeMemoryModeSoftwareInput>() ||
            value.SoftwareOutputStructSize != SizeOf<NativeMemoryModeDesiredSoftwareOutput>() ||
            value.SoftwareCapacity != configuration.MaximumSoftwareCount ||
            value.OutputCapacity != configuration.MaximumOutputCount ||
            value.Reserved[0] != 0 || value.Reserved[1] != 0 ||
            value.Reserved[2] != 0 || value.Reserved[3] != 0)
        {
            throw new InvalidOperationException(
                "Native memory mode controller capacity contract mismatch.");
        }
    }

    private static partial class NativeMethods
    {
        private const string LibraryName = "ResourceManager.NativeCore";

        [LibraryImport(LibraryName, EntryPoint = "rm_memory_mode_controller_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "rm_memory_mode_controller_create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Create(
            in NativeMemoryModeConfiguration configuration,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_memory_mode_controller_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_memory_mode_controller_reconfigure")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reconfigure(
            IntPtr handle,
            in NativeMemoryModeConfiguration configuration);

        [LibraryImport(LibraryName, EntryPoint = "rm_memory_mode_controller_query_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryCapacity(
            IntPtr handle,
            out NativeMemoryModeCapacity capacity,
            uint capacitySize);

        [LibraryImport(LibraryName, EntryPoint = "rm_memory_mode_controller_plan")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Plan(
            IntPtr handle,
            in NativeMemoryModeGenerationEnvelope envelope,
            NativeMemoryModeSoftwareInput* software,
            uint softwareCapacity,
            NativeMemoryModeDesiredSoftwareOutput* outputs,
            uint outputCapacity,
            ref NativeMemoryModeSnapshot snapshot);
    }
}

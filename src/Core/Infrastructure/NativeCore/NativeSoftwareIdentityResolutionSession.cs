using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativeSoftwareIdentityResolutionSession : IDisposable
{
    private readonly object sync = new();
    private IntPtr handle;
    private NativeSoftwareIdentityResolutionCapacity capacity;
    private ulong configurationGeneration;

    public NativeSoftwareIdentityResolutionSession(
        in NativeSoftwareIdentityResolutionConfiguration configuration)
    {
        var actualVersion = NativeMethods.GetAbiVersion();
        if (actualVersion != NativeSoftwareIdentityResolutionAbi.Version)
        {
            throw new InvalidOperationException(
                $"Native software identity resolution ABI mismatch: expected 0x{NativeSoftwareIdentityResolutionAbi.Version:X8}, actual 0x{actualVersion:X8}.");
        }

        var status = (NativeSoftwareIdentityStatus)NativeMethods.Create(in configuration, out handle);
        if (status != NativeSoftwareIdentityStatus.Ok || handle == IntPtr.Zero)
        {
            handle = IntPtr.Zero;
            throw new InvalidOperationException(
                $"Native software identity resolution session creation failed with {status}.");
        }

        try
        {
            capacity = QueryAndValidateCapacity(handle, in configuration);
            configurationGeneration = configuration.Generation;
        }
        catch
        {
            NativeMethods.Destroy(Interlocked.Exchange(ref handle, IntPtr.Zero));
            throw;
        }
    }

    ~NativeSoftwareIdentityResolutionSession() => Dispose(false);

    internal NativeSoftwareIdentityResolutionCapacity Capacity
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

    internal ulong ConfigurationGeneration
    {
        get
        {
            lock (sync)
            {
                _ = RequireHandle();
                return configurationGeneration;
            }
        }
    }

    internal static uint GetAbiVersion() => NativeMethods.GetAbiVersion();

    internal NativeSoftwareIdentityStatus Reconfigure(
        in NativeSoftwareIdentityResolutionConfiguration configuration)
    {
        lock (sync)
        {
            var current = RequireHandle();
            var status = (NativeSoftwareIdentityStatus)NativeMethods.Reconfigure(
                current,
                in configuration);
            if (status == NativeSoftwareIdentityStatus.Ok)
            {
                capacity = QueryAndValidateCapacity(current, in configuration);
                configurationGeneration = configuration.Generation;
            }
            return status;
        }
    }

    internal unsafe NativeSoftwareIdentityStatus ReplacePolicy(
        in NativeSoftwareIdentityPolicyReplaceInput input,
        ReadOnlySpan<NativeSoftwareIdentitySourcePolicyInput> policies)
    {
        lock (sync)
        {
            fixed (NativeSoftwareIdentitySourcePolicyInput* policyPointer = policies)
            {
                return (NativeSoftwareIdentityStatus)NativeMethods.ReplacePolicy(
                    RequireHandle(),
                    in input,
                    policyPointer,
                    checked((uint)policies.Length));
            }
        }
    }

    internal unsafe NativeSoftwareIdentityStatus Resolve(
        in NativeSoftwareIdentityResolveInput input,
        ReadOnlySpan<NativeSoftwareIdentityObservationInput> observations,
        out NativeSoftwareIdentityResolutionOutput output)
    {
        lock (sync)
        {
            output = new NativeSoftwareIdentityResolutionOutput
            {
                AbiVersion = NativeSoftwareIdentityResolutionAbi.Version,
                StructSize = SizeOf<NativeSoftwareIdentityResolutionOutput>()
            };
            fixed (NativeSoftwareIdentityObservationInput* observationPointer = observations)
            {
                return (NativeSoftwareIdentityStatus)NativeMethods.Resolve(
                    RequireHandle(),
                    in input,
                    observationPointer,
                    checked((uint)observations.Length),
                    ref output);
            }
        }
    }

    internal NativeSoftwareIdentityStatus Snapshot(
        out NativeSoftwareIdentityResolutionSummary output)
    {
        lock (sync)
        {
            output = new NativeSoftwareIdentityResolutionSummary
            {
                AbiVersion = NativeSoftwareIdentityResolutionAbi.Version,
                StructSize = SizeOf<NativeSoftwareIdentityResolutionSummary>()
            };
            return (NativeSoftwareIdentityStatus)NativeMethods.Snapshot(
                RequireHandle(),
                ref output);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

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
            : throw new ObjectDisposedException(nameof(NativeSoftwareIdentityResolutionSession));
    }

    private static NativeSoftwareIdentityResolutionCapacity QueryAndValidateCapacity(
        IntPtr session,
        in NativeSoftwareIdentityResolutionConfiguration configuration)
    {
        var status = (NativeSoftwareIdentityStatus)NativeMethods.QueryCapacity(
            session,
            out var result,
            SizeOf<NativeSoftwareIdentityResolutionCapacity>());
        if (status != NativeSoftwareIdentityStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native software identity resolution capacity query failed with {status}.");
        }

        unsafe
        {
            if (result.StructSize != SizeOf<NativeSoftwareIdentityResolutionCapacity>()
                || result.PolicyCapacity != configuration.MaximumPolicyCount
                || result.ObservationCapacity != configuration.MaximumObservationCount
                || result.PolicyIndexCapacity != configuration.PolicyIndexCapacity
                || result.ResidentByteCount == 0
                || result.ResidentByteCount > configuration.ResidentByteBudget
                || result.Reserved[0] != 0
                || result.Reserved[1] != 0
                || result.Reserved[2] != 0
                || result.Reserved[3] != 0
                || result.Reserved[4] != 0)
            {
                throw new InvalidOperationException(
                    "Native software identity resolution capacity contract mismatch.");
            }
        }

        return result;
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static partial class NativeMethods
    {
        private const string LibraryName = "ResourceManager.NativeCore";

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_resolution_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_resolution_create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Create(
            in NativeSoftwareIdentityResolutionConfiguration configuration,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_resolution_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_resolution_reconfigure")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reconfigure(
            IntPtr handle,
            in NativeSoftwareIdentityResolutionConfiguration configuration);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_resolution_query_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryCapacity(
            IntPtr handle,
            out NativeSoftwareIdentityResolutionCapacity capacity,
            uint outputSize);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_resolution_replace_policy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ReplacePolicy(
            IntPtr handle,
            in NativeSoftwareIdentityPolicyReplaceInput input,
            NativeSoftwareIdentitySourcePolicyInput* policies,
            uint policyCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_resolution_resolve")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Resolve(
            IntPtr handle,
            in NativeSoftwareIdentityResolveInput input,
            NativeSoftwareIdentityObservationInput* observations,
            uint observationCount,
            ref NativeSoftwareIdentityResolutionOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_resolution_snapshot")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Snapshot(
            IntPtr handle,
            ref NativeSoftwareIdentityResolutionSummary output);
    }
}

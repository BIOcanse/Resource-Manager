using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativePortableSoftwareRegistrySession : IDisposable
{
    private readonly object sync = new();
    private IntPtr handle;
    private NativePortableSoftwareRegistryCapacity capacity;
    private ulong configurationGeneration;

    public NativePortableSoftwareRegistrySession(
        in NativePortableSoftwareRegistryConfiguration configuration)
    {
        var actualVersion = NativeMethods.GetAbiVersion();
        if (actualVersion != NativePortableSoftwareRegistryAbi.Version)
        {
            throw new InvalidOperationException(
                $"Native portable software registry ABI version mismatch: expected 0x{NativePortableSoftwareRegistryAbi.Version:X8}, actual 0x{actualVersion:X8}.");
        }

        var status = (NativePortableSoftwareRegistryStatus)NativeMethods.Create(
            in configuration,
            out handle);
        if (status != NativePortableSoftwareRegistryStatus.Ok || handle == IntPtr.Zero)
        {
            handle = IntPtr.Zero;
            throw new InvalidOperationException(
                $"Native portable software registry session creation failed with {status}.");
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

    ~NativePortableSoftwareRegistrySession() => Dispose(false);

    public NativePortableSoftwareRegistryCapacity Capacity
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

    public NativePortableSoftwareRegistryStatus Reconfigure(
        in NativePortableSoftwareRegistryConfiguration configuration)
    {
        lock (sync)
        {
            var current = RequireHandle();
            var status = (NativePortableSoftwareRegistryStatus)NativeMethods.Reconfigure(
                current,
                in configuration);
            if (status == NativePortableSoftwareRegistryStatus.Ok)
            {
                capacity = QueryAndValidateCapacity(current, in configuration);
                configurationGeneration = configuration.Generation;
            }
            return status;
        }
    }

    public unsafe NativePortableSoftwareRegistryStatus Import(
        in NativePortableSoftwareImportInput input,
        ReadOnlySpan<NativePortableSoftwarePersistedPathInput> rows,
        ReadOnlySpan<byte> keyBytes)
    {
        lock (sync)
        {
            fixed (NativePortableSoftwarePersistedPathInput* rowPointer = rows)
            fixed (byte* keyPointer = keyBytes)
            {
                return (NativePortableSoftwareRegistryStatus)NativeMethods.Import(
                    RequireHandle(),
                    in input,
                    rowPointer,
                    checked((uint)rows.Length),
                    keyPointer,
                    checked((uint)keyBytes.Length));
            }
        }
    }

    public unsafe NativePortableSoftwareRegistryStatus Observe(
        in NativePortableSoftwareObserveInput input,
        ReadOnlySpan<byte> keyBytes)
    {
        lock (sync)
        {
            fixed (byte* keyPointer = keyBytes)
            {
                return (NativePortableSoftwareRegistryStatus)NativeMethods.Observe(
                    RequireHandle(),
                    in input,
                    keyPointer,
                    checked((uint)keyBytes.Length));
            }
        }
    }

    public unsafe NativePortableSoftwareRegistryStatus ConfirmRoot(
        in NativePortableSoftwareConfirmRootInput input,
        ReadOnlySpan<byte> keyBytes,
        out uint confirmedCount)
    {
        lock (sync)
        {
            confirmedCount = 0;
            fixed (byte* keyPointer = keyBytes)
            {
                return (NativePortableSoftwareRegistryStatus)NativeMethods.ConfirmRoot(
                    RequireHandle(),
                    in input,
                    keyPointer,
                    checked((uint)keyBytes.Length),
                    ref confirmedCount);
            }
        }
    }

    public NativePortableSoftwareRegistryStatus MarkMissing(
        in NativePortableSoftwareMarkMissingInput input)
    {
        lock (sync)
        {
            return (NativePortableSoftwareRegistryStatus)NativeMethods.MarkMissing(
                RequireHandle(),
                in input);
        }
    }

    public unsafe NativePortableSoftwareRegistryStatus PlanPersistence(
        in NativePortableSoftwarePlanPersistenceInput input,
        Span<NativePortableSoftwarePersistenceOperation> operations,
        out NativePortableSoftwarePersistencePlanOutput output)
    {
        lock (sync)
        {
            output = default;
            fixed (NativePortableSoftwarePersistenceOperation* operationPointer = operations)
            {
                return (NativePortableSoftwareRegistryStatus)NativeMethods.PlanPersistence(
                    RequireHandle(),
                    in input,
                    operationPointer,
                    checked((uint)operations.Length),
                    ref output);
            }
        }
    }

    public unsafe NativePortableSoftwareRegistryStatus ApplyFeedback(
        in NativePortableSoftwarePersistenceFeedbackInput input,
        ReadOnlySpan<NativePortableSoftwarePersistenceFeedback> feedback)
    {
        lock (sync)
        {
            fixed (NativePortableSoftwarePersistenceFeedback* feedbackPointer = feedback)
            {
                return (NativePortableSoftwareRegistryStatus)NativeMethods.ApplyFeedback(
                    RequireHandle(),
                    in input,
                    feedbackPointer,
                    checked((uint)feedback.Length));
            }
        }
    }

    public unsafe NativePortableSoftwareRegistryStatus Snapshot(
        in NativePortableSoftwareSnapshotInput input,
        Span<NativePortableSoftwareRegistrationSnapshot> registrations,
        Span<NativePortableSoftwarePathSnapshot> paths,
        out NativePortableSoftwareSnapshotOutput output)
    {
        lock (sync)
        {
            output = default;
            fixed (NativePortableSoftwareRegistrationSnapshot* registrationPointer = registrations)
            fixed (NativePortableSoftwarePathSnapshot* pathPointer = paths)
            {
                return (NativePortableSoftwareRegistryStatus)NativeMethods.Snapshot(
                    RequireHandle(),
                    in input,
                    registrationPointer,
                    checked((uint)registrations.Length),
                    pathPointer,
                    checked((uint)paths.Length),
                    ref output);
            }
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
            : throw new ObjectDisposedException(nameof(NativePortableSoftwareRegistrySession));
    }

    private static NativePortableSoftwareRegistryCapacity QueryAndValidateCapacity(
        IntPtr session,
        in NativePortableSoftwareRegistryConfiguration configuration)
    {
        var status = (NativePortableSoftwareRegistryStatus)NativeMethods.QueryCapacity(
            session,
            out var result,
            SizeOf<NativePortableSoftwareRegistryCapacity>());
        if (status != NativePortableSoftwareRegistryStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native portable software registry capacity query failed with {status}.");
        }

        unsafe
        {
            if (result.StructSize != SizeOf<NativePortableSoftwareRegistryCapacity>()
                || result.RegistrationCapacity != configuration.MaximumRegistrationCount
                || result.PathCapacity != configuration.MaximumPathCount
                || result.PersistenceOperationCapacity != configuration.MaximumPersistenceOperationCount
                || result.RegistrationSnapshotCapacity != configuration.MaximumRegistrationSnapshotCount
                || result.PathSnapshotCapacity != configuration.MaximumPathSnapshotCount
                || result.ExecutablePathByteCapacityPerPath != configuration.MaximumExecutablePathByteCount
                || result.RootPathByteCapacityPerPath != configuration.MaximumRootPathByteCount
                || result.RegistrationIndexCapacity != configuration.RegistrationIndexCapacity
                || result.PathIndexCapacity != configuration.PathIndexCapacity
                || result.ReservedU32 != 0
                || result.ResidentByteCount == 0
                || result.ResidentByteCount > configuration.ResidentByteBudget
                || result.Reserved[0] != 0
                || result.Reserved[1] != 0
                || result.Reserved[2] != 0
                || result.Reserved[3] != 0)
            {
                throw new InvalidOperationException(
                    "Native portable software registry capacity contract mismatch.");
            }
        }

        return result;
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static partial class NativeMethods
    {
        private const string LibraryName = "ResourceManager.NativeCore";

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_portable_registry_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_portable_registry_create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Create(
            in NativePortableSoftwareRegistryConfiguration configuration,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_portable_registry_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_portable_registry_reconfigure")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reconfigure(
            IntPtr handle,
            in NativePortableSoftwareRegistryConfiguration configuration);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_portable_registry_query_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryCapacity(
            IntPtr handle,
            out NativePortableSoftwareRegistryCapacity capacity,
            uint outputSize);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_portable_registry_import")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Import(
            IntPtr handle,
            in NativePortableSoftwareImportInput input,
            NativePortableSoftwarePersistedPathInput* rows,
            uint rowCount,
            byte* keyBytes,
            uint keyByteCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_portable_registry_observe")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Observe(
            IntPtr handle,
            in NativePortableSoftwareObserveInput input,
            byte* keyBytes,
            uint keyByteCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_portable_registry_confirm_root")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ConfirmRoot(
            IntPtr handle,
            in NativePortableSoftwareConfirmRootInput input,
            byte* keyBytes,
            uint keyByteCount,
            ref uint confirmedCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_portable_registry_mark_missing")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int MarkMissing(
            IntPtr handle,
            in NativePortableSoftwareMarkMissingInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_portable_registry_plan_persistence")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int PlanPersistence(
            IntPtr handle,
            in NativePortableSoftwarePlanPersistenceInput input,
            NativePortableSoftwarePersistenceOperation* operations,
            uint operationCapacity,
            ref NativePortableSoftwarePersistencePlanOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_portable_registry_apply_feedback")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int ApplyFeedback(
            IntPtr handle,
            in NativePortableSoftwarePersistenceFeedbackInput input,
            NativePortableSoftwarePersistenceFeedback* feedback,
            uint feedbackCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_portable_registry_snapshot")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Snapshot(
            IntPtr handle,
            in NativePortableSoftwareSnapshotInput input,
            NativePortableSoftwareRegistrationSnapshot* registrations,
            uint registrationCapacity,
            NativePortableSoftwarePathSnapshot* paths,
            uint pathCapacity,
            ref NativePortableSoftwareSnapshotOutput output);
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal interface INativeAppliedOwnershipSession : IDisposable
{
    NativeAppliedOwnershipCapacity Capacity { get; }

    NativeAppliedOwnershipStatus Promote(in NativeAppliedOwnershipPromoteInput input);

    NativeAppliedOwnershipStatus PlanTransition(
        in NativeAppliedOwnershipPrimaryIdentity primary,
        in NativeAppliedOwnershipOriginalBinding transitionBinding,
        in NativeAppliedOwnershipDurablePayloadReference transitionPayload,
        out NativeAppliedOwnershipTransitionInput transition);

    NativeAppliedOwnershipStatus Transition(in NativeAppliedOwnershipTransitionInput input);

    NativeAppliedOwnershipStatus Remove(in NativeAppliedOwnershipRemoveInput input);

    NativeAppliedOwnershipStatus Get(
        in NativeAppliedOwnershipPrimaryIdentity primary,
        out NativeAppliedOwnershipRecord record);

    NativeAppliedOwnershipStatus GetSnapshot(
        ref NativeAppliedOwnershipSnapshotHeader header,
        Span<NativeAppliedOwnershipRecord> records);

    NativeAppliedOwnershipStatus Encode(Span<byte> image, out ulong written);

    NativeAppliedOwnershipStatus DecodeReplace(ReadOnlySpan<byte> image);
}

internal interface INativeAppliedOwnershipSessionFactory
{
    INativeAppliedOwnershipSession Create(
        in NativeAppliedOwnershipCreateConfiguration configuration);

    NativeAppliedOwnershipStatus TryOpenExisting(
        in NativeAppliedOwnershipOpenConfiguration configuration,
        ReadOnlySpan<byte> image,
        out INativeAppliedOwnershipSession? session);
}

internal sealed class NativeAppliedOwnershipSessionFactory
    : INativeAppliedOwnershipSessionFactory
{
    public INativeAppliedOwnershipSession Create(
        in NativeAppliedOwnershipCreateConfiguration configuration)
        => new NativeAppliedOwnershipSession(in configuration);

    public NativeAppliedOwnershipStatus TryOpenExisting(
        in NativeAppliedOwnershipOpenConfiguration configuration,
        ReadOnlySpan<byte> image,
        out INativeAppliedOwnershipSession? session)
    {
        var status = NativeAppliedOwnershipSession.TryOpenExisting(
            in configuration,
            image,
            out var nativeSession);
        session = nativeSession;
        return status;
    }
}

internal sealed partial class NativeAppliedOwnershipSession
    : INativeAppliedOwnershipSession
{
    private readonly object sync = new();
    private readonly NativeAppliedOwnershipHandle handle;
    private readonly NativeAppliedOwnershipCapacity capacity;

    public NativeAppliedOwnershipSession(
        in NativeAppliedOwnershipCreateConfiguration configuration)
    {
        EnsureAbi();
        var result = (NativeAppliedOwnershipStatus)NativeMethods.CreateNew(
            in configuration,
            out var nativeHandle);
        if (result != NativeAppliedOwnershipStatus.Ok)
        {
            if (nativeHandle != IntPtr.Zero)
            {
                NativeMethods.Destroy(nativeHandle);
                throw new InvalidOperationException(
                    "Native applied ownership returned a handle on failed creation.");
            }
            throw new InvalidOperationException(
                $"Native applied ownership creation failed with {result}.");
        }
        if (nativeHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                "Native applied ownership returned no handle on successful creation.");
        }

        handle = new NativeAppliedOwnershipHandle(nativeHandle);
        try
        {
            capacity = QueryAndValidateCapacity(
                handle.DangerousGetHandle(),
                configuration.RecordCapacity,
                configuration.PrimaryIndexCapacity,
                configuration.PayloadIndexCapacity,
                configuration.MaximumImageBytes,
                configuration.MaximumResidentBytes);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private NativeAppliedOwnershipSession(
        IntPtr nativeHandle,
        in NativeAppliedOwnershipOpenConfiguration configuration)
    {
        handle = new NativeAppliedOwnershipHandle(nativeHandle);
        try
        {
            capacity = QueryAndValidateCapacity(
                handle.DangerousGetHandle(),
                configuration.MaximumRecordCapacity,
                configuration.MaximumPrimaryIndexCapacity,
                configuration.MaximumPayloadIndexCapacity,
                configuration.MaximumImageBytes,
                configuration.MaximumResidentBytes);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public NativeAppliedOwnershipCapacity Capacity
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

    public static unsafe NativeAppliedOwnershipStatus TryOpenExisting(
        in NativeAppliedOwnershipOpenConfiguration configuration,
        ReadOnlySpan<byte> image,
        out NativeAppliedOwnershipSession? session)
    {
        EnsureAbi();
        session = null;
        fixed (byte* imagePointer = image)
        {
            var result = (NativeAppliedOwnershipStatus)NativeMethods.OpenExisting(
                in configuration,
                imagePointer,
                checked((ulong)image.Length),
                out var nativeHandle);
            if (result != NativeAppliedOwnershipStatus.Ok)
            {
                if (nativeHandle != IntPtr.Zero)
                {
                    NativeMethods.Destroy(nativeHandle);
                    throw new InvalidOperationException(
                        "Native applied ownership returned a handle on failed open.");
                }
                return result;
            }
            if (nativeHandle == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Native applied ownership returned no handle on successful open.");
            }

            session = new NativeAppliedOwnershipSession(nativeHandle, in configuration);
            return NativeAppliedOwnershipStatus.Ok;
        }
    }

    public NativeAppliedOwnershipStatus Promote(
        in NativeAppliedOwnershipPromoteInput input)
    {
        lock (sync)
        {
            return (NativeAppliedOwnershipStatus)NativeMethods.Promote(
                RequireHandle(),
                in input);
        }
    }

    public NativeAppliedOwnershipStatus Transition(
        in NativeAppliedOwnershipTransitionInput input)
    {
        lock (sync)
        {
            return (NativeAppliedOwnershipStatus)NativeMethods.Transition(
                RequireHandle(),
                in input);
        }
    }

    public NativeAppliedOwnershipStatus PlanTransition(
        in NativeAppliedOwnershipPrimaryIdentity primary,
        in NativeAppliedOwnershipOriginalBinding transitionBinding,
        in NativeAppliedOwnershipDurablePayloadReference transitionPayload,
        out NativeAppliedOwnershipTransitionInput transition)
    {
        lock (sync)
        {
            return (NativeAppliedOwnershipStatus)NativeMethods.PlanTransition(
                RequireHandle(),
                in primary,
                in transitionBinding,
                in transitionPayload,
                out transition);
        }
    }

    public NativeAppliedOwnershipStatus Remove(in NativeAppliedOwnershipRemoveInput input)
    {
        lock (sync)
        {
            return (NativeAppliedOwnershipStatus)NativeMethods.Remove(
                RequireHandle(),
                in input);
        }
    }

    public NativeAppliedOwnershipStatus Get(
        in NativeAppliedOwnershipPrimaryIdentity primary,
        out NativeAppliedOwnershipRecord record)
    {
        lock (sync)
        {
            return (NativeAppliedOwnershipStatus)NativeMethods.Get(
                RequireHandle(),
                in primary,
                out record);
        }
    }

    public unsafe NativeAppliedOwnershipStatus GetSnapshot(
        ref NativeAppliedOwnershipSnapshotHeader header,
        Span<NativeAppliedOwnershipRecord> records)
    {
        lock (sync)
        {
            fixed (NativeAppliedOwnershipRecord* recordsPointer = records)
            {
                return (NativeAppliedOwnershipStatus)NativeMethods.Snapshot(
                    RequireHandle(),
                    ref header,
                    recordsPointer,
                    checked((uint)records.Length));
            }
        }
    }

    public unsafe NativeAppliedOwnershipStatus Encode(Span<byte> image, out ulong written)
    {
        lock (sync)
        {
            fixed (byte* imagePointer = image)
            {
                return (NativeAppliedOwnershipStatus)NativeMethods.Encode(
                    RequireHandle(),
                    imagePointer,
                    checked((ulong)image.Length),
                    out written);
            }
        }
    }

    public unsafe NativeAppliedOwnershipStatus DecodeReplace(ReadOnlySpan<byte> image)
    {
        lock (sync)
        {
            fixed (byte* imagePointer = image)
            {
                return (NativeAppliedOwnershipStatus)NativeMethods.DecodeReplace(
                    RequireHandle(),
                    imagePointer,
                    checked((ulong)image.Length));
            }
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            handle.Dispose();
        }
    }

    public static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private IntPtr RequireHandle()
        => !handle.IsClosed && !handle.IsInvalid
            ? handle.DangerousGetHandle()
            : throw new ObjectDisposedException(nameof(NativeAppliedOwnershipSession));

    private static void EnsureAbi()
    {
        var actualVersion = NativeMethods.GetAbiVersion();
        if (actualVersion != NativeAppliedOwnershipAbi.Version)
        {
            throw new InvalidOperationException(
                $"Native applied ownership ABI version mismatch: expected 0x{NativeAppliedOwnershipAbi.Version:X8}, actual 0x{actualVersion:X8}.");
        }
    }

    private static NativeAppliedOwnershipCapacity QueryAndValidateCapacity(
        IntPtr nativeHandle,
        uint maximumRecordCapacity,
        uint maximumPrimaryIndexCapacity,
        uint maximumPayloadIndexCapacity,
        ulong maximumImageBytes,
        ulong maximumResidentBytes)
    {
        var result = (NativeAppliedOwnershipStatus)NativeMethods.QueryCapacity(
            nativeHandle,
            out var nativeCapacity,
            SizeOf<NativeAppliedOwnershipCapacity>());
        if (result != NativeAppliedOwnershipStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native applied ownership capacity query failed with {result}.");
        }

        unsafe
        {
            var minimumImageBytes = checked(
                NativeAppliedOwnershipAbi.ImageHeaderSize +
                (ulong)nativeCapacity.RecordCapacity * NativeAppliedOwnershipAbi.RecordSize);
            if (nativeCapacity.StructSize != NativeAppliedOwnershipAbi.CapacitySize ||
                nativeCapacity.RecordCapacity == 0 ||
                nativeCapacity.RecordCapacity > maximumRecordCapacity ||
                nativeCapacity.PrimaryIndexCapacity < nativeCapacity.RecordCapacity ||
                nativeCapacity.PrimaryIndexCapacity > maximumPrimaryIndexCapacity ||
                !IsPowerOfTwo(nativeCapacity.PrimaryIndexCapacity) ||
                nativeCapacity.PayloadIndexCapacity < nativeCapacity.RecordCapacity ||
                nativeCapacity.PayloadIndexCapacity > maximumPayloadIndexCapacity ||
                !IsPowerOfTwo(nativeCapacity.PayloadIndexCapacity) ||
                nativeCapacity.RecordSize != NativeAppliedOwnershipAbi.RecordSize ||
                nativeCapacity.Flags != 0 ||
                nativeCapacity.MaximumImageBytes < minimumImageBytes ||
                nativeCapacity.MaximumImageBytes > maximumImageBytes ||
                nativeCapacity.ResidentBytes == 0 ||
                nativeCapacity.ResidentBytes > maximumResidentBytes ||
                nativeCapacity.Reserved[0] != 0 ||
                nativeCapacity.Reserved[1] != 0 ||
                nativeCapacity.Reserved[2] != 0)
            {
                throw new InvalidOperationException(
                    "Native applied ownership capacity contract mismatch.");
            }
        }
        return nativeCapacity;
    }

    private static bool IsPowerOfTwo(uint value)
        => value != 0 && (value & (value - 1)) == 0;

    private sealed class NativeAppliedOwnershipHandle
        : SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeAppliedOwnershipHandle(IntPtr nativeHandle)
            : base(ownsHandle: true)
            => SetHandle(nativeHandle);

        protected override bool ReleaseHandle()
        {
            NativeMethods.Destroy(handle);
            return true;
        }
    }

    private static partial class NativeMethods
    {
        private const string LibraryName = "ResourceManager.NativeCore";

        [LibraryImport(LibraryName, EntryPoint = "rm_applied_ownership_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "rm_applied_ownership_create_new")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int CreateNew(
            in NativeAppliedOwnershipCreateConfiguration configuration,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_applied_ownership_open_existing")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int OpenExisting(
            in NativeAppliedOwnershipOpenConfiguration configuration,
            byte* image,
            ulong imageLength,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_applied_ownership_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_applied_ownership_query_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryCapacity(
            IntPtr handle,
            out NativeAppliedOwnershipCapacity capacity,
            uint capacitySize);

        [LibraryImport(LibraryName, EntryPoint = "rm_applied_ownership_promote")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Promote(
            IntPtr handle,
            in NativeAppliedOwnershipPromoteInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_applied_ownership_transition")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Transition(
            IntPtr handle,
            in NativeAppliedOwnershipTransitionInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_applied_ownership_plan_transition")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int PlanTransition(
            IntPtr handle,
            in NativeAppliedOwnershipPrimaryIdentity primary,
            in NativeAppliedOwnershipOriginalBinding transitionBinding,
            in NativeAppliedOwnershipDurablePayloadReference transitionPayload,
            out NativeAppliedOwnershipTransitionInput transition);

        [LibraryImport(LibraryName, EntryPoint = "rm_applied_ownership_remove")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Remove(
            IntPtr handle,
            in NativeAppliedOwnershipRemoveInput input);

        [LibraryImport(LibraryName, EntryPoint = "rm_applied_ownership_get")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Get(
            IntPtr handle,
            in NativeAppliedOwnershipPrimaryIdentity primary,
            out NativeAppliedOwnershipRecord record);

        [LibraryImport(LibraryName, EntryPoint = "rm_applied_ownership_snapshot")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Snapshot(
            IntPtr handle,
            ref NativeAppliedOwnershipSnapshotHeader header,
            NativeAppliedOwnershipRecord* records,
            uint recordCapacity);

        [LibraryImport(LibraryName, EntryPoint = "rm_applied_ownership_encode")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Encode(
            IntPtr handle,
            byte* output,
            ulong outputCapacity,
            out ulong written);

        [LibraryImport(LibraryName, EntryPoint = "rm_applied_ownership_decode_replace")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int DecodeReplace(
            IntPtr handle,
            byte* image,
            ulong imageLength);
    }
}

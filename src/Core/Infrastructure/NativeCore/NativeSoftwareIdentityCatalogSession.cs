using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed partial class NativeSoftwareIdentityCatalogSession : IDisposable
{
    private readonly object sync = new();
    private IntPtr handle;
    private NativeSoftwareIdentityCatalogCapacity capacity;
    private ulong configurationGeneration;

    public NativeSoftwareIdentityCatalogSession(
        in NativeSoftwareIdentityCatalogConfiguration configuration)
    {
        var actualVersion = NativeMethods.GetAbiVersion();
        if (actualVersion != NativeSoftwareIdentityCatalogAbi.Version)
        {
            throw new InvalidOperationException(
                $"Native software identity catalog ABI mismatch: expected 0x{NativeSoftwareIdentityCatalogAbi.Version:X8}, actual 0x{actualVersion:X8}.");
        }

        var status = (NativeSoftwareIdentityStatus)NativeMethods.Create(in configuration, out handle);
        if (status != NativeSoftwareIdentityStatus.Ok || handle == IntPtr.Zero)
        {
            handle = IntPtr.Zero;
            throw new InvalidOperationException(
                $"Native software identity catalog session creation failed with {status}.");
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

    ~NativeSoftwareIdentityCatalogSession() => Dispose(false);

    internal NativeSoftwareIdentityCatalogCapacity Capacity
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
        in NativeSoftwareIdentityCatalogConfiguration configuration)
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

    internal unsafe NativeSoftwareIdentityStatus Replace(
        in NativeSoftwareIdentityCatalogReplaceInput input,
        ReadOnlySpan<NativeSoftwareIdentityCatalogEntryInput> entries,
        ReadOnlySpan<NativeSoftwareIdentityCatalogAliasInput> aliases,
        ReadOnlySpan<NativeSoftwareIdentityCatalogRootInput> roots,
        ReadOnlySpan<byte> keyBytes)
    {
        lock (sync)
        {
            fixed (NativeSoftwareIdentityCatalogEntryInput* entryPointer = entries)
            fixed (NativeSoftwareIdentityCatalogAliasInput* aliasPointer = aliases)
            fixed (NativeSoftwareIdentityCatalogRootInput* rootPointer = roots)
            fixed (byte* keyPointer = keyBytes)
            {
                return (NativeSoftwareIdentityStatus)NativeMethods.Replace(
                    RequireHandle(),
                    in input,
                    entryPointer,
                    checked((uint)entries.Length),
                    aliasPointer,
                    checked((uint)aliases.Length),
                    rootPointer,
                    checked((uint)roots.Length),
                    keyPointer,
                    checked((uint)keyBytes.Length));
            }
        }
    }

    internal unsafe NativeSoftwareIdentityStatus Match(
        in NativeSoftwareIdentityCatalogQueryInput input,
        ReadOnlySpan<NativeSoftwareIdentityCatalogFactInput> facts,
        ReadOnlySpan<byte> keyBytes,
        out NativeSoftwareIdentityCatalogMatchOutput output)
    {
        lock (sync)
        {
            output = new NativeSoftwareIdentityCatalogMatchOutput
            {
                AbiVersion = NativeSoftwareIdentityCatalogAbi.Version,
                StructSize = SizeOf<NativeSoftwareIdentityCatalogMatchOutput>()
            };
            fixed (NativeSoftwareIdentityCatalogFactInput* factPointer = facts)
            fixed (byte* keyPointer = keyBytes)
            {
                return (NativeSoftwareIdentityStatus)NativeMethods.Match(
                    RequireHandle(),
                    in input,
                    factPointer,
                    checked((uint)facts.Length),
                    keyPointer,
                    checked((uint)keyBytes.Length),
                    ref output);
            }
        }
    }

    internal unsafe NativeSoftwareIdentityStatus MatchKnown(
        in NativeSoftwareIdentityKnownQueryInput input,
        ReadOnlySpan<NativeSoftwareIdentityKnownSignalInput> signals,
        ReadOnlySpan<byte> keyBytes,
        out NativeSoftwareIdentityKnownMatchOutput output)
    {
        lock (sync)
        {
            output = new NativeSoftwareIdentityKnownMatchOutput
            {
                AbiVersion = NativeSoftwareIdentityCatalogAbi.Version,
                StructSize = SizeOf<NativeSoftwareIdentityKnownMatchOutput>()
            };
            fixed (NativeSoftwareIdentityKnownSignalInput* signalPointer = signals)
            fixed (byte* keyPointer = keyBytes)
            {
                return (NativeSoftwareIdentityStatus)NativeMethods.MatchKnown(
                    RequireHandle(),
                    in input,
                    signalPointer,
                    checked((uint)signals.Length),
                    keyPointer,
                    checked((uint)keyBytes.Length),
                    ref output);
            }
        }
    }

    internal NativeSoftwareIdentityStatus Snapshot(out NativeSoftwareIdentityCatalogSummary output)
    {
        lock (sync)
        {
            output = new NativeSoftwareIdentityCatalogSummary
            {
                AbiVersion = NativeSoftwareIdentityCatalogAbi.Version,
                StructSize = SizeOf<NativeSoftwareIdentityCatalogSummary>()
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
            : throw new ObjectDisposedException(nameof(NativeSoftwareIdentityCatalogSession));
    }

    private static NativeSoftwareIdentityCatalogCapacity QueryAndValidateCapacity(
        IntPtr session,
        in NativeSoftwareIdentityCatalogConfiguration configuration)
    {
        var status = (NativeSoftwareIdentityStatus)NativeMethods.QueryCapacity(
            session,
            out var result,
            SizeOf<NativeSoftwareIdentityCatalogCapacity>());
        if (status != NativeSoftwareIdentityStatus.Ok)
        {
            throw new InvalidOperationException(
                $"Native software identity catalog capacity query failed with {status}.");
        }

        unsafe
        {
            if (result.StructSize != SizeOf<NativeSoftwareIdentityCatalogCapacity>()
                || result.EntryCapacity != configuration.MaximumEntryCount
                || result.AliasCapacity != configuration.MaximumAliasCount
                || result.RootCapacity != configuration.MaximumRootCount
                || result.CatalogKeyByteCapacity != configuration.MaximumCatalogKeyByteCount
                || result.QueryFactCapacity != configuration.MaximumQueryFactCount
                || result.QuerySignalCapacity != configuration.MaximumQuerySignalCount
                || result.QueryKeyByteCapacity != configuration.MaximumQueryKeyByteCount
                || result.EntryIndexCapacity != configuration.EntryIndexCapacity
                || result.AliasIndexCapacity != configuration.AliasIndexCapacity
                || result.IdentityIndexCapacity != configuration.IdentityIndexCapacity
                || result.RootIndexCapacity != configuration.RootIndexCapacity
                || result.ResidentByteCount == 0
                || result.ResidentByteCount > configuration.ResidentByteBudget
                || result.Reserved[0] != 0
                || result.Reserved[1] != 0
                || result.Reserved[2] != 0)
            {
                throw new InvalidOperationException(
                    "Native software identity catalog capacity contract mismatch.");
            }
        }

        return result;
    }

    private static uint SizeOf<T>() where T : unmanaged
        => checked((uint)Unsafe.SizeOf<T>());

    private static partial class NativeMethods
    {
        private const string LibraryName = "ResourceManager.NativeCore";

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_catalog_abi_version")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial uint GetAbiVersion();

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_catalog_create")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Create(
            in NativeSoftwareIdentityCatalogConfiguration configuration,
            out IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_catalog_destroy")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial void Destroy(IntPtr handle);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_catalog_reconfigure")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Reconfigure(
            IntPtr handle,
            in NativeSoftwareIdentityCatalogConfiguration configuration);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_catalog_query_capacity")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int QueryCapacity(
            IntPtr handle,
            out NativeSoftwareIdentityCatalogCapacity capacity,
            uint outputSize);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_catalog_replace")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Replace(
            IntPtr handle,
            in NativeSoftwareIdentityCatalogReplaceInput input,
            NativeSoftwareIdentityCatalogEntryInput* entries,
            uint entryCount,
            NativeSoftwareIdentityCatalogAliasInput* aliases,
            uint aliasCount,
            NativeSoftwareIdentityCatalogRootInput* roots,
            uint rootCount,
            byte* keyBytes,
            uint keyByteCount);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_catalog_match")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int Match(
            IntPtr handle,
            in NativeSoftwareIdentityCatalogQueryInput input,
            NativeSoftwareIdentityCatalogFactInput* facts,
            uint factCount,
            byte* keyBytes,
            uint keyByteCount,
            ref NativeSoftwareIdentityCatalogMatchOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_catalog_match_known")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static unsafe partial int MatchKnown(
            IntPtr handle,
            in NativeSoftwareIdentityKnownQueryInput input,
            NativeSoftwareIdentityKnownSignalInput* signals,
            uint signalCount,
            byte* keyBytes,
            uint keyByteCount,
            ref NativeSoftwareIdentityKnownMatchOutput output);

        [LibraryImport(LibraryName, EntryPoint = "rm_software_identity_catalog_snapshot")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Snapshot(
            IntPtr handle,
            ref NativeSoftwareIdentityCatalogSummary output);
    }
}

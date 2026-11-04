using System.Collections.Immutable;
using ResourceManager.ReadWrite.Native.DataArea;

namespace ResourceManager.ReadWrite.Windows.DataArea;

// Run-plan inputs only. Layout, validation and region ownership remain native.
internal sealed record BackendDataAreaDeclaration(
    DataAreaRegion Region,
    ImmutableArray<DataAreaSourceIdentity> Sources,
    ImmutableArray<DataAreaPositionDeclaration> Positions);

internal sealed class BackendDataAreaException : Exception
{
    internal string Operation { get; }
    internal DataAreaStatus Status { get; }
    internal DataAreaApplyFailure? ApplyFailure { get; }

    internal BackendDataAreaException(string operation, DataAreaStatus status,
        DataAreaApplyFailure? failure = null)
        : base($"Data area {operation}: {status} ({(uint)status}).")
    {
        Operation = operation;
        Status = status;
        ApplyFailure = failure;
    }
}

/// <summary>
/// A callback-only view, never an owning lease. The address, spans and native
/// readers made from it must not be retained beyond the callback's completion.
/// </summary>
internal readonly unsafe ref struct BackendDataAreaRegion
{
    internal byte* Address { get; }
    internal ulong ByteLength { get; }
    internal ulong Epoch { get; }
    internal ulong LayoutGeneration { get; }
    internal DataAreaRegion Region { get; }

    internal BackendDataAreaRegion(in DataAreaBorrow borrow)
    {
        Address = borrow.RegionAddress;
        ByteLength = borrow.ByteLength;
        Epoch = borrow.Epoch;
        LayoutGeneration = borrow.LayoutGeneration;
        Region = borrow.Region;
    }
}

// A synchronous scope entry may return asynchronous work. The owner waits for
// that work before releasing the native borrow; the ref-struct itself cannot
// become an async state-machine field or escape through a returned result.
internal delegate ValueTask BackendDataAreaCallback(
    scoped BackendDataAreaRegion region, CancellationToken cancellationToken);

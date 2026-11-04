using System.Collections.Immutable;
using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Native.DataArea;

namespace ResourceManager.ReadWrite.Windows.DataArea;

// C ABI marshalling only: no managed layout calculator, region or value store.
internal static unsafe class BackendDataAreaNative
{
    internal static IntPtr Create(DataAreaSettings settings)
    {
        if (DataAreaAbi.AbiVersion() != DataAreaAbi.Version)
            throw new BackendDataAreaException("abi-version", DataAreaStatus.AbiMismatch);
        IntPtr handle = IntPtr.Zero;
        Check("create", DataAreaAbi.Create(&settings, (uint)sizeof(DataAreaSettings), &handle));
        return handle;
    }

    internal static void Apply(IntPtr handle, ImmutableArray<BackendDataAreaDeclaration> declarations,
        long nowUnixMilliseconds)
    {
        if (declarations.IsDefault) throw new ArgumentException("Declarations must be initialized.", nameof(declarations));
        var native = new DataAreaDeclaration[declarations.Length];
        var pins = new List<GCHandle>();
        try
        {
            var index = 0;
            while (index < declarations.Length)
            {
                var declaration = declarations[index];
                ArgumentNullException.ThrowIfNull(declaration);
                if (declaration.Sources.IsDefault || declaration.Positions.IsDefault)
                    throw new ArgumentException("Declaration arrays must be initialized.", nameof(declarations));
                native[index] = new()
                {
                    Region = declaration.Region,
                    SourceCount = checked((uint)declaration.Sources.Length),
                    PositionCount = checked((uint)declaration.Positions.Length),
                    Sources = (DataAreaSourceIdentity*)Pin(declaration.Sources, pins),
                    Positions = (DataAreaPositionDeclaration*)Pin(declaration.Positions, pins),
                };
                index++;
            }
            DataAreaApplyFailure failure;
            fixed (DataAreaDeclaration* inputs = native)
            {
                var status = DataAreaAbi.Apply(handle, inputs, checked((uint)native.Length), nowUnixMilliseconds, &failure);
                if (status != DataAreaStatus.Ok) throw new BackendDataAreaException("apply", status, failure);
            }
        }
        finally
        {
            var index = pins.Count;
            while (index != 0) pins[--index].Free();
        }
    }

    private static IntPtr Pin<T>(ImmutableArray<T> values, List<GCHandle> pins) where T : unmanaged
    {
        if (values.IsDefault) throw new ArgumentException("Declaration arrays must be initialized.", nameof(values));
        if (values.IsEmpty) return IntPtr.Zero;
        // The immutable input is pinned for this ABI call only, with no copy of
        // the declaration's contents or storage retained by the managed owner.
        var pin = GCHandle.Alloc(ImmutableCollectionsMarshal.AsArray(values), GCHandleType.Pinned);
        try { pins.Add(pin); }
        catch { pin.Free(); throw; }
        return pin.AddrOfPinnedObject();
    }

    internal static DataAreaBinding Binding(IntPtr handle, uint declaration)
    {
        DataAreaBinding binding;
        Check("binding", DataAreaAbi.Binding(handle, declaration, &binding));
        return binding;
    }

    internal static DataAreaStatus ReadView(IntPtr handle, ReadOnlySpan<uint> declarations,
        Span<uint> sources, out DataAreaReadView view)
    {
        DataAreaReadView local;
        DataAreaStatus status;
        fixed (uint* declared = declarations)
        fixed (uint* selected = sources)
            status = DataAreaAbi.ReadView(handle, declared, checked((uint)declarations.Length),
                selected, checked((uint)sources.Length), &local);
        view = local;
        return status;
    }

    internal static DataAreaBorrow Acquire(IntPtr handle, DataAreaRegion region, ulong generation)
    {
        DataAreaBorrow borrow;
        Check("acquire", DataAreaAbi.Acquire(handle, region, generation, &borrow));
        return borrow;
    }

    internal static void Release(IntPtr handle, DataAreaBorrow borrow)
        => Check("release", DataAreaAbi.Release(handle, &borrow));

    internal static ValueTask Invoke(BackendDataAreaCallback callback, in DataAreaBorrow borrow,
        CancellationToken cancellationToken)
        => callback(new BackendDataAreaRegion(in borrow), cancellationToken);

    internal static void Check(string operation, DataAreaStatus status)
    {
        if (status != DataAreaStatus.Ok) throw new BackendDataAreaException(operation, status);
    }
}

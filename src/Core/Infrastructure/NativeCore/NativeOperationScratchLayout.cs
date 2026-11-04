using System.Runtime.CompilerServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeOperationScratchLayout
{
    internal int OperationCapacity { get; }
    internal int ActionCapacity { get; }
    internal int PersistenceCapacity { get; }
    internal ulong Operations { get; }
    internal ulong Actions { get; }
    internal ulong Persistence { get; }
    internal ulong Length { get; }

    internal NativeOperationScratchLayout(uint read, uint actions, uint operations)
    {
        ArgumentOutOfRangeException.ThrowIfZero(read);
        ArgumentOutOfRangeException.ThrowIfZero(actions);
        ArgumentOutOfRangeException.ThrowIfZero(operations);
        OperationCapacity = checked((int)read);
        ActionCapacity = checked((int)actions);
        PersistenceCapacity = checked((int)operations);
        ulong end = 0;
        ulong Column(int width, int count)
        {
            var start = checked((end + 7) / 8 * 8);
            end = checked(start + (ulong)width * (ulong)count);
            return start;
        }
        Operations = Column(Unsafe.SizeOf<NativeOperationOutput>(), OperationCapacity);
        Actions = Column(Unsafe.SizeOf<NativeOperationActionOutput>(), ActionCapacity);
        Persistence = Column(Unsafe.SizeOf<NativeOperationOutput>(), PersistenceCapacity);
        Length = checked((end + 7) / 8 * 8);
    }
}

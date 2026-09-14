namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeAppliedOwnershipWorkspace
{
    public NativeAppliedOwnershipWorkspace(in NativeAppliedOwnershipCapacity capacity)
    {
        if (capacity.RecordCapacity == 0 ||
            capacity.RecordSize != NativeAppliedOwnershipAbi.RecordSize ||
            capacity.MaximumImageBytes < NativeAppliedOwnershipAbi.ImageHeaderSize ||
            capacity.MaximumImageBytes > int.MaxValue ||
            capacity.ResidentBytes == 0)
        {
            throw new InvalidOperationException(
                "Applied ownership workspace capacity is invalid.");
        }

        Records = new NativeAppliedOwnershipRecord[capacity.RecordCapacity];
        Image = new byte[checked((int)capacity.MaximumImageBytes)];
    }

    public NativeAppliedOwnershipRecord[] Records { get; }

    public byte[] Image { get; }
}

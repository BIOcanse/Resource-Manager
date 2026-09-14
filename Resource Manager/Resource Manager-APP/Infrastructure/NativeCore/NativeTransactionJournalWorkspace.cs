namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeTransactionJournalWorkspace
{
    public NativeTransactionJournalWorkspace(in NativeTransactionJournalCapacity capacity)
    {
        if (capacity.RecordCapacity == 0 ||
            capacity.MaximumImageLength < NativeTransactionJournalAbi.ImageHeaderSize ||
            capacity.ResidentBytes == 0)
        {
            throw new InvalidOperationException("Transaction journal workspace capacity is empty.");
        }

        Records = new NativeTransactionJournalRecord[capacity.RecordCapacity];
        Image = new byte[checked((int)capacity.MaximumImageLength)];
    }

    public NativeTransactionJournalRecord[] Records { get; }

    public byte[] Image { get; }
}

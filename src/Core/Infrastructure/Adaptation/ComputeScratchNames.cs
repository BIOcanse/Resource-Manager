namespace ResourceManager.App.Infrastructure.Adaptation;

// Gives a newly created scratch the user and rows /api/debug/native-memory
// reports. Rows read capacity/live, for example "items=12/live=9". Only a
// Ledger-created scratch carries these fields; other factories have none.
internal static class ComputeScratchNames
{
    internal static void Name(IComputeScratch scratch, string workArea, string rows)
    {
        if (scratch is not ResourceManagerLedgerOwner.ComputeScratch described) return;
        described.WorkArea = workArea;
        described.Rows = rows;
    }
}

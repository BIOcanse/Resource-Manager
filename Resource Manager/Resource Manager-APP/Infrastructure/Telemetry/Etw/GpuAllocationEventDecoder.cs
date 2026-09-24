namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

internal static class GpuAllocationEventDecoder
{
    internal static void Apply(GpuAllocationLedger ledger, int eventId, Func<string, object> payload, DateTime timestampUtc)
    {
        static ulong Unsigned(object value) => value switch
        {
            ulong number => number,
            long number => unchecked((ulong)number),
            uint number => number,
            int number => unchecked((uint)number),
            IntPtr pointer => unchecked((ulong)pointer.ToInt64()),
            _ => Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture)
        };
        ulong Field(string field) => Unsigned(payload(field));
        if (eventId is 53 or 306 or 307)
        {
            var offset = Field(eventId == 53 ? "TransferOffset" : "AllocationOffset");
            if (offset != 0)
                throw new NotSupportedException("Nonzero-offset GPU paging needs a verified physical-placement normalization.");
        }
        switch (eventId)
        {
            case 110:
                ledger.SetAdapter(Field("pDxgAdapter"), Field("AdapterLuid"));
                break;
            case 33: case 35:
                ledger.SetAllocation(Field("pDxgAdapter"), Field("hVidMmGlobalAlloc"), Field("allocSize"),
                    Convert.ToBoolean(payload("PageTableOrDirectory"), System.Globalization.CultureInfo.InvariantCulture));
                break;
            case 36: case 38:
                ledger.Open(Field("pDxgAdapter"), Field("hVidMmGlobalAlloc"), Field("hVidMmAlloc"),
                    checked((int)Field("hProcessId")), timestampUtc.ToFileTimeUtc());
                break;
            case 37: case 39: case 40:
                ledger.Close(Field("hVidMmAlloc"));
                break;
            case 34:
                ledger.Delete(Field("pDxgAdapter"), Field("hVidMmGlobalAlloc"));
                break;
            case 78:
                ledger.SetSegment(Field("pDxgAdapter"), checked((uint)Field("ulSegmentId")), checked((uint)Field("Flags")));
                break;
            case 80:
                ledger.SetCommittedReference(Field("hAllocationHandle"), checked((int)Field("hProcessId")),
                    checked((uint)Field("ulSegmentId")), Field("Size"), Field("SegmentOffset"));
                break;
            case 227: case 391:
                ledger.SetCommittedGlobal(Field("hGlobalAllocationHandle"), checked((uint)Field("ulSegmentId")), Field("SegmentOffset"));
                break;
            case 54: case 307:
                ledger.AddResidentRange(Field("pDxgAdapter"), Field("hAllocationGlobalHandle"),
                    checked((uint)Field("SegmentId")), eventId == 307 ? Field("AllocationOffset") : 0, Field("FillSize"), Field("SegmentOffset"));
                break;
            case 53: case 306:
                // Both physical endpoints are occupied; a copy does not retire its source.
                ledger.AddResidentRange(Field("pDxgAdapter"), Field("hAllocationGlobalHandle"),
                    checked((uint)Field("SourceSegmentId")), eventId == 306 ? Field("AllocationOffset") : Field("TransferOffset"),
                    Field("TransferSize"), Field("SourceSegmentOffset"));
                ledger.AddResidentRange(Field("pDxgAdapter"), Field("hAllocationGlobalHandle"),
                    checked((uint)Field("DestinationSegmentId")), eventId == 306 ? Field("AllocationOffset") : Field("TransferOffset"),
                    Field("TransferSize"), Field("DestinationSegmentOffset"));
                if (eventId == 306 && Field("SourceSegmentId") != 0 && Field("DestinationSegmentId") == 0)
                    ledger.RecordTransferredSource(Field("pDxgAdapter"), Field("hAllocationGlobalHandle"),
                        checked((uint)Field("SourceSegmentId")), Field("SourceSegmentOffset"), Field("TransferSize"));
                break;
            case 74:
                ledger.CompleteEviction(Field("hGlobalAllocationHandle"));
                break;
            case 55: case 314:
                ledger.DiscardResident(Field("pDxgAdapter"), Field("hAllocationGlobalHandle"), checked((uint)Field("SegmentId")),
                    eventId == 55 ? Field("SegmentOffset") : null);
                break;
            case 313:
                ledger.SetCommittedGlobal(Field("hAllocationGlobalHandle"), checked((uint)Field("SegmentId")));
                break;
        }
    }
}

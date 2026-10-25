namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

internal static class GpuAllocationEventDecoder
{
    // Named access is the recorded-payload oracle; production supplies the cached indexed reader.
    internal static void Apply(GpuAllocationLedger ledger, int eventId, Func<string, object> payload, DateTime timestampUtc)
        => Apply(ledger, eventId, new GpuAllocationNamedPayload(payload), timestampUtc);

    internal static void Apply<TPayload>(GpuAllocationLedger ledger, int eventId, TPayload payload, DateTime timestampUtc)
        where TPayload : IGpuAllocationPayload
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
        ulong Field(GpuAllocationField field) => Unsigned(payload.Get(field));
        if (eventId is 53 or 306 or 307)
        {
            var offset = Field(eventId == 53 ? GpuAllocationField.TransferOffset : GpuAllocationField.AllocationOffset);
            if (offset != 0)
                throw new NotSupportedException("Nonzero-offset GPU paging needs a verified physical-placement normalization.");
        }
        switch (eventId)
        {
            case 110:
                ledger.SetAdapter(Field(GpuAllocationField.pDxgAdapter), Field(GpuAllocationField.AdapterLuid));
                break;
            case 33: case 35:
                ledger.SetAllocation(Field(GpuAllocationField.pDxgAdapter), Field(GpuAllocationField.hVidMmGlobalAlloc), Field(GpuAllocationField.allocSize),
                    Convert.ToBoolean(payload.Get(GpuAllocationField.PageTableOrDirectory), System.Globalization.CultureInfo.InvariantCulture));
                break;
            case 36: case 38:
                ledger.Open(Field(GpuAllocationField.pDxgAdapter), Field(GpuAllocationField.hVidMmGlobalAlloc), Field(GpuAllocationField.hVidMmAlloc),
                    checked((int)Field(GpuAllocationField.hProcessId)), timestampUtc.ToFileTimeUtc());
                break;
            case 37: case 39: case 40:
                ledger.Close(Field(GpuAllocationField.hVidMmAlloc));
                break;
            case 34:
                ledger.Delete(Field(GpuAllocationField.pDxgAdapter), Field(GpuAllocationField.hVidMmGlobalAlloc));
                break;
            case 78:
                ledger.SetSegment(Field(GpuAllocationField.pDxgAdapter), checked((uint)Field(GpuAllocationField.ulSegmentId)), checked((uint)Field(GpuAllocationField.Flags)));
                break;
            case 80:
                ledger.SetCommittedReference(Field(GpuAllocationField.hAllocationHandle), checked((int)Field(GpuAllocationField.hProcessId)),
                    checked((uint)Field(GpuAllocationField.ulSegmentId)), Field(GpuAllocationField.Size), Field(GpuAllocationField.SegmentOffset));
                break;
            case 227: case 391:
                ledger.SetCommittedGlobal(Field(GpuAllocationField.hGlobalAllocationHandle), checked((uint)Field(GpuAllocationField.ulSegmentId)), Field(GpuAllocationField.SegmentOffset));
                break;
            case 54: case 307:
                ledger.AddResidentRange(Field(GpuAllocationField.pDxgAdapter), Field(GpuAllocationField.hAllocationGlobalHandle),
                    checked((uint)Field(GpuAllocationField.SegmentId)), eventId == 307 ? Field(GpuAllocationField.AllocationOffset) : 0, Field(GpuAllocationField.FillSize), Field(GpuAllocationField.SegmentOffset));
                break;
            case 53: case 306:
                // Both physical endpoints are occupied; a copy does not retire its source.
                ledger.AddResidentRange(Field(GpuAllocationField.pDxgAdapter), Field(GpuAllocationField.hAllocationGlobalHandle),
                    checked((uint)Field(GpuAllocationField.SourceSegmentId)), eventId == 306 ? Field(GpuAllocationField.AllocationOffset) : Field(GpuAllocationField.TransferOffset),
                    Field(GpuAllocationField.TransferSize), Field(GpuAllocationField.SourceSegmentOffset));
                ledger.AddResidentRange(Field(GpuAllocationField.pDxgAdapter), Field(GpuAllocationField.hAllocationGlobalHandle),
                    checked((uint)Field(GpuAllocationField.DestinationSegmentId)), eventId == 306 ? Field(GpuAllocationField.AllocationOffset) : Field(GpuAllocationField.TransferOffset),
                    Field(GpuAllocationField.TransferSize), Field(GpuAllocationField.DestinationSegmentOffset));
                if (eventId == 306 && Field(GpuAllocationField.SourceSegmentId) != 0 && Field(GpuAllocationField.DestinationSegmentId) == 0)
                    ledger.RecordTransferredSource(Field(GpuAllocationField.pDxgAdapter), Field(GpuAllocationField.hAllocationGlobalHandle),
                        checked((uint)Field(GpuAllocationField.SourceSegmentId)), Field(GpuAllocationField.SourceSegmentOffset), Field(GpuAllocationField.TransferSize));
                break;
            case 74:
                ledger.CompleteEviction(Field(GpuAllocationField.hGlobalAllocationHandle));
                break;
            case 55: case 314:
                ledger.DiscardResident(Field(GpuAllocationField.pDxgAdapter), Field(GpuAllocationField.hAllocationGlobalHandle), checked((uint)Field(GpuAllocationField.SegmentId)),
                    eventId == 55 ? Field(GpuAllocationField.SegmentOffset) : null);
                break;
            case 313:
                ledger.SetCommittedGlobal(Field(GpuAllocationField.hAllocationGlobalHandle), checked((uint)Field(GpuAllocationField.SegmentId)));
                break;
        }
    }
}

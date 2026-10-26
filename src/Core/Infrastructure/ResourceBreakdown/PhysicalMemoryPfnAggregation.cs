using ResourceManager.App.Application.ResourceBreakdown;
using ResourceManager.App.Domain.ResourceBreakdown;

namespace ResourceManager.App.Infrastructure.ResourceBreakdown;

/// <summary>Streaming PFN counts; the only retained result is 16 uses x 8 lists.</summary>
internal sealed class PhysicalMemoryPfnAggregation
{
    private readonly ulong[,] pages = new ulong[16, 8];
    internal void Add(ReadOnlySpan<PfnIdentityLayout> identities)
    {
        foreach (ref readonly var page in identities)
            pages[page.Use, page.List] = checked(pages[page.Use, page.List] + 1);
    }

    internal PhysicalMemoryResidualFrame CreateFrame(DateTimeOffset capturedAt, ulong usedBytes,
        ulong attributedPrivateBytes, PhysicalMemoryResidualCapture tags, Action<string> report)
    {
        if (attributedPrivateBytes > usedBytes)
        {
            report("attributed-private-exceeds-used");
            return PhysicalMemoryResidualFrame.Unavailable(capturedAt, "attributed-private-exceeds-used");
        }
        var parts = new List<PhysicalMemoryResidualPart>();
        // phnt ntmmapi.h MMPFNUSE_* / MMPFNLIST_*. Only ACTIVE (6) is admitted here.
        // Use 12 is the working-set-internals category in the supplied 26300 machine evidence;
        // phnt retains the numeric UNKNOWN12 name rather than a canonical Windows name.
        (int Use, string Kind)[] uses = [(1,"mapped-file"),(2,"shareable"),(3,"page-table"),
            (4,"paged-pool-resident"),(6,"system-pte"),(8,"metafile"),(10,"driver-locked"),
            (11,"kernel-stack"),(12,"working-set-internals")];
        foreach (var use in uses) Add("system-memory/" + use.Kind, Bytes(pages[use.Use, 6]));
        var pool = Bytes(pages[5, 6]);
        if (pool > 0)
        {
            // Reuse the existing exact nonpaged leaf partition with a PFN parent, not counter totals.
            var provider = new PhysicalMemoryResidualBreakdownProvider(tags with
            { Counters = new(pool, null, null, null) }, new StaticResourceResidualBreakdownProvider(),
                state => { if (state != "ready") report(state); });
            foreach (var row in provider.CreateResidualSegments(new ResourceResidualBreakdownRequest(
                "memory.usage", pool, usedBytes, 0, new HashSet<int>())))
                Add(row.AttributionKind, checked((ulong)row.Value));
        }
        ulong modified = 0;
        for (var use = 0; use < 16; use++) modified = checked(modified + pages[use, 3] + pages[use, 4]);
        Add("system-memory/modified", Bytes(modified));
        var privateBytes = Bytes(pages[0, 6]);
        string? reason = null;
        if (privateBytes < attributedPrivateBytes)
        {
            reason = "process-private-below-attributed";
            report(reason); // Withhold a negative reading; never clamp it into a measured zero.
        }
        else Add("system-memory/process-private-not-attributed", privateBytes - attributedPrivateBytes);
        ulong sum = 0;
        foreach (var part in parts) sum = checked(sum + part.Bytes);
        var residual = usedBytes - attributedPrivateBytes;
        if (sum > residual)
        {
            report("components-exceed-residual");
            return PhysicalMemoryResidualFrame.Unavailable(capturedAt, "components-exceed-residual");
        }
        return new(capturedAt, "ready", reason, usedBytes, attributedPrivateBytes,
            residual, residual - sum, parts.ToArray());

        void Add(string kind, ulong bytes) { if (bytes > 0) parts.Add(new(kind, bytes)); }
    }
    private static ulong Bytes(ulong count) => checked(count * (ulong)Environment.SystemPageSize);
}

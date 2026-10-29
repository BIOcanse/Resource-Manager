using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MemoryMeasurement;

internal sealed class MemoryClass
{
    public string Class { get; set; } = "";
    public string Module { get; set; } = "";
    public ulong CommittedBytes { get; set; }
    public ulong ReadBytes { get; set; }
    public ulong UniqueBytes { get; set; }
    public ulong DuplicateBytes { get; set; }
    public ulong ZeroBytes { get; set; }
    public ulong ObservedZeroBytes { get; set; }
    public ulong UnreadBytes { get; set; }
    public ulong ChangedBytes { get; set; }
}
internal sealed class CopyRegion
{
    public int RegionId { get; set; }
    public ulong Address { get; set; }
    public string AddressHex => $"0x{Address:x}";
    public string Class { get; set; } = "";
    public string Module { get; set; } = "";
    public long SliceCount { get; set; }
    public List<ulong> ExampleOffsets { get; set; } = new();
}
internal sealed class DuplicateGroup
{
    public string Hash { get; set; } = "";
    public int SliceBytes { get; set; }
    public long SliceCount { get; set; }
    public ulong Bytes => (ulong)SliceCount * (ulong)SliceBytes;
    public ulong DuplicateBytes => (ulong)(SliceCount - 1) * (ulong)SliceBytes;
    public string Hex { get; set; } = "";
    public string Ascii { get; set; } = "";
    public string Utf16 { get; set; } = "";
    public bool AllZero { get; set; }
    public List<CopyRegion> Regions { get; set; } = new();
}
internal sealed class DuplicateReport
{
    public int ProcessId { get; set; }
    public DateTime CapturedUtc { get; set; } = DateTime.UtcNow;
    public int SliceBytes { get; set; }
    public bool SeparateZero { get; set; }
    public string HashAlgorithm => "FNV-1a-64, followed by full byte equality for every duplicate member";
    public string Consistency => "Live read-only scan, not an atomic snapshot. Duplicate candidates are re-read and rehashed; changed/unreadable candidates are excluded. Unique/zero bytes describe their initial reads.";
    public string Accounting => "Per class: committed = unique + duplicate + zero(separated only) + unread + changed. Cross-class groups retain the lowest-address confirmed member; all other bytes are charged as duplicate to their source class. This is identical virtual content, not guaranteed reclaimable physical RAM.";
    public List<MemoryClass> Classes { get; set; } = new();
    public List<Region> Regions { get; set; } = new();
    public List<DuplicateGroup> TopGroups { get; set; } = new();
    public long DuplicateGroups { get; set; }
    public long HashCollisionPartitions { get; set; }
    public long IndexedSlices { get; set; }
    public ManagedReport Managed { get; set; } = null!;
    public double ElapsedSeconds { get; set; }
    public long ToolPeakWorkingSetBytes { get; set; }
    public long ToolPeakCommittedBytes { get; set; }
}

// External sort retains only 262144 x 24-byte records plus <=128 merge heads in memory.
// Run files and two collision spools have DeleteOnClose handles; no memory contents are written to disk.
// At most 33554432 nonzero slices (768 MiB runs plus two <=768 MiB collision spools),
// 65536 regions, 100 top groups and 262144 region references across retained groups. Exceeding any
// bound fails the scan explicitly. The launcher also enforces a process-tree committed-memory ceiling.
internal static class Duplicates
{
    const int RunRecords = 262144, MaxRuns = 128;
    internal readonly record struct Index(ulong Hash, ulong Address, int Region, int Length);
    sealed class Order : IComparer<Index>
    {
        public int Compare(Index a, Index b)
        {
            int c = a.Hash.CompareTo(b.Hash); if (c != 0) return c;
            c = a.Length.CompareTo(b.Length); return c != 0 ? c : a.Address.CompareTo(b.Address);
        }
    }
    static readonly Order SortOrder = new();
    delegate ulong SliceHash(ReadOnlySpan<byte> bytes);
    static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        ulong hash = 14695981039346656037;
        int i = 0; while (i < bytes.Length) { hash ^= bytes[i++]; hash = unchecked(hash * 1099511628211); }
        return hash;
    }
    static bool IsZero(ReadOnlySpan<byte> bytes) => bytes.IndexOfAnyExcept((byte)0) < 0;
    static FileStream Scratch(string directory) => new(Path.Combine(directory, $"index-{Guid.NewGuid():N}.tmp"), FileMode.CreateNew,
        FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
    static void Write(BinaryWriter writer, Index record)
    { writer.Write(record.Hash); writer.Write(record.Address); writer.Write(record.Region); writer.Write(record.Length); }
    static Index Read(BinaryReader reader) => new(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadInt32(), reader.ReadInt32());

    internal static DuplicateReport Run(int pid, string directory, int slice, bool separateZero, int top, int sample, string? clrMd)
    {
        var timer = Stopwatch.StartNew(); var budget = new Budget();
        using var process = Native.Open(pid);
        var managed = ManagedHeap.Read(pid, false, clrMd, _ => "unresolved");
        var regions = AddressSpace.Read(process, managed.Ranges, budget);
        var classes = new Dictionary<string, MemoryClass>(StringComparer.OrdinalIgnoreCase);
        var counters = new MemoryClass[regions.Count];
        int ri = 0;
        while (ri < regions.Count)
        {
            var region = regions[ri];
            if (!classes.TryGetValue(region.Key, out var counter))
            { counter = new MemoryClass { Class = region.Class, Module = region.Module }; classes.Add(region.Key, counter); }
            counter.CommittedBytes += region.Bytes; counters[ri++] = counter;
        }
        var report = new DuplicateReport { ProcessId = pid, SliceBytes = slice, SeparateZero = separateZero, Regions = regions, Managed = managed };
        var runs = new List<FileStream>(); var records = new Index[RunRecords]; int used = 0;
        var buffer = new byte[1024 * 1024]; var fallback = new byte[slice];
        void Flush()
        {
            if (used == 0) return;
            if (runs.Count >= MaxRuns) throw new InvalidOperationException("33554432 indexed-slice bound exceeded; use a larger SliceBytes.");
            Array.Sort(records, 0, used, SortOrder);
            var file = Scratch(directory); runs.Add(file);
            using var writer = new BinaryWriter(file, Encoding.UTF8, true);
            int i = 0; while (i < used) Write(writer, records[i++]); writer.Flush(); file.Position = 0; used = 0;
        }
        void Accept(Region region, ulong address, ReadOnlySpan<byte> data)
        {
            var counter = counters[region.Id]; counter.ReadBytes += (ulong)data.Length;
            if (IsZero(data))
            {
                region.ObservedZeroBytes += (ulong)data.Length;
                counter.ObservedZeroBytes += (ulong)data.Length;
                if (separateZero) { counter.ZeroBytes += (ulong)data.Length; return; }
            }
            records[used++] = new Index(Hash(data), address, region.Id, data.Length); report.IndexedSlices++;
            if (used == RunRecords) Flush();
        }
        try
        {
            ri = 0; ulong scanned = 0;
            while (ri < regions.Count)
            {
                budget.Check(); var region = regions[ri++];
                if (!region.Readable) { counters[region.Id].UnreadBytes += region.Bytes; continue; }
                ulong offset = 0;
                while (offset < region.Bytes)
                {
                    budget.Check(); int length = (int)Math.Min((ulong)buffer.Length, region.Bytes - offset);
                    scanned += (ulong)length;
                    if (scanned > 128UL * 1024 * 1024 * 1024) throw new InvalidOperationException("128 GiB readable-byte bound exceeded.");
                    int read = Native.Read(process, region.Address + offset, buffer, length);
                    int start = 0;
                    while (start < length)
                    {
                        int size = Math.Min(slice, length - start);
                        if (read == length) Accept(region, region.Address + offset + (ulong)start, buffer.AsSpan(start, size));
                        else if (Native.Read(process, region.Address + offset + (ulong)start, fallback, size) == size)
                            Accept(region, region.Address + offset + (ulong)start, fallback.AsSpan(0, size));
                        else counters[region.Id].UnreadBytes += (ulong)size;
                        start += size;
                    }
                    offset += (ulong)length;
                }
            }
            Flush();
            var readers = runs.Select(f => new BinaryReader(f, Encoding.UTF8, true)).ToArray();
            try
            {
                var queue = new PriorityQueue<int, Index>(SortOrder); int r = 0;
                while (r < readers.Length) { if (runs[r].Length > 0) queue.Enqueue(r, Read(readers[r])); r++; }
                using var candidates = Scratch(directory); using var collisions = Scratch(directory);
                using var groupWriter = new BinaryWriter(candidates, Encoding.UTF8, true);
                Index? first = null; long count = 0;
                void Finish()
                {
                    if (first == null) return;
                    groupWriter.Flush();
                    if (count == 1) counters[first.Value.Region].UniqueBytes += (ulong)first.Value.Length;
                    else Confirm(process, candidates, collisions, count, regions, counters, report, top, sample, budget, Hash);
                    candidates.SetLength(0); candidates.Position = 0; count = 0;
                }
                while (queue.TryDequeue(out int run, out Index record))
                {
                    budget.Check();
                    if (first != null && (first.Value.Hash != record.Hash || first.Value.Length != record.Length)) Finish();
                    if (count == 0) first = record;
                    Write(groupWriter, record); count++;
                    if (runs[run].Position < runs[run].Length) queue.Enqueue(run, Read(readers[run]));
                }
                Finish();
            }
            finally { int r = 0; while (r < readers.Length) readers[r++].Dispose(); }
        }
        finally { int i = 0; while (i < runs.Count) runs[i++].Dispose(); }
        report.TopGroups.Sort((a, b) => b.DuplicateBytes.CompareTo(a.DuplicateBytes));
        report.Classes = classes.Values.OrderByDescending(c => c.CommittedBytes).ToList();
        foreach (var c in report.Classes)
            if (c.CommittedBytes != c.UniqueBytes + c.DuplicateBytes + c.ZeroBytes + c.UnreadBytes + c.ChangedBytes)
                throw new InvalidDataException("Memory accounting did not reconcile.");
        report.ElapsedSeconds = timer.Elapsed.TotalSeconds;
        using var self = Process.GetCurrentProcess(); self.Refresh();
        report.ToolPeakWorkingSetBytes = self.PeakWorkingSet64; report.ToolPeakCommittedBytes = self.PeakPagedMemorySize64;
        return report;
    }

    static void Confirm(SafeProcessHandle process, FileStream source, FileStream scratch, long count, List<Region> regions,
        MemoryClass[] counters, DuplicateReport report, int top, int sample, Budget budget, SliceHash hash)
    {
        var representative = new byte[65536]; var current = new byte[65536];
        // Partition equal-hash records by actual bytes. A true hash collision is written to the other spool,
        // then compared with a new representative; no collision ever gets charged as a duplicate.
        while (count > 0)
        {
            budget.Check(); source.Position = 0; scratch.SetLength(0); scratch.Position = 0;
            using var reader = new BinaryReader(source, Encoding.UTF8, true);
            using var writer = new BinaryWriter(scratch, Encoding.UTF8, true);
            Index? first = null; long remaining = 0; long copies = 0;
            var memberRegions = new Dictionary<int, CopyRegion>();
            var memberCounts = new Dictionary<int, long>();
            long n = 0;
            while (n++ < count)
            {
                if ((n & 1023) == 0) budget.Check();
                Index member = Read(reader);
                if (Native.Read(process, member.Address, current, member.Length) != member.Length || hash(current.AsSpan(0, member.Length)) != member.Hash)
                { counters[member.Region].ChangedBytes += (ulong)member.Length; continue; }
                if (first == null) { first = member; current.AsSpan(0, member.Length).CopyTo(representative); }
                if (!current.AsSpan(0, member.Length).SequenceEqual(representative.AsSpan(0, member.Length)))
                { Write(writer, member); remaining++; continue; }
                copies++;
                if (!memberRegions.TryGetValue(member.Region, out var regionCopies))
                {
                    var region = regions[member.Region];
                    regionCopies = new CopyRegion { RegionId = region.Id, Address = region.Address, Class = region.Class, Module = region.Module };
                    memberRegions.Add(member.Region, regionCopies);
                }
                regionCopies.SliceCount++;
                if (regionCopies.ExampleOffsets.Count < 4) regionCopies.ExampleOffsets.Add(member.Address - regions[member.Region].Address);
                memberCounts.TryGetValue(member.Region, out long previous); memberCounts[member.Region] = previous + 1;
            }
            writer.Flush();
            if (first != null)
            {
                Index canonical = first.Value;
                counters[canonical.Region].UniqueBytes += (ulong)canonical.Length;
                foreach (var member in memberCounts)
                    counters[member.Key].DuplicateBytes += (ulong)(member.Value - (member.Key == canonical.Region ? 1 : 0)) * (ulong)canonical.Length;
                if (copies > 1)
                {
                    report.DuplicateGroups++;
                    ulong redundant = (ulong)(copies - 1) * (ulong)canonical.Length;
                    ulong minimum = report.TopGroups.Count == 0 ? 0 : report.TopGroups.Min(g => g.DuplicateBytes);
                    if (report.TopGroups.Count < top || redundant > minimum)
                    {
                        int length = Math.Min(sample, canonical.Length); var preview = representative.AsSpan(0, length);
                        var group = new DuplicateGroup { Hash = canonical.Hash.ToString("x16"), SliceBytes = canonical.Length, SliceCount = copies,
                            AllZero = IsZero(representative.AsSpan(0, canonical.Length)),
                            Hex = Convert.ToHexString(preview), Ascii = Printable(Encoding.ASCII.GetString(preview)),
                            Utf16 = Printable(Encoding.Unicode.GetString(preview[..(length & ~1)])), Regions = memberRegions.Values.ToList() };
                        if (report.TopGroups.Count == top) report.TopGroups.RemoveAt(report.TopGroups.FindIndex(g => g.DuplicateBytes == minimum));
                        report.TopGroups.Add(group);
                        if (report.TopGroups.Sum(g => g.Regions.Count) > 262144) throw new InvalidOperationException("262144 top-group region-reference bound exceeded; reduce Top.");
                    }
                }
            }
            if (remaining > 0) report.HashCollisionPartitions++;
            count = remaining; (source, scratch) = (scratch, source);
        }
    }

    // Force hash collisions against fixture memory in this process. Production always uses FNV-1a.
    internal static void TestCollisionConfirmation(string directory)
    {
        const int slice = 64;
        ulong address = Native.VirtualAlloc(0, 4096, 0x3000, 4);
        if (address == 0) throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        try
        {
            var bytes = new byte[slice * 4];
            bytes.AsSpan(0, slice * 2).Fill(11);
            bytes.AsSpan(slice * 2, slice).Fill(22);
            bytes.AsSpan(slice * 3, slice).Fill(33);
            System.Runtime.InteropServices.Marshal.Copy(bytes, 0, (IntPtr)address, bytes.Length);
            using var process = Native.Open(Environment.ProcessId);
            using var source = Scratch(directory); using var scratch = Scratch(directory);
            using var writer = new BinaryWriter(source, Encoding.UTF8, true);
            int i = 0;
            while (i < 4) { Write(writer, new Index(0, address + (ulong)(i * slice), 0, slice)); i++; }
            // The fifth record has a stale hash and must be excluded, not charged as unique or duplicate.
            Write(writer, new Index(1, address, 0, slice)); writer.Flush();
            var regions = new List<Region> { new() { Id = 0, Address = address, Bytes = 320, Class = "private" } };
            var counters = new[] { new MemoryClass { Class = "private", CommittedBytes = 320 } };
            var report = new DuplicateReport();
            Confirm(process, source, scratch, 5, regions, counters, report, 30, 16, new Budget(), _ => 0);
            if (report.DuplicateGroups != 1 || report.TopGroups.Single().SliceCount != 2 ||
                counters[0].UniqueBytes != 192 || counters[0].DuplicateBytes != 64 || counters[0].ChangedBytes != 64 ||
                report.HashCollisionPartitions != 2)
                throw new InvalidDataException("Forced collision/change confirmation failed.");
        }
        finally { Native.VirtualFree(address, 0, 0x8000); }
    }
    static string Printable(string text)
    {
        var builder = new StringBuilder(text.Length); int i = 0;
        while (i < text.Length) { char c = text[i++]; builder.Append(char.IsControl(c) || char.IsSurrogate(c) || c == '\u007f' ? '.' : c); }
        return builder.ToString();
    }

    internal static string Text(DuplicateReport r)
    {
        var text = new StringBuilder();
        text.AppendLine($"pid={r.ProcessId} slice={r.SliceBytes} separateZero={r.SeparateZero} seconds={r.ElapsedSeconds:F3}");
        text.AppendLine(r.Consistency); text.AppendLine(r.Accounting);
        text.AppendLine($"GC marking: {r.Managed.Status}: {r.Managed.Reason}");
        text.AppendLine("class/module | committed | unique | duplicate | zero | unread | changed (bytes)");
        foreach (var c in r.Classes) text.AppendLine($"{c.Class} {c.Module} | {c.CommittedBytes} | {c.UniqueBytes} | {c.DuplicateBytes} | {c.ZeroBytes} | {c.UnreadBytes} | {c.ChangedBytes}");
        text.AppendLine($"duplicate groups={r.DuplicateGroups}; indexed slices={r.IndexedSlices}; hash collision partitions={r.HashCollisionPartitions}");
        foreach (var g in r.TopGroups)
        {
            text.AppendLine($"\nhash={g.Hash} count={g.SliceCount} slice={g.SliceBytes} bytes={g.Bytes} redundant={g.DuplicateBytes} allZero={g.AllZero}");
            foreach (var m in g.Regions) text.AppendLine($"  region={m.RegionId} {m.AddressHex} {m.Class} {m.Module} copies={m.SliceCount} offsets={string.Join(',', m.ExampleOffsets)}");
            text.AppendLine($"  hex: {g.Hex}\n  ASCII: {g.Ascii}\n  UTF16: {g.Utf16}");
        }
        text.AppendLine($"tool peak working set={r.ToolPeakWorkingSetBytes}; peak committed={r.ToolPeakCommittedBytes}");
        return text.ToString();
    }
}

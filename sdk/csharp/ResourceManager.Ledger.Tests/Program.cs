using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using ResourceManager.Ledger;
using ResourceManager.Ledger.Analyzers;
using ResourceManager.Ledger.Identity;
using System.Text.Json;
using System.Reflection.PortableExecutable;
using System.Buffers.Binary;
using System.Text;
using System.Buffers;

int cases = 0;
try
{
    if (args.Length == 0) await CheckAnalyzer();
    else if (args[0] == "end-contract")
    {
        cases += await LeaseEndCases.RunAsync(args[1], args[2], args[3] == "negative");
    }
    else
    {
        bool enabled = args[1] == "on";
        if (args[2] == "256") await CheckNative<LedgerUid256>(args[0], enabled);
        else await CheckNative<LedgerUid128>(args[0], enabled);
    }
    Console.WriteLine($"PASS {cases} Ledger scoped-lease cases");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception);
    return 1;
}

void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    cases++;
    Console.WriteLine("PASS " + name);
}

async Task CheckAnalyzer()
{
    cases += await AnalyzerCases.RunAsync();
}
async Task CheckNative<TUid>(string path, bool analysisEnabled) where TUid : unmanaged, ILedgerUid<TUid>
{
    using var manifest = JsonDocument.Parse(File.ReadAllBytes(args[3]));
    var fields = manifest.RootElement;
    var identity = new LedgerBuildIdentity(
        fields.GetProperty("tableCount").GetUInt32(), fields.GetProperty("fixedCount").GetUInt32(),
        fields.GetProperty("resourceCapacity").GetUInt32(), fields.GetProperty("partitionCapacity").GetUInt32(),
        fields.GetProperty("indexNodeCount").GetUInt32(), fields.GetProperty("indexHomeCount").GetUInt32(),
        Convert.FromHexString(fields.GetProperty("structureSha256").GetString()!),
        Convert.FromHexString(fields.GetProperty("policySha256").GetString()!));
    using var owner = LedgerOwner<TUid>.Open(path, identity);
    Check(LeaseEndCases.NoFaultHooks(path), "production Native has no fault controls");
    byte[] uidBytes = Enumerable.Range(1, TUid.ByteLength).Select(value => unchecked((byte)(17 * value))).ToArray();
    TUid uid = TUid.FromBytes(uidBytes);
    var pushed = owner.Push(LedgerTarget<TUid>.ForTable(0), uid, 32);
    Check(pushed.Status == LedgerStatus.NewlyAllocated, "real Native allocation " + TUid.ByteLength);
    using (LedgerLease<TUid> loading = pushed.Lease!)
    {
        loading.Span.Fill(0x5a);
        loading.PublishOnDispose();
    }
    Check(owner.LeaseAnalysisAvailable == analysisEnabled, "analysis export availability " + analysisEnabled);
    Check(ImportsSymbol(path, "GetTickCount64") == analysisEnabled, "Native clock import compiled out " + !analysisEnabled);
    Check(ImportsSymbol(path, "GetWriteWatch") == analysisEnabled &&
        ImportsSymbol(path, "ResetWriteWatch") == analysisEnabled, "Native write observation imports compiled out " + !analysisEnabled);
    Check(typeof(LedgerLease<TUid>).GetMethod("RecordRead") is null &&
        typeof(LedgerLease<TUid>).GetMethod("RecordWrite") is null &&
        typeof(LedgerLease<TUid>).GetMethod("DeclareAnalysisCoverage") is null, "caller marker APIs removed");
    LedgerUseResult<TUid> borrowed = owner.Use(uid);
    await using (LedgerLease<TUid> lease = borrowed.Lease!)
    {
        Check(lease.Span[0] == 0x5a, "first real read fragment");
        // Direct region accesses need no ledger calls or caller markers.
        {
            Span<byte> region = lease.Span;
            for (int i = 0; i < 10000; i++) region[0] = unchecked((byte)i);
        }
        for (int i = 0; i < 12; i++) _ = owner.Tick();
        Check(owner.Close(uid) == LedgerStatus.InUse, "held region survives idle ticks");
        await Task.Yield();
        Check(lease.Span[1] == 0x5a, "region valid across async code scope");
        if (analysisEnabled)
        {
            LedgerLeaseAnalysisSnapshot<TUid> snapshot = owner.GetLeaseAnalysis();
            LedgerLeaseAnalysisRecord<TUid> record = snapshot.Records.Single(item => item.State == LedgerLeaseAnalysisState.Active);
            Check(record.Uid.Equals(uid) && record.AllocationNumber == lease.AllocationNumber &&
                record.UseGeneration == lease.UseGeneration && record.Slot == lease.Slot, "full-width diagnostic identity");
            Check(record.DirtyPageSamples > 0 && record.WriteWindowCount > 0, "automatic plain writes observed without caller markers");
            Check((record.Support & LedgerLeaseObservationSupport.ReadsUnobservable) != 0, "pure reads explicitly unobservable");
            Check(snapshot.ActiveCount == 1 && snapshot.CompletedCount == 1, "query distinguishes active and ended records");
        }
        else
        {
            bool unavailable = false;
            try { _ = owner.GetLeaseAnalysis(); } catch (NotSupportedException) { unavailable = true; }
            Check(unavailable, "compiled-out analysis is unavailable, not fake zero");
        }
        ValueTask ending;
        {
            using MemoryHandle pin = lease.GetMemoryChunk(0, 32).Pin();
            ending = lease.DisposeAsync();
            Check(!ending.IsCompleted, "async scope return waits for a real memory pin");
            Check(owner.Close(uid) == LedgerStatus.InUse, "pending scope return retains the Native lease");
        }
        await ending;
    }
    // The final held round stays active; it must not become immediately idle on return.
    _ = owner.Tick();
    LedgerUseResult<TUid> probe = owner.Use(uid);
    Check(probe.Status == LedgerStatus.InMemory, "held windows remain active after return");
    using (LedgerLease<TUid> probeLease = probe.Lease!) { Check(probeLease.Length == 32, "short code-scope probe"); }
    Check(owner.ForceRelease(uid) == LedgerStatus.Ok, "explicit release after probe quiescence");
    Check(owner.GetTableView(0).SystemPageBytes == 0, "payload pages returned");
}

static bool ImportsSymbol(string path, string expected)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    int descriptor = pe.PEHeaders.PEHeader!.ImportTableDirectory.RelativeVirtualAddress;
    if (descriptor == 0) return false;
    for (int i = 0; i < 1024; i++)
    {
        ReadOnlySpan<byte> data = pe.GetSectionData(descriptor + 20 * i).GetContent(0, 20).AsSpan();
        uint original = BinaryPrimitives.ReadUInt32LittleEndian(data);
        uint name = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        uint first = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        if (name == 0 && original == 0 && first == 0) return false;
        int thunk = checked((int)(original == 0 ? first : original));
        for (int j = 0; j < 65536; j++)
        {
            ulong entry = BinaryPrimitives.ReadUInt64LittleEndian(pe.GetSectionData(thunk + 8 * j).GetContent(0, 8).AsSpan());
            if (entry == 0) break;
            if ((entry & (1UL << 63)) != 0) continue;
            ReadOnlySpan<byte> symbol = pe.GetSectionData(checked((int)entry + 2)).GetContent().AsSpan();
            int end = symbol.IndexOf((byte)0);
            if (end < 0) throw new InvalidDataException("Unterminated import name");
            if (Encoding.ASCII.GetString(symbol[..end]) == expected) return true;
        }
    }
    throw new InvalidDataException("Unterminated import descriptors");
}

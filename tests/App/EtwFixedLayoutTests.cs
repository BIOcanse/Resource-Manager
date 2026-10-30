using System.Buffers.Binary;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using Xunit.Abstractions;
using G = ResourceManager.App.Infrastructure.Telemetry.Etw.GpuAllocationField;

namespace Resource_Manager_APP.Tests;

// Fixed-layout decoding of the GPU allocation, Present and VidMm user-mode ETW sessions
// (EtwFixedLayout.cs). Recorded inputs and their provenance: Fixtures/EtwFixedLayout.
public sealed class EtwFixedLayoutTests(ITestOutputHelper output)
{
    private static readonly Guid DxgKrnl = GpuAllocationCollector.Provider;

    [Fact]
    public void PresentLayoutsReadEveryRecordedFieldLikeTheManifestParser()
    {
        var decoded = AssertLayoutsMatchManifest("present.etl.bin", PresentFrameEtwLayouts.Catalog, Enum.GetNames<PresentFrameField>());
        var kernelProcess = PresentFrameEtwSource.KernelProcessProvider;
        var dxgi = PresentFrameEtwSource.DxgiProvider;
        var win32k = PresentFrameEtwSource.Win32kProvider;
        Assert.Equal(new[]
        {
            (kernelProcess, 1, 4), (kernelProcess, 2, 2), (dxgi, 42, 0), (dxgi, 43, 0),
            (DxgKrnl, 17, 0), (DxgKrnl, 178, 1), (DxgKrnl, 184, 1), (DxgKrnl, 252, 0), (win32k, 201, 1), (win32k, 301, 1)
        }.Order(), decoded.Order());
    }

    [Fact]
    public void GpuAllocationLayoutsReadEveryRecordedFieldLikeTheManifestParser()
    {
        var decoded = AssertLayoutsMatchManifest("vidmm.etl.bin", GpuAllocationFixedPayload.Catalog, GpuAllocationNamedPayload.Names);
        Assert.Superset(new HashSet<(Guid, int, int)>
        {
            (DxgKrnl, 33, 3), (DxgKrnl, 34, 3), (DxgKrnl, 35, 3), (DxgKrnl, 36, 2), (DxgKrnl, 37, 2),
            (DxgKrnl, 38, 2), (DxgKrnl, 39, 0), (DxgKrnl, 55, 0), (DxgKrnl, 307, 1)
        }, decoded);
    }

    [Fact]
    public void VidMmLayoutsReadEveryRecordedFieldAndKeepTheManifestEventNames()
    {
        var decoded = AssertLayoutsMatchManifest("vidmm-process.etl.bin", DxgkrnlVidMmEtwLayouts.Catalog, Enum.GetNames<VidMmField>(),
            data => Assert.Equal(data.EventName, DxgkrnlVidMmEtwLayouts.Events[(int)data.ID].EventName));
        Assert.Equal(new[] { (DxgKrnl, 367, 0), (DxgKrnl, 371, 0) }.Order(), decoded.Order());
        // The zone's product keywords (0x203100c0) enable none of its process-memory events.
        Assert.Empty(AssertLayoutsMatchManifest("vidmm.etl.bin", DxgkrnlVidMmEtwLayouts.Catalog, Enum.GetNames<VidMmField>()));
    }

    [Theory]
    [InlineData("vidmm-process.etl.bin", true)]
    [InlineData("vidmm.etl.bin", false)]
    public void VidMmProcessMemoryReadsMatchTheRemovedNameHeuristic(string fixture, bool consumes)
    {
        var events = 0;
        var consumed = 0;
        using var source = new ETWTraceEventSource(Fixture(fixture), TraceEventSourceType.FileOnly);
        source.Dynamic.All += data =>
        {
            events++;
            var removed = RemovedNameHeuristic(data, out var removedProcessId, out var removedBytes);
            var current = DxgkrnlVidMmEtwTelemetryZone.TryReadProcessMemory(data, out var processId, out var valueBytes, out var eventName);
            Assert.Equal(removed, current);
            if (!current) return;
            Assert.Equal(removedProcessId, processId);
            Assert.Equal(removedBytes, valueBytes);
            Assert.Equal(data.EventName, eventName);
            consumed++;
        };
        source.Process();
        Assert.True(events > 0);
        Assert.Equal(consumes, consumed > 0);
        output.WriteLine($"{fixture}: {events} events, {consumed} process-memory readings identical to the removed name heuristic.");
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void PointerSizedFieldsFollowTheEventPointerSize(int pointerSize)
    {
        // AllocateAllocation 33 v3: pDxgAdapter at 2 pointers, allocSize at 4 + 3 pointers,
        // hVidMmGlobalAlloc at 40 + 3 pointers, PageTableOrDirectory at 86 + 7 pointers; 90 + 7 pointers long.
        var end = 90 + 7 * pointerSize;
        var adapter = pointerSize == 4 ? (object)0x80000010u : 0xFFFF900000000010ul;
        var bytes = Bytes(end, (2 * pointerSize, adapter), (4 + 3 * pointerSize, 0x100002000L),
            (40 + 3 * pointerSize, pointerSize == 4 ? (object)0x1234u : 0x1234ul), (86 + 7 * pointerSize, (byte)1));
        var payload = new GpuAllocationFixedPayload();
        var values = WithPayload(bytes, pointerSize, raw =>
        {
            Assert.Equal(EtwFixedLayoutStatus.Decoded, payload.SetEvent(DxgKrnl, 33, 3, raw));
            return new[] { payload.Get(G.pDxgAdapter), payload.Get(G.allocSize), payload.Get(G.hVidMmGlobalAlloc), payload.Get(G.PageTableOrDirectory) };
        });
        // A 4-byte pointer widens with sign extension, as the manifest parser's ulong did.
        Assert.Equal(new object[] { pointerSize == 4 ? 0xFFFFFFFF80000010ul : 0xFFFF900000000010ul, 0x100002000L, 0x1234ul, true }, values);
        Assert.Equal(EtwFixedLayoutStatus.ShortPayload,
            WithPayload(bytes[..(end - 1)], pointerSize, raw => payload.SetEvent(DxgKrnl, 33, 3, raw)));
    }

    [Fact]
    public void UnknownVersionsAndOtherEventsAreNotDecoded()
    {
        var bytes = new byte[256];
        var payload = new GpuAllocationFixedPayload();
        Assert.Equal(EtwFixedLayoutStatus.UnknownVersion, WithPayload(bytes, 8, raw => payload.SetEvent(DxgKrnl, 33, 4, raw)));
        Assert.Equal(EtwFixedLayoutStatus.UnknownVersion, WithPayload(bytes, 8, raw => payload.SetEvent(DxgKrnl, 33, 2, raw)));
        Assert.Equal(EtwFixedLayoutStatus.UnknownVersion, WithPayload(bytes, 8,
            raw => PresentFrameEtwLayouts.Catalog.Find(PresentFrameEtwSource.KernelProcessProvider, 1, 5, raw, out _)));
        Assert.Equal(EtwFixedLayoutStatus.NotConsumed, WithPayload(bytes, 8, raw => payload.SetEvent(DxgKrnl, 41, 0, raw)));
        Assert.Equal(EtwFixedLayoutStatus.NotConsumed, WithPayload(bytes, 8, raw => payload.SetEvent(Guid.NewGuid(), 33, 3, raw)));
        Assert.Throws<InvalidDataException>(() => payload.Get(G.pDxgAdapter));
        // A field the event's layout does not carry is never read.
        Assert.Equal(EtwFixedLayoutStatus.Decoded, WithPayload(bytes, 8, raw => payload.SetEvent(DxgKrnl, 33, 3, raw)));
        Assert.Throws<InvalidDataException>(() => payload.Get(G.AdapterLuid));
        // Present (184) is read from the event header only: any version, any length.
        Assert.Equal(EtwFixedLayoutStatus.Decoded, WithPayload([], 8,
            raw => PresentFrameEtwLayouts.Catalog.Find(DxgKrnl, 184, 7, raw, out _)));
    }

    [Theory]
    [InlineData(8, 16)]
    [InlineData(4, 0)]
    public void CountedBytesMoveTheFieldsAfterThem(int pointerSize, int configBytes)
    {
        // DxgKrnl adapter event 110 v1: the UInt32 byte count sits after pDxgAdapter, and
        // AdapterLuid at 40 + 1 pointer + that count; 48 + 1 pointer + count long.
        var end = 48 + pointerSize + configBytes;
        var bytes = Bytes(end, (0, pointerSize == 4 ? (object)0x7000u : 0x7000ul), (pointerSize, (uint)configBytes),
            (40 + pointerSize + configBytes, 0x10000ABCDL));
        var payload = new GpuAllocationFixedPayload();
        var luid = WithPayload(bytes, pointerSize, raw =>
        {
            Assert.Equal(EtwFixedLayoutStatus.Decoded, payload.SetEvent(DxgKrnl, 110, 1, raw));
            Assert.Equal(0x7000ul, payload.Get(G.pDxgAdapter));
            return payload.Get(G.AdapterLuid);
        });
        Assert.Equal(0x10000ABCDL, luid);
        Assert.Equal(EtwFixedLayoutStatus.ShortPayload, WithPayload(bytes[..(end - 1)], pointerSize, raw => payload.SetEvent(DxgKrnl, 110, 1, raw)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(pointerSize), 0xFFFFFFF0u);
        Assert.Equal(EtwFixedLayoutStatus.ShortPayload, WithPayload(bytes, pointerSize, raw => payload.SetEvent(DxgKrnl, 110, 1, raw)));
        Assert.Equal(EtwFixedLayoutStatus.ShortPayload, WithPayload(bytes[..(pointerSize + 3)], pointerSize, raw => payload.SetEvent(DxgKrnl, 110, 1, raw)));
    }

    [Theory]
    [InlineData("RM-GpuAllocations-1")]
    [InlineData("")]
    public void BoundaryStringsEndAtTheirTerminator(string session)
    {
        // GpuAllocationBoundary.BeginScan (2): string session, long scan, long eventsLost.
        var text = session.Length * sizeof(char) + sizeof(char);
        var bytes = Bytes(text + 16, (0, session), (text, 41L), (text + 8, 3L));
        var payload = new GpuAllocationFixedPayload();
        var boundary = GpuAllocationCollector.BoundaryProvider;
        var values = WithPayload(bytes, 8, raw =>
        {
            Assert.Equal(EtwFixedLayoutStatus.Decoded, payload.SetEvent(boundary, 2, 0, raw));
            return new[] { payload.Get(G.session), payload.Get(G.scan), payload.Get(G.eventsLost) };
        });
        Assert.Equal(new object[] { session, 41L, 3L }, values);
        Assert.Equal(EtwFixedLayoutStatus.ShortPayload, WithPayload(bytes[..^1], 8, raw => payload.SetEvent(boundary, 2, 0, raw)));
        // EndScan (3): string session, long scan.
        Assert.Equal(EtwFixedLayoutStatus.Decoded, WithPayload(bytes[..(text + 8)], 8, raw => payload.SetEvent(boundary, 3, 0, raw)));
        // A string without its terminator does not fit the payload.
        var unterminated = Bytes(session.Length * sizeof(char), (0, session));
        Assert.Equal(EtwFixedLayoutStatus.ShortPayload, WithPayload(unterminated, 8, raw => payload.SetEvent(boundary, 3, 0, raw)));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public void FileTimesOutsideDateTimeRangeAreNotDates(long fileTime)
    {
        // Kernel-Process ProcessStart (1) v4: ProcessID at 0, CreateTime at 12; 48-byte fixed prefix.
        var valid = new DateTime(2026, 10, 30, 8, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        var kernelProcess = PresentFrameEtwSource.KernelProcessProvider;
        var (validTime, processId) = WithPayload(Bytes(48, (0, 4321u), (12, valid)), 8, raw =>
        {
            Assert.Equal(EtwFixedLayoutStatus.Decoded, PresentFrameEtwLayouts.Catalog.Find(kernelProcess, 1, 4, raw, out var payload));
            Assert.True(payload.TryFileTime((int)PresentFrameField.CreateTime, out var created));
            Assert.Equal(created, payload.Value((int)PresentFrameField.CreateTime));
            return (created, payload.Int32((int)PresentFrameField.ProcessID));
        });
        Assert.Equal(DateTime.FromFileTime(valid), validTime);
        Assert.Equal(DateTimeKind.Local, validTime.Kind);
        Assert.Equal(4321, processId);
        WithPayload(Bytes(48, (12, fileTime)), 8, raw =>
        {
            Assert.Equal(EtwFixedLayoutStatus.Decoded, PresentFrameEtwLayouts.Catalog.Find(kernelProcess, 1, 4, raw, out var payload));
            Assert.False(payload.TryFileTime((int)PresentFrameField.CreateTime, out _));
            Assert.Null(payload.Value((int)PresentFrameField.CreateTime));
            return 0;
        });
    }

    [Fact]
    public void RejectedEventsAreCountedLoggedOnceAndNeverDelivered()
    {
        var provider = DxgkrnlVidMmEtwLayouts.Provider;
        // The recording holds 367 v0 and 23-byte 371 v0 events. This catalog knows 367 only at
        // v1 and expects 371 to be 4 KiB, so it rejects both.
        var catalog = new EtwFixedLayoutCatalog(1)
            .Add(provider, [367], 1, new(23, 1))
            .Add(provider, [371], 0, new(4096));
        var owner = $"fixed-layout-test-{Guid.NewGuid():N}";
        var logger = new WarningLogger();
        foreach (var replay in new[] { 1, 2 })
        {
            var name = $"{owner}-{replay}";
            var run = EtwEventDropDiagnostics.Start(name, () => 0);
            var gate = new EtwFixedLayoutGate(logger, owner, run);
            var seen = new Dictionary<int, long>();
            var delivered = new List<int>();
            using (var source = new ETWTraceEventSource(Fixture("vidmm-process.etl.bin"), TraceEventSourceType.FileOnly))
            {
                source.UnhandledEvents += data =>
                {
                    var counter = run.Kind(data.ProviderGuid, (int)data.ID, (int)data.Opcode);
                    counter.Receive();
                    if (data.ProviderGuid == provider) seen[(int)data.ID] = seen.GetValueOrDefault((int)data.ID) + 1;
                    if (gate.Admit(data, counter, catalog.Find(data, out _)) && data.ProviderGuid == provider) delivered.Add((int)data.ID);
                };
                source.Process();
                AssertNoManifestParser(source);
            }

            Assert.Empty(delivered);
            Assert.True(seen[367] > 0 && seen[371] > 0);
            var kinds = Assert.Single(EtwEventDropDiagnostics.Read().Sessions, item => item.Name == name).Kinds;
            foreach (var (id, reason) in new[] { (367, "unknown-version"), (371, "short-payload") })
            {
                var named = Assert.Single(kinds, item => item.Kind == $"fixed-layout/{provider:D}/{id}/v0/{reason}");
                Assert.Equal((seen[id], seen[id]), (named.Received, named.Dropped));
                var perKind = kinds.Where(item => item.Kind.StartsWith($"{provider:D}/{id}/", StringComparison.Ordinal)).ToArray();
                Assert.Equal((seen[id], seen[id]), (perKind.Sum(item => item.Received), perKind.Sum(item => item.Dropped)));
            }
            run.Complete();
        }

        // One warning per (owner, provider, ID, version, reason) in the process, not per event or session.
        Assert.Equal(2, logger.Warnings.Count);
        Assert.Contains(logger.Warnings, line => line.Contains("/367 version 0", StringComparison.Ordinal) && line.Contains("unknown-version", StringComparison.Ordinal));
        Assert.Contains(logger.Warnings, line => line.Contains("/371 version 0", StringComparison.Ordinal) && line.Contains("short-payload", StringComparison.Ordinal));
    }

    [Fact]
    public void DecodingConsumedEventsAllocatesNothing()
    {
        var catalogs = new[]
        {
            ("present.etl.bin", PresentFrameEtwLayouts.Catalog),
            ("vidmm.etl.bin", GpuAllocationFixedPayload.Catalog),
            ("vidmm-process.etl.bin", DxgkrnlVidMmEtwLayouts.Catalog)
        };
        long allocated = 0, checksum = 0;
        var decoded = 0;
        // The first pass initializes the catalogs and JIT-compiles the readers.
        for (var pass = 0; pass < 2; pass++)
        {
            var measured = pass == 1;
            foreach (var (fixture, catalog) in catalogs)
            {
                using var source = new ETWTraceEventSource(Fixture(fixture), TraceEventSourceType.FileOnly);
                source.UnhandledEvents += data =>
                {
                    var before = GC.GetAllocatedBytesForCurrentThread();
                    var status = catalog.Find(data, out var payload);
                    if (status == EtwFixedLayoutStatus.Decoded)
                        for (var field = 0; field < payload.Layout!.Fields.Length; field++)
                            checksum += ReadTyped(payload, field);
                    var used = GC.GetAllocatedBytesForCurrentThread() - before;
                    if (!measured) return;
                    allocated += used;
                    if (status == EtwFixedLayoutStatus.Decoded) decoded++;
                };
                source.Process();
            }
        }
        Assert.True(decoded > 0);
        Assert.Equal(0, allocated);
        output.WriteLine($"{decoded} decoded events, {allocated} managed bytes allocated by lookup and typed reads (checksum {checksum}).");
    }

    [Fact]
    public void GpuAllocationSessionNeverConstructsTheManifestParsers()
    {
        using var source = new ETWTraceEventSource(Fixture("vidmm.etl.bin"), TraceEventSourceType.FileOnly);
        var gpu = new TraceEventGpuAllocationSession(FileSession($"fixed-layout-gpu-{Guid.NewGuid():N}", source), NullLogger.Instance);
        var consumed = new HashSet<int>(GpuAllocationFixedPayload.Catalog.Layouts.Where(item => item.Provider == DxgKrnl).Select(item => item.Id));
        var delivered = 0;
        gpu.Bind((provider, id, payload, _) =>
        {
            if (provider != DxgKrnl || !consumed.Contains(id)) return;
            Assert.IsType<GpuAllocationFixedPayload>(payload);
            delivered++;
        });
        source.Process();
        Assert.True(delivered > 0);
        AssertNoManifestParser(source);
    }

    [Fact]
    public void PresentSessionNeverConstructsTheManifestParsers()
    {
        using var source = new ETWTraceEventSource(Fixture("present.etl.bin"), TraceEventSourceType.FileOnly);
        var present = (TraceEventPresentFrameEtwSession)RuntimeHelpers.GetUninitializedObject(typeof(TraceEventPresentFrameEtwSession));
        Set(present, "name", $"fixed-layout-present-{Guid.NewGuid():N}");
        Set(present, "logger", NullLogger.Instance);
        Set(present, "source", source);
        var delivered = 0;
        present.BindUsage(data =>
        {
            if (PresentFrameEtwLayouts.Catalog.Find(data, out _) == EtwFixedLayoutStatus.Decoded) delivered++;
            return true;
        });
        source.Process();
        Assert.True(delivered > 0);
        AssertNoManifestParser(source);
    }

    [Fact]
    public void VidMmSessionNeverConstructsTheManifestParsers()
    {
        using var source = new ETWTraceEventSource(Fixture("vidmm-process.etl.bin"), TraceEventSourceType.FileOnly);
        var vidMm = (TraceEventVidMmSession)RuntimeHelpers.GetUninitializedObject(typeof(TraceEventVidMmSession));
        var drops = EtwEventDropDiagnostics.Start($"fixed-layout-vidmm-{Guid.NewGuid():N}", () => 0);
        Set(vidMm, "session", FileSession("fixed-layout-vidmm", source));
        Set(vidMm, "drops", drops);
        Set(vidMm, "layouts", new EtwFixedLayoutGate(NullLogger.Instance, "VidMm test", drops));
        var readings = 0;
        vidMm.BindUsage(data =>
        {
            if (DxgkrnlVidMmEtwTelemetryZone.TryReadProcessMemory(data, out _, out _, out _)) readings++;
            return true;
        });
        source.Process();
        drops.Complete();
        Assert.True(readings > 0);
        AssertNoManifestParser(source);
    }

    [Fact]
    public void ManifestParserProbeSeesDynamicAccess()
    {
        using var source = new ETWTraceEventSource(Fixture("vidmm-process.etl.bin"), TraceEventSourceType.FileOnly);
        AssertNoManifestParser(source);
        _ = source.Dynamic;
        Assert.NotNull(SourceField("_Dynamic").GetValue(source));
        Assert.NotNull(LastChanceHandlers.GetValue(source));
    }

    // Source.Dynamic and Source.Registered build TraceEvent's manifest parsers; either one
    // registers a last-chance handler that resolves every unknown event through TDH.
    internal static void AssertNoManifestParser(TraceEventDispatcher source)
    {
        Assert.Null(SourceField("_Dynamic").GetValue(source));
        Assert.Null(SourceField("_Registered").GetValue(source));
        Assert.Null(LastChanceHandlers.GetValue(source));
    }

    private static readonly FieldInfo LastChanceHandlers =
        typeof(TraceEventDispatcher).GetField("lastChanceHandlers", BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static FieldInfo SourceField(string name)
        => typeof(TraceEventSource).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    // Every field the layouts read, compared with the manifest parser's value of the same event:
    // boxed type and value, and the typed accessor the consumers use.
    private HashSet<(Guid, int, int)> AssertLayoutsMatchManifest(string fixture, EtwFixedLayoutCatalog catalog, string[] names,
        Action<TraceEvent>? check = null)
    {
        var decoded = new HashSet<(Guid, int, int)>();
        var events = 0;
        var fields = 0;
        using var source = new ETWTraceEventSource(Fixture(fixture), TraceEventSourceType.FileOnly);
        source.Dynamic.All += data =>
        {
            var status = catalog.Find(data, out var payload);
            if (status == EtwFixedLayoutStatus.NotConsumed) return;
            Assert.True(status == EtwFixedLayoutStatus.Decoded, $"{data.ProviderName}/{(int)data.ID}/v{data.Version}: {status}");
            decoded.Add((data.ProviderGuid, (int)data.ID, data.Version));
            events++;
            for (var field = 0; field < names.Length; field++)
            {
                if (!payload.Has(field)) continue;
                var index = data.PayloadIndex(names[field]);
                Assert.True(index >= 0, $"{data.ProviderName}/{(int)data.ID}/v{data.Version} has no field {names[field]}.");
                var expected = data.PayloadValue(index);
                Assert.Equal(Describe(expected), Describe(payload.Value(field)));
                AssertTypedRead(payload, field, expected);
                fields++;
            }
            check?.Invoke(data);
        };
        source.Process();
        output.WriteLine($"{fixture}: {events} consumed events, {fields} field values identical to the manifest parser, {decoded.Count} ID/version kinds.");
        return decoded;
    }

    private static void AssertTypedRead(EtwFixedPayload payload, int field, object expected)
    {
        switch (payload.Layout!.Fields[field].Type)
        {
            case EtwFieldType.Boolean: Assert.Equal((bool)expected, payload.Boolean(field)); break;
            case EtwFieldType.UInt32:
                Assert.Equal((int)expected, payload.Int32(field));
                Assert.Equal(unchecked((uint)(int)expected), payload.UInt32(field));
                break;
            case EtwFieldType.UInt64 or EtwFieldType.Int64:
                Assert.Equal((long)expected, payload.Int64(field));
                Assert.Equal(unchecked((ulong)(long)expected), payload.UInt64(field));
                break;
            case EtwFieldType.Pointer: Assert.Equal((ulong)expected, payload.Pointer(field)); break;
            case EtwFieldType.FileTime:
                Assert.True(payload.TryFileTime(field, out var time));
                Assert.Equal((DateTime)expected, time);
                break;
            case EtwFieldType.UnicodeString: Assert.Equal((string)expected, payload.UnicodeString(field)); break;
            default: throw new InvalidDataException($"Field {field} has no type.");
        }
    }

    private static long ReadTyped(EtwFixedPayload payload, int field) => payload.Layout!.Fields[field].Type switch
    {
        EtwFieldType.Boolean => payload.Boolean(field) ? 1 : 0,
        EtwFieldType.UInt32 => payload.UInt32(field),
        EtwFieldType.UInt64 or EtwFieldType.Int64 => payload.Int64(field),
        EtwFieldType.Pointer => unchecked((long)payload.Pointer(field)),
        EtwFieldType.FileTime => payload.TryFileTime(field, out var time) ? time.Ticks : -1,
        _ => 0
    };

    private static string Describe(object? value) => value switch
    {
        null => "null",
        DateTime time => $"DateTime {time.Ticks} {time.Kind}",
        _ => $"{value.GetType().Name} {Convert.ToString(value, CultureInfo.InvariantCulture)}"
    };

    // DxgkrnlVidMmEtwTelemetryZone before fixed layouts: event-name match, then the first
    // positive ProcessId-like payload or the header process, then the best-scored plausible
    // byte-value payload by name.
    private static bool RemovedNameHeuristic(TraceEvent data, out int processId, out double valueBytes)
    {
        processId = 0;
        valueBytes = 0;
        if (!string.Equals(data.ProviderName, "Microsoft-Windows-DxgKrnl", StringComparison.OrdinalIgnoreCase)
            || !new[] { "VidMmProcessUsageChange", "VidMmProcessCommitmentChange", "VidMmProcessDemotedCommitmentChange",
                "ReportCommittedAllocation", "ProcessAllocation", "ProcessAllocationDetails" }
                .Contains(data.EventName, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var found = false;
        foreach (var name in new[] { "ProcessId", "ProcessID", "Pid", "PID", "OwnerProcessId", "OwnerProcessID" })
        {
            if (TryRead(data, name, Convert.ToInt32, out processId) && processId > 0) { found = true; break; }
        }
        if (!found) processId = data.ProcessID;
        if (processId <= 0) return false;

        static bool IsBytes(string name) => new[] { "Usage", "Commit", "Resident", "Bytes", "Size" }
            .Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
        static int Score(string name) => new[] { ("Current", 20), ("Usage", 18), ("Commitment", 16), ("Committed", 14),
            ("Resident", 12), ("Bytes", 8), ("Size", 4) }.Where(part => name.Contains(part.Item1, StringComparison.OrdinalIgnoreCase)).Sum(part => part.Item2);
        foreach (var name in data.PayloadNames.Where(IsBytes).OrderByDescending(Score))
        {
            if (TryRead(data, name, Convert.ToDouble, out valueBytes)
                && !double.IsNaN(valueBytes) && !double.IsInfinity(valueBytes)
                && valueBytes >= 4096 && valueBytes <= 512d * 1024 * 1024 * 1024)
            {
                return true;
            }
        }
        valueBytes = 0;
        return false;
    }

    private static bool TryRead<T>(TraceEvent data, string name, Func<object, T> convert, out T value)
    {
        value = default!;
        try
        {
            if (data.PayloadByName(name) is not { } payload) return false;
            value = convert(payload);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static T WithPayload<T>(byte[] bytes, int pointerSize, Func<EtwRawPayload, T> read)
    {
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            return read(new EtwRawPayload(handle.AddrOfPinnedObject(), bytes.Length, pointerSize));
        }
        finally
        {
            handle.Free();
        }
    }

    // Little-endian payload bytes; strings are UTF-16 and end at the next zero byte pair.
    private static byte[] Bytes(int length, params (int Offset, object Value)[] values)
    {
        var bytes = new byte[length];
        foreach (var (offset, value) in values)
        {
            var target = bytes.AsSpan(offset);
            switch (value)
            {
                case byte number: target[0] = number; break;
                case uint number: BinaryPrimitives.WriteUInt32LittleEndian(target, number); break;
                case ulong number: BinaryPrimitives.WriteUInt64LittleEndian(target, number); break;
                case long number: BinaryPrimitives.WriteInt64LittleEndian(target, number); break;
                case string text: Encoding.Unicode.GetBytes(text).CopyTo(target); break;
                default: throw new ArgumentException($"Unsupported payload value {value.GetType().Name}.");
            }
        }
        return bytes;
    }

    // A session object whose Source is the given file source: no native session exists.
    private static TraceEventSession FileSession(string name, ETWTraceEventSource source)
    {
        var session = (TraceEventSession)RuntimeHelpers.GetUninitializedObject(typeof(TraceEventSession));
        GC.SuppressFinalize(session); // This reflection-only fixture owns no native session to release.
        Set(session, "m_SessionName", name);
        Set(session, "m_source", source);
        return session;
    }

    private static void Set(object target, string field, object? value)
        => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static string Fixture(string name, [CallerFilePath] string source = "")
        => Path.Combine(Path.GetDirectoryName(source)!, "Fixtures/EtwFixedLayout", name);

    private sealed class WarningLogger : ILogger
    {
        internal List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }
}

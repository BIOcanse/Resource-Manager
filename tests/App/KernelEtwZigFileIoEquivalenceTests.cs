using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace Resource_Manager_APP.Tests;

/// <summary>Independent raw-native state replay, then exact streaming comparison with TraceEvent.</summary>
public sealed class KernelEtwZigFileIoEquivalenceTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly Guid FileProvider = new("90cbdc39-4a3e-11d1-84f4-0000f80464e3");
    private static readonly Guid ThreadProvider = new("3d6fa8d1-fe05-11d0-9dda-00c04fd7ba7c");
    private static readonly Guid NamespaceProvider = new("9b79ee91-b5fd-41c0-a243-4248e266e9d0");

    [EnvironmentVariableFact("RESOURCE_MANAGER_IO_ETL_REPLAY_PATH")]
    public unsafe void RecordedFileIoFieldsAndNamesMatchTheIndependentNativeStateExactly()
    {
        var path = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_IO_ETL_REPLAY_PATH");
        Assert.True(File.Exists(path), $"Owned FileIO ETL is absent: {path}");
        var scratch = Environment.GetEnvironmentVariable("RESOURCE_MANAGER_FILEIO_SPOOL_ROOT");
        Assert.False(string.IsNullOrWhiteSpace(scratch), "Supply the lifecycle-prepared scratch through RESOURCE_MANAGER_FILEIO_SPOOL_ROOT.");
        // Disposable spool belongs to the validation batch, separate from the
        // small accepted report and original recording retained after comparison.
        var spool = Path.Combine(scratch!, "fileio-native-fields.bin");
        Assert.True(Directory.Exists(Path.GetDirectoryName(spool)));
        var rawCounts = new long[256];
        long rawRecords = 0, threadRecords = 0, namespaceRecords = 0, names = 0;
        long resolvedIdentities = 0, resolvedMissingHeaderPid = 0;
        uint rawEventsLost = 0, rawBuffersLost = 0;
        Exception? failure = null;
        Assert.Equal(0, Create(out var replay));
        using (var writer = new BinaryWriter(new FileStream(spool, FileMode.Create, FileAccess.Write, FileShare.Read, 65536)))
        {
            RawCallback callback = record =>
            {
                ++rawRecords;
                var file = record->Provider == FileProvider;
                if (file) ++rawCounts[record->Opcode];
                if (failure is not null) return;
                var thread = record->Provider == ThreadProvider && record->Opcode is >= 1 and <= 4;
                var ns = record->Provider == NamespaceProvider;
                if (!file && !thread && !ns) return;
                try
                {
                    var status = Observe(replay, record->Provider, record->Opcode, record->Version,
                        (byte)((record->Flags & 0x20) != 0 ? 4 : 8), record->Qpc, record->ProcessId, record->ThreadId,
                        record->Payload, record->PayloadLength, out var view);
                    Assert.True(status == 0, $"Native FileIO replay status={status}; provider={record->Provider}; opcode={record->Opcode}; version={record->Version}; bytes={record->PayloadLength}; QPC={record->Qpc}.");
                    if (thread) ++threadRecords;
                    if (ns) ++namespaceRecords;
                    if (!file) return;
                    if (view.Kind == 0) return; // Outside the actual native FileIoState contract; counted explicitly.
                    if ((view.Kind is >= 2 and <= 7 or 9) && view.Pid != uint.MaxValue && view.Tid != uint.MaxValue)
                    {
                        ++resolvedIdentities;
                        if (record->ProcessId == uint.MaxValue) ++resolvedMissingHeaderPid;
                    }
                    var text = view.NameLength == 0 ? string.Empty : Marshal.PtrToStringUni(view.Name, checked((int)view.NameLength))!;
                    view.Name = 0;
                    Assert.InRange(checked(writer.BaseStream.Position + sizeof(FileView) + sizeof(int) + text.Length * 2L), 0, 1024L * 1024 * 1024);
                    writer.Write(new ReadOnlySpan<byte>(&view, sizeof(FileView)));
                    writer.Write(text.Length);
                    writer.Write(MemoryMarshal.AsBytes(text.AsSpan())); // Preserve every UTF-16 code unit.
                    if (text.Length != 0) ++names;
                }
                catch (Exception error) { failure = error; }
            };
            try
            {
                fixed (char* name = path)
                {
                    var log = new RawLogFile { FileName = (nint)name, Mode = 0x10001000,
                        Callback = Marshal.GetFunctionPointerForDelegate(callback) };
                    var handle = OpenTrace(ref log);
                    Assert.NotEqual(ulong.MaxValue, handle);
                    try
                    {
                        Assert.Equal(1u, log.ClockType);
                        Assert.True(log.Frequency > 0);
                        Assert.Equal(0u, ProcessTrace(ref handle, 1, 0, 0));
                        rawEventsLost = log.EventsLost; rawBuffersLost = log.BuffersLost;
                    }
                    finally { Assert.Equal(0u, CloseTrace(handle)); }
                }
            }
            finally
            {
                GC.KeepAlive(callback);
                try
                {
                    Assert.Equal(0, Usage(replay, out var usage));
                    output.WriteLine($"Independent native FileIO storage: current={usage.Current}; peak={usage.Peak}; cap={usage.Cap}; entries={usage.Entries}; versions={usage.Versions}; internedNames={usage.Names}.");
                }
                finally { Assert.Equal(0, Destroy(replay)); }
            }
        }
        output.WriteLine($"Raw FileIO inventory EOF: records={rawRecords}; threadRecords={threadRecords}; namespaceRecords={namespaceRecords}; "
            + $"nonemptyNames={names}; resolvedIdentities={resolvedIdentities}; resolvedMissingHeaderPid={resolvedMissingHeaderPid}; EventsLost={rawEventsLost}; BuffersLost={rawBuffersLost}; "
            + string.Join(", ", Enumerable.Range(0, 256).Where(x => rawCounts[x] != 0).Select(x => $"opcode{x}={rawCounts[x]}")));
        if (failure is not null) throw new InvalidOperationException("The independent raw FileIO replay failed.", failure);

        var compared = new long[256];
        using var native = new BinaryReader(new FileStream(spool, FileMode.Open, FileAccess.Read, FileShare.Read, 65536));
        using var source = new ETWTraceEventSource(path);
        var parser = new KernelTraceEventParser(source);
        long managedRecords = 0;
        (FileView View, string Name) Next(TraceEvent data)
        {
            Assert.True(native.BaseStream.Position < native.BaseStream.Length, $"Native typed stream ended before opcode={data.Opcode}.");
            var bytes = native.ReadBytes(sizeof(FileView));
            Assert.Equal(sizeof(FileView), bytes.Length);
            var view = MemoryMarshal.Read<FileView>(bytes);
            var length = native.ReadInt32();
            Assert.InRange(length, 0, 32768);
            var textBytes = native.ReadBytes(checked(length * 2));
            Assert.Equal(length * 2, textBytes.Length);
            var text = new string(MemoryMarshal.Cast<byte, char>(textBytes));
            Assert.Equal((uint)data.Opcode, view.Opcode);
#pragma warning disable CS0618
            Assert.Equal(data.TimeStampQPC, view.Qpc);
#pragma warning restore CS0618
            ++compared[(byte)data.Opcode];
            return (view, text);
        }
        static void Identity(FileView expected, TraceEvent actual)
        {
            Assert.Equal(unchecked((uint)actual.ProcessID), expected.Pid);
            Assert.Equal(unchecked((uint)actual.ThreadID), expected.Tid);
        }
        void Name(FileIONameTraceData data)
        {
            var (expected, text) = Next(data);
            Assert.True(expected.Kind is 1 or 8);
            Assert.Equal(data.FileKey, expected.FileKey); Assert.Equal(data.FileName, text);
        }
        parser.FileIOName += Name;
        parser.FileIOFileCreate += Name;
        parser.FileIOFileDelete += Name;
        parser.FileIOFileRundown += Name;
        parser.FileIOCreate += data =>
        {
            var (expected, text) = Next(data); Assert.Equal(2u, expected.Kind); Identity(expected, data);
            Assert.Equal(data.IrpPtr, expected.Irp); Assert.Equal(data.FileObject, expected.FileObject);
            Assert.Equal(unchecked((uint)data.CreateOptions), expected.CreateOptions);
            Assert.Equal(unchecked((uint)data.CreateDisposition), expected.CreateDisposition);
            Assert.Equal(unchecked((uint)data.FileAttributes), expected.FileAttributes);
            Assert.Equal(unchecked((uint)data.ShareAccess), expected.ShareAccess); Assert.Equal(data.FileName, text);
        };
        void Simple(FileIOSimpleOpTraceData data)
        {
            var (expected, text) = Next(data); Assert.Equal(9u, expected.Kind); Identity(expected, data);
            Assert.Equal(data.IrpPtr, expected.Irp); Assert.Equal(data.FileObject, expected.FileObject);
            Assert.Equal(data.FileKey, expected.FileKey); Assert.Equal(data.FileName, text);
        }
        parser.FileIOCleanup += Simple;
        parser.FileIOClose += Simple;
        void Transfer(FileIOReadWriteTraceData data)
        {
            var (expected, text) = Next(data); Assert.Equal((byte)data.Opcode == 67 ? 3u : 4u, expected.Kind); Identity(expected, data);
            Assert.Equal(data.IrpPtr, expected.Irp); Assert.Equal(data.FileObject, expected.FileObject); Assert.Equal(data.FileKey, expected.FileKey);
            Assert.Equal(data.Offset, expected.Offset); Assert.Equal(unchecked((uint)data.IoSize), expected.IoSize);
            Assert.Equal(unchecked((uint)data.IoFlags), expected.IoFlags); Assert.Equal(data.FileName, text);
        }
        parser.FileIORead += Transfer;
        parser.FileIOWrite += Transfer;
        void Info(FileIOInfoTraceData data)
        {
            var (expected, text) = Next(data); Assert.Equal((byte)data.Opcode switch { 69 => 5u, 70 => 6u, 71 => 7u, _ => 0u }, expected.Kind); Identity(expected, data);
            Assert.Equal(data.IrpPtr, expected.Irp); Assert.Equal(data.FileObject, expected.FileObject); Assert.Equal(data.FileKey, expected.FileKey);
            Assert.Equal(data.ExtraInfo, expected.ExtraInfo); Assert.Equal(unchecked((uint)data.InfoClass), expected.InfoClass); Assert.Equal(data.FileName, text);
        }
        parser.FileIOSetInfo += Info;
        parser.FileIODelete += Info;
        parser.FileIORename += Info;
        parser.ThreadStartGroup += _ => { };
        parser.ThreadEndGroup += _ => { };
        source.AllEvents += _ => ++managedRecords;
        source.Process(); // Full finite file; no skipped prefix, family or mismatching row.
        Assert.Equal(native.BaseStream.Length, native.BaseStream.Position);
        Assert.Equal(rawRecords, managedRecords);
        Assert.Equal(rawEventsLost, checked((uint)source.EventsLost));
        foreach (var opcode in new[] { 0, 32, 35, 36, 64, 65, 66, 67, 68, 69, 70, 71 })
            Assert.Equal(rawCounts[opcode], compared[opcode]);
        output.WriteLine($"Independent FileIO EOF: records={rawRecords}; nativeThreadRecords={threadRecords}; namespaceRecords={namespaceRecords}; nonemptyNames={names}; "
            + $"recordedEventsLost={source.EventsLost}; exact counts: "
            + string.Join(", ", Enumerable.Range(0, 256).Where(x => rawCounts[x] != 0).Select(x => $"{x}={rawCounts[x]} (compared={compared[x]})")));
        output.WriteLine($"Coverage qualification: recorded namespace events={namespaceRecords}; FileIO header-PID fallback resolutions={resolvedMissingHeaderPid}. "
            + "Zero is a real-input coverage gap, not equality evidence; separate synthetic namespace/FileIO fallback tests remain independent.");
        foreach (var opcode in new[] { 32, 35, 64, 65, 66, 67, 68, 69, 70, 71 })
            Assert.True(compared[opcode] > 0, $"FileIO opcode {opcode} is absent: equality cannot establish its real-input coverage.");
        Assert.True(names > 0 && threadRecords > 0 && resolvedIdentities > 0);
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileView
    {
        public uint Kind, Opcode, Pid, Tid; public long Qpc;
        public ulong FileKey, FileObject, Irp; public long Offset; public ulong ExtraInfo;
        public uint IoSize, IoFlags, CreateOptions, CreateDisposition, FileAttributes, ShareAccess, InfoClass, NameLength;
        public nint Name;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileUsage
    {
        public ulong Current, Peak, Cap, Entries, Versions, Names;
    }
    [StructLayout(LayoutKind.Explicit, Size = 112)] private unsafe struct RawRecord
    {
        [FieldOffset(4)] public ushort Flags;
        [FieldOffset(8)] public uint ThreadId;
        [FieldOffset(12)] public uint ProcessId;
        [FieldOffset(16)] public long Qpc;
        [FieldOffset(24)] public Guid Provider;
        [FieldOffset(42)] public byte Version;
        [FieldOffset(45)] public byte Opcode;
        [FieldOffset(86)] public ushort PayloadLength;
        [FieldOffset(96)] public byte* Payload;
    }
    [StructLayout(LayoutKind.Explicit, Size = 448)] private struct RawLogFile
    {
        [FieldOffset(0)] public nint FileName;
        [FieldOffset(28)] public uint Mode;
        [FieldOffset(376)] public long Frequency;
        [FieldOffset(392)] public uint ClockType;
        [FieldOffset(396)] public uint BuffersLost;
        [FieldOffset(416)] public uint EventsLost;
        [FieldOffset(424)] public nint Callback;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private unsafe delegate void RawCallback(RawRecord* record);
    [DllImport("advapi32.dll", EntryPoint = "OpenTraceW", SetLastError = true)] private static extern ulong OpenTrace(ref RawLogFile logfile);
    [DllImport("advapi32.dll", EntryPoint = "ProcessTrace")] private static extern uint ProcessTrace(ref ulong handle, uint count, nint start, nint end);
    [DllImport("advapi32.dll", EntryPoint = "CloseTrace")] private static extern uint CloseTrace(ulong handle);
    [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_io_replay_file_create", CallingConvention = CallingConvention.Cdecl)] private static extern int Create(out nint replay);
    [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_io_replay_file_destroy", CallingConvention = CallingConvention.Cdecl)] private static extern int Destroy(nint replay);
    [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_io_replay_file_usage", CallingConvention = CallingConvention.Cdecl)] private static extern int Usage(nint replay, out FileUsage usage);
    [DllImport("ResourceManager.NativeCore", EntryPoint = "rm_kernel_etw_io_replay_file_observe", CallingConvention = CallingConvention.Cdecl)] private static extern unsafe int Observe(nint replay, Guid provider, byte opcode, byte version, byte pointerSize, long qpc, uint pid, uint tid, byte* payload, uint payloadLength, out FileView view);
}

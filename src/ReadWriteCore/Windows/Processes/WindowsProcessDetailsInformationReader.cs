using System.ComponentModel;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.ResourceBreakdown;

namespace ResourceManager.ReadWrite.Windows.Processes;

internal interface IWindowsProcessDetailsInformationReader
{
    IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult> Read(
        IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsFields> selection,
        CancellationToken cancellationToken);
}

internal interface IWindowsProcessDetailsNativeApi
{
    IntPtr OpenProcess(uint desiredAccess, int processId);
    uint GetProcessId(IntPtr process);
    bool TryGetCreationFileTime(IntPtr process, out ulong creationFileTime);
    bool IsRunning(IntPtr process);
    bool TryReadImagePath(IntPtr process, out string path);
    bool TryReadModules(IntPtr process, out IReadOnlyList<WindowsProcessLoadedModule> modules);
    bool TryVerifyImageSignature(string path, out int status);
    bool TryReadSystemProcessInformation(out byte[] data);
    bool CloseHandle(IntPtr handle);
}

/// <summary>Per-call, read-only process detail reader. It retains no handle, baseline, or snapshot.</summary>
internal sealed class WindowsProcessDetailsInformationReader : IWindowsProcessDetailsInformationReader
{
    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint ProcessVmRead = 0x0010;
    internal const uint Synchronize = 0x00100000;
    private readonly IWindowsProcessDetailsNativeApi api;
    private readonly WindowsProcessDetailsOptions options;

    internal WindowsProcessDetailsInformationReader() : this(WindowsProcessDetailsOptions.Default) { }

    internal WindowsProcessDetailsInformationReader(WindowsProcessDetailsOptions options)
        : this(new WindowsProcessDetailsNativeApi(options), options) { }

    internal WindowsProcessDetailsInformationReader(IWindowsProcessDetailsNativeApi api)
        : this(api, WindowsProcessDetailsOptions.Default) { }

    internal WindowsProcessDetailsInformationReader(IWindowsProcessDetailsNativeApi api, WindowsProcessDetailsOptions options)
    {
        this.api = api ?? throw new ArgumentNullException(nameof(api));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult> Read(
        IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsFields> selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();
        if (selection.Count == 0 || selection.Count > options.MaximumProcesses)
            return new Dictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult>();
        var results = selection.Keys.ToDictionary(static key => key, static _ => WindowsProcessDetailsReadResult.Unavailable);

        var duplicatePids = selection.Keys.GroupBy(static key => key.ProcessId)
            .Where(static group => group.Count() > 1).Select(static group => group.Key).ToHashSet();
        var prepared = new List<PreparedProcess>(selection.Count);
        var threadSnapshotPids = new HashSet<int>();
        try
        {
            foreach (var pair in selection)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pair.Value == WindowsProcessDetailsFields.None || pair.Key.ProcessId <= 0
                    || pair.Key.StartKey <= 0 || duplicatePids.Contains(pair.Key.ProcessId)) continue;

                var access = ProcessQueryLimitedInformation | Synchronize;
                if ((pair.Value & WindowsProcessDetailsFields.LoadedModules) != 0)
                    access |= ProcessQueryInformation | ProcessVmRead;
                var handle = SafeOpen(access, pair.Key.ProcessId);
                if (!IsValidHandle(handle)) continue;
                PreparedProcess item;
                try
                {
                    item = new(pair.Key, pair.Value, handle);
                    prepared.Add(item);
                }
                catch
                {
                    try { api.CloseHandle(handle); } catch { }
                    throw;
                }
                item.IdentityValidBefore = Matches(item);
                if (item.IdentityValidBefore && (pair.Value & WindowsProcessDetailsFields.ThreadState) != 0)
                    threadSnapshotPids.Add(pair.Key.ProcessId);
            }

            Dictionary<int, IReadOnlyList<WindowsProcessThreadState>>? threadRows = null;
            if (threadSnapshotPids.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (api.TryReadSystemProcessInformation(out var bytes)
                        && WindowsProcessSystemThreadParser.TryParse(bytes, threadSnapshotPids, options, out var parsed))
                        threadRows = parsed;
                }
                catch (Exception error) when (IsSourceFailure(error)) { }
                cancellationToken.ThrowIfCancellationRequested();
            }

            foreach (var item in prepared)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!item.IdentityValidBefore) continue;
                if ((item.Fields & WindowsProcessDetailsFields.ImageSignature) != 0)
                    ReadSignature(item, cancellationToken);
                if ((item.Fields & WindowsProcessDetailsFields.LoadedModules) != 0)
                    ReadModules(item, cancellationToken);
                if ((item.Fields & WindowsProcessDetailsFields.ThreadState) != 0
                    && threadRows?.TryGetValue(item.Key.ProcessId, out var rows) == true)
                {
                    item.ThreadsSucceeded = true;
                    item.Threads = rows;
                }
                item.IdentityValidAfter = Matches(item);
            }
        }
        finally
        {
            foreach (var item in prepared)
            {
                var closed = false;
                try { closed = api.CloseHandle(item.Handle); }
                catch (Exception) { }
                if (!closed) item.IdentityValidAfter = false;
            }
        }

        foreach (var item in prepared)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!item.IdentityValidBefore || !item.IdentityValidAfter) continue;
            results[item.Key] = new(
                item.SignatureSucceeded, item.SignatureStatus,
                item.ModulesSucceeded, item.Modules.ToImmutableArray(),
                item.ThreadsSucceeded, item.Threads.ToImmutableArray());
        }
        return results;
    }

    private void ReadSignature(PreparedProcess item, CancellationToken cancellationToken)
    {
        try
        {
            if (!api.TryReadImagePath(item.Handle, out var path) || string.IsNullOrWhiteSpace(path)) return;
            cancellationToken.ThrowIfCancellationRequested();
            if (!api.TryVerifyImageSignature(path, out var status)) return;
            item.SignatureStatus = unchecked((uint)status);
            item.SignatureSucceeded = true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsSourceFailure(error)) { }
    }

    private void ReadModules(PreparedProcess item, CancellationToken cancellationToken)
    {
        try
        {
            if (!api.TryReadModules(item.Handle, out var modules)) return;
            cancellationToken.ThrowIfCancellationRequested();
            item.Modules = modules;
            item.ModulesSucceeded = true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (IsSourceFailure(error)) { }
    }

    private bool Matches(PreparedProcess item)
    {
        try
        {
            return api.GetProcessId(item.Handle) == (uint)item.Key.ProcessId
                && api.TryGetCreationFileTime(item.Handle, out var creation)
                && creation == (ulong)item.Key.StartKey
                && api.IsRunning(item.Handle);
        }
        catch (Exception error) when (IsSourceFailure(error)) { return false; }
    }

    private IntPtr SafeOpen(uint access, int processId)
    {
        try { return api.OpenProcess(access, processId); }
        catch (Exception error) when (IsSourceFailure(error)) { return IntPtr.Zero; }
    }

    private static bool IsValidHandle(IntPtr handle) => handle != IntPtr.Zero && handle != new IntPtr(-1);

    private static bool IsSourceFailure(Exception error) => error is Win32Exception or InvalidOperationException
        or ExternalException or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException
        or NotSupportedException or UnauthorizedAccessException or ArgumentException or IOException or TimeoutException;

    private sealed class PreparedProcess(ProcessInstanceKey key, WindowsProcessDetailsFields fields, IntPtr handle)
    {
        internal ProcessInstanceKey Key { get; } = key;
        internal WindowsProcessDetailsFields Fields { get; } = fields;
        internal IntPtr Handle { get; } = handle;
        internal bool IdentityValidBefore;
        internal bool IdentityValidAfter;
        internal bool SignatureSucceeded;
        internal uint? SignatureStatus;
        internal bool ModulesSucceeded;
        internal IReadOnlyList<WindowsProcessLoadedModule> Modules = [];
        internal bool ThreadsSucceeded;
        internal IReadOnlyList<WindowsProcessThreadState> Threads = [];
    }
}

/// <summary>Bounds and validates the documented variable-length process/thread table.</summary>
internal static class WindowsProcessSystemThreadParser
{
    internal static unsafe bool TryParseInventory(ReadOnlySpan<byte> data, WindowsProcessDetailsOptions options,
        out WindowsProcessInventoryEntry[] processes)
    {
        processes = [];
        if (!TryParseCreations(data, options, out var creations)) return false;
        var rows = new List<WindowsProcessInventoryEntry>(creations.Count);
        var offset = 0;
        fixed (byte* start = data)
        {
            while (offset < data.Length)
            {
                var header = MemoryMarshal.Read<SystemProcessInformationHeader>(data[offset..]);
                var pid = (int)PointerValue(header.UniqueProcessId);
                var parent = PointerValue(header.InheritedFromUniqueProcessId);
                var image = header.ImageName;
                string? name = null;
                if (image.Length != 0)
                {
                    var imageOffset = unchecked((ulong)image.Buffer.ToInt64() - (ulong)start);
                    if ((image.Length & 1) == 0 && image.Length <= image.MaximumLength
                        && imageOffset <= (ulong)data.Length && image.Length <= (ulong)data.Length - imageOffset)
                    {
                        // Process.ProcessName omits the executable extension.
                        var characters = MemoryMarshal.Cast<byte, char>(data.Slice((int)imageOffset, image.Length));
                        if (characters.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) characters = characters[..^4];
                        name = new string(characters);
                    }
                }
                var creation = header.CreationTime;
                rows.Add(new(pid, new(creation > 0 ? ProcessReadStatus.Current : ProcessReadStatus.Unavailable,
                    creation > 0 ? creation : null), parent <= int.MaxValue ? (int)parent : 0, name,
                    ThreadCount: header.NumberOfThreads <= options.MaximumThreadsPerProcess ? header.NumberOfThreads : null));
                if (header.NextEntryOffset == 0) break;
                offset += (int)header.NextEntryOffset;
            }
        }
        processes = rows.ToArray();
        return true;
    }

    internal static bool TryParseCreations(ReadOnlySpan<byte> data, WindowsProcessDetailsOptions options,
        out Dictionary<int, long> creations)
    {
        creations = [];
        var headerSize = ProcessHeaderSize;
        if (data.Length == 0 || data.Length > options.MaximumSystemInformationBufferBytes) return false;
        var offset = 0;
        var count = 0;
        while (offset < data.Length && count++ < options.MaximumProcesses)
        {
            if (offset % IntPtr.Size != 0 || data.Length - offset < headerSize) return false;
            var header = MemoryMarshal.Read<SystemProcessInformationHeader>(data.Slice(offset, headerSize));
            var remaining = data.Length - offset;
            if (header.NextEntryOffset != 0 && (header.NextEntryOffset < headerSize
                || header.NextEntryOffset > remaining || header.NextEntryOffset % (uint)IntPtr.Size != 0)) return false;
            var pid = PointerValue(header.UniqueProcessId);
            if (pid > int.MaxValue || !creations.TryAdd((int)pid, header.CreationTime)) return false;
            if (header.NextEntryOffset == 0) return true;
            offset += (int)header.NextEntryOffset;
        }
        return false;
    }
    internal static int ProcessHeaderSize => Marshal.SizeOf<SystemProcessInformationHeader>();
    internal static int ThreadInformationSize => Marshal.SizeOf<SystemThreadInformation>();
    internal static int ProcessIdOffset => Marshal.OffsetOf<SystemProcessInformationHeader>(nameof(SystemProcessInformationHeader.UniqueProcessId)).ToInt32();
    internal static int ThreadProcessIdOffset => Marshal.OffsetOf<SystemThreadInformation>(nameof(SystemThreadInformation.ClientProcessId)).ToInt32();
    internal static int ThreadIdOffset => Marshal.OffsetOf<SystemThreadInformation>(nameof(SystemThreadInformation.ClientThreadId)).ToInt32();
    internal static int ThreadStateOffset => Marshal.OffsetOf<SystemThreadInformation>(nameof(SystemThreadInformation.ThreadState)).ToInt32();
    internal static int ThreadWaitReasonOffset => Marshal.OffsetOf<SystemThreadInformation>(nameof(SystemThreadInformation.WaitReason)).ToInt32();

    internal static bool TryParse(byte[] data, IReadOnlySet<int> selectedPids, WindowsProcessDetailsOptions options,
        out Dictionary<int, IReadOnlyList<WindowsProcessThreadState>> processes)
        => TryParseCore(data, selectedPids, options, selectAll: false, out processes);

    // Idle/System are not placement targets and can contain synthetic thread IDs (including zero).
    internal static bool TryParsePlacementInventory(byte[] data, WindowsProcessDetailsOptions options,
        out Dictionary<int, IReadOnlyList<WindowsProcessThreadState>> processes)
        => TryParseCore(data, null, options, selectAll: true, out processes);

    private static bool TryParseCore(byte[] data, IReadOnlySet<int>? selectedPids, WindowsProcessDetailsOptions options,
        bool selectAll, out Dictionary<int, IReadOnlyList<WindowsProcessThreadState>> processes)
    {
        processes = [];
        if (data is null || (!selectAll && selectedPids is null) || options is null || data.Length == 0
            || data.Length > options.MaximumSystemInformationBufferBytes) return false;
        var headerSize = Marshal.SizeOf<SystemProcessInformationHeader>();
        var threadSize = Marshal.SizeOf<SystemThreadInformation>();
        if (headerSize <= 0 || threadSize <= 0) return false;
        try
        {
            var seenPids = new HashSet<uint>();
            var offset = 0;
            var rows = 0;
            while (offset < data.Length && rows++ < options.MaximumProcesses)
            {
                if (offset % IntPtr.Size != 0 || data.Length - offset < headerSize) return false;
                var header = MemoryMarshal.Read<SystemProcessInformationHeader>(data.AsSpan(offset, headerSize));
                var recordLength = header.NextEntryOffset == 0 ? data.Length - offset : checked((int)header.NextEntryOffset);
                if (recordLength < headerSize || recordLength > data.Length - offset) return false;
                if (header.NumberOfThreads > options.MaximumThreadsPerProcess) return false;
                var threadBytes = checked((ulong)header.NumberOfThreads * (ulong)threadSize);
                if (threadBytes > (ulong)(recordLength - headerSize)) return false;

                var pidValue = PointerValue(header.UniqueProcessId);
                if (pidValue <= uint.MaxValue && !seenPids.Add((uint)pidValue)) return false;
                if (pidValue <= int.MaxValue && (selectAll ? pidValue > 4 : selectedPids!.Contains((int)pidValue)))
                {
                    var threads = new List<WindowsProcessThreadState>(checked((int)header.NumberOfThreads));
                    var threadIds = new HashSet<uint>();
                    for (var index = 0; index < header.NumberOfThreads; index++)
                    {
                        var threadOffset = checked(offset + headerSize + (int)((ulong)index * (ulong)threadSize));
                        var thread = MemoryMarshal.Read<SystemThreadInformation>(data.AsSpan(threadOffset, threadSize));
                        if (PointerValue(thread.ClientProcessId) != pidValue) return false;
                        var tidValue = PointerValue(thread.ClientThreadId);
                        if (tidValue == 0 || tidValue > uint.MaxValue || !threadIds.Add((uint)tidValue)) return false;
                        threads.Add(new((uint)tidValue, thread.ThreadState, thread.WaitReason));
                    }
                    processes.Add((int)pidValue, threads.AsReadOnly());
                }

                if (header.NextEntryOffset == 0)
                    return offset + recordLength == data.Length && rows <= options.MaximumProcesses;
                if (header.NextEntryOffset < headerSize || header.NextEntryOffset % (uint)IntPtr.Size != 0) return false;
                offset = checked(offset + recordLength);
            }
            return false;
        }
        catch (Exception error) when (error is ArgumentException or OverflowException or ExternalException)
        {
            processes = [];
            return false;
        }
    }

    private static ulong PointerValue(IntPtr value) => unchecked((ulong)value.ToInt64());

    [StructLayout(LayoutKind.Sequential)]
    internal struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemProcessInformationHeader
    {
        internal uint NextEntryOffset;
        internal uint NumberOfThreads;
        // Inline reserved bytes allow reading directly from the bounded native
        // byte table without boxed structs or unused managed reserved arrays.
        internal long Reserved1a, Reserved1b, Reserved1c, CreationTime, Reserved1e, Reserved1f;
        internal UnicodeString ImageName;
        internal int BasePriority;
        internal IntPtr UniqueProcessId;
        internal IntPtr InheritedFromUniqueProcessId;
        internal uint HandleCount;
        internal uint SessionId;
        internal IntPtr Reserved3;
        internal UIntPtr PeakVirtualSize;
        internal UIntPtr VirtualSize;
        internal uint Reserved4;
        internal UIntPtr PeakWorkingSetSize;
        internal UIntPtr WorkingSetSize;
        internal IntPtr Reserved5;
        internal UIntPtr QuotaPagedPoolUsage;
        internal IntPtr Reserved6;
        internal UIntPtr QuotaNonPagedPoolUsage;
        internal UIntPtr PagefileUsage;
        internal UIntPtr PeakPagefileUsage;
        internal UIntPtr PrivatePageCount;
        internal long Reserved7a, Reserved7b, Reserved7c, Reserved7d, Reserved7e, Reserved7f;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemThreadInformation
    {
        internal long Reserved1a;
        internal long Reserved1b;
        internal long Reserved1c;
        internal uint Reserved2;
        internal IntPtr StartAddress;
        internal IntPtr ClientProcessId;
        internal IntPtr ClientThreadId;
        internal int Priority;
        internal int BasePriority;
        internal uint Reserved3;
        internal uint ThreadState;
        internal uint WaitReason;
    }
}

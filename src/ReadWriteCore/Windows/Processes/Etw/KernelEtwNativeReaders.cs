using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Infrastructure.CpuTopology;
using ResourceManager.ReadWrite.Windows.Processes;

namespace ResourceManager.App.Infrastructure.Telemetry.Etw;

/// <summary>Cold commands and leased native publications. No event delivery enters managed code.</summary>
internal static unsafe partial class KernelEtwNativeReaders
{
    private const string Library = "ResourceManager.NativeCore";
    internal const uint Residency = 1;

    internal static void ConfigureProcessors(NativeKernelEtwSession session, int[] ids)
    {
        using var owner = session.AcquireReadLease();
        fixed (int* pointer = ids) Check(ConfigureNative(owner.OwnerHandle, pointer, checked((uint)ids.Length)), "configure processor map");
    }

    internal static void SetEnabled(NativeKernelEtwSession session, uint mask, bool enabled, bool refresh = true)
    {
        // Only the finite CPU observation can pause within its held run.
        // IRQ and IO intake follow output holds and have no mode transitions.
        if (mask != Residency) throw new ArgumentOutOfRangeException(nameof(mask));
        using var owner = session.AcquireReadLease();
        Check(SetEnabledNative(owner.OwnerHandle, mask, enabled ? mask : 0), "set capture enabled");
        if (refresh) session.RequestRefresh(Environment.TickCount64);
    }

    // Window begin: the floor QPC and the slots per window (0 = finite window, 1..2047 = continuous).
    internal static ulong ResetContinuity(NativeKernelEtwSession session, long requestedQpc, int slotsPerWindow)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slotsPerWindow);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slotsPerWindow, MaximumSlotsPerWindow);
        using var owner = session.AcquireReadLease();
        var input = new Input { Kind = 9, Qpc = requestedQpc, Unique = (ulong)slotsPerWindow };
        Check(SubmitTrackedNative(owner.OwnerHandle, &input, 1, out var ticket), "submit continuity barrier");
        return ticket;
    }
    internal static (ulong Ticket, bool Pending) RequestResidency(NativeKernelEtwSession session)
    {
        using var owner = session.AcquireReadLease();
        CheckFailure(owner.OwnerHandle);
        Check(RequestResidencyNative(owner.OwnerHandle, out var ticket, out var pending), "request residency sample");
        CheckFailure(owner.OwnerHandle);
        return (ticket, pending != 0);
    }

    // This only checks a leased immutable CPU component. It neither requests
    // work nor projects rows; callers release this lease before waiting.
    internal static bool IsResidencyApplied(NativeKernelEtwSession session, ulong requiredTicket)
    {
        using var owner = session.AcquireReadLease();
        CheckFailure(owner.OwnerHandle);
        var status = AcquireResidency(owner.OwnerHandle, out _, out _, out _, out var appliedTicket, out var token);
        if (status == 3) { CheckFailure(owner.OwnerHandle); return false; }
        Check(status, "check residency sample acknowledgment");
        try
        {
            CheckFailure(owner.OwnerHandle);
            var applied = appliedTicket >= requiredTicket;
            CheckFailure(owner.OwnerHandle);
            return applied;
        }
        finally { Check(ReleaseNative(owner.OwnerHandle, token), "release residency acknowledgment"); }
    }
    internal static ulong ResetInterruptBinding(NativeKernelEtwSession session, long tick)
    {
        using var owner = session.AcquireReadLease();
        var input = new Input { Kind = 15, Qpc = tick };
        Check(SubmitTrackedNative(owner.OwnerHandle, &input, 1, out var ticket), "submit interrupt binding reset");
        return ticket;
    }
    internal static void ConfigureInterruptProcessors(NativeKernelEtwSession session, int processorCount)
        => Submit(session, new Input { Kind = 21, ProcessId = processorCount });
    internal const int MaximumSlotsPerWindow = 2047;
    internal static void ConfigureResidency(NativeKernelEtwSession session, int windowMilliseconds, int maximumPairs)
        => Submit(session, new Input { Kind = 16, Qpc = windowMilliseconds, TimeTicks = 0, Unique = checked((ulong)maximumPairs) });

    // Commands per seed submit: 80 B each, so one chunk is 5 KiB of stack.
    private const int SeedChunkInputs = 64;
    private const int SeedNameStackBytes = 512;

    /// <summary>One process and its threads: kind 11, then kind 12 per thread, in one submit per chunk of 64 commands.</summary>
    internal static void SeedProcess(NativeKernelEtwSession session, int pid, long birth,
        ReadOnlySpan<char> name, ReadOnlySpan<int> threadIds, int eventsLost)
    {
        var byteCount = Encoding.UTF8.GetByteCount(name);
        byte[]? rented = byteCount > SeedNameStackBytes ? System.Buffers.ArrayPool<byte>.Shared.Rent(byteCount) : null;
        try
        {
            Span<byte> bytes = rented is null ? stackalloc byte[SeedNameStackBytes] : rented;
            var written = Encoding.UTF8.GetBytes(name, bytes);
            var inputs = stackalloc Input[SeedChunkInputs];
            using var owner = session.AcquireReadLease();
            fixed (byte* text = bytes)
            {
                inputs[0] = new Input { Kind = 11, Flags = 1, ProcessId = pid, Start = birth, Qpc = eventsLost, Name = (nint)text, NameLength = checked((uint)written) };
                var count = 1;
                foreach (var tid in threadIds)
                {
                    if (count == SeedChunkInputs)
                    {
                        Check(SubmitNative(owner.OwnerHandle, inputs, (uint)count), "submit process seed");
                        count = 0;
                    }
                    inputs[count++] = new Input { Kind = 12, ProcessId = pid, Start = birth, ThreadId = tid, Qpc = eventsLost };
                }
                Check(SubmitNative(owner.OwnerHandle, inputs, (uint)count), "submit process seed");
            }
        }
        finally { if (rented is not null) System.Buffers.ArrayPool<byte>.Shared.Return(rented); }
    }

    private static void Submit(NativeKernelEtwSession session, Input input)
    {
        using var owner = session.AcquireReadLease();
        Check(SubmitNative(owner.OwnerHandle, &input, 1), "submit reader command");
    }

    internal static void RequestRefresh(NativeKernelEtwSession session, long tick)
    {
        session.RequestRefresh(tick);
    }

    internal static void RegisterDiagnostics(NativeKernelEtwSession session, ulong id, int pid, long birth, DateTimeOffset requested)
    {
        Submit(session, new Input { Kind = 13, Unique = id, ProcessId = pid, Start = birth, TimeTicks = requested.UtcDateTime.ToFileTimeUtc() });
        session.RequestRefresh(Environment.TickCount64);
    }
    internal static void UnregisterDiagnostics(NativeKernelEtwSession session, ulong id)
    {
        Submit(session, new Input { Kind = 14, Unique = id });
        session.RequestRefresh(Environment.TickCount64);
    }
    internal static ulong SampleDiagnostics(NativeKernelEtwSession session, ulong id, DateTimeOffset from, DateTimeOffset through)
    {
        using var owner = session.AcquireReadLease();
        var input = new Input { Kind = 19, Unique = id, TimeTicks = from.UtcDateTime.ToFileTimeUtc(), Qpc = through.UtcDateTime.ToFileTimeUtc() };
        Check(SubmitTrackedNative(owner.OwnerHandle, &input, 1, out var ticket), "submit diagnostic sample");
        return ticket;
    }
    internal static bool ReadDiagnosticsAt<T>(NativeKernelEtwSession session, ulong id, ulong requiredTicket,
        DateTimeOffset from, DateTimeOffset through, Func<DiagnosticRequestView, T> copy, out T? result, out bool acknowledged)
    {
        using var owner = session.AcquireReadLease();
        CheckFailure(owner.OwnerHandle);
        var status = AcquireDiagnosticsAt(owner.OwnerHandle, id, requiredTicket, from.UtcDateTime.ToFileTimeUtc(), through.UtcDateTime.ToFileTimeUtc(),
            out var view, out _, out var appliedTicket, out var token);
        result = default;
        acknowledged = appliedTicket >= requiredTicket;
        if (status == 3) { CheckFailure(owner.OwnerHandle); return false; }
        Check(status, "acquire thread diagnostic publication");
        try
        {
            CheckFailure(owner.OwnerHandle);
            result = copy(view); CheckFailure(owner.OwnerHandle); return true;
        }
        finally { Check(ReleaseNative(owner.OwnerHandle, token), "release thread diagnostic publication"); }
    }

    // The domain model is managed. Copy its final rows once per native sequence;
    // the capture owner caches the resulting domain snapshot for all subscribers.
    internal static CpuResidencyAggregationSnapshot? ReadResidency(NativeKernelEtwSession session, ref ulong sequence, int expectedLoss, ulong requiredTicket,
        CpuResidencyAggregationSnapshot? current, Func<ResidencyView, CpuResidencyAggregationSnapshot> project)
    {
        using var owner = session.AcquireReadLease();
        CheckFailure(owner.OwnerHandle);
        var status = AcquireResidency(owner.OwnerHandle, out var view, out var next, out var eventsLost, out var appliedTicket, out var token);
        if (status == 3) { CheckFailure(owner.OwnerHandle); return null; }
        Check(status, "acquire residency publication");
        try
        {
            CheckFailure(owner.OwnerHandle);
            if (eventsLost != checked((ulong)expectedLoss) || appliedTicket < requiredTicket) { CheckFailure(owner.OwnerHandle); return null; }
            if (next == sequence) { CheckFailure(owner.OwnerHandle); return current; }
            if (view.Count != 0 && view.Rows == 0) throw new InvalidDataException("Native residency rows are absent.");
            var result = project(view);
            CheckFailure(owner.OwnerHandle);
            sequence = next;
            return result;
        }
        finally { Check(ReleaseNative(owner.OwnerHandle, token), "release residency publication"); }
    }

    // This list is used only inside the leased projection callback. It avoids
    // an intermediate managed row array before producing the final domain rows.
    internal sealed class BorrowedResidencyRecords(nint rows, uint count) : IReadOnlyList<CpuExecutionTimeAggregate>
    {
        private readonly Dictionary<long, string> names = [];
        public int Count { get; } = checked((int)count);
        public CpuExecutionTimeAggregate this[int index]
        {
            get
            {
                if ((uint)index >= count) throw new ArgumentOutOfRangeException(nameof(index));
                var row = ((ResidencyRow*)rows)[index];
                if (!names.TryGetValue(row.ProcessInstance, out var name))
                    names.Add(row.ProcessInstance, name = Utf8(row.Name, row.NameLength));
                return new(row.ProcessInstance, row.ProcessId, (row.Flags & 1) != 0 ? row.Start : null,
                    name, row.ThreadInstance, row.ThreadId, row.Processor, row.Duration, row.Switches);
            }
        }
        public IEnumerator<CpuExecutionTimeAggregate> GetEnumerator()
        { for (var index = 0; index < Count; index++) yield return this[index]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal static bool ReadInterrupts<T>(NativeKernelEtwSession session, ref ulong sequence, Func<InterruptView, T> copy,
        out T? result, out bool hasPublication)
        => ReadInterruptsCore(session, null, ref sequence, copy, out result, out hasPublication);

    internal static bool ReadInterrupts<T>(NativeKernelEtwSession session, ulong requiredTicket, ref ulong sequence,
        Func<InterruptView, T> copy, out T? result, out bool hasPublication)
        => ReadInterruptsCore(session, requiredTicket, ref sequence, copy, out result, out hasPublication);

    private static bool ReadInterruptsCore<T>(NativeKernelEtwSession session, ulong? requiredTicket, ref ulong sequence,
        Func<InterruptView, T> copy, out T? result, out bool hasPublication)
    {
        using var owner = session.AcquireReadLease();
        CheckFailure(owner.OwnerHandle);
        InterruptView view;
        ulong next, token;
        var status = requiredTicket is { } ticket
            ? AcquireInterruptsAt(owner.OwnerHandle, ticket, out view, out next, out token)
            : AcquireInterrupts(owner.OwnerHandle, out view, out next, out token);
        result = default;
        hasPublication = status != 3;
        if (status == 3) { CheckFailure(owner.OwnerHandle); return false; }
        Check(status, "acquire interrupt publication");
        try
        {
            CheckFailure(owner.OwnerHandle);
            if (next == sequence) { CheckFailure(owner.OwnerHandle); return false; }
            result = copy(view);
            CheckFailure(owner.OwnerHandle);
            sequence = next;
            return true;
        }
        finally { Check(ReleaseNative(owner.OwnerHandle, token), "release interrupt publication"); }
    }

    internal static string Utf8(nint pointer, uint length)
        => length == 0 ? string.Empty : pointer != 0 ? Marshal.PtrToStringUTF8(pointer, checked((int)length))!
            : throw new InvalidDataException("Native UTF-8 publication is absent.");
    internal static string? Utf16(nint pointer, uint length)
        => length == 0 ? null : pointer != 0 ? Marshal.PtrToStringUni(pointer, checked((int)length))
            : throw new InvalidDataException("Native UTF-16 publication is absent.");
    internal static CpuThreadDiagnosticFacts CopyDiagnosticFacts(DiagnosticThreadView row)
        => new((row.Flags & 1) != 0 ? row.Birth : null, Utf16(row.Description, row.DescriptionLength),
            (row.Flags & 2) != 0 ? row.DescriptionHResult : null, (row.Flags & 4) != 0 ? row.Start : null,
            row.StartSource switch { 0 => null, 1 => CpuThreadStartAddressSource.Win32StartAddress,
                2 => CpuThreadStartAddressSource.SystemProcessInformation, _ => throw new InvalidDataException("Unknown native thread start-address source.") },
            Utf16(row.ModulePath, row.ModulePathLength),
            (row.Flags & 8) != 0 ? new(DateTimeOffset.FromFileTime(row.ReadFrom), DateTimeOffset.FromFileTime(row.ReadThrough), row.KernelDelta, row.UserDelta) : null,
            (row.Flags & 16) != 0 ? row.NativeError : null);
    internal static void Check(int result, string operation)
    {
        if (result != 0) throw new InvalidOperationException($"Native kernel ETW {operation} failed ({result}).");
    }
    internal sealed class CaptureFailure(int result) : InvalidOperationException($"Native kernel ETW capture failed ({result}).")
    { internal int Result { get; } = result; }
    private static void CheckFailure(nint handle)
    {
        var failure = QueryFailureNative(handle);
        if (failure != 0) throw new CaptureFailure(failure);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Kind, Flags;
        public long Qpc, TimeTicks;
        public int ProcessId, ThreadId, OldProcessId, OldThreadId, Processor;
        public uint Reserved;
        public ulong Unique;
        public long Start;
        public nint Name;
        public uint NameLength, Reserved2;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ResidencyView
    {
        public long Frequency, StartQpc, ThroughQpc, TimeTicks;
        public uint Flags, Count;
        public nint Rows;
        public ulong CurrentBytes, PeakBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ResidencyRow
    {
        public long ProcessInstance;
        public int ProcessId;
        public uint Flags;
        public long Start, ThreadInstance;
        public int ThreadId, Processor;
        public long Duration;
        public int Switches;
        public uint NameLength;
        public nint Name;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ModuleIdentity
    {
        public nint Path, Name;
        public uint PathLength, NameLength;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct InterruptDriver
    {
        public ModuleIdentity Module;
        public double TotalDuration, MaximumDuration, CapacityPercent;
        public ulong EventCount, LongCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct InterruptBucket
    {
        public long StartedTick, EndedTick;
        public double TotalDuration, CapacityPercent;
        public ulong EventCount;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct InterruptView
    {
        public ulong Generation, EventsLost;
        public long StartedTick, EndedTick, WindowMilliseconds;
        public uint ProcessorCount, Flags;
        public double TotalDuration, MaximumDuration, CapacityPercent;
        public ulong EventCount, LongCount;
        public uint MaximumKind, Reserved;
        public ModuleIdentity MaximumModule;
        public uint DriverCount, BucketCount;
        public nint Drivers, Buckets;
        public ulong CurrentBytes, PeakBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DiagnosticThreadView
    {
        public long ThreadInstance;
        public uint ThreadId, Flags;
        public long Birth;
        public ulong Start;
        public long ReadFrom, ReadThrough, KernelDelta, UserDelta;
        public int DescriptionHResult, NativeError;
        public uint StartSource, Reserved;
        public nint Description, ModulePath;
        public uint DescriptionLength, ModulePathLength;
        public long ExitFileTime;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct DiagnosticRequestView
    {
        public ulong RequestId;
        public long ProcessBirth, RequestedAt;
        public uint ProcessId, Flags;
        public int NativeError;
        public uint ThreadCount;
        public nint Threads;
        public long SampledFrom, SampledThrough;
    }

    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_configure_processors", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ConfigureNative(nint owner, int* ids, uint count);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_set_enabled", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetEnabledNative(nint owner, uint mask, uint enabled);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_submit", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SubmitNative(nint owner, Input* inputs, uint count);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_request_refresh", CallingConvention = CallingConvention.Cdecl)]
    private static extern int RequestRefreshNative(nint owner, long tick);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_acquire_residency", CallingConvention = CallingConvention.Cdecl)]
    private static extern int AcquireResidency(nint owner, out ResidencyView view, out ulong sequence, out ulong eventsLost, out ulong appliedTicket, out ulong token);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_request_residency", CallingConvention = CallingConvention.Cdecl)]
    private static extern int RequestResidencyNative(nint owner, out ulong ticket, out uint pending);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_acquire_interrupts", CallingConvention = CallingConvention.Cdecl)]
    private static extern int AcquireInterrupts(nint owner, out InterruptView view, out ulong sequence, out ulong token);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_acquire_interrupts_at", CallingConvention = CallingConvention.Cdecl)]
    private static extern int AcquireInterruptsAt(nint owner, ulong requiredTicket, out InterruptView view, out ulong sequence, out ulong token);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_release", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ReleaseNative(nint owner, ulong token);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_acquire_thread_diagnostics_at", CallingConvention = CallingConvention.Cdecl)]
    private static extern int AcquireDiagnosticsAt(nint owner, ulong id, ulong requiredTicket, long from, long through,
        out DiagnosticRequestView view, out ulong sequence, out ulong appliedTicket, out ulong token);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_submit_tracked", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SubmitTrackedNative(nint owner, Input* inputs, uint count, out ulong ticket);
    [DllImport(Library, EntryPoint = "rm_kernel_etw_shared_query_failure", CallingConvention = CallingConvention.Cdecl)]
    private static extern int QueryFailureNative(nint owner);
}

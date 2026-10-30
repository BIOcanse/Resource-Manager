using System.Runtime.InteropServices;

namespace ResourceManager.ReadWrite.Windows.Processes;

internal interface IWindowsProcessInventoryBufferApi
{
    int Query(IntPtr buffer, uint length, out uint returned);
    IntPtr Resize(IntPtr buffer, int length);
    void Free(IntPtr buffer);
}

/// <summary>When a process-inventory workspace is returned: <see cref="ReleaseAfterUse"/> frees it after each
/// parse; otherwise it shrinks after <see cref="ShrinkAfterLowReads"/> consecutive low-use reads (each using at
/// most a quarter), and zero there never shrinks and reserves the previous peak after a release.</summary>
internal readonly record struct WindowsProcessInventoryBufferReturn(bool ReleaseAfterUse, int ShrinkAfterLowReads)
{
    /// <summary>The workspace's own default: shrink after 8 low-use reads (memory/CPU tier 3).</summary>
    internal static WindowsProcessInventoryBufferReturn Default => new(false, 8);
    internal bool KeepPeak => !ReleaseAfterUse && ShrinkAfterLowReads <= 0;
}

/// <summary>One capture owner's kernel-table workspace. Borrowed spans last until
/// the next read/release; the owner's capture gate covers both query and parsing.</summary>
internal sealed class WindowsProcessInventoryBuffer : IDisposable
{
    private readonly WindowsProcessDetailsOptions options;
    private readonly IWindowsProcessInventoryBufferApi api;
    private readonly TimeProvider clock;
    private IntPtr buffer;
    private bool disposed;
    private int lowUsageReads;
    private int peakBytes;
    private int lowUsageMaximumBytes;
    internal int CapacityBytes { get; private set; }

    /// <summary>The return policy, read at every borrow and every <see cref="ReturnAfterUse"/>; a change
    /// applies from the next read. The owner sets it from the memory/CPU tier option bufferReturn.</summary>
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.EventDriven,
        "Memory/CPU tier option bufferReturn, read at the next borrow")]
    internal Func<WindowsProcessInventoryBufferReturn> ReturnPolicy { get; set; } = static () => WindowsProcessInventoryBufferReturn.Default;

    internal WindowsProcessInventoryBuffer(WindowsProcessDetailsOptions? options = null,
        IWindowsProcessInventoryBufferApi? api = null, TimeProvider? clock = null)
    {
        this.options = options ?? WindowsProcessDetailsOptions.Default;
        this.api = api ?? NativeApi.Instance;
        this.clock = clock ?? TimeProvider.System;
    }

    /// <summary><paramref name="queryTiming"/> brackets the one successful query that produced
    /// <paramref name="data"/>; it is null when the clock was not continuous across that call.
    /// It is a value so that the query itself stays free of managed allocation.</summary>
    internal unsafe bool TryRead(out ReadOnlySpan<byte> data,
        out (long QueryStartTicks, long QueryReturnTicks, long TicksPerSecond)? queryTiming)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        data = default;
        queryTiming = null;
        // Resize before querying: the kernel table contains absolute pointers, so
        // moving a successful table would invalidate its embedded string pointers.
        var policy = ReturnPolicy();
        if (policy.ShrinkAfterLowReads > 0 && lowUsageReads >= policy.ShrinkAfterLowReads)
            Resize(Math.Max(options.InitialSystemInformationBufferBytes, checked(lowUsageMaximumBytes * 2)));
        if (CapacityBytes == 0)
            Resize(policy.KeepPeak ? Math.Max(options.InitialSystemInformationBufferBytes, peakBytes)
                : options.InitialSystemInformationBufferBytes);
        for (var attempt = 0; attempt < options.MaximumSystemInformationAttempts; attempt++)
        {
            var frequency = clock.TimestampFrequency;
            var start = clock.GetTimestamp();
            var status = api.Query(buffer, checked((uint)CapacityBytes), out var returned);
            var end = clock.GetTimestamp();
            if (status == 0)
            {
                if (returned == 0 || returned > CapacityBytes) { ResetLowUsage(); return false; }
                if (CapacityBytes > options.InitialSystemInformationBufferBytes && returned <= CapacityBytes / 4)
                {
                    lowUsageReads++;
                    lowUsageMaximumBytes = Math.Max(lowUsageMaximumBytes, checked((int)returned));
                }
                else ResetLowUsage();
                data = new ReadOnlySpan<byte>(buffer.ToPointer(), checked((int)returned));
                // The query return is the per-process IO reference point, as one handle query's return was;
                // the bracket only proves the clock was continuous across the call.
                queryTiming = frequency > 0 && frequency == clock.TimestampFrequency && start >= 0 && end >= start
                    ? (start, end, frequency) : null;
                return true;
            }
            ResetLowUsage();
            if (status is not (unchecked((int)0xC0000004) or unchecked((int)0xC0000023))) return false;
            var next = Math.Max((long)returned, (long)CapacityBytes * 2);
            if (attempt + 1 == options.MaximumSystemInformationAttempts
                || next > options.MaximumSystemInformationBufferBytes || next <= CapacityBytes) return false;
            Resize(checked((int)next));
        }
        return false;
    }

    private void Resize(int bytes)
    {
        if (bytes <= 0 || bytes > options.MaximumSystemInformationBufferBytes)
            throw new ArgumentOutOfRangeException(nameof(bytes));
        var next = api.Resize(buffer, bytes);
        if (next == IntPtr.Zero) throw new OutOfMemoryException("Cannot allocate the process inventory workspace.");
        buffer = next;
        CapacityBytes = bytes;
        peakBytes = Math.Max(peakBytes, bytes);
        ResetLowUsage();
    }

    private void ResetLowUsage() { lowUsageReads = 0; lowUsageMaximumBytes = 0; }

    internal void Release()
    {
        if (buffer != IntPtr.Zero) api.Free(buffer);
        buffer = IntPtr.Zero;
        CapacityBytes = 0;
        ResetLowUsage();
    }

    /// <summary>Called by the owner once a read's data is parsed: frees the workspace when the return
    /// policy says release after use, and otherwise keeps it for the next read.</summary>
    internal void ReturnAfterUse()
    {
        if (ReturnPolicy().ReleaseAfterUse) Release();
    }

    public void Dispose()
    {
        if (disposed) return;
        Release();
        disposed = true;
    }

    private sealed class NativeApi : IWindowsProcessInventoryBufferApi
    {
        internal static readonly NativeApi Instance = new();
        [RuntimeRead(ReadVolatilityKind.Sampled)]
        public int Query(IntPtr buffer, uint length, out uint returned)
            => NtQuerySystemInformation(5, buffer, length, out returned);
        public IntPtr Resize(IntPtr buffer, int length) => Marshal.ReAllocHGlobal(buffer, new IntPtr(length));
        public void Free(IntPtr buffer) => Marshal.FreeHGlobal(buffer);
        [DllImport("ntdll.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int NtQuerySystemInformation(uint informationClass, IntPtr information, uint length, out uint returned);
    }
}

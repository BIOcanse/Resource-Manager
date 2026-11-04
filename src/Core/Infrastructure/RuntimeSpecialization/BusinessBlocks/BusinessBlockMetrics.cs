using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;

namespace ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

/// <summary>
/// Collects CPU time and managed allocations for one business block. Reads return
/// fresh cumulative facts without changing recorded measurements.
/// </summary>
public sealed class BusinessBlockMetrics
{
    private const uint ThreadQueryInformation = 0x0040;

    private readonly object gate = new();
    private readonly Dictionary<uint, RegisteredThread> activeThreads = new();
    private readonly HashSet<uint> activeSegmentThreads = new();

    private long totalCpuTicks;
    private long totalAllocatedBytes;
    private bool observedSynchronousSegments;
    private bool observedDedicatedThreads;


    /// <summary>
    /// Starts a synchronous-only measurement on the calling thread. The returned
    /// scope must be disposed on this same thread and must not cross an await.
    /// Nested scopes and scopes on registered threads are rejected so work is counted once.
    /// </summary>
    public IDisposable MeasureSegment()
    {
        var nativeThreadId = NativeMethods.GetCurrentThreadId();
        SegmentScope scope;
        lock (gate)
        {
            if (activeThreads.ContainsKey(nativeThreadId))
            {
                throw new InvalidOperationException("A synchronous measurement cannot run on a registered business-block thread.");
            }

            if (!activeSegmentThreads.Add(nativeThreadId))
            {
                throw new InvalidOperationException("Synchronous business-block measurement scopes cannot be nested on the same thread.");
            }

            scope = new SegmentScope(this, nativeThreadId);
        }

        try
        {
            scope.Start();
            lock (gate)
            {
                observedSynchronousSegments = true;
            }

            return scope;
        }
        catch
        {
            lock (gate)
            {
                activeSegmentThreads.Remove(nativeThreadId);
            }

            throw;
        }
    }

    /// <summary>
    /// Registers the calling thread for its active session. Dispose the returned
    /// scope on the same thread when that session ends.
    /// </summary>
    public IDisposable RegisterCurrentThread(string sessionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionName);

        var nativeThreadId = NativeMethods.GetCurrentThreadId();
        var handle = NativeThreadHandle.Open(nativeThreadId);
        try
        {
            var state = new RegisteredThread(
                nativeThreadId,
                Thread.CurrentThread.Name,
                sessionName,
                DateTimeOffset.UtcNow,
                handle,
                0,
                0);
            var registration = new ThreadRegistration(this, state);

            lock (gate)
            {
                if (activeSegmentThreads.Contains(nativeThreadId))
                {
                    throw new InvalidOperationException("A registered business-block thread cannot start inside a synchronous measurement scope.");
                }

                if (activeThreads.ContainsKey(nativeThreadId))
                {
                    throw new InvalidOperationException("The current thread is already registered with these business-block metrics.");
                }

                activeThreads.Add(nativeThreadId, state);
                try
                {
                    state.LastCpuTicks = NativeMethods.GetThreadCpuTicks(handle);
                    state.InitialAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
                    observedDedicatedThreads = true;
                }
                catch
                {
                    activeThreads.Remove(nativeThreadId);
                    throw;
                }
            }

            return registration;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Read live cumulative counters without sampling or scheduling work.</summary>
    public BusinessBlockMetricFacts Read()
    {
        lock (gate)
        {
            var activeFacts = activeThreads.Count == 0
                ? Array.Empty<BusinessBlockThreadMetricFacts>()
                : new BusinessBlockThreadMetricFacts[activeThreads.Count];
            var liveCpuTicks = 0L;
            var index = 0;
            foreach (var state in activeThreads.Values)
            {
                var cpuTicks = NativeMethods.GetThreadCpuTicks(state.Handle);
                var delta = GetDelta(cpuTicks, state.LastCpuTicks, "A registered thread CPU counter moved backwards.");
                liveCpuTicks = checked(liveCpuTicks + delta);
                activeFacts[index++] = new BusinessBlockThreadMetricFacts(
                    state.NativeThreadId,
                    state.Name,
                    state.SessionName,
                    state.RegisteredAt,
                    TimeSpan.FromTicks(checked(state.CpuTicks + delta)));
            }

            return CreateFacts(
                checked(totalCpuTicks + liveCpuTicks),
                totalAllocatedBytes,
                activeThreads.Count == 0 && activeSegmentThreads.Count == 0,
                activeFacts,
                GetCoverage());
        }
    }

    /// <summary>Clears counters when no thread or synchronous segment is active.</summary>
    public void Reset()
    {
        lock (gate)
        {
            if (activeThreads.Count != 0 || activeSegmentThreads.Count != 0)
            {
                throw new InvalidOperationException("Business-block metrics can only be reset when no thread or measurement segment is active.");
            }

            totalCpuTicks = 0;
            totalAllocatedBytes = 0;
            observedSynchronousSegments = false;
            observedDedicatedThreads = false;
        }
    }

    private void FinishSegment(uint nativeThreadId, long initialCpuTicks, long initialAllocatedBytes)
    {
        var finalCpuTicks = NativeMethods.GetCurrentThreadCpuTicks();
        var finalAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        var cpuDelta = GetDelta(finalCpuTicks, initialCpuTicks, "The current thread CPU counter moved backwards.");
        var allocatedDelta = GetDelta(finalAllocatedBytes, initialAllocatedBytes, "The current thread allocation counter moved backwards.");

        lock (gate)
        {
            if (!activeSegmentThreads.Remove(nativeThreadId))
            {
                throw new InvalidOperationException("The synchronous measurement scope is no longer active.");
            }

            totalCpuTicks = checked(totalCpuTicks + cpuDelta);
            totalAllocatedBytes = checked(totalAllocatedBytes + allocatedDelta);
        }
    }

    private void FinishThread(RegisteredThread state)
    {
        lock (gate)
        {
            if (!activeThreads.TryGetValue(state.NativeThreadId, out var current) || !ReferenceEquals(current, state))
            {
                throw new InvalidOperationException("The registered business-block thread is no longer active.");
            }

            try
            {
                var finalCpuTicks = NativeMethods.GetThreadCpuTicks(state.Handle);
                var finalAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
                var cpuDelta = GetDelta(finalCpuTicks, state.LastCpuTicks, "A registered thread CPU counter moved backwards.");
                var allocatedDelta = GetDelta(finalAllocatedBytes, state.InitialAllocatedBytes, "A registered thread allocation counter moved backwards.");

                totalCpuTicks = checked(totalCpuTicks + cpuDelta);
                totalAllocatedBytes = checked(totalAllocatedBytes + allocatedDelta);
            }
            finally
            {
                activeThreads.Remove(state.NativeThreadId);
                state.Handle.Dispose();
            }
        }
    }


    private BusinessBlockAttributionCoverage GetCoverage()
        => (observedSynchronousSegments, observedDedicatedThreads) switch
        {
            (false, false) => BusinessBlockAttributionCoverage.None,
            (true, false) => BusinessBlockAttributionCoverage.SynchronousSegments,
            (false, true) => BusinessBlockAttributionCoverage.DedicatedThreads,
            (true, true) => BusinessBlockAttributionCoverage.DedicatedThreadsAndSynchronousSegments
        };

    private static long GetDelta(long current, long initial, string error)
    {
        if (current < initial)
        {
            throw new InvalidOperationException(error);
        }

        return current - initial;
    }

    private static BusinessBlockMetricFacts CreateFacts(
        long totalCpuTicks,
        long totalAllocatedBytes,
        bool allocationCoverageComplete,
        BusinessBlockThreadMetricFacts[] activeThreads,
        BusinessBlockAttributionCoverage coverage)
        => new(
            TimeSpan.FromTicks(totalCpuTicks),
            totalAllocatedBytes,
            allocationCoverageComplete,
            activeThreads,
            coverage);

    private sealed class RegisteredThread(
        uint nativeThreadId,
        string? name,
        string sessionName,
        DateTimeOffset registeredAt,
        NativeThreadHandle handle,
        long initialCpuTicks,
        long initialAllocatedBytes)
    {
        public uint NativeThreadId { get; } = nativeThreadId;
        public string? Name { get; } = name;
        public string SessionName { get; } = sessionName;
        public DateTimeOffset RegisteredAt { get; } = registeredAt;
        public NativeThreadHandle Handle { get; } = handle;
        public long LastCpuTicks { get; set; } = initialCpuTicks;
        public long CpuTicks { get; set; }
        public long InitialAllocatedBytes { get; set; } = initialAllocatedBytes;
    }

    private sealed class SegmentScope(BusinessBlockMetrics owner, uint nativeThreadId) : IDisposable
    {
        private long initialCpuTicks;
        private long initialAllocatedBytes;
        private int disposed;

        public void Start()
        {
            initialCpuTicks = NativeMethods.GetCurrentThreadCpuTicks();
            initialAllocatedBytes = GC.GetAllocatedBytesForCurrentThread();
        }

        public void Dispose()
        {
            if (NativeMethods.GetCurrentThreadId() != nativeThreadId)
            {
                throw new InvalidOperationException("A synchronous measurement scope must be disposed on the thread where it started.");
            }

            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            owner.FinishSegment(nativeThreadId, initialCpuTicks, initialAllocatedBytes);
            Interlocked.Exchange(ref disposed, 1);
        }
    }

    private sealed class ThreadRegistration(BusinessBlockMetrics owner, RegisteredThread state) : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (NativeMethods.GetCurrentThreadId() != state.NativeThreadId)
            {
                throw new InvalidOperationException("A business-block thread registration must be disposed on the registered thread.");
            }

            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            owner.FinishThread(state);
            Interlocked.Exchange(ref disposed, 1);
        }
    }

    private sealed class NativeThreadHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private NativeThreadHandle(IntPtr handle) : base(true)
        {
            SetHandle(handle);
        }

        public static NativeThreadHandle Open(uint nativeThreadId)
        {
            var handle = NativeMethods.OpenThread(ThreadQueryInformation, false, nativeThreadId);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return new NativeThreadHandle(handle);
        }

        protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetThreadTimes(IntPtr threadHandle, out NativeFileTime creationTime, out NativeFileTime exitTime, out NativeFileTime kernelTime, out NativeFileTime userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetThreadTimes(NativeThreadHandle threadHandle, out NativeFileTime creationTime, out NativeFileTime exitTime, out NativeFileTime kernelTime, out NativeFileTime userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint GetCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        public static long GetCurrentThreadCpuTicks()
        {
            if (!GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return GetCombinedCpuTicks(kernel, user);
        }

        public static long GetThreadCpuTicks(NativeThreadHandle threadHandle)
        {
            if (!GetThreadTimes(threadHandle, out _, out _, out var kernel, out var user))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return GetCombinedCpuTicks(kernel, user);
        }

        private static long GetCombinedCpuTicks(NativeFileTime kernel, NativeFileTime user)
        {
            var kernelTicks = ToInt64(kernel);
            var userTicks = ToInt64(user);
            return checked(kernelTicks + userTicks);
        }

        private static long ToInt64(NativeFileTime value)
            => checked((long)(((ulong)value.HighDateTime << 32) | value.LowDateTime));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }
}

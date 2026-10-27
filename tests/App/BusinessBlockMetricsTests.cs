using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ResourceManager.App.Application.RuntimeSpecialization.BusinessBlocks;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.BusinessBlocks;

namespace Resource_Manager_APP.Tests;

public sealed class BusinessBlockMetricsTests
{
    [Fact]
    public void UninstrumentedBlockReportsNoAttributionCoverage()
    {
        var metrics = new BusinessBlockMetrics();

        Assert.Equal(BusinessBlockAttributionCoverage.None, metrics.Read().Coverage);
        Assert.Equal(BusinessBlockAttributionCoverage.None, metrics.Read().Coverage);
    }

    [Fact]
    public void RegisteredThreadCpuTracksIndependentThreadTimesAndLeavesNoHistoryAfterExit()
    {
        var metrics = new BusinessBlockMetrics();
        metrics.Reset();

        using var started = new ManualResetEventSlim();
        using var stop = new ManualResetEventSlim();
        Exception? workerError = null;
        long nativeThreadId = 0;
        long cpuBeforeRegistration = 0;
        long cpuAfterRegistration = 0;
        long cpuBeforeUnregistration = 0;
        long cpuAfterUnregistration = 0;

        var worker = new Thread(() =>
        {
            try
            {
                cpuBeforeRegistration = NativeThreadTimes.GetCurrentThreadCpuTicks();
                using (metrics.RegisterCurrentThread("busy-session"))
                {
                    nativeThreadId = NativeThreadTimes.GetCurrentThreadId();
                    cpuAfterRegistration = NativeThreadTimes.GetCurrentThreadCpuTicks();
                    started.Set();

                    while (!stop.IsSet)
                    {
                        Thread.SpinWait(20_000);
                    }
                    cpuBeforeUnregistration = NativeThreadTimes.GetCurrentThreadCpuTicks();
                }

                cpuAfterUnregistration = NativeThreadTimes.GetCurrentThreadCpuTicks();
            }
            catch (Exception exception)
            {
                workerError = exception;
                started.Set();
            }
        })
        {
            Name = "BusinessBlockMetrics.Busy"
        };

        worker.Start();
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        Assert.Null(workerError);

        var threadHandle = IntPtr.Zero;
        try
        {
            threadHandle = NativeThreadTimes.Open(checked((uint)nativeThreadId));
            NativeThreadTimes.WaitForCpuTicksAfter(threadHandle, cpuAfterRegistration);
            var independentBefore = NativeThreadTimes.GetCpuTicks(threadHandle) - cpuAfterRegistration;
            var sample = metrics.Read();
            var activeThread = Assert.Single(sample.ActiveThreads);
            var independentAfter = NativeThreadTimes.GetCpuTicks(threadHandle) - cpuBeforeRegistration;

            Assert.Equal(checked((uint)nativeThreadId), activeThread.NativeThreadId);
            Assert.Equal("BusinessBlockMetrics.Busy", activeThread.Name);
            Assert.Equal("busy-session", activeThread.SessionName);
            Assert.True(activeThread.CpuTime > TimeSpan.Zero);
            Assert.InRange(activeThread.CpuTime.Ticks, independentBefore, independentAfter);
            Assert.False(sample.AllocationCoverageComplete);
            Assert.Equal(BusinessBlockAttributionCoverage.DedicatedThreads, sample.Coverage);

            stop.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(workerError);

            var completedRead = metrics.Read();
            Assert.Empty(completedRead.ActiveThreads);
            Assert.True(completedRead.AllocationCoverageComplete);
            Assert.True(completedRead.TotalCpuTime > TimeSpan.Zero);
            Assert.Equal(BusinessBlockAttributionCoverage.DedicatedThreads, completedRead.Coverage);
            var final = metrics.Read();
            Assert.Empty(final.ActiveThreads);
            Assert.True(final.AllocationCoverageComplete);
            Assert.True(final.TotalCpuTime > TimeSpan.Zero);
            Assert.Equal(BusinessBlockAttributionCoverage.DedicatedThreads, final.Coverage);

            // Bracket the measurement's registration and release reads. Subtracting
            // only an after-registration baseline can be one CPU quantum too small.
            Assert.InRange(
                final.TotalCpuTime.Ticks,
                cpuBeforeUnregistration - cpuAfterRegistration,
                cpuAfterUnregistration - cpuBeforeRegistration);
        }
        finally
        {
            stop.Set();
            if (worker.IsAlive)
            {
                worker.Join(TimeSpan.FromSeconds(5));
            }

            if (threadHandle != IntPtr.Zero)
            {
                NativeThreadTimes.Close(threadHandle);
            }
        }
    }

    [Fact]
    public void SynchronousSegmentCountsAllocationsButNotSleepAsCpu()
    {
        var metrics = new BusinessBlockMetrics();
        metrics.Reset();

        using (metrics.MeasureSegment())
        {
            var allocation = new byte[5 * 1024 * 1024];
            GC.KeepAlive(allocation);
            Thread.Sleep(250);

            var active = metrics.Read();
            Assert.False(active.AllocationCoverageComplete);
            Assert.Equal(0, active.TotalAllocatedBytes);
        }

        var facts = metrics.Read();

        Assert.True(facts.TotalAllocatedBytes >= 5 * 1024 * 1024);
        Assert.True(facts.TotalCpuTime < TimeSpan.FromMilliseconds(150));
        Assert.True(facts.AllocationCoverageComplete);
        Assert.Equal(BusinessBlockAttributionCoverage.SynchronousSegments, facts.Coverage);
    }

    [Fact]
    public void ReadReturnsFreshCumulativeFactsWithoutChangingMeasurements()
    {
        var metrics = new BusinessBlockMetrics();
        metrics.Reset();

        using (metrics.MeasureSegment())
        {
            var allocation = new byte[32 * 1024];
            GC.KeepAlive(allocation);
            Thread.SpinWait(20_000);
        }

        var beforeFirstSample = metrics.Read();
        Assert.True(beforeFirstSample.TotalAllocatedBytes >= 32 * 1024);
        var firstRead = metrics.Read();
        var repeatedRead = metrics.Read();
        Assert.Equal(firstRead.TotalCpuTime, repeatedRead.TotalCpuTime);
        Assert.Equal(firstRead.TotalAllocatedBytes, repeatedRead.TotalAllocatedBytes);
        Assert.Equal(BusinessBlockAttributionCoverage.SynchronousSegments, firstRead.Coverage);

        using (metrics.MeasureSegment())
        {
            var allocation = new byte[64 * 1024];
            GC.KeepAlive(allocation);
            Thread.SpinWait(20_000);
        }

        var beforeNextSample = metrics.Read();
        Assert.True(beforeNextSample.TotalAllocatedBytes >= firstRead.TotalAllocatedBytes + 64 * 1024);
        Assert.True(beforeNextSample.TotalCpuTime >= firstRead.TotalCpuTime);

        using var registered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Exception? workerError = null;
        var worker = new Thread(() =>
        {
            try
            {
                using (metrics.RegisterCurrentThread("release-check"))
                {
                    var allocation = new byte[2 * 1024 * 1024];
                    GC.KeepAlive(allocation);
                    registered.Set();
                    while (!release.IsSet)
                    {
                        Thread.SpinWait(20_000);
                    }
                }
            }
            catch (Exception exception)
            {
                workerError = exception;
                registered.Set();
            }
        })
        {
            Name = "BusinessBlockMetrics.Release"
        };

        worker.Start();
        try
        {
            Assert.True(registered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Null(workerError);
            var firstActiveRead = metrics.Read();
            Assert.Equal("release-check", Assert.Single(firstActiveRead.ActiveThreads).SessionName);
            Assert.False(firstActiveRead.AllocationCoverageComplete);
            Assert.Equal(BusinessBlockAttributionCoverage.DedicatedThreadsAndSynchronousSegments, firstActiveRead.Coverage);

            // A spinning thread gains CPU time only once it is scheduled and a clock tick passes; under load
            // a fixed short sleep can observe no change, so wait for the growth with a deadline.
            var activeRead = metrics.Read();
            var growthDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (activeRead.ActiveThreads.Single().CpuTime <= firstActiveRead.ActiveThreads.Single().CpuTime
                && DateTime.UtcNow < growthDeadline)
            {
                Thread.Sleep(20);
                activeRead = metrics.Read();
            }
            Assert.True(activeRead.TotalCpuTime > firstActiveRead.TotalCpuTime);
            Assert.True(activeRead.ActiveThreads.Single().CpuTime > firstActiveRead.ActiveThreads.Single().CpuTime);
            Assert.False(activeRead.AllocationCoverageComplete);

            release.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(workerError);
            var completedRead = metrics.Read();
            Assert.Empty(completedRead.ActiveThreads);
            Assert.True(completedRead.AllocationCoverageComplete);
            Assert.True(completedRead.TotalAllocatedBytes >= firstRead.TotalAllocatedBytes + 64 * 1024 + 2 * 1024 * 1024);
            var final = metrics.Read();
            Assert.Empty(final.ActiveThreads);
            Assert.True(final.AllocationCoverageComplete);
            Assert.True(final.TotalAllocatedBytes >= firstRead.TotalAllocatedBytes + 64 * 1024 + 2 * 1024 * 1024);
            Assert.Equal(BusinessBlockAttributionCoverage.DedicatedThreadsAndSynchronousSegments, final.Coverage);

            metrics.Reset();
            var reset = metrics.Read();
            Assert.Equal(BusinessBlockAttributionCoverage.None, reset.Coverage);
            Assert.Equal(TimeSpan.Zero, reset.TotalCpuTime);
            Assert.Equal(0, reset.TotalAllocatedBytes);
        }
        finally
        {
            release.Set();
            if (worker.IsAlive)
            {
                worker.Join(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task BlockDiagnosticsAttributeBusyConsumerCpuAndReleaseItOnStop()
    {
        ControllerBusyBlock? block = null;
        using var services = new ServiceCollection().BuildServiceProvider();
        var registrations = new[]
        {
            new BusinessBlockRegistration(117, Array.Empty<int>(), _ => block!)
        };
        var options = Options.Create(new BusinessBlockOptions {  });
        var diagnostics = new BusinessBlockDiagnosticsRegistry(registrations, NullLogger<BusinessBlockDiagnosticsRegistry>.Instance);
        await using var controller = new BusinessBlockScheduler(
            services,
            registrations,
            options,
            NullLogger<BusinessBlockScheduler>.Instance, BusinessBlockTestSupport.CreateAuthority());
        block = new ControllerBusyBlock(diagnostics);

        var reason = new BusinessBlockReason(BusinessBlockRequestKind.User, "metrics-test", "busy-block", 1);
        var started = false;
        try
        {
            await controller.SetDemandAsync("metrics", 117, reason);
            started = true;
            var thread = await block.Started.WaitAsync(TimeSpan.FromSeconds(5));
            var threadHandle = NativeThreadTimes.Open(thread.NativeThreadId);
            try
            {
                var independentBefore = NativeThreadTimes.GetCpuTicks(threadHandle) - thread.CpuTicksAtRegistration;
                var beforeRead = diagnostics.Read(117);
                // Read the independent baseline after the product read, then require real CPU progress.
                NativeThreadTimes.WaitForCpuTicksAfter(threadHandle, NativeThreadTimes.GetCpuTicks(threadHandle));
                var blockFacts = diagnostics.Read(117);
                var metrics = blockFacts.Metrics;
                var threadFacts = Assert.Single(metrics.ActiveThreads);
                var independentAfter = NativeThreadTimes.GetCpuTicks(threadHandle) - thread.CpuTicksAtRegistration;
                var previousMetrics = beforeRead.Metrics;

                Assert.Equal(117, blockFacts.Id);
                Assert.True(metrics.TotalCpuTime > TimeSpan.Zero);
                Assert.True(metrics.TotalCpuTime > previousMetrics.TotalCpuTime);
                Assert.False(metrics.AllocationCoverageComplete);
                Assert.True(threadFacts.CpuTime > TimeSpan.Zero);
                Assert.Equal(threadFacts.CpuTime, metrics.TotalCpuTime);
                Assert.Equal(BusinessBlockAttributionCoverage.DedicatedThreads, metrics.Coverage);
                Assert.Equal(thread.NativeThreadId, threadFacts.NativeThreadId);
                Assert.Equal("BusinessBlockMetrics.ControllerBusy", threadFacts.Name);
                Assert.Equal("busy-test", threadFacts.SessionName);

                var sampleAge = TimeSpan.Zero;
                var toleranceTicks = TimeSpan.FromMilliseconds(100).Ticks + sampleAge.Ticks;
                var lowerBound = Math.Max(0, independentBefore - toleranceTicks);
                var upperBound = independentAfter + toleranceTicks;
                Assert.InRange(threadFacts.CpuTime.Ticks, lowerBound, upperBound);
            }
            finally
            {
                NativeThreadTimes.Close(threadHandle);
            }

            await controller.SetDemandAsync("metrics", null, reason);
            await block.Completed.WaitAsync(TimeSpan.FromSeconds(5));
            started = false;

            var stopped = diagnostics.Read(117);
            Assert.Empty(stopped.Metrics.ActiveThreads);
            Assert.Equal(BusinessBlockAttributionCoverage.DedicatedThreads, stopped.Metrics.Coverage);
        }
        finally
        {
            if (started)
            {
                await controller.SetDemandAsync("metrics", null, reason);
                await block.Completed.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    private sealed class ControllerBusyBlock(BusinessBlockDiagnosticsRegistry diagnostics) : IBusinessBlock
    {
        private readonly TaskCompletionSource<RegisteredThreadFacts> started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RegisteredThreadFacts> Started => started.Task;
        private readonly TaskCompletionSource completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completed => completed.Task;

        public async Task RunAsync(BusinessBlockRunContext context)
        {
            var completion = new TaskCompletionSource<BusinessBlockResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() =>
            {
                try
                {
                    using (diagnostics.RegisterConsumerThread(117, "busy-test"))
                    {
                        context.ReportRunning();
                        started.TrySetResult(new(
                            NativeThreadTimes.GetCurrentThreadId(),
                            NativeThreadTimes.GetCurrentThreadCpuTicks()));
                        while (!context.StopNotification.IsCancellationRequested)
                        {
                            Thread.SpinWait(20_000);
                        }
                    }

                    completion.TrySetResult(BusinessBlockResult.Stopped);
                }
                catch (Exception error)
                {
                    started.TrySetException(error);
                    completion.TrySetException(error);
                }
            })
            {
                Name = "BusinessBlockMetrics.ControllerBusy",
                IsBackground = true
            };
            worker.Start();
            try { diagnostics.RecordResult(117, await completion.Task); }
            finally { worker.Join(); completed.TrySetResult(); }
        }
    }

    private sealed record RegisteredThreadFacts(uint NativeThreadId, long CpuTicksAtRegistration);

    private static class NativeThreadTimes
    {
        private const uint ThreadQueryInformation = 0x0040;

        public static IntPtr Open(uint threadId)
        {
            var handle = OpenThread(ThreadQueryInformation, false, threadId);
            if (handle == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return handle;
        }

        public static long GetCurrentThreadCpuTicks()
            => GetCpuTicks(GetCurrentThread());

        public static void WaitForCpuTicksAfter(IntPtr threadHandle, long previous)
            => Assert.True(SpinWait.SpinUntil(() => GetCpuTicks(threadHandle) > previous, TimeSpan.FromSeconds(10)),
                "The busy thread did not receive CPU time within the test deadline.");

        public static long GetCpuTicks(IntPtr threadHandle)
        {
            if (!GetThreadTimes(threadHandle, out _, out _, out var kernel, out var user))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return checked(ToInt64(kernel) + ToInt64(user));
        }

        public static uint GetCurrentThreadId() => GetNativeCurrentThreadId();

        public static void Close(IntPtr threadHandle)
        {
            if (!CloseHandle(threadHandle))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        private static long ToInt64(NativeFileTime value)
            => checked((long)(((ulong)value.HighDateTime << 32) | value.LowDateTime));

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenThread(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint threadId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetThreadTimes(IntPtr threadHandle, out NativeFileTime creationTime, out NativeFileTime exitTime, out NativeFileTime kernelTime, out NativeFileTime userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", EntryPoint = "GetCurrentThreadId", SetLastError = true)]
        private static extern uint GetNativeCurrentThreadId();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeFileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;
        }
    }
}

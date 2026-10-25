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
        metrics.Sample(NextSampleAfter(metrics.Read().SampledAt));
        Assert.Equal(BusinessBlockAttributionCoverage.None, metrics.Read().Coverage);
    }

    [Fact]
    public void RegisteredThreadCpuTracksIndependentThreadTimesAndLeavesNoHistoryAfterExit()
    {
        var metrics = new BusinessBlockMetrics();
        metrics.Reset(DateTimeOffset.UtcNow);

        using var started = new ManualResetEventSlim();
        using var stop = new ManualResetEventSlim();
        Exception? workerError = null;
        long nativeThreadId = 0;
        long cpuAtRegistration = 0;
        long cpuAfterRegistration = 0;

        var worker = new Thread(() =>
        {
            try
            {
                using (metrics.RegisterCurrentThread("busy-session"))
                {
                    nativeThreadId = NativeThreadTimes.GetCurrentThreadId();
                    cpuAtRegistration = NativeThreadTimes.GetCurrentThreadCpuTicks();
                    started.Set();

                    while (!stop.IsSet)
                    {
                        Thread.SpinWait(20_000);
                    }
                }

                cpuAfterRegistration = NativeThreadTimes.GetCurrentThreadCpuTicks();
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
            Thread.Sleep(200);
            var independentBefore = NativeThreadTimes.GetCpuTicks(threadHandle) - cpuAtRegistration;
            metrics.Sample(DateTimeOffset.UtcNow);
            var sample = metrics.Read();
            var activeThread = Assert.Single(sample.ActiveThreads);
            var independentAfter = NativeThreadTimes.GetCpuTicks(threadHandle) - cpuAtRegistration;

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

            var staleRead = metrics.Read();
            Assert.Single(staleRead.ActiveThreads);
            metrics.Sample(DateTimeOffset.UtcNow);
            var final = metrics.Read();
            Assert.Empty(final.ActiveThreads);
            Assert.True(final.AllocationCoverageComplete);
            Assert.True(final.TotalCpuTime > TimeSpan.Zero);
            Assert.Equal(BusinessBlockAttributionCoverage.DedicatedThreads, final.Coverage);

            var independentlyMeasuredTicks = cpuAfterRegistration - cpuAtRegistration;
            Assert.InRange(
                Math.Abs(final.TotalCpuTime.Ticks - independentlyMeasuredTicks),
                0,
                TimeSpan.FromMilliseconds(100).Ticks);
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
        var start = DateTimeOffset.UtcNow;
        metrics.Reset(start);

        using (metrics.MeasureSegment())
        {
            var allocation = new byte[5 * 1024 * 1024];
            GC.KeepAlive(allocation);
            Thread.Sleep(250);
        }

        metrics.Sample(DateTimeOffset.UtcNow);
        var facts = metrics.Read();

        Assert.True(facts.IntervalAllocatedBytes >= 5 * 1024 * 1024);
        Assert.True(facts.IntervalCpuTime < TimeSpan.FromMilliseconds(150));
        Assert.True(facts.AllocationCoverageComplete);
        Assert.Equal(BusinessBlockAttributionCoverage.SynchronousSegments, facts.Coverage);
    }

    [Fact]
    public void ReadIsPureAndSamplesPublishIntervalsAndReleaseExitedThreads()
    {
        var metrics = new BusinessBlockMetrics();
        var start = DateTimeOffset.UtcNow;
        metrics.Reset(start);

        using (metrics.MeasureSegment())
        {
            var allocation = new byte[32 * 1024];
            GC.KeepAlive(allocation);
            Thread.SpinWait(20_000);
        }

        var firstSampleAt = NextSampleAfter(start);
        metrics.Sample(firstSampleAt);
        var firstRead = metrics.Read();
        var repeatedRead = metrics.Read();
        Assert.Equal(firstRead.IntervalStartedAt, repeatedRead.IntervalStartedAt);
        Assert.Equal(firstRead.SampledAt, repeatedRead.SampledAt);
        Assert.Equal(firstRead.TotalCpuTime, repeatedRead.TotalCpuTime);
        Assert.Equal(firstRead.TotalAllocatedBytes, repeatedRead.TotalAllocatedBytes);
        Assert.Equal(start, firstRead.IntervalStartedAt);
        Assert.Equal(firstSampleAt, firstRead.SampledAt);
        Assert.Equal(BusinessBlockAttributionCoverage.SynchronousSegments, firstRead.Coverage);

        using (metrics.MeasureSegment())
        {
            var allocation = new byte[64 * 1024];
            GC.KeepAlive(allocation);
            Thread.SpinWait(20_000);
        }

        var beforeNextSample = metrics.Read();
        Assert.Equal(firstRead.TotalAllocatedBytes, beforeNextSample.TotalAllocatedBytes);
        Assert.Equal(firstRead.SampledAt, beforeNextSample.SampledAt);

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
                    release.Wait();
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

            var activeSampleAt = NextSampleAfter(firstSampleAt);
            metrics.Sample(activeSampleAt);
            var activeRead = metrics.Read();
            Assert.Equal(activeSampleAt, activeRead.SampledAt);
            Assert.Equal("release-check", Assert.Single(activeRead.ActiveThreads).SessionName);
            Assert.False(activeRead.AllocationCoverageComplete);
            Assert.Equal(BusinessBlockAttributionCoverage.DedicatedThreadsAndSynchronousSegments, activeRead.Coverage);
            var duplicateActiveRead = metrics.Read();
            Assert.Equal(activeRead.TotalCpuTime, duplicateActiveRead.TotalCpuTime);
            Assert.Equal(activeRead.ActiveThreads.Single(), duplicateActiveRead.ActiveThreads.Single());

            release.Set();
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(workerError);
            Assert.Single(metrics.Read().ActiveThreads);

            var finalSampleAt = NextSampleAfter(activeSampleAt);
            metrics.Sample(finalSampleAt);
            var final = metrics.Read();
            Assert.Equal(activeSampleAt, final.IntervalStartedAt);
            Assert.Equal(finalSampleAt, final.SampledAt);
            Assert.Empty(final.ActiveThreads);
            Assert.True(final.AllocationCoverageComplete);
            Assert.True(final.IntervalAllocatedBytes >= 2 * 1024 * 1024);
            Assert.True(final.TotalAllocatedBytes >= firstRead.TotalAllocatedBytes + 64 * 1024 + 2 * 1024 * 1024);
            Assert.Equal(BusinessBlockAttributionCoverage.DedicatedThreadsAndSynchronousSegments, final.Coverage);

            metrics.Reset(NextSampleAfter(finalSampleAt));
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
    public async Task ControllerQueryAttributesBusyConsumerCpuAndReleasesItOnStop()
    {
        ControllerBusyBlock? block = null;
        using var services = new ServiceCollection().BuildServiceProvider();
        var registrations = new[]
        {
            new BusinessBlockRegistration("busy", Array.Empty<string>(), _ => block!)
        };
        var options = Options.Create(new BusinessBlockOptions { MetricsInterval = TimeSpan.FromMilliseconds(20) });
        await using var controller = new BusinessBlockController(
            services,
            registrations,
            options,
            NullLogger<BusinessBlockController>.Instance);
        block = new ControllerBusyBlock(controller);

        var reason = new BusinessBlockReason(BusinessBlockRequestKind.User, "metrics-test", "busy-block", 1);
        var started = false;
        try
        {
            await controller.StartAsync("busy", reason);
            started = true;
            var thread = await block.Started.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);

            var threadHandle = NativeThreadTimes.Open(thread.NativeThreadId);
            try
            {
                var independentBefore = NativeThreadTimes.GetCpuTicks(threadHandle) - thread.CpuTicksAtRegistration;
                var snapshot = controller.Read();
                var blockFacts = Assert.Single(snapshot.Blocks);
                var metrics = blockFacts.Metrics;
                var threadFacts = Assert.Single(metrics.ActiveThreads);
                var independentAfter = NativeThreadTimes.GetCpuTicks(threadHandle) - thread.CpuTicksAtRegistration;

                Assert.Equal("busy", blockFacts.Id);
                Assert.True(metrics.TotalCpuTime > TimeSpan.Zero);
                Assert.True(threadFacts.CpuTime > TimeSpan.Zero);
                Assert.Equal(threadFacts.CpuTime, metrics.TotalCpuTime);
                Assert.Equal(BusinessBlockAttributionCoverage.DedicatedThreads, metrics.Coverage);
                Assert.Equal(thread.NativeThreadId, threadFacts.NativeThreadId);
                Assert.Equal("BusinessBlockMetrics.ControllerBusy", threadFacts.Name);
                Assert.Equal("busy-test", threadFacts.SessionName);
                Assert.True(metrics.SampledAt >= threadFacts.RegisteredAt);
                Assert.True(snapshot.At >= metrics.SampledAt);

                var sampleAge = snapshot.At - metrics.SampledAt;
                var toleranceTicks = TimeSpan.FromMilliseconds(100).Ticks + sampleAge.Ticks;
                var lowerBound = Math.Max(0, independentBefore - toleranceTicks);
                var upperBound = independentAfter + toleranceTicks;
                Assert.InRange(threadFacts.CpuTime.Ticks, lowerBound, upperBound);
            }
            finally
            {
                NativeThreadTimes.Close(threadHandle);
            }

            await controller.StopAsync("busy", reason);
            started = false;

            var stopped = Assert.Single(controller.Read().Blocks);
            Assert.Equal(BusinessBlockState.Stopped, stopped.State);
            Assert.Empty(stopped.Metrics.ActiveThreads);
            Assert.False(stopped.ExecutionOutstanding);
            Assert.Equal(BusinessBlockAttributionCoverage.DedicatedThreads, stopped.Metrics.Coverage);
        }
        finally
        {
            if (started)
            {
                await controller.StopAsync("busy", reason);
            }
        }
    }

    private static DateTimeOffset NextSampleAfter(DateTimeOffset previous)
    {
        var now = DateTimeOffset.UtcNow;
        return now > previous ? now : previous.AddTicks(1);
    }

    private sealed class ControllerBusyBlock(BusinessBlockController controller) : IBusinessBlock
    {
        private readonly TaskCompletionSource<RegisteredThreadFacts> started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<RegisteredThreadFacts> Started => started.Task;

        public Task<BusinessBlockResult> RunAsync(CancellationToken stopNotification)
        {
            var completion = new TaskCompletionSource<BusinessBlockResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() =>
            {
                try
                {
                    using (controller.RegisterConsumerThread("busy", "busy-test"))
                    {
                        started.TrySetResult(new(
                            NativeThreadTimes.GetCurrentThreadId(),
                            NativeThreadTimes.GetCurrentThreadCpuTicks()));
                        while (!stopNotification.IsCancellationRequested)
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
            return completion.Task;
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

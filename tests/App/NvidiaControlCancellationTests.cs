using ResourceManager.ReadWrite.Vendors.Nvidia;

namespace Resource_Manager_APP.Tests;

public sealed class NvidiaControlCancellationTests
{
    [Theory]
    [InlineData("gpu.power-limit", false)]
    [InlineData("gpu.temperature-limit", false)]
    [InlineData("gpu.core-clock-maximum", false)]
    [InlineData("gpu.core-clock-offset", false)]
    [InlineData("gpu.core-clock-maximum", true)]
    public async Task CancellationDuringNativeProbePreventsTheNextSetter(string capabilityId, bool restore)
    {
        using var cancellation = new CancellationTokenSource();
        var nvml = new RecordedNvml();
        var nvapi = new RecordedNvapi();
        if (capabilityId == "gpu.core-clock-offset") nvapi.OnHandle = cancellation.Cancel;
        else if (capabilityId == "gpu.core-clock-maximum" && !restore) nvml.OnMaximum = cancellation.Cancel;
        else nvml.OnHandle = cancellation.Cancel;
        using var owner = new NvidiaGpuControlResource(nvml, nvapi);
        var target = new NvidiaControlObject("gpu:recorded", 0, null, true);
        var capability = new NvidiaControlCapability(capabilityId);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (restore) await owner.RestoreAsync(target, capability, new HashSet<string>(), cancellation.Token);
            else await owner.WriteAsync(target, capability, new(capabilityId, Number: 100), cancellation.Token);
        });

        Assert.Equal(0, nvml.Writes + nvapi.Writes);
    }

    private sealed class RecordedNvapi : NvidiaNvapiControlBridge
    {
        public Action? OnHandle;
        public int Writes;
        public override IntPtr? FindHandleByAdapterIndex(int index) { OnHandle?.Invoke(); return new(1); }
        public override bool TryWriteClockOffsetMhz(IntPtr device, uint domain, double offset) { Writes++; return true; }
    }
    private sealed class RecordedNvml : NvidiaNvmlControlBridge
    {
        public Action? OnHandle, OnMaximum;
        public int Writes;
        public override IntPtr? FindHandleByAdapterIndex(int index) { OnHandle?.Invoke(); return new(1); }
        public override double? ReadMaximumClockMhz(IntPtr device, uint domain) { OnMaximum?.Invoke(); return 3000; }
        public override int WritePowerLimitWatts(IntPtr device, double watts) { Writes++; return 0; }
        public override int WriteTemperatureThreshold(IntPtr device, uint threshold, int celsius) { Writes++; return 0; }
        public override double? ReadTemperatureThreshold(IntPtr device, uint threshold) => 100;
        public override int WriteLockedClocks(IntPtr device, uint domain, uint minimum, uint maximum) { Writes++; return 0; }
        public override int ResetLockedClocks(IntPtr device, uint domain) { Writes++; return 0; }
    }
}

namespace Resource_Manager_APP.Tests;

public sealed class NvidiaGpuVfLegacyReleaseTests
{
    [Theory]
    [InlineData(true, 0d, 0, true)]
    [InlineData(false, 0d, 0, false)]
    [InlineData(true, 0.001d, 1, true)]
    [InlineData(true, 0.002d, 1, false)]
    [InlineData(true, double.NaN, 1, false)]
    public async Task LegacyZeroReleaseUsesOnlyInjectedScalarSetterAndVerifiedReadback(
        bool accepted, double observedMhz, int toleranceKhz, bool expected)
    {
        var bridge = new ScalarFixture { Accepted = accepted, ObservedMhz = observedMhz };
        using var resource = new NvidiaGpuControlResource(nvapi: bridge);
        var result = await resource.RestoreLegacyCoreOffsetAsync(new("gpu:fixture", 0), new(toleranceKhz), default);
        Assert.Equal(expected, result);
        Assert.Equal(0d, bridge.RequestedMhz);
        Assert.Equal(0u, bridge.Domain);
        Assert.Equal(accepted ? 1 : 0, bridge.Reads);
    }

    private sealed class ScalarFixture : NvidiaNvapiControlBridge
    {
        internal bool Accepted;
        internal double ObservedMhz, RequestedMhz;
        internal uint Domain;
        internal int Reads;
        public override IntPtr? FindHandleByAdapterIndex(int index) => new(1);
        public override bool TryWriteClockOffsetMhz(IntPtr handle, uint domain, double offsetMhz)
        { Domain = domain; RequestedMhz = offsetMhz; return Accepted; }
        public override double? ReadClockOffsetMhz(IntPtr handle, uint domain)
        { Assert.Equal(0u, domain); Reads++; return ObservedMhz; }
    }
}

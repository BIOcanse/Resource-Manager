using System.Runtime.InteropServices;
using System.Security.Principal;
using ResourceManager.Shared.Overlay;

namespace Resource_Manager_APP.Tests;

public sealed class PerformanceOverlayProtocolTests
{
    [Fact]
    public void NativeHeaderOffsetsAndSectionIdentityMatch()
    {
        Assert.Equal(80, Marshal.SizeOf<PerformanceOverlayHeader>());
        Assert.Equal(16, Marshal.OffsetOf<PerformanceOverlayHeader>(nameof(PerformanceOverlayHeader.Generation)).ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<PerformanceOverlayHeader>(nameof(PerformanceOverlayHeader.ConsumedGeneration)).ToInt32());
        Assert.Equal(32, Marshal.OffsetOf<PerformanceOverlayHeader>(nameof(PerformanceOverlayHeader.FrameCounter)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<PerformanceOverlayHeader>(nameof(PerformanceOverlayHeader.DrawSuccessCounter)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<PerformanceOverlayHeader>(nameof(PerformanceOverlayHeader.TargetWindow)).ToInt32());
        Assert.Equal(72, Marshal.OffsetOf<PerformanceOverlayHeader>(nameof(PerformanceOverlayHeader.SwapChainWidth)).ToInt32());
        Assert.Equal("Local\\ResourceManager.PerformanceOverlay.0000002A.01DA000000000001",
            PerformanceOverlaySharedMemory.SectionName(42, 0x01DA000000000001));
    }

    [Fact]
    public void SectionDaclNamesOnlySystemAndTheSelectedAccount()
    {
        var sid = WindowsIdentity.GetCurrent().User!;
        Assert.Equal($"D:P(A;;GA;;;SY)(A;;GA;;;{sid.Value})",
            PerformanceOverlaySharedMemory.SectionSddl(sid));
        Assert.Throws<ArgumentException>(() => PerformanceOverlaySharedMemory.SectionSddl(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null)));
    }
}

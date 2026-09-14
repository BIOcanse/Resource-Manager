using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed class NativeMemoryCleanupAbiTests
{
    [Fact]
    public void Abi_UsesPublishedVersionAndFixedPodLayouts()
    {
        Assert.Equal(NativeMemoryCleanupAbi.Version, NativeCoreLibrary.GetMemoryCleanupAbiVersion());
        Assert.Equal(104, Marshal.SizeOf<NativeMemoryCleanupConfig>());
        Assert.Equal(72, Marshal.SizeOf<NativeMemoryCleanupPlanHeader>());
        Assert.Equal(56, Marshal.SizeOf<NativeMemoryCleanupCandidateInput>());
        Assert.Equal(48, Marshal.SizeOf<NativeMemoryCleanupDecisionOutput>());
        Assert.Equal(16, Marshal.SizeOf<NativeMemoryCleanupFeedbackInput>());
    }
}

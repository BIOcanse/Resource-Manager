using System.Runtime.InteropServices;
using ResourceManager.App.Hosting;

namespace Resource_Manager_APP.Tests;

[Collection(NativeLaunchPolicyProcessStateCollection.Name)]
public sealed class GpuWindowActionLaunchErrorPolicyTests
{
    [Fact]
    public void EstablishForLaunchReportsTheMissingBitAndTheEstablishedMode()
    {
        var original = GetErrorMode();
        try
        {
            var missingBit = original & ~0x0002U;
            _ = SetErrorMode(missingBit);

            var mode = WindowsNativeLaunchErrorPolicy.EstablishForLaunch();

            Assert.Equal(missingBit, mode.Before);
            Assert.Equal(missingBit | WindowsNativeLaunchErrorPolicy.RequiredMode, mode.After);
            Assert.Equal(mode.After, GetErrorMode());
        }
        finally
        {
            _ = SetErrorMode(original);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();

    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}

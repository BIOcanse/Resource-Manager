using System.Reflection;
using System.Runtime.InteropServices;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.ReadWrite.Vendors.Intel;
using static Resource_Manager_APP.Tests.MetricSnapshotOwnerTestFixture;

namespace Resource_Manager_APP.Tests;

public sealed class MetricScratchDirectoryFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnknownIntelCloseEffectKeepsTheHolderFailedWithoutRepeatingNativeClose(bool borrowLast)
    {
        var closes = 0;
        int Init(ref IntelGclInitArgs args, out IntPtr api) { api = new(1); return 0; }
        int Enumerate(IntPtr api, ref uint count, IntPtr[]? devices) => throw new InvalidOperationException("No device access.");
        int Properties(IntPtr device, ref IntelGclDeviceProperties properties) => throw new InvalidOperationException("No device access.");
        int Telemetry(IntPtr device, ref IntelGclPowerTelemetry telemetry) => throw new InvalidOperationException("No device access.");
        var native = IntelGclNativeSession.Open(new(Init, _ => { closes++; return -17; }, Enumerate, Properties, Telemetry));
        Assert.NotNull(native);
        var directory = new IntelGclFanDirectoryLease(native, []);
        IDisposable loan = directory;
        if (borrowLast)
        {
            loan = directory.AcquireLease();
            directory.Dispose();
            Assert.Equal(0, closes);
        }
        var plan = HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace = NativeMetricSnapshotHostConfiguration.CreateWorkspace(plan,
            Path.Combine(Path.GetTempPath(), $"metric-intel-close-{Guid.NewGuid():N}.bin"));
        var closed = 0;
        var holder = new HostManagerMetricSnapshotOwner.WorkspaceHolder(1, workspace, Topology([]), _ => closed++);
        typeof(HostManagerMetricSnapshotOwner.WorkspaceHolder)
            .GetField("intelFanDirectoryLease", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(holder, loan);
        var failure = Assert.Throws<ExternalException>(() => holder.Retire());
        Assert.Equal(-17, failure.ErrorCode);
        Assert.Equal(1, closes);
        Assert.Same(failure, holder.CloseFailure);
        Assert.Same(failure, Assert.Throws<ExternalException>(() => holder.CloseIfUnused()));
        Assert.Same(failure, Assert.Throws<ExternalException>(() => loan.Dispose()));
        Assert.Same(failure, Assert.Throws<ExternalException>(() => directory.Dispose()));
        Assert.Throws<ObjectDisposedException>(() => directory.AcquireLease());
        Assert.Equal(1, closes);
        Assert.Equal(0, closed); // The holder is still an explicit failed close obligation.
        Assert.Same(failure, holder.CloseFailure);
    }
}

using System.Reflection;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.RuntimeSpecialization.FreedomPoints;
using ResourceManager.App.Infrastructure.Telemetry.Etw;

namespace Resource_Manager_APP.Tests;

// The VidMm, GPU allocation and Present real-time ETW sessions take their buffer pools from
// monitoring vidmm_session_buffers / gpu_allocation_session_buffers / present_session_buffers.
public sealed class UserEtwSessionBufferSettingsTests
{
    private static readonly string[] PointPaths =
    [
        BackendFreedomPointPaths.VidMmSessionBuffers,
        BackendFreedomPointPaths.GpuAllocationSessionBuffers,
        BackendFreedomPointPaths.PresentSessionBuffers
    ];
    public static TheoryData<string> Points => new(PointPaths);

    private static EtwSessionBuffers Read(CompiledMonitoringSettings settings, string path) => path switch
    {
        BackendFreedomPointPaths.VidMmSessionBuffers => settings.VidMmSessionBuffers,
        BackendFreedomPointPaths.GpuAllocationSessionBuffers => settings.GpuAllocationSessionBuffers,
        BackendFreedomPointPaths.PresentSessionBuffers => settings.PresentSessionBuffers,
        _ => throw new ArgumentOutOfRangeException(nameof(path))
    };

    [Fact]
    public void ProductDefaultIsThirtyTwoKibTimesSixtyFourBuffersForEachSession()
    {
        var plan = HostManagerTestPlanFactory.CreatePlan();
        foreach (var settings in new[] { plan.Monitoring!, MonitoringSettings.ProductDefaults })
            foreach (var buffers in new[] { settings.VidMmSessionBuffers, settings.GpuAllocationSessionBuffers, settings.PresentSessionBuffers })
                Assert.Equal((32, 64, 2), (buffers.BufferKib, buffers.MinimumBuffers, buffers.PoolMib));
    }

    [Theory]
    [MemberData(nameof(Points))]
    public void DeclaredPoolFlowsIntoOnlyItsOwnSession(string path)
    {
        var baseline = HostManagerTestPlanFactory.CreatePlan();
        var changed = HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
        {
            var value = FreedomPointTestFactory.Point(root, path)["value"]!;
            value["buffer_kib"] = 64;
            value["minimum_buffers"] = 48;
        });
        Assert.Equal((64, 48, 3), (Read(changed.Monitoring!, path).BufferKib, Read(changed.Monitoring!, path).MinimumBuffers,
            Read(changed.Monitoring!, path).PoolMib));
        foreach (var other in PointPaths.Where(other => other != path))
            Assert.Equal(Read(baseline.Monitoring!, other), Read(changed.Monitoring!, other));
        Assert.NotEqual(baseline.BuildSha256, changed.BuildSha256);
    }

    [Theory]
    [InlineData("buffer_kib", 0)]
    [InlineData("buffer_kib", -32)]
    [InlineData("minimum_buffers", 0)]
    [InlineData("minimum_buffers", 3)] // 3 x 32 KiB is not whole MiB.
    public void InvalidPoolFailsCompilationInsteadOfSubstitutingADefault(string field, int value)
    {
        foreach (var path in PointPaths)
            Assert.Throws<InvalidDataException>(() => HostManagerTestPlanFactory.CreatePlan(editFreedomPoints: root =>
            {
                FreedomPointTestFactory.Point(root, path)["value"]![field] = value;
            }));
    }

    [Fact]
    public void PresentSessionIsCreatedWithTheDeclaredPoolInsteadOfAFixedSixteenMib()
    {
        var name = "ResourceManager.UserEtwSessionBufferSettingsTests." + Guid.NewGuid().ToString("N");
        var wrapper = new TraceEventPresentFrameEtwSession(name, new EtwSessionBuffers(64, 48), NullLogger.Instance,
            ResourceManager.App.Infrastructure.Monitoring.MonitoringSettings.ProductDefaults.FramePipeline);
        var session = (TraceEventSession)typeof(TraceEventPresentFrameEtwSession)
            .GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(wrapper)!;
        // No provider was enabled, so no native session exists; nothing here starts or stops one.
        Assert.Equal((64, 3), (session.BufferQuantumKB, session.BufferSizeMB));
        GC.SuppressFinalize(session);
        GC.SuppressFinalize(wrapper);
    }
}

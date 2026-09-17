using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Infrastructure.Control;

namespace Resource_Manager_APP.Tests;

/// <summary>
/// 控制面把设定交给写入器这条路。
///
/// 守的是同一条规矩：**"能不能写"只有写入器说了算**。
/// 目录里的 Supported 和执行器实际走的那条路必须来自同一个回答 ——
/// 否则界面上能调的和真能写下去的是两批东西。
/// </summary>
public sealed class ControlWriterDispatchTests
{
    private static ControlObject GpuObject(ControlNumberRange? range = null) => new(
        "gpu:test",
        ControlObjectKinds.Gpu,
        "Test GPU",
        new ControlObjectPlatform(ControlOperatingSystems.Windows, ControlVendors.Nvidia),
        [
            new ControlCapability(
                "gpu.core-clock-offset",
                "核心频率偏移",
                ControlValueKinds.Number,
                Supported: false,
                UnavailableReason: "写入器还没接",
                Range: range ?? new ControlNumberRange(-500, 500, 5, "MHz", 0))
        ],
        AdapterIndex: 0);

    [Fact]
    public void CatalogTakesAvailabilityAndRangeFromTheWriter()
    {
        var writer = new FakeWriter
        {
            Availability = ControlWriteAvailability.Yes(
                new ControlNumberRange(0, 120, 1, "W", 60))
        };
        var catalog = new StubCatalog(GpuObject());

        var resolved = new ControlObjectCatalogWithWriters(catalog, [writer])
            .ReadObjects().Objects[0].Capabilities[0];

        Assert.True(resolved.Supported);
        Assert.Null(resolved.UnavailableReason);
        // 真实范围只有硬件知道，所以目录里的形状要让位给写入器报的那个。
        Assert.Equal(120, resolved.Range!.Maximum);
        Assert.Equal("W", resolved.Range.Unit);
    }

    [Fact]
    public async Task ExecutorWritesThroughTheWriterThatClaimsTheCapability()
    {
        var writer = new FakeWriter { Availability = ControlWriteAvailability.Yes() };
        var executor = new ControlPlanExecutor(new StubCatalog(GpuObject()), [writer]);

        var report = await executor.ApplyAsync(
            new ControlDesiredState([
                new ControlObjectDesiredState(
                    "gpu:test",
                    [new ControlSetting("gpu.core-clock-offset", Number: 45)])
            ]),
            CancellationToken.None);

        Assert.Equal(ControlApplyStatuses.Applied, report.Outcomes[0].Status);
        Assert.Equal(45, Assert.Single(writer.Written).Number);
    }

    [Fact]
    public async Task ExecutorReportsTheWriterReasonWhenItCannotWrite()
    {
        var writer = new FakeWriter
        {
            Availability = ControlWriteAvailability.No("这块卡不开放这一项。")
        };
        var executor = new ControlPlanExecutor(new StubCatalog(GpuObject()), [writer]);

        var report = await executor.ApplyAsync(
            new ControlDesiredState([
                new ControlObjectDesiredState(
                    "gpu:test",
                    [new ControlSetting("gpu.core-clock-offset", Number: 45)])
            ]),
            CancellationToken.None);

        Assert.Equal(ControlApplyStatuses.Unsupported, report.Outcomes[0].Status);
        Assert.Equal("这块卡不开放这一项。", report.Outcomes[0].Message);
        Assert.Empty(writer.Written);
    }

    /// <summary>
    /// 撤销一项设定，硬件要回到默认。
    ///
    /// 只是"以后不再写它"是不够的：上次写进去的偏移还留在卡里，
    /// 用户撤销之后看到的仍然是超频状态，而他的设定里已经什么都没有了。
    /// </summary>
    [Fact]
    public async Task ReleasingASettingRestoresTheHardwareDefault()
    {
        var writer = new FakeWriter { Availability = ControlWriteAvailability.Yes() };
        var catalog = new StubCatalog(GpuObject());
        var store = new InMemoryStore(new ControlDesiredState([
            new ControlObjectDesiredState(
                "gpu:test",
                [new ControlSetting("gpu.core-clock-offset", Number: 45)])
        ]));
        var plane = new ControlPlane(
            store,
            new ControlPlanExecutor(catalog, [writer]),
            catalog);

        await plane.SetObjectSettingsAsync("gpu:test", [], CancellationToken.None);

        // 写下去的是默认值 0，而不是什么都不写。
        Assert.Equal(0, Assert.Single(writer.Written).Number);
        // 但存起来的期望状态里不该冒出一条用户没设过的"= 0"。
        Assert.Empty(store.Saved.Objects);
    }

    private sealed class FakeWriter : IControlWriter
    {
        public ControlWriteAvailability Availability { get; set; }

        public List<ControlSetting> Written { get; } = [];

        public ControlWriteAvailability Probe(ControlObject target, ControlCapability capability)
            => Availability;

        public Task<ControlApplyOutcome> WriteAsync(
            ControlObject target,
            ControlCapability capability,
            ControlSetting setting,
            CancellationToken cancellationToken)
        {
            Written.Add(setting);
            return Task.FromResult(new ControlApplyOutcome(
                target.Id,
                capability.Id,
                ControlApplyStatuses.Applied,
                null));
        }
    }

    private sealed class StubCatalog(ControlObject controlObject) : IControlObjectCatalog
    {
        public ControlObjectCatalog ReadObjects()
            => new([controlObject], DateTimeOffset.UnixEpoch);
    }

    /// <summary>
    /// 只做"逐项问写入器"这一件事，和 Windows 那个目录里的做法一致。
    /// 这里不去构造真的监控快照 —— 要验的是分派，不是采集。
    /// </summary>
    private sealed class ControlObjectCatalogWithWriters(
        IControlObjectCatalog inner,
        IReadOnlyList<IControlWriter> writers) : IControlObjectCatalog
    {
        public ControlObjectCatalog ReadObjects()
        {
            var objects = inner.ReadObjects().Objects
                .Select(entry => entry with
                {
                    Capabilities = entry.Capabilities.Select(capability =>
                    {
                        foreach (var writer in writers)
                        {
                            var availability = writer.Probe(entry, capability);
                            if (!availability.IsMine)
                            {
                                continue;
                            }
                            return capability with
                            {
                                Supported = availability.CanWrite,
                                UnavailableReason = availability.CanWrite
                                    ? null
                                    : availability.Reason,
                                Range = availability.Range ?? capability.Range
                            };
                        }
                        return capability;
                    }).ToArray()
                })
                .ToArray();
            return new ControlObjectCatalog(objects, DateTimeOffset.UnixEpoch);
        }
    }

    private sealed class InMemoryStore(ControlDesiredState initial) : IControlDesiredStateStore
    {
        public ControlDesiredState Saved { get; private set; } = initial;

        public Task<ControlDesiredState> LoadAsync(CancellationToken cancellationToken)
            => Task.FromResult(Saved);

        public Task SaveAsync(ControlDesiredState desired, CancellationToken cancellationToken)
        {
            Saved = desired;
            return Task.CompletedTask;
        }
    }
}

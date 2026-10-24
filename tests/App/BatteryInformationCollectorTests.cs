using System.Buffers.Binary;
using System.Text;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed partial class BatteryInformationCollectorTests
{
    private static readonly HashSet<MetricFieldCode> AllFields = MetricFieldRegistry.Definitions
        .Where(static field => field.Resource == MetricResource.Battery).Select(static field => field.Code).ToHashSet();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnitsSignedRateAndDerivedRatiosStayWithinOneDevice(bool relative)
    {
        var value = WindowsBatteryReader.Project("device", ParseInfo(relative), Status(0x8000000D), 3600, AllFields);
        Assert.Equal(60000, value.Values[relative ? MetricFieldCode.BatteryDesignCapacityRelative : MetricFieldCode.BatteryDesignCapacityAbsolute]);
        Assert.Equal(25000, value.Values[relative ? MetricFieldCode.BatteryRemainingCapacityRelative : MetricFieldCode.BatteryRemainingCapacityAbsolute]);
        Assert.Equal(-15000, value.Values[relative ? MetricFieldCode.BatteryRateRelative : MetricFieldCode.BatteryRateAbsolute]);
        Assert.Equal(50, value.Values[MetricFieldCode.BatteryChargePercent]);
        Assert.Equal(50000d * 100 / 60000, value.Values[MetricFieldCode.BatteryFullToDesignRatio]);
        Assert.Equal(0x8000000DU, value.PowerState);
        Assert.Equal(3600, value.Values[MetricFieldCode.BatteryRemainingTime]);
        Assert.All(value.Values.Keys, code => Assert.True(WindowsBatteryReader.AppliesTo(code, relative)));
    }

    [Fact]
    public void UnknownSentinelsAndUnsupportedCyclesAreAbsentButRealZeroIsPreserved()
    {
        var unknown = WindowsBatteryReader.Project("device", ParseInfo(false, uint.MaxValue, uint.MaxValue, 0),
            Status(0, uint.MaxValue, uint.MaxValue, int.MinValue), uint.MaxValue, AllFields);
        Assert.Empty(unknown.Values); Assert.Equal(0U, unknown.PowerState);
        var zero = WindowsBatteryReader.Project("device", ParseInfo(false, 0, 0, 0), Status(0, 0, 0, 0), 0, AllFields);
        Assert.Equal(0, zero.Values[MetricFieldCode.BatteryRateAbsolute]);
        Assert.Equal(0, zero.Values[MetricFieldCode.BatteryRemainingCapacityAbsolute]);
        Assert.Equal(0, zero.Values[MetricFieldCode.BatteryRemainingTime]);
        Assert.False(zero.Values.ContainsKey(MetricFieldCode.BatteryChargePercent));
        Assert.False(zero.Values.ContainsKey(MetricFieldCode.BatteryCycleCount));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(17)]
    public void MalformedStatusDoesNotEraseIndependentInformation(int length)
    {
        var value = WindowsBatteryReader.Project("device", ParseInfo(false), new byte[length], null, AllFields);
        Assert.Equal(60000, value.Values[MetricFieldCode.BatteryDesignCapacityAbsolute]);
        Assert.Equal(120, value.Values[MetricFieldCode.BatteryCycleCount]);
        Assert.False(value.Values.ContainsKey(MetricFieldCode.BatteryVoltage)); Assert.Null(value.PowerState);
        Assert.Null(WindowsBatteryReader.ParseInformation(new byte[35]));
    }

    [Fact]
    public void RatiosAreUnclampedReportedRelationshipsAndHealthDoesNotNeedStatus()
    {
        var value = WindowsBatteryReader.Project("device", ParseInfo(false, 100, 110), Status(1, 120, 0, 0), null, AllFields);
        Assert.Equal(110, value.Values[MetricFieldCode.BatteryFullToDesignRatio]);
        Assert.Equal(120d * 100 / 110, value.Values[MetricFieldCode.BatteryChargePercent]);
        Assert.True(WindowsBatteryReader.Project("device", ParseInfo(false), null, null, AllFields)
            .Values.ContainsKey(MetricFieldCode.BatteryFullToDesignRatio));
    }

    [Fact]
    public void DiscoveryDistinguishesEmptySlotsFailedReadsAndPhysicalIdentity()
    {
        var found = new WindowsBatteryReader(() => ["b", "a"], path => new DeviceQuery { TagError = path == "a" ? 2 : 0 }).ReadIdentities();
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, found.Status);
        Assert.Equal("b", Assert.Single(found.Identities).DevicePath);
        Assert.Empty(new WindowsBatteryReader(() => ["a"], _ => new DeviceQuery { TagError = 2 }).ReadIdentities().Identities);
        var failedRow = new WindowsBatteryReader(() => ["a"], _ => new DeviceQuery { TagError = 5 }).ReadIdentities();
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, failedRow.Status);
        Assert.Equal(1, failedRow.RejectedCount);
        Assert.Empty(failedRow.Identities);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, new WindowsBatteryReader(() => [], _ => null).ReadIdentities().Status);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, new WindowsBatteryReader(() => null, _ => null).ReadIdentities().Status);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, new WindowsBatteryReader(() => ["a", "A"], _ => null).ReadIdentities().Status);
        Assert.Equal(1, new WindowsBatteryReader(() => ["a"], _ => new DeviceQuery { UniqueId = null }).ReadIdentities().RejectedCount);
        Assert.NotEqual(BatteryCatalogIdentity.CreateIdentity("path", "A"), BatteryCatalogIdentity.CreateIdentity("path", "B"));
        Assert.Equal(BatteryCatalogIdentity.CreateIdentity("PATH", "A"), BatteryCatalogIdentity.CreateIdentity("path", "A"));
    }

    [Fact]
    public void UnreadableBatteryRowsDoNotDiscardEarlierOrLaterVerifiedIdentities()
    {
        var read = new WindowsBatteryReader(() => ["a", "b", "c", "d", "e", "f"], path => path switch
        {
            "b" => null,
            "c" => new DeviceQuery { UniqueId = null },
            "d" => new DeviceQuery { ChangedTag = true },
            "e" => throw new IOException("device removed"),
            _ => new DeviceQuery { UniqueId = path + "-unique" }
        }).ReadIdentities(includeDeclarations: true);
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, read.Status);
        Assert.Equal(4, read.RejectedCount);
        Assert.Equal(new[] { "a", "f" }, read.Identities.Select(static row => row.DevicePath));
        Assert.Equal(new[] { BatteryCatalogIdentity.CreateIdentity("a", "a-unique"),
            BatteryCatalogIdentity.CreateIdentity("f", "f-unique") }, read.Identities.Select(static row => row.ExactIdentityKey));
    }

    [Fact]
    public void DemandSelectsDevicesAndQueryGroupsAndAllRequestsHaveZeroWait()
    {
        var opened = new List<string>(); var queries = new List<DeviceQuery>();
        var reader = new WindowsBatteryReader(() => throw new InvalidOperationException("Sampling must not enumerate."), path =>
        { opened.Add(path); var query = new DeviceQuery(); queries.Add(query); return query; });
        var zone = new WindowsBatteryMonitoringZone(reader);
        Assert.Empty(zone.Read([Device(0), Device(1)], new Dictionary<int, IReadOnlySet<MetricFieldCode>>()));
        Assert.Empty(opened);
        var capacity = zone.Read([Device(0), Device(1)], new Dictionary<int, IReadOnlySet<MetricFieldCode>>
            { [1] = new HashSet<MetricFieldCode> { MetricFieldCode.BatteryDesignCapacityAbsolute } });
        Assert.Equal(["path-1"], opened);
        Assert.Equal(60000, Assert.Single(capacity).Values[MetricFieldCode.BatteryDesignCapacityAbsolute]);
        Assert.DoesNotContain(queries[0].Calls, call => call.Code == WindowsBatteryReader.QueryStatus || call.Level == 3);
        var timed = zone.Read([Device(0)], new Dictionary<int, IReadOnlySet<MetricFieldCode>>
            { [0] = new HashSet<MetricFieldCode> { MetricFieldCode.BatteryVoltage, MetricFieldCode.BatteryRemainingTime } });
        Assert.Equal(3600, Assert.Single(timed).Values[MetricFieldCode.BatteryRemainingTime]);
        Assert.Contains(queries[1].Calls, call => call.Code == WindowsBatteryReader.QueryStatus);
        Assert.Contains(queries[1].Calls, call => call.Level == 3);
        Assert.All(queries, query => Assert.True(query.Disposed));
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("identity")]
    [InlineData("units")]
    public void ChangeDuringSamplingRejectsValuesAndRequestsExistingOwnerRefresh(string change)
    {
        var query = new DeviceQuery { ChangedTag = change == "tag", UniqueId = change == "identity" ? "replacement" : "uid", Relative = change == "units" };
        var value = new WindowsBatteryReader(enumerate: null, open: _ => query).Read(Device(0), AllFields);
        Assert.Empty(value.Values); Assert.Null(value.PowerState);
        Assert.True(value.RequiresTopologyRefresh); Assert.True(query.Disposed);
    }

    [Fact]
    public void OptionalStatusAndEstimateFailuresLeaveOnlyDependentFieldsAbsent()
    {
        var value = new WindowsBatteryReader(enumerate: null, open: _ => new DeviceQuery { StatusAvailable = false, EstimatedTime = uint.MaxValue }).Read(Device(0), AllFields);
        Assert.False(value.RequiresTopologyRefresh);
        Assert.Equal(60000, value.Values[MetricFieldCode.BatteryDesignCapacityAbsolute]);
        Assert.False(value.Values.ContainsKey(MetricFieldCode.BatteryChargePercent));
        Assert.False(value.Values.ContainsKey(MetricFieldCode.BatteryRemainingTime));
    }

    private static BatteryCatalogIdentity Device(int index, bool relative = false) => new(index, $"path-{index}", "uid", ParseInfo(relative));
    private static BatteryInformation ParseInfo(bool relative, uint design = 60000, uint full = 50000, uint cycles = 120)
        => WindowsBatteryReader.ParseInformation(Info(relative, design, full, cycles))!;
    private static byte[] Info(bool relative = false, uint design = 60000, uint full = 50000, uint cycles = 120)
    {
        var bytes = new byte[36]; Write(bytes, 0, relative ? 0xC0000000 : 0x80000000);
        Encoding.ASCII.GetBytes("LION").CopyTo(bytes, 8);
        Write(bytes, 12, design); Write(bytes, 16, full); Write(bytes, 32, cycles); return bytes;
    }
    private static byte[] Status(uint flags = 5, uint capacity = 25000, uint voltage = 12000, int rate = -15000)
    {
        var bytes = new byte[16]; Write(bytes, 0, flags); Write(bytes, 4, capacity); Write(bytes, 8, voltage);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), rate); return bytes;
    }
    private static void Write(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private sealed class DeviceQuery : IWindowsBatteryQuery
    {
        private int tagReads;
        internal uint Tag { get; init; } = 7;
        internal int TagError { get; init; }
        public int LastError { get; private set; }
        internal bool ChangedTag { get; init; }
        internal bool Relative { get; init; }
        internal string? UniqueId { get; init; } = "uid";
        internal bool StatusAvailable { get; init; } = true;
        internal uint EstimatedTime { get; init; } = 3600;
        internal bool Disposed { get; private set; }
        internal List<(uint Code, uint? Level)> Calls { get; } = [];
        public byte[]? Query(uint controlCode, byte[] input, int maximumOutputBytes)
        {
            uint? level = controlCode == WindowsBatteryReader.QueryInformation ? BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(4)) : null;
            Calls.Add((controlCode, level));
            if (controlCode == WindowsBatteryReader.QueryTag)
            {
                Assert.Equal(new byte[4], input);
                LastError = TagError;
                if (TagError != 0) return null;
                var bytes = new byte[4]; Write(bytes, 0, ChangedTag && ++tagReads > 1 ? Tag + 1 : Tag); return bytes;
            }
            Assert.Equal(Tag, BinaryPrimitives.ReadUInt32LittleEndian(input));
            if (controlCode == WindowsBatteryReader.QueryStatus)
            { Assert.Equal(new byte[16], input[4..]); return StatusAvailable ? Status() : null; }
            Assert.Equal(new byte[4], input[8..]);
            return level switch
            {
                0 => Info(Relative), 7 => UniqueId is null ? null : Encoding.Unicode.GetBytes(UniqueId + "\0"),
                3 => BitConverter.GetBytes(EstimatedTime), _ => null
            };
        }
        public void Dispose() => Disposed = true;
    }
}

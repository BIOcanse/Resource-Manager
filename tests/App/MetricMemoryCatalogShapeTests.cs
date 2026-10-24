using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ResourceManager.ReadWrite.Native;
using ResourceManager.ReadWrite.Windows.Metrics.Configuration;

namespace Resource_Manager_APP.Tests;

public sealed class MetricMemoryCatalogShapeTests
{
    [Fact]
    public void Recorded96DeviceCatalogAllocatesActualRowsAndReplacesASmallerCatalog()
    {
        // Actual candidate .13 source/rule inputs from service-tray-c13.dmp.
        // Only numeric native rows and shape counts are retained in this fixture.
        var assembly = typeof(MetricMemoryCatalogShapeTests).Assembly;
        using var input = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames()
            .Single(static name => name.EndsWith("c13-metric-catalog.json.gz", StringComparison.Ordinal)))!;
        using var expanded = new GZipStream(input, CompressionMode.Decompress);
        var fixture = JsonSerializer.Deserialize<CatalogFixture>(expanded,
            new JsonSerializerOptions { IncludeFields = true })!;
        Assert.Equal(96, fixture.DeviceCount);
        Assert.Equal(60, fixture.NetworkInterfaceCount);
        Assert.Equal(24, fixture.Sources.Length);
        Assert.Equal(1358, fixture.Rules.Length);
        Assert.Equal(1352, fixture.Rules.Select(static row => row.MetricHandle).Distinct().Count());

        var configuration = WindowsMetricConfiguration.Default.Configuration;
        using var session = new NativeMetricSnapshotSession(in configuration);
        Assert.Equal(16384U, session.Capacity.RuleCapacity);
        Assert.Equal(13488U, session.Capacity.MetricCapacity);
        var smallHandles = fixture.Rules.Select(static row => row.MetricHandle).Distinct().Take(2).ToHashSet();
        var smallRules = fixture.Rules.Where(row => smallHandles.Contains(row.MetricHandle)).ToArray();
        Replace(smallRules, 1);
        var smallBytes = session.Capacity.ResidentByteCount;
        Replace(fixture.Rules, 2);
        Assert.True(session.Capacity.ResidentByteCount > smallBytes);
        Assert.InRange(session.Capacity.ResidentByteCount, 3_772_704UL, 4_100_000UL);
        Assert.Equal(892160UL, session.Capacity.ScratchByteCount);
        Assert.Equal(NativeMetricSnapshotStatus.Ok, session.QueryHeader(out var header));
        Assert.Equal(1358U, header.RuleCount);
        Assert.Equal(1352U, header.MetricCount);

        void Replace(NativeMetricSnapshotMetricDefinitionInput[] rules, ulong generation)
        {
            var replace = new NativeMetricSnapshotCatalogReplaceInput
            {
                AbiVersion = NativeMetricSnapshotAbi.Version,
                StructSize = (uint)Unsafe.SizeOf<NativeMetricSnapshotCatalogReplaceInput>(),
                ConfigurationGeneration = configuration.Generation,
                CatalogGeneration = generation, OperationEpoch = generation, CommandAtMilliseconds = 100,
                SourceCount = (uint)fixture.Sources.Length, RuleCount = (uint)rules.Length,
                MetricCount = (uint)rules.Select(static row => row.MetricHandle).Distinct().Count(),
                GpuInventorySourceCount = (uint)fixture.Sources.Count(static row =>
                    row.SourceRole == (uint)NativeMetricSnapshotSourceRole.GpuInventory),
                SourceIndexCapacity = session.Capacity.SourceIndexCapacity,
                RuleIndexCapacity = session.Capacity.RuleIndexCapacity,
                MetricIndexCapacity = session.Capacity.MetricIndexCapacity,
                SemanticFingerprint = NativeMetricSnapshotSession.CalculateCatalogFingerprint(fixture.Sources, rules)
            };
            Assert.Equal(NativeMetricSnapshotStatus.Ok, session.ReplaceCatalog(in replace, fixture.Sources, rules));
        }
    }

    private sealed class CatalogFixture
    {
        public int DeviceCount { get; set; }
        public int NetworkInterfaceCount { get; set; }
        public NativeMetricSnapshotSourcePolicyInput[] Sources { get; set; } = [];
        public NativeMetricSnapshotMetricDefinitionInput[] Rules { get; set; } = [];
    }
}

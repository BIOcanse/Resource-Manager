using System.Buffers.Binary;
using System.Reflection;
using System.Text.Json;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;

namespace Resource_Manager_APP.Tests;

public sealed partial class NetworkCounterInformationCollectorTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper output;
    public NetworkCounterInformationCollectorTests(Xunit.Abstractions.ITestOutputHelper output) => this.output=output;
    [Theory]
    [InlineData("windows-sdk-tables.json", 8)]
    [InlineData("windows-sdk-tables-x86.json", 4)]
    public void OfficialSdkCounterOffsetsAndIntegersVerifyBothAbis(string fixture, int pointerSize)
    {
        using var data = Fixture(fixture);
        Assert.Equal(pointerSize, data.RootElement.GetProperty("pointerSize").GetInt32());
        var api = new Api(Raw(fixture));
        var zone = new IpHelperNetworkMonitoringZone(api, () => 456789, 10000000);
        var identities = zone.ReadInterfaceIdentities().Identities;
        var fields = Fields();
        Assert.Equal(19, fields.Length);
        var selected = identities.Single(row => row.ExactIdentityKey == NetworkAddressInformationCollectorTests.IdentityA);
        var read = Assert.Single(zone.ReadInterfaces(identities, Request(selected, fields.Select(row => row.Code).ToArray())));
        ulong[] expected = [0, 1, 9007199254740993, ulong.MaxValue, 104,105,106,107,108,109,110,111,112,113,114,115,116];
        var offsets = data.RootElement.GetProperty("counterOffsets").EnumerateObject().ToArray();
        for (var i = 0; i < offsets.Length; i++)
        {
            var field = fields.Single(field => field.ValueKind == "unsigned64"
                && IpHelperNetworkMonitoringZone.CounterOffset(field.Code) == offsets[i].Value.GetInt32());
            Assert.Equal(offsets[i].Value.GetInt32(), IpHelperNetworkMonitoringZone.CounterOffset(field.Code));
            Assert.Equal(expected[i], read.Counters[field.Code]);
            Assert.Equal("unsigned64", field.ValueKind); Assert.False(field.HasLegacyAddress);
        }
        Assert.Equal(new NativeMetricSnapshotTimedCounter(0,456789,10000000), read.Rates[MetricFieldCode.NetworkRxRate]);
        Assert.Equal(new NativeMetricSnapshotTimedCounter(109,456789,10000000), read.Rates[MetricFieldCode.NetworkTxRate]);
        Assert.Single(api.Reads); Assert.Equal(selected.Luid, api.Reads[0]);
    }

    [Fact]
    public void NoDemandUnknownFieldAndFreezeDoNotReadAnyInterface()
    {
        var api = new Api(); var zone = new IpHelperNetworkMonitoringZone(api);
        Assert.Equal(0, api.DirectoryReads);
        var identities = zone.ReadInterfaceIdentities().Identities;
        Assert.Empty(zone.ReadInterfaces(identities, new Dictionary<string, IReadOnlySet<MetricFieldCode>>()));
        Assert.Empty(zone.ReadInterfaces(identities, Request(identities[0], MetricFieldCode.CpuUsage)));
        zone.ApplyMode(ResourceManagerComputeZoneMode.Freeze);
        Assert.Empty(zone.ReadInterfaces(identities, Request(identities[0], MetricFieldCode.NetworkRxOctets)));
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable, zone.ReadInterfaceIdentities().Status);
        Assert.Empty(api.Reads); Assert.Equal(1, api.DirectoryReads);
    }

    [Fact]
    public void ActualWindowsReadOnlyDirectoryAndBoundInterfaceUseTheProductionApi()
    {
        Assert.True(OperatingSystem.IsWindows());
        var zone = new IpHelperNetworkMonitoringZone();
        var directory = zone.ReadInterfaceIdentities();
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Complete, directory.Status);
        Assert.NotEmpty(directory.Identities);
        output.WriteLine($"Read-only production OS directory: {directory.Identities.Count} interfaces.");
        // The decoded MIB_IF_ROW2.Description must match the OS adapter description for the same interface GUID.
        var osDescriptions = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(static adapter => Guid.TryParse(adapter.Id, out _))
            .ToDictionary(static adapter => Guid.Parse(adapter.Id), static adapter => adapter.Description.Trim());
        var compared = directory.Identities.Where(identity => osDescriptions.ContainsKey(identity.Guid)).ToArray();
        Assert.NotEmpty(compared);
        Assert.All(compared, identity => Assert.Equal(osDescriptions[identity.Guid], identity.NameDeclared));
        output.WriteLine($"Declared descriptions matched the OS adapter list for {compared.Length} interfaces.");
        var selected = directory.Identities[0];
        var result = Assert.Single(zone.ReadInterfaces(directory.Identities,Request(selected,Fields().Select(field=>field.Code).ToArray())));
        Assert.False(result.RequiresTopologyRefresh);
        Assert.Equal(17,result.Counters.Count);Assert.Equal(2,result.Rates.Count);
        Assert.All(result.Rates.Values,counter=>Assert.True(counter.TicksPerSecond>0));
    }

    [Fact]
    public void InterfaceDescriptionFromTheSameRowIsTheDeclaredDeviceName()
    {
        const string description = "Motorcomm YT6801 Gigabit Ethernet Adapter";
        var api = new Api();
        var row = api.Table!.AsSpan(ResourceManager.ReadWrite.Windows.Devices.WindowsNetworkInterfaceTableDecoder.TableOffset,
            ResourceManager.ReadWrite.Windows.Devices.WindowsNetworkInterfaceTableDecoder.InterfaceRowSize);
        var luid = BinaryPrimitives.ReadUInt64LittleEndian(row);
        System.Text.Encoding.Unicode.GetBytes(description).CopyTo(
            row[ResourceManager.ReadWrite.Windows.Devices.WindowsNetworkInterfaceTableDecoder.InterfaceDescriptionOffset..]);
        var identities = new IpHelperNetworkMonitoringZone(api).ReadInterfaceIdentities().Identities;
        var named = Assert.Single(identities, identity => identity.Luid == luid);
        Assert.Equal(description, named.NameDeclared);
        // The SDK fixture's other row declares no description; it stays null instead of a synthesized name.
        Assert.Null(Assert.Single(identities, identity => identity.Luid != luid).NameDeclared);

        var catalog = NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(HostManagerTestPlanFactory.CreatePlan().MetricSnapshot,
            [], [], [], NativeMetricSnapshotCatalogHandleMap.Empty, networkInterfaces: identities);
        var bindings = catalog.RuleBindings.Values.Where(binding => binding.DeviceIdentity == named.ExactIdentityKey).ToArray();
        Assert.NotEmpty(bindings);
        Assert.All(bindings, binding =>
        {
            Assert.Equal("network_interface", binding.Device.Kind);
            Assert.Equal(description, binding.Device.DisplayName);
        });
        Assert.All(catalog.RuleBindings.Values.Where(binding => binding.Device.Kind == "network_interface"
            && binding.DeviceIdentity != named.ExactIdentityKey), binding => Assert.Null(binding.Device.DisplayName));
    }

    [Theory]
    [InlineData(1U,true)] [InlineData(3U,false)]
    public void TimedCounterRequiresFloatOutput(uint kind,bool accepted)
    {
        var template=HostManagerTestPlanFactory.CreatePlan().MetricSnapshot.HotPublish.RuleTemplates.First(row=>row.MetricKind==15);
        Assert.Equal(accepted,(template with { ValueKind=kind }).IsPublished);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void FailureOrIdentityMismatchCannotBorrowAnotherInterfacesCounters(int failure)
    {
        var api = new Api(); var zone = new IpHelperNetworkMonitoringZone(api);
        var identities = zone.ReadInterfaceIdentities().Identities;
        var selected = identities.Single(row => row.ExactIdentityKey == NetworkAddressInformationCollectorTests.IdentityA);
        api.Transform = row => {
            if (failure == 0) return null;
            if (failure == 1) throw new IOException("synthetic API error");
            if (failure == 2) return row[..^1];
            if (failure == 3) BinaryPrimitives.WriteUInt64LittleEndian(row, selected.Luid+1);
            if (failure == 4) BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(8), selected.Index+1);
            if (failure == 5) Guid.NewGuid().TryWriteBytes(row.AsSpan(12,16));
            return row;
        };
        var result = Assert.Single(zone.ReadInterfaces(identities, Request(selected, MetricFieldCode.NetworkRxOctets, MetricFieldCode.NetworkRxRate)));
        Assert.Empty(result.Counters); Assert.Empty(result.Rates);
        Assert.Equal(failure >= 3, result.RequiresTopologyRefresh);
        Assert.Equal(new[] { selected.Luid }, api.Reads);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void InvalidDirectoryIsUnavailableWithoutPartialIdentityPublication(int failure)
    {
        var api = new Api();
        if (failure == 0) api.Table = null;
        if (failure == 1) api.Table = api.Table![..^1];
        if (failure == 2) BinaryPrimitives.WriteUInt32LittleEndian(api.Table!, 1025);
        if (failure == 3) Array.Clear(api.Table!,8,8);
        if (failure == 4) Array.Copy(api.Table!,8,api.Table!,1360,28);
        var result = new IpHelperNetworkMonitoringZone(api).ReadInterfaceIdentities();
        Assert.Equal(NativeMetricSnapshotCatalogTopologyStatus.Unavailable,result.Status);
        Assert.Empty(result.Identities); Assert.Empty(api.Reads);
    }

    [Fact]
    public void RawUnsignedCounterPublishesWithoutDoubleNarrowingAndFailureIsNull()
    {
        using var h = new Workspace();
        Assert.Equal(MetricInteger.Parse("18446744073709551615"), h.PublishUnsigned(ulong.MaxValue).IntegerValue);
        Assert.Equal(MetricInteger.Parse("9007199254740993"), h.PublishUnsigned(9007199254740993).IntegerValue);
        Assert.Equal(MetricInteger.Parse("0"), h.PublishUnsigned(0).IntegerValue);
        Assert.Null(h.PublishUnsigned(null).IntegerValue);
        Assert.Equal(MetricInteger.Parse("5"), h.PublishUnsigned(5).IntegerValue);
    }

    [Fact]
    public void RateUsesActualMonotonicIntervalAndExactIntegerDeltaBeforeConversion()
    {
        using var h = new Workspace();
        Assert.Null(h.Publish(new(9007199254740993,1000000,10000000)));
        Assert.Equal(800, h.Publish(new(9007199254741193,3500000,10000000)));
        Assert.Equal(250UL, h.ReadNative().SampleDurationMilliseconds);
        Assert.Equal(0, h.Publish(new(9007199254741193,6000000,10000000)));
        Assert.Equal("B/s", h.ReadField().Unit);
    }

    [Theory]
    [InlineData(90UL,200UL,1000UL)] [InlineData(200UL,90UL,1000UL)]
    [InlineData(200UL,100UL,1000UL)] [InlineData(200UL,200UL,2000UL)]
    [InlineData(200UL,101UL,1000000UL)]
    public void InvalidIntervalsAreEmptyAndCurrentEndpointBecomesNextBaseline(ulong counter,ulong ticks,ulong frequency)
    {
        using var h = new Workspace(); h.Publish(new(100,100,1000));
        Assert.Null(h.Publish(new(counter,ticks,frequency)));
        Assert.Equal(150, h.Publish(new(counter+150,ticks+frequency,frequency)));
    }

    [Fact]
    public void FailureCapabilityIncarnationFreezeAndCancellationStartNewIntervals()
    {
        using var h = new Workspace(); h.Publish(new(0,0,1000)); Assert.Equal(150,h.Publish(new(150,1000,1000)));
        Assert.Null(h.Publish(null)); Assert.Null(h.Publish(new(200,2000,1000))); Assert.Equal(150,h.Publish(new(350,3000,1000)));
        Assert.Null(h.Publish(new(500,4000,1000),capability:2)); Assert.Equal(150,h.Publish(new(650,5000,1000),capability:2));
        Assert.Null(h.Publish(new(800,6000,1000),capability:2,incarnation:2)); Assert.Equal(150,h.Publish(new(950,7000,1000),capability:2,incarnation:2));
        h.Sync([]); Assert.Equal(150,h.ReadField().NumericValue);
        Assert.Null(h.Publish(new(1100,8000,1000),capability:2,incarnation:2)); Assert.Equal(150,h.Publish(new(1250,9000,1000),capability:2,incarnation:2));
        var frozen=h.Native.PlanCollection([h.Handle],h.Modes(2,NativeMetricSnapshotZoneMode.Freeze),false,h.NextTime());
        Assert.Empty(frozen.Sources); Assert.Equal(150,h.ReadField().NumericValue);
        Assert.Null(h.Publish(new(1400,10000,1000),capability:2,incarnation:2));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void RejectedObservationCannotAdvanceCommittedBaseline(int corruption)
    {
        using var h = new Workspace(); h.Publish(new(0,0,1000)); Assert.Equal(150,h.Publish(new(150,1000,1000)));
        Assert.Throws<InvalidOperationException>(()=>h.Publish(new(900,2000,1000),corruption:corruption));
        Assert.Equal(150,h.ReadField().NumericValue);
        Assert.Equal(150,h.Publish(new(450,3000,1000)));
    }

    [Fact]
    public void InterfacesAndReceiveSendRatesHaveIndependentBaselines()
    {
        using var h = new Workspace();
        h.Publish(new(0,0,1000)); h.Publish(new(10000,0,1000),second:true); h.Publish(new(1000,0,1000),send:true);
        Assert.Equal(150,h.Publish(new(150,1000,1000)));
        Assert.Equal(50,h.Publish(new(10050,1000,1000),second:true));
        Assert.Equal(700,h.Publish(new(1700,1000,1000),send:true));
    }

    [Fact]
    public void BindingChangesRebuildBaselineWhileReorderedDirectoryIsStable()
    {
        using var h = new Workspace();
        var configuration=h.Native.CatalogConfiguration; var map=h.Catalog.HandleMap;
        var reordered=NativeMetricSnapshotCatalogProjection.Create(configuration,[],[],[],map,networkInterfaces:h.Identities.Reverse().ToArray());
        Assert.Equal(h.Catalog.CatalogIdentitySha256,reordered.CatalogIdentitySha256);
        var changed=NativeMetricSnapshotCatalogProjection.Create(configuration,[],[],[],map,networkInterfaces:[h.Identities[0] with { Luid=99 },h.Identities[1]]);
        Assert.NotEqual(h.Catalog.CatalogIdentitySha256,changed.CatalogIdentitySha256);
        Assert.Equal(h.Catalog.MetricHandles,changed.MetricHandles);
        h.Publish(new(0,0,1000)); Assert.Equal(150,h.Publish(new(150,1000,1000)));
        Assert.False(HostManagerMetricSnapshotOwner.CanRetainCatalog(h.Catalog,changed));
        using var replacement=new Workspace(identities:[h.Identities[0] with { Luid=99 },h.Identities[1]]);
        Assert.Null(replacement.Publish(new(500,2000,1000)));
    }

    [Fact]
    public void ActualConfiguredWorkspaceFitsResidentBudgetAndMaximumInterfaceExpansion()
    {
        var compiled=HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var native=NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,Path.Combine(Path.GetTempPath(),$"network-capacity-{Guid.NewGuid():N}.bin"));
        Assert.True(native.Capacity.ResidentByteCount <= 64UL*1024*1024);
        output.WriteLine($"Native resident bytes: {native.Capacity.ResidentByteCount}; budget: {compiled.Recreate.Capacity.ResidentByteBudget}.");
        Assert.Equal(13_504U,native.Capacity.MetricCapacity);
        var ids=Enumerable.Range(0,128).Select(i=>new NetworkInterfaceCatalogIdentity(i,new Guid(i+1,1,2,new byte[8]),(ulong)i+1,(uint)i+1)).ToArray();
        var catalog=NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,[],[],[],native.CreateCatalogHandleMap(),networkInterfaces:ids);
        native.ReplaceCatalog(catalog,DateTimeOffset.UtcNow);
        Assert.Equal(2432,catalog.RuleBindings.Values.Count(row=>row.SourceId=="windows.network-interface"));
        Assert.Throws<InvalidOperationException>(()=>NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,[],[],[],catalog.HandleMap,networkInterfaces:ids.Append(new(128,Guid.NewGuid(),129,129)).ToArray()));
    }

    [Fact]
    public void WorstTemplateExpansionRespectsCurrentCapacityAndNoSilentTruncation()
    {
        var compiled=HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        var ids=new HashSet<string>(StringComparer.Ordinal);ulong rules=0;
        foreach(var row in compiled.HotPublish.RuleTemplates)
        {
            var count=row.ScopeExpansion is 1 or 2 or 3 or 7 or 15?1U:row.ScopeExpansion==4?Math.Min(row.MaximumInstanceCount,compiled.Recreate.Capacity.MaximumGpuAdapterCount):row.MaximumInstanceCount;
            rules+=count;
            var placeholder=row.ScopeExpansion switch { 4=>"{gpu}",5=>"{disk}",6=>"{fan}",8=>"{battery}",9=>"{numa}",10=>"{core}",11=>"{package}",12=>"{thread}",13=>"{die}",14=>"{interface}",15=>"{platform}",16=>"{npu}",17=>"{engine}",_=>"" };
            for(uint i=0;i<count;i++)ids.Add(placeholder.Length==0?row.MetricIdTemplate:row.MetricIdTemplate.Replace(placeholder,i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        var diskRules=compiled.HotPublish.RuleTemplates.Where(row=>row.SourceId=="windows.disk-performance").ToArray();
        Assert.Equal(7,diskRules.Length);
        Assert.All(diskRules,row=>{Assert.Equal(5U,row.ScopeExpansion);Assert.Equal(32U,row.MaximumInstanceCount);});
        var addedDiskExpansion=diskRules.Aggregate(0UL,(total,row)=>total+row.MaximumInstanceCount);
        Assert.Equal(224UL,addedDiskExpansion);
        var performanceState=Assert.Single(compiled.HotPublish.RuleTemplates,row=>row.TemplateId=="gpu_nvidia_performance_state.optional");
        var addedPerformanceStates=Math.Min(performanceState.MaximumInstanceCount,compiled.Recreate.Capacity.MaximumGpuAdapterCount);
        Assert.Equal(16U,addedPerformanceStates);
        var fanRules=compiled.HotPublish.RuleTemplates.Where(row=>row.TemplateId is "fan.nvmlSpeedPercentOfMax" or "fan.nvmlSpeedRpm").ToArray();
        Assert.Equal(2,fanRules.Length);
        var addedFanInstances=fanRules.Aggregate(0UL,(total,row)=>total+row.MaximumInstanceCount);
        Assert.Equal(512UL,addedFanInstances);
        var platformRules=compiled.HotPublish.RuleTemplates.Where(row=>row.ScopeExpansion==15).ToArray();
        Assert.Equal(2,platformRules.Length);
        Assert.All(platformRules,row=>Assert.Equal(1U,row.MaximumInstanceCount));
        var npuRules=compiled.HotPublish.RuleTemplates.Where(row=>row.SourceId=="windows.npu-memory").ToArray();
        Assert.Equal(4,npuRules.Length);
        Assert.All(npuRules,row=>{Assert.Equal(16U,row.ScopeExpansion);Assert.Equal(64U,row.MaximumInstanceCount);Assert.Contains("{npu}",row.MetricIdTemplate,StringComparison.Ordinal);});
        var npuIds=new HashSet<string>(StringComparer.Ordinal);
        foreach(var row in npuRules)
            for(uint i=0;i<row.MaximumInstanceCount;i++)
                Assert.True(npuIds.Add(row.MetricIdTemplate.Replace("{npu}",i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.Equal(256,npuIds.Count);
        Assert.True(npuIds.IsSubsetOf(ids));
        Assert.Equal(24,compiled.HotPublish.SourcePolicies.Length);
        var legacyMetricBoundary=11262+(int)addedDiskExpansion+(int)addedPerformanceStates+(int)addedFanInstances+platformRules.Length+npuIds.Count+16+512;
        var legacyRuleBoundary=11633UL+addedDiskExpansion+addedPerformanceStates+addedFanInstances+(ulong)platformRules.Length+(ulong)npuIds.Count+16UL+512UL;
        Assert.Equal(12800,legacyMetricBoundary);Assert.Equal(13171UL,legacyRuleBoundary);
        var numaCapacity=Assert.Single(compiled.HotPublish.RuleTemplates,row=>row.TemplateId=="memory_all_numa_node_capacity_mib.optional");
        Assert.Equal(64U,numaCapacity.MaximumInstanceCount);
        var effectiveFrequency=Assert.Single(compiled.HotPublish.RuleTemplates,row=>row.TemplateId=="cpu_intel_effective_frequency_mhz.optional");
        Assert.Equal(512U,effectiveFrequency.MaximumInstanceCount);
        var npuDeviceBusy=Assert.Single(compiled.HotPublish.RuleTemplates,row=>row.TemplateId=="npu_all_engine_busy_percent.optional");
        Assert.Equal(64U,npuDeviceBusy.MaximumInstanceCount);
        var amdNpuRules=compiled.HotPublish.RuleTemplates.Where(row=>row.TemplateId is "gpu_amd_npu_activity_percent.optional" or "gpu_amd_npu_frequency_mhz.optional").ToArray();
        Assert.Equal(2,amdNpuRules.Length);Assert.All(amdNpuRules,row=>{Assert.Equal(4U,row.ScopeExpansion);Assert.Equal(32U,row.MaximumInstanceCount);});
        var addedAmdNpuInstances=amdNpuRules.Aggregate(0UL,(total,row)=>total+Math.Min(row.MaximumInstanceCount,compiled.Recreate.Capacity.MaximumGpuAdapterCount));
        Assert.Equal(32UL,addedAmdNpuInstances);
        var addedNewRulesAndMetrics=(ulong)numaCapacity.MaximumInstanceCount+effectiveFrequency.MaximumInstanceCount+npuDeviceBusy.MaximumInstanceCount+addedAmdNpuInstances;
        Assert.Equal(672UL,addedNewRulesAndMetrics);
        var ccd=Assert.Single(compiled.HotPublish.RuleTemplates,row=>row.SourceId=="amd.smn");
        Assert.Equal(16U,ccd.MaximumInstanceCount);
        addedNewRulesAndMetrics+=ccd.MaximumInstanceCount;
        var sharedMemory=Assert.Single(compiled.HotPublish.RuleTemplates,row=>row.TemplateId=="gpu.sharedMemoryUsed.pdh");
        addedNewRulesAndMetrics+=Math.Min(sharedMemory.MaximumInstanceCount,compiled.Recreate.Capacity.MaximumGpuAdapterCount);
        Assert.Equal(13504UL,(ulong)legacyMetricBoundary+addedNewRulesAndMetrics);
        Assert.Equal(13875UL,legacyRuleBoundary+addedNewRulesAndMetrics);
        Assert.Equal(13504,ids.Count);Assert.Equal(13875UL,rules);
        Assert.True(HostManagerTestPlanFactory.CreatePlan(root=>root["host_recreate"]!["metric_snapshot"]!["capacity"]!["maximum_metric_count"]=ids.Count).IsPublished);
        Assert.Throws<InvalidDataException>(()=>HostManagerTestPlanFactory.CreatePlan(root=>root["host_recreate"]!["metric_snapshot"]!["capacity"]!["maximum_metric_count"]=ids.Count-1));
    }

    [Fact]
    public void RestartClearsCurrentRateAndCounterBaseline()
    {
        var path=Path.Combine(Path.GetTempPath(),$"network-rate-restore-{Guid.NewGuid():N}.bin");
        try
        {
            using(var prior=new Workspace(path)) { prior.Publish(new(0,0,1000)); Assert.Equal(150,prior.Publish(new(150,1000,1000))); prior.Native.PersistRecoveryCheckpointIfAdvanced(prior.NextTime()); }
            using var restored=new Workspace(path,100); Assert.Null(restored.ReadField().NumericValue);
            Assert.Null(restored.Publish(new(10000,1000,1000))); Assert.Equal(50,restored.Publish(new(10050,2000,1000)));
        }
        finally { File.Delete(path); }
    }

    private static MetricFieldDefinition[] Fields()=>MetricFieldRegistry.Definitions.Where(static row=>row.RequestTemplate.StartsWith("interface.{interface}.",StringComparison.Ordinal)).ToArray();
    internal static HardwareMetricSnapshot CommittedSnapshot()
    {
        using var h = new Workspace();
        h.PublishUnsigned(ulong.MaxValue); h.Publish(new(0,0,1000)); h.Publish(new(200,1000,1000));
        return NativeMetricSnapshotCommittedProjection.Create(frame: h.Native.ReadCommitted(), workspaceIdentity: 1);
    }
    internal static IReadOnlyDictionary<string,IReadOnlySet<MetricFieldCode>> Request(NetworkInterfaceCatalogIdentity device,params MetricFieldCode[] fields)
        =>new Dictionary<string,IReadOnlySet<MetricFieldCode>> { [device.ExactIdentityKey]=fields.ToHashSet() };
    private static JsonDocument Fixture(string fixture)
        =>JsonDocument.Parse(Assembly.GetExecutingAssembly().GetManifestResourceStream("Resource_Manager_APP.Tests.Fixtures.Network."+fixture)!);
    private static byte[] Raw(string fixture="windows-sdk-tables.json") { using var json=Fixture(fixture);return Convert.FromHexString(json.RootElement.GetProperty("interfaceTableHex").GetString()!); }

    internal sealed class Api(byte[]? table=null) : IWindowsNetworkInterfaceCounterApi
    {
        internal byte[]? Table=table??Raw(); internal int DirectoryReads;
        internal readonly List<ulong> Reads=[];
        internal Func<byte[],byte[]?>? Transform;
        public byte[]? ReadInterfaces() { DirectoryReads++; return Table; }
        public byte[]? ReadInterface(ulong luid)
        {
            Reads.Add(luid);var table=Table;
            if(table is null)return null;
            for(var offset=8;offset+1352<=table.Length;offset+=1352)
                if(BinaryPrimitives.ReadUInt64LittleEndian(table.AsSpan(offset))==luid)
                { var row=table.AsSpan(offset,1352).ToArray();return Transform is null?row:Transform(row); }
            return null;
        }
    }

    private sealed class Workspace : IDisposable
    {
        private readonly DateTimeOffset start=DateTimeOffset.UtcNow;
        private ulong sequence;
        internal NativeMetricSnapshotWorkspace Native { get; }
        internal NativeMetricSnapshotCatalogProjection Catalog { get; }
        internal IReadOnlyList<NetworkInterfaceCatalogIdentity> Identities { get; }
        internal ulong Handle=>Find(Identities[0],MetricFieldCode.NetworkRxRate);
        internal Workspace(string? path=null,ulong sequence=0,IReadOnlyList<NetworkInterfaceCatalogIdentity>? identities=null)
        {
            this.sequence=sequence;Identities=identities??new IpHelperNetworkMonitoringZone(new Api()).ReadInterfaceIdentities().Identities;
            var compiled=HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
            Native=NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,path??Path.Combine(Path.GetTempPath(),$"network-counter-{Guid.NewGuid():N}.bin"));
            Catalog=NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,[],[],[],Native.CreateCatalogHandleMap(),networkInterfaces:Identities);
            Native.ReplaceCatalog(Catalog,start);
        }
        private ulong Find(NetworkInterfaceCatalogIdentity device,MetricFieldCode field)=>Catalog.MetricBindings.Values.Single(b=>b.DeviceIdentity==device.ExactIdentityKey&&b.FieldCode==field).MetricHandle;
        internal DateTimeOffset NextTime()=>start.AddMilliseconds(++sequence*100);
        internal NativeMetricSnapshotSourceRuntimeMode[] Modes(ulong capability=1,NativeMetricSnapshotZoneMode mode=NativeMetricSnapshotZoneMode.Normal)
            =>Catalog.Sources.Select(s=>new NativeMetricSnapshotSourceRuntimeMode(s.SourceHandle,capability,mode,NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        internal void Sync(ulong[] active)=>Native.SynchronizeCounterDemand(active,NextTime());
        internal double? Publish(NativeMetricSnapshotTimedCounter? counter,ulong capability=1,ulong incarnation=1,int corruption=-1,bool second=false,bool send=false)
        {
            var device=Identities[second?1:0];var field=send?MetricFieldCode.NetworkTxRate:MetricFieldCode.NetworkRxRate;
            Capture(device,field,null,counter,capability,incarnation,corruption);
            return ReadField(device,field).NumericValue;
        }
        internal DeviceMetricFieldValue PublishUnsigned(ulong? value) { Capture(Identities[0],MetricFieldCode.NetworkRxErrors,value,null,1,1,-1);return ReadField(Identities[0],MetricFieldCode.NetworkRxErrors); }
        private void Capture(NetworkInterfaceCatalogIdentity device,MetricFieldCode field,ulong? value,NativeMetricSnapshotTimedCounter? counter,ulong capability,ulong incarnation,int corruption)
        {
            var at=NextTime();var plan=Native.PlanCollection([Find(device,field)],Modes(capability),false,at);var source=Assert.Single(plan.Sources);
            var read=new NetworkInterfaceMetricRead(device.ExactIdentityKey,value is {} number?new Dictionary<MetricFieldCode,ulong> { [field]=number }:[],counter is {} pair?new Dictionary<MetricFieldCode,NativeMetricSnapshotTimedCounter> { [field]=pair }:[]);
            var completion=WindowsHardwareMetricSampler.CreateNetworkInterfaceCompletion(plan,source,new(source.SourceHandle,incarnation,sequence),at,[read]);
            if(corruption>=0) { var observation=completion.Observations[0]; if(corruption==0)observation.Flags=0;if(corruption==1)observation.CounterTicksPerSecond=0;if(corruption==2)observation.ByteCount=1;if(corruption==3)observation.SampleDurationMilliseconds=1;completion=completion with { Observations=completion.Observations.SetItem(0,observation) }; }
            Native.CompleteSource(plan,completion,at);
        }
        internal DeviceMetricFieldValue ReadField()=>ReadField(Identities[0],MetricFieldCode.NetworkRxRate);
        private DeviceMetricFieldValue ReadField(NetworkInterfaceCatalogIdentity device,MetricFieldCode field)
            =>MetricDeviceFields.Read(NativeMetricSnapshotCommittedProjection.Create(frame: Native.ReadCommitted(), workspaceIdentity: 1),device.ExactIdentityKey).Single().Fields.Single(f=>f.Name==MetricFieldRegistry.Definitions.Single(definition=>definition.Code==field).Name);
        internal NativeMetricSnapshotMetricOutput ReadNative()=>Native.ReadCommitted().Metrics.Single(m=>m.MetricHandle==Handle);
        public void Dispose()=>Native.Dispose();
    }
}

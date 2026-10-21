using System.Runtime.InteropServices;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Application.Metrics;
using ResourceManager.App.Endpoints;
using ResourceManager.App.Infrastructure.Monitoring;
using ResourceManager.App.Infrastructure.NativeCore;
using ResourceManager.App.Infrastructure.RuntimeSpecialization;
using Xunit.Abstractions;

namespace Resource_Manager_APP.Tests;

public sealed partial class AdlxMetricsCollectorTests(ITestOutputHelper output)
{
    private const int AllFlags = 1 | 32 | 4096;
    private static readonly string[] Names = ["gpu_amd_activity_percent", "gpu_amd_voltage_reported_volts", "gpu_amd_sample_timestamp_reported_ms"];
    private static readonly HashSet<MetricFieldCode> Codes = [MetricFieldCode.AdlxActivity, MetricFieldCode.AdlxVoltage, MetricFieldCode.AdlxSampleTimestamp];
    private const string PnpA = @"PCI\VEN_1002&DEV_744C\A";
    private const string PnpB = @"PCI\VEN_1002&DEV_744C\B";
    private static AmdAdlxReadRequest Request(params int[] indexes) => new(indexes.ToHashSet(), true, false, false, false,
        false, false, false, false, false, false, true, false, true);

    [Fact]
    public async Task MetadataFirstThenOnlySelectedDeviceAndFields()
    {
        var native = new Bridge { Metadata = [Row(10,PnpA),Row(20,PnpB)], Values = [Row(20,PnpB,AllFlags)] };
        var request = Request(3) with { NativeFlagsByDisplayIndex = new Dictionary<int,int> { [3] = 32 } };
        var result = await new AmdAdlxBridgeReader(native).ReadAsync(request, Inventory(), CancellationToken.None);
        Assert.Equal(1,native.MetadataReads); Assert.Equal(1,native.SelectedReads);
        var selection = Assert.Single(native.Selections);
        Assert.Equal(20,selection.UniqueId); Assert.Equal(32,selection.RequestedFlags);
        Assert.Equal(3,Assert.Single(result).Index);
    }

    [Fact]
    public async Task NoDemandOrPreCancelledNeverCallsNative()
    {
        var native = new Bridge(); var reader = new AmdAdlxBridgeReader(native);
        Assert.Empty(await reader.ReadAsync(Request() with { IncludeUsage=false,IncludeVoltage=false,IncludeSampleTimestamp=false },Inventory(),CancellationToken.None));
        Assert.Equal(0,native.AbiReads);
        await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadAsync(Request(9),Inventory(),new(true)));
        Assert.Equal(0,native.AbiReads);
    }

    [Fact]
    public async Task CancellationAfterMetadataPreventsMetricRead()
    {
        using var cancel = new CancellationTokenSource();
        var native = new Bridge { Metadata=[Row(10,PnpA)], AfterMetadata=cancel.Cancel };
        await Assert.ThrowsAsync<OperationCanceledException>(() => new AmdAdlxBridgeReader(native).ReadAsync(Request(9),Inventory(),cancel.Token));
        Assert.Equal(1,native.MetadataReads); Assert.Equal(0,native.SelectedReads);
    }

    [Theory]
    [InlineData(4U,840,520)] [InlineData(5U,824,520)] [InlineData(5U,840,516)]
    public async Task IncompatibleAbiNeverReads(uint abi,int size,int selectionSize)
    {
        var native = new Bridge { Abi=abi,Size=size,SelectionSize=selectionSize };
        Assert.Empty(await new AmdAdlxBridgeReader(native).ReadAsync(Request(9),Inventory(),CancellationToken.None));
        Assert.Equal(0,native.MetadataReads); Assert.Equal(0,native.SelectedReads);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task MissingNativeRuntimeOrExportPublishesNoValues(int missing)
    {
        var native=new Bridge { Failure=missing switch { 0=>new DllNotFoundException(),1=>new EntryPointNotFoundException(),_=>new BadImageFormatException() } };
        Assert.Empty(await new AmdAdlxBridgeReader(native).ReadAsync(Request(9),Inventory(),CancellationToken.None));
        Assert.Equal(0,native.MetadataReads); Assert.Equal(0,native.SelectedReads);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(33)]
    public async Task FailedOrOverCapacityMetadataDoesNotReadMetrics(int count)
    {
        var native=new Bridge { Metadata=[Row(10,PnpA)], MetadataCount=count };
        Assert.Empty(await new AmdAdlxBridgeReader(native).ReadAsync(Request(9),Inventory(),CancellationToken.None));
        Assert.Equal(0,native.SelectedReads);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public async Task UnprovenOrAmbiguousIdentityDoesNotReadMetrics(int defect)
    {
        var row = Row(10,PnpA); var inventory = Inventory();
        NativeAdlxGpuMetrics[] metadata = [row];
        switch (defect)
        {
            case 0: row.IdentityValid=0; metadata=[row]; break;
            case 1: row.StructSize=824; metadata=[row]; break;
            case 2: row.AbiVersion=4; metadata=[row]; break;
            case 3: metadata=[row,Row(10,PnpB)]; break;
            case 4: metadata=[row,Row(11,PnpA)]; break;
            case 5: inventory=inventory with { SkippedCount=1 }; break;
            case 6: inventory=Inventory(0x10DE); break;
        }
        var native = new Bridge { Metadata=metadata };
        Assert.Empty(await new AmdAdlxBridgeReader(native).ReadAsync(Request(9),inventory,CancellationToken.None));
        Assert.Equal(0,native.SelectedReads);
    }

    [Fact]
    public async Task ChangedIdentityOrDuplicateSelectedRowsPublishesNoDevice()
    {
        var native = new Bridge { Metadata=[Row(10,PnpA)], Values=[Row(10,PnpB,AllFlags)] };
        var reader = new AmdAdlxBridgeReader(native);
        Assert.Empty(await reader.ReadAsync(Request(9),Inventory(),CancellationToken.None));
        native.Values=[Row(11,PnpA,AllFlags)];
        Assert.Empty(await reader.ReadAsync(Request(9),Inventory(),CancellationToken.None));
        native.Values=[Row(10,PnpA,AllFlags),Row(10,PnpA,AllFlags)];
        Assert.Empty(await reader.ReadAsync(Request(9),Inventory(),CancellationToken.None));
    }

    [Fact]
    public async Task FailedIdentityDefaultIdDoesNotConflictWithSuccessfulZeroId()
    {
        var failed=Row(0,PnpB); failed.IdentityValid=0;
        var native=new Bridge { Metadata=[Row(0,PnpA),failed],Values=[Row(0,PnpA,AllFlags)] };
        Assert.Equal(9,Assert.Single(await new AmdAdlxBridgeReader(native).ReadAsync(Request(9),Inventory(),CancellationToken.None)).Index);
        Assert.Equal(0,Assert.Single(native.Selections).UniqueId);
    }

    [Theory]
    [InlineData(0d,0,0L,true,true,true)]
    [InlineData(100d,1250,9007199254740993L,true,true,true)]
    [InlineData(-1d,-1,-1L,false,false,false)]
    [InlineData(100.1d,0,0L,false,true,true)]
    [InlineData(double.NaN,1100,long.MaxValue,false,true,true)]
    [InlineData(double.PositiveInfinity,1100,0L,false,true,true)]
    public void ValuesRespectSourceUnitsAndIntegerPrecision(double usage,int mv,long timestamp,bool activity,bool voltage,bool time)
    {
        var row=Row(10,PnpA,AllFlags); row.UsagePercent=usage; row.VoltageMillivolts=mv; row.SampleTimestampMilliseconds=timestamp;
        var gpu=Assert.IsType<GpuMetrics>(AmdAdlxBridgeReader.ToGpuMetrics(row,Request(9),Inventory()));
        Assert.Equal(activity,gpu.IsUsageAvailable);
        Assert.Equal(voltage ? mv/1000d : (double?)null,gpu.Sensors.CoreVoltageVolts);
        Assert.Equal(time,gpu.AdditionalIntegerReadings.ContainsKey(MetricFieldCode.AdlxSampleTimestamp));
        if(time) Assert.Equal((ulong)timestamp,gpu.AdditionalIntegerReadings[MetricFieldCode.AdlxSampleTimestamp]);
        row.AvailableFlags=0;
        gpu=Assert.IsType<GpuMetrics>(AmdAdlxBridgeReader.ToGpuMetrics(row,Request(9),Inventory()));
        Assert.False(gpu.IsUsageAvailable); Assert.Null(gpu.Sensors.CoreVoltageVolts); Assert.Empty(gpu.AdditionalIntegerReadings);
    }

    [Fact]
    public void OriginalWorkspaceKeepsThreeFieldsPerDeviceAndClearsIndividualFailure()
    {
        var compiled=HostManagerTestPlanFactory.CreatePlan().MetricSnapshot;
        using var workspace=NativeMetricSnapshotHostConfiguration.CreateWorkspace(compiled,Path.Combine(Path.GetTempPath(),$"adlx-native-{Guid.NewGuid():N}.bin"));
        Assert.InRange(workspace.Capacity.ResidentByteCount,1UL,64UL*1024*1024);
        output.WriteLine($"Native workspace resident bytes: {workspace.Capacity.ResidentByteCount}");
        var catalog=NativeMetricSnapshotHostConfiguration.CreateCatalogProjection(compiled,
            [new(9,"AMD A","gpu-a",0xA1,1,2,true),new(3,"AMD B","gpu-b",0xB1,1,2,true),new(7,"NVIDIA","gpu-nvidia",0xC1,1,1,true)],[],[],workspace.CreateCatalogHandleMap());
        var bindings=catalog.MetricBindings.Values.Where(binding=>Codes.Contains(binding.FieldCode)).ToArray();
        Assert.Equal(6,bindings.Length); Assert.All(bindings,binding=>Assert.Equal("amd.adlx",binding.SourceId));
        var a=bindings.Where(binding=>binding.DisplayIndex==9).ToArray();
        var b=bindings.Where(binding=>binding.DisplayIndex==3).ToArray();
        var request=WindowsHardwareMetricSampler.CreateAdlxReadRequest([a.Single(binding=>binding.FieldCode==MetricFieldCode.AdlxActivity),
            b.Single(binding=>binding.FieldCode==MetricFieldCode.AdlxVoltage),b.Single(binding=>binding.FieldCode==MetricFieldCode.AdlxSampleTimestamp)]);
        Assert.Equal(1,request.NativeFlagsByDisplayIndex![9]); Assert.Equal(32|4096,request.NativeFlagsByDisplayIndex[3]);
        var now=DateTimeOffset.UtcNow; workspace.ReplaceCatalog(catalog,now);
        var modes=catalog.Sources.Select(source=>new NativeMetricSnapshotSourceRuntimeMode(source.SourceHandle,1,NativeMetricSnapshotZoneMode.Normal,NativeMetricSnapshotSourceAvailability.Available)).ToArray();
        for(var phase=0;phase<5;phase++)
        {
            var at=now.AddSeconds(phase+1);
            var plan=workspace.PlanCollection(bindings.Select(binding=>binding.MetricHandle).ToArray(),modes,false,at);
            var source=Assert.Single(plan.Sources);
            var row=Row(10,PnpA,phase==1 ? 1|4096 : AllFlags); row.VoltageMillivolts=1200; row.SampleTimestampMilliseconds=9007199254740993;
            var other=Row(20,PnpB,AllFlags); other.UsagePercent=75; other.VoltageMillivolts=950; other.SampleTimestampMilliseconds=0;
            workspace.CompleteSource(plan,WindowsHardwareMetricSampler.CreateAdlxCompletion(plan,source,new(source.SourceHandle,1,(ulong)phase+1),at,
                phase==3 ? [] : [AmdAdlxBridgeReader.ToGpuMetrics(row,Request(9),Inventory())!,AmdAdlxBridgeReader.ToGpuMetrics(other,Request(3),Inventory())!]),at);
            var snapshot=NativeMetricSnapshotCommittedProjection.Create(workspace.ReadCommitted(),"CPU","RAM",1);
            var first=Fields(snapshot,a[0].DeviceIdentity);
            if(phase==3)
            {
                Assert.All(first.Values.Concat(Fields(snapshot,b[0].DeviceIdentity).Values),field=>
                    { Assert.Null(field.NumericValue); Assert.Null(field.IntegerValue); Assert.Equal(SamplingObservationStatus.Unavailable,field.Status); });
                continue;
            }
            Assert.Equal(0d,first[Names[0]].NumericValue); Assert.Equal(phase==1 ? null : 1.2d,first[Names[1]].NumericValue);
            Assert.Equal("9007199254740993",first[Names[2]].IntegerValue); Assert.Null(first[Names[2]].NumericValue);
            Assert.Contains("\"integerValue\":\"9007199254740993\"",JsonSerializer.Serialize(first[Names[2]],new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            Assert.Equal("unsigned64",first[Names[2]].ValueKind);
            var second=Fields(snapshot,b[0].DeviceIdentity); Assert.Equal(75d,second[Names[0]].NumericValue); Assert.Equal(.95d,second[Names[1]].NumericValue);
            Assert.Equal("0",second[Names[2]].IntegerValue);
            Assert.Equal(3,MetricDeviceFields.CreateRequest(snapshot,a[0].DeviceIdentity,Names).Ids.Count);
        }
    }

    [Fact]
    public async Task OriginalWorkerSamplesSubscribedDeviceAndGetsCacheAfterCancellation()
    {
        using var slots=new HostManagerSamplingSubscriptionTestFixture();
        var root=Path.Combine(Path.GetTempPath(),$"adlx-worker-{Guid.NewGuid():N}");
        var inventory=Inventory(); var inventoryZone=new WindowsGpuAdapterOrderMonitoringZone(()=>inventory);
        using var owner=new HostManagerMetricSnapshotOwner(slots.Provider,new HostManagerMetricSnapshotRuntime(slots.Provider,slots.Deployment),
            new HostManagerMetricSnapshotCatalogTopologySource(()=>new(1,NativeMetricSnapshotCatalogTopologyStatus.Complete,
                [new(9,"AMD A","gpu-a",0xA1,1,2,true),new(3,"AMD B","gpu-b",0xB1,1,2,true)],
                NativeMetricSnapshotCatalogTopologyStatus.Complete,[],NativeMetricSnapshotCatalogTopologyStatus.Complete,[])),
            new Environment(root),NullLogger<HostManagerMetricSnapshotOwner>.Instance, ResourceManager.ReadWrite.Native.NativeMetricSnapshotScratch.CreateOrdinary);
        await owner.StartAsync(CancellationToken.None); var dashboard=new DashboardMonitoringCatalogState();
        using(var lease=owner.Acquire()) dashboard.PublishCatalogProbe(NativeMetricSnapshotCommittedProjection.Create(lease.Workspace.ReadCommitted(),"CPU","RAM",lease.WorkspaceIdentity));
        var device=dashboard.Current!.FieldBindings.Values.Single(binding=>binding.Field.Code==MetricFieldCode.AdlxActivity && binding.DeviceIdentity.Contains("gpu-a")).DeviceIdentity;
        var row=Row(10,PnpA,AllFlags); row.VoltageMillivolts=1250; row.SampleTimestampMilliseconds=9007199254740993;
        var native=new Bridge { Metadata=[Row(10,PnpA),Row(20,PnpB)],Values=[row] };
        var zone=new AmdAdlxMonitoringZone(new AmdAdlxBridgeReader(native));
        var registry=new MonitoringSourceZoneRegistry(MonitoringSourceZoneCatalog.SourceIds.Select(id=>id==zone.DisplayName ? zone : new MonitoringSourceZone(id)).ToArray());
        var memory=new WindowsMemoryMonitoringZone(_=>throw new InvalidOperationException("No memory demand"),()=>throw new InvalidOperationException("No memory demand"));
        using var sampler=new WindowsHardwareMetricSampler(new WindowsCpuMonitoringZone(),memory,null!,inventoryZone,
            null!,null!,null!,null!,null!,zone,null!,new AmdIntegratedGpuSensorRoute(),null!,owner,dashboard,registry,
            slots.Owner,slots.Provider,NullLogger<WindowsHardwareMetricSampler>.Instance);
        var request=MetricDeviceFields.CreateRequest(dashboard.Current!,device,Names);
        await using var app=await StartHttpAsync(sampler,dashboard);
        using var client=new HttpClient { BaseAddress=new Uri(Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses)) };
        var query="?device="+Uri.EscapeDataString(device)+"&fields="+string.Join(',',Names);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Null(Fields(await sampler.GetSnapshotAsync(request,timeout.Token),device)[Names[2]].IntegerValue);
        using(var cached=JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot"+query,timeout.Token)))
            CheckWire(cached.RootElement[0].GetProperty("fields"),true);
        Assert.Equal(0,native.MetadataReads); await sampler.StartAsync(timeout.Token);
        try
        {
            await using(var stream=sampler.SubscribeAsync("adlx-consumer",request,TimeSpan.FromMilliseconds(100),timeout.Token).GetAsyncEnumerator(timeout.Token))
            {
                Assert.True(await stream.MoveNextAsync()); Assert.Equal("9007199254740993",Fields(stream.Current,device)[Names[2]].IntegerValue);
                Assert.Equal(AllFlags,Assert.Single(native.Selections).RequestedFlags); Assert.Equal(10,native.Selections[0].UniqueId);
            }
            using(var pushCancel=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token))
            using(var push=new HttpRequestMessage(HttpMethod.Post,"/api/subscriptions/stream")
                { Content=JsonContent.Create(new { version=1,subscriptions=new[] { new { id="adlx-wire",path="/api/metrics/subscribe"+query } } }) })
            using(var response=await client.SendAsync(push,HttpCompletionOption.ResponseHeadersRead,pushCancel.Token))
            {
                Assert.Equal(HttpStatusCode.OK,response.StatusCode);
                using var lines=new StreamReader(await response.Content.ReadAsStreamAsync(pushCancel.Token));
                using var received=JsonDocument.Parse((await lines.ReadLineAsync(pushCancel.Token))!);
                CheckWire(received.RootElement.GetProperty("value")[0].GetProperty("fields"),false);
                await pushCancel.CancelAsync();
            }
            await Task.Delay(150,timeout.Token); var reads=Volatile.Read(ref native.SelectedReads); Assert.True(reads>0);
            Assert.Equal("9007199254740993",Fields(await sampler.GetSnapshotAsync(request,timeout.Token),device)[Names[2]].IntegerValue);
            await Task.Delay(150,timeout.Token); Assert.Equal(reads,Volatile.Read(ref native.SelectedReads));
            await sampler.StopAsync(CancellationToken.None);
            Assert.Equal("9007199254740993",Fields(await sampler.GetSnapshotAsync(request,timeout.Token),device)[Names[2]].IntegerValue);
            using(var cached=JsonDocument.Parse(await client.GetStringAsync("/api/metrics/fields/snapshot"+query,timeout.Token)))
                CheckWire(cached.RootElement[0].GetProperty("fields"),false);
            Assert.Equal(reads,Volatile.Read(ref native.SelectedReads));
        }
        finally { await app.StopAsync(); await sampler.StopAsync(CancellationToken.None); }
    }

    private static void CheckWire(JsonElement fields,bool empty)
    {
        var values=fields.EnumerateArray().ToDictionary(field=>field.GetProperty("name").GetString()!);
        Assert.Equal(3,values.Count);
        Assert.All(values.Values,field=>Assert.Equal("amd.adlx",field.GetProperty("sourceId").GetString()));
        var time=values[Names[2]];
        Assert.Equal(empty ? null : "9007199254740993",time.GetProperty("integerValue").GetString());
        Assert.Equal("unsigned64",time.GetProperty("valueKind").GetString()); Assert.Equal("ms",time.GetProperty("unit").GetString());
        Assert.Equal(JsonValueKind.Null,time.GetProperty("numericValue").ValueKind);
        if(!empty) { Assert.Equal(0,values[Names[0]].GetProperty("numericValue").GetDouble()); Assert.Equal(1.25,values[Names[1]].GetProperty("numericValue").GetDouble()); }
    }
    private static async Task<WebApplication> StartHttpAsync(WindowsHardwareMetricSampler sampler,DashboardMonitoringCatalogState catalog,
        TaskCompletionSource? streamCompleted = null)
    {
        var builder=WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddSingleton(catalog); builder.Services.AddSingleton<IMetricSampler>(sampler); builder.Services.AddSingleton<IMetricSnapshotPushSource>(sampler);
        var app=builder.Build();
        if (streamCompleted is not null)
            app.Use(async (context, next) =>
            {
                try { await next(context); }
                finally
                {
                    if (context.Request.Path == "/api/subscriptions/stream")
                        streamCompleted.TrySetResult();
                }
            });
        foreach(var name in new[] { "MapMetricFieldEndpoints","MapSubscriptionChannelEndpoints" })
            typeof(ResourceManagerEndpointRouteBuilderExtensions).GetMethod(name,BindingFlags.Static|BindingFlags.NonPublic)!.Invoke(null,[app]);
        await app.StartAsync(); return app;
    }

    private static Dictionary<string,DeviceMetricFieldValue> Fields(HardwareMetricSnapshot snapshot,string device)=>
        Assert.Single(MetricDeviceFields.Read(snapshot,device,Names.ToHashSet())).Fields.ToDictionary(field=>field.Name);
    private static NativeAdlxGpuMetrics Row(int id,string pnp,int flags=0)
    {
        var row=new NativeAdlxGpuMetrics { StructSize=Marshal.SizeOf<NativeAdlxGpuMetrics>(),AbiVersion=5,IdentityValid=1,UniqueId=id,SupportedFlags=flags,AvailableFlags=flags };
        var bytes=MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref row,1));
        Encoding.UTF8.GetBytes(pnp).CopyTo(bytes.Slice(Marshal.OffsetOf<NativeAdlxGpuMetrics>(nameof(NativeAdlxGpuMetrics.PnpString)).ToInt32(),512));
        return row;
    }
    private static WindowsGpuAdapterInventoryRead Inventory(uint vendor=0x1002)=>new(SamplingObservationStatus.Current,1,DateTimeOffset.UtcNow.UtcTicks,2,0,0,1,[Adapter(9,PnpA,0xA1,vendor),Adapter(3,PnpB,0xB1,vendor)]);
    private static WindowsGpuAdapter Adapter(int index,string pnp,uint luid,uint vendor)=>new(index,"AMD",vendor,0x744C,0,new AdapterLuid { LowPart=luid },false,8UL<<30,WindowsGpuAdapterKind.Dedicated)
    {
        PhysicalIdentityStatus=SamplingObservationStatus.Current,
        PhysicalIdentities=[new(0,pnp,WindowsGpuPnpKind.Pci,true,false,0,(uint)index,0,0,vendor,0x744C,0,0,1,
            WindowsGpuIdentityEvidence.KmtLuidOpen|WindowsGpuIdentityEvidence.KmtHardwarePnpKey|WindowsGpuIdentityEvidence.CmExactDevNode|
            WindowsGpuIdentityEvidence.CmCanonicalInstanceRoundTrip|WindowsGpuIdentityEvidence.KmtDeviceIds|WindowsGpuIdentityEvidence.CmPciAddress,
            WindowsGpuPhysicalIdentityStatus.Bound,0)]
    };
    private sealed class Bridge : IAmdAdlxNativeBridge
    {
        public uint Abi=5; public int Size=840,SelectionSize=520,AbiReads,MetadataReads,SelectedReads;
        public Exception? Failure; public int? MetadataCount;
        public NativeAdlxGpuMetrics[] Metadata=[],Values=[]; public NativeAdlxGpuSelection[] Selections=[]; public Action? AfterMetadata;
        public uint GetAbiVersion() { AbiReads++; if(Failure is not null) throw Failure; return Abi; }
        public int GetMetricsSize()=>Size; public int GetSelectionSize()=>SelectionSize;
        public int ReadMetadata(NativeAdlxGpuMetrics[] output,StringBuilder message) { MetadataReads++; Metadata.CopyTo(output,0); AfterMetadata?.Invoke(); return MetadataCount ?? Metadata.Length; }
        public int ReadSelected(NativeAdlxGpuMetrics[] output,NativeAdlxGpuSelection[] selections,StringBuilder message)
        { Interlocked.Increment(ref SelectedReads); Selections=selections; Values.CopyTo(output,0); return Values.Length; }
    }
    private sealed class Environment(string root) : IHostEnvironment
    {
        public string EnvironmentName { get; set; }="Tests"; public string ApplicationName { get; set; }="ResourceManager";
        public string ContentRootPath { get; set; }=root; public IFileProvider ContentRootFileProvider { get; set; }=new NullFileProvider();
    }
}

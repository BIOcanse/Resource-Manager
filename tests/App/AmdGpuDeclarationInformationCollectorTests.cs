using System.Runtime.InteropServices;
using System.Text;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class AmdGpuDeclarationInformationCollectorTests
{
    internal const string PnpA=@"PCI\VEN_1002&DEV_744C\A",PnpB=@"PCI\VEN_1002&DEV_744C\B";
    internal const string IdentityA="windows-pnp|"+PnpA;
    internal static DeviceTopologyAmdGpuInformation[] Declarations()=>[new(PnpA,"020.003","GDDR6","113-TEST","04/31/2024")];
    internal static (Func<IReadOnlyList<DeviceTopologyAmdGpuInformation>?> Read,Func<int> Calls) CreateCollectorProbe()
    {
        var bridge=new Bridge { Metadata=[Metadata(10,PnpA)],Values=[Value(10,PnpA)] };
        var reader=new AmdAdlxBridgeReader(bridge);
        return (()=>reader.ReadDeclarations(Inventory()),()=>Volatile.Read(ref bridge.Calls));
    }

    [Fact]
    public void OnlyExactWindowsPhysicalMembersAreReadAndEmptyOrDisabledNeverCallsSdk()
    {
        var bridge=new Bridge { Metadata=[Metadata(20,PnpB),Metadata(10,PnpA),Metadata(30,"unknown")],Values=[Value(10,PnpA),Value(20,PnpB)] };
        var reader=new AmdAdlxBridgeReader(bridge);
        var rows=reader.ReadDeclarations(Inventory())!;
        Assert.Equal(2,rows.Count); Assert.Equal(2,bridge.Selections.Length);
        Assert.Equal([20,10],bridge.Selections.Select(row=>row.UniqueId).ToArray()); Assert.All(bridge.Selections,row=>Assert.Equal(15,row.RequestedFlags));
        Assert.All(rows,row=> { Assert.Equal("020.003",row.VbiosVersion); Assert.Equal("GDDR6",row.VramType); Assert.Equal("04/31/2024",row.VbiosDateRaw); });
        var calls=bridge.Calls;
        Assert.Empty(reader.ReadDeclarations(Inventory() with { Adapters=[],ObservedCount=0 })!);
        Assert.All(reader.ReadDeclarations(Inventory(),false)!,row=>Assert.Null(row.VbiosVersion));
        Assert.Equal(calls,bridge.Calls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void InvalidInventoryIsUnavailableWithoutSdkCalls(int defect)
    {
        var inventory=Inventory();
        inventory=defect switch { 0=>inventory with { Status=SamplingObservationStatus.Unavailable },1=>inventory with { Generation=0 },
            2=>inventory with { ObservedCount=1 },3=>inventory with { SkippedCount=1 },4=>inventory with { OverflowCount=1 },
            5=>inventory with { Adapters=[Adapter(9,PnpA) with { PhysicalIdentities=[] },Adapter(3,PnpB)] },
            6=>inventory with { Adapters=[Adapter(9,PnpA),Adapter(3,PnpA)] },
            _=>inventory with { Adapters=[Adapter(9,PnpA) with { PhysicalIdentityStatus=SamplingObservationStatus.Unavailable },Adapter(3,PnpB)] } };
        var bridge=new Bridge(); Assert.Null(new AmdAdlxBridgeReader(bridge).ReadDeclarations(inventory)); Assert.Equal(0,bridge.Calls);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void MissingSdkAbiOrMetadataFailureKeepsKnownIdentitiesEmpty(int defect)
    {
        var bridge=new Bridge { Metadata=[Metadata(10,PnpA)], Failure=defect switch { 0=>new DllNotFoundException(),1=>new EntryPointNotFoundException(),2=>new BadImageFormatException(),_=>null },
            Abi=defect==3 ? 4U : 5U,DeclarationAbi=defect==4 ? 2U : 1U,Size=defect==5 ? 1036 : 1040,Count=defect==6 ? 33 : null };
        var rows=new AmdAdlxBridgeReader(bridge).ReadDeclarations(Inventory())!;
        Assert.Equal(2,rows.Count); Assert.All(rows,row=> { Assert.Null(row.VbiosVersion); Assert.Null(row.VramType); Assert.Null(row.VbiosPartNumber); Assert.Null(row.VbiosDateRaw); });
        Assert.Empty(bridge.Selections);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void AmbiguousOrFailedMetadataDoesNotReadDeclarations(int defect)
    {
        var row=Metadata(10,PnpA);
        var metadata=defect switch { 0=>new[] { row,Metadata(10,PnpB) },1=>new[] { row,Metadata(11,PnpA) },
            2=>new[] { Metadata(10,"unknown") },3=>new[] { row with { } },_=>new[] { row } };
        if(defect==3) metadata[0].IdentityValid=0;
        if(defect==4) metadata[0].AbiVersion=4;
        var bridge=new Bridge { Metadata=metadata };
        Assert.All(new AmdAdlxBridgeReader(bridge).ReadDeclarations(Inventory())!,gpu=>Assert.Null(gpu.VbiosVersion)); Assert.Empty(bridge.Selections);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void ChangedOrDuplicateReturnedIdentityCannotPublish(int defect)
    {
        var row=Value(10,PnpA);
        if(defect==0) row.UniqueId=11;
        if(defect==1) Write(ref row,nameof(NativeAdlxGpuDeclaration.PnpString),PnpB);
        if(defect==2) row.StructSize=1036;
        if(defect==3) row.AbiVersion=2;
        var bridge=new Bridge { Metadata=[Metadata(10,PnpA)],Values=defect==4 ? [row,row] : [row] };
        Assert.All(new AmdAdlxBridgeReader(bridge).ReadDeclarations(Inventory())!,gpu=>Assert.Null(gpu.VbiosVersion));
    }

    [Fact]
    public void PartialFailureAndRecoveryPreserveIndependentDeclarationsAndZeroId()
    {
        var valid=Metadata(0,PnpA); var failed=Metadata(0,PnpB); failed.IdentityValid=0;
        var row=Value(0,PnpA); row.AvailableFlags=2;
        var bridge=new Bridge { Metadata=[valid,failed],Values=[row] }; var reader=new AmdAdlxBridgeReader(bridge);
        var first=Assert.Single(reader.ReadDeclarations(Inventory())!,gpu=>gpu.PnpInstanceId==PnpA);
        Assert.Null(first.VbiosVersion); Assert.Equal("GDDR6",first.VramType);
        bridge.Values=[Value(0,PnpA)]; Assert.Equal("020.003",Assert.Single(reader.ReadDeclarations(Inventory())!,gpu=>gpu.PnpInstanceId==PnpA).VbiosVersion);
        bridge.Values=[]; Assert.All(reader.ReadDeclarations(Inventory())!,gpu=>Assert.Null(gpu.VramType));
    }

    [Theory]
    [InlineData("0",true)] [InlineData("  v1  ",true)] [InlineData("版本1",true)] [InlineData("",false)] [InlineData("   ",false)]
    public void CompleteUtf8TextIsPreservedWithoutParsingOrTrimming(string text,bool valid)
    {
        var row=Value(10,PnpA); Write(ref row,nameof(NativeAdlxGpuDeclaration.VbiosVersion),text);
        var bytes=MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref row,1));
        var offset=Marshal.OffsetOf<NativeAdlxGpuDeclaration>(nameof(NativeAdlxGpuDeclaration.VbiosVersion)).ToInt32();
        Assert.Equal(valid ? text : null,AmdAdlxBridgeReader.DecodeDeclaration(row.VbiosVersion));
        bytes[offset]=0xFF; bytes[offset+1]=0; Assert.Null(AmdAdlxBridgeReader.DecodeDeclaration(row.VbiosVersion));
        bytes.Slice(offset,128).Fill(65); Assert.Null(AmdAdlxBridgeReader.DecodeDeclaration(row.VbiosVersion));
    }

    [Fact]
    public void TypedProjectionMergesGpuPnpAndClearsPersistedValuesAndHashIgnoresListOrder()
    {
        var snapshot=DeviceInformationTestData.Snapshot() with { AmdGpuInformation=Declarations(),
            GpuInformation=[new(PnpA,"00000000000000A1","AMD","1002","744C","31.0")] };
        var projected=DeviceTopologyInformationProjection.Create(snapshot,DateTimeOffset.UtcNow);
        var gpu=Assert.Single(projected.Devices,device=>device.DeviceIdentity==IdentityA);
        Assert.Equal("gpu_adapter",gpu.ObjectType); Assert.Equal(9,gpu.Fields.Count);
        Assert.Equal("04/31/2024",Assert.Single(gpu.Fields,field=>field.Definition.Name=="gpu_amd_vbios_date_raw").Value!.Value.GetString());
        var persisted=DeviceTopologyInformationProjection.Create(snapshot,null);
        Assert.All(Assert.Single(persisted.Devices,device=>device.DeviceIdentity==IdentityA).Fields,field=>Assert.Null(field.Value));
        var comparer=new DeviceTopologySemanticComparer();
        var second=new DeviceTopologyAmdGpuInformation(PnpB,"2",null,null,null);
        Assert.Equal(comparer.ComputeHash(snapshot with { AmdGpuInformation=[..Declarations(),second] }),
            comparer.ComputeHash(snapshot with { AmdGpuInformation=[second,..Declarations()] }));
    }

    [Fact]
    public void OriginalTopologyCompositionUsesOneInventoryAndVendorFailuresAreIndependent()
    {
        var reads=0; var bridge=new Bridge { Metadata=[Metadata(10,PnpA)],Values=[Value(10,PnpA)] };
        var inventory=new WindowsGpuAdapterOrderMonitoringZone(()=> { reads++; return Inventory(); });
        var nvidia=new NvidiaNvmlMonitoringZone((_,_,_)=>throw new InvalidOperationException(),_=>throw new InvalidOperationException("NVML failed"));
        var amd=new AmdAdlxMonitoringZone(new AmdAdlxBridgeReader(bridge));
        var result=WindowsDeviceTopologyReader.ReadGpuDeclarations(nvidia,inventory,amd);
        Assert.Equal(1,reads); Assert.NotNull(result.Windows); Assert.Null(result.Nvidia);
        Assert.Equal("020.003",Assert.Single(result.Amd!,gpu=>gpu.PnpInstanceId==PnpA).VbiosVersion);
    }

    private static NativeAdlxGpuMetrics Metadata(int id,string pnp)
    {
        var row=new NativeAdlxGpuMetrics { StructSize=840,AbiVersion=5,UniqueId=id,IdentityValid=1 };
        Write(ref row,nameof(NativeAdlxGpuMetrics.PnpString),pnp); return row;
    }
    private static NativeAdlxGpuDeclaration Value(int id,string pnp)
    {
        var row=new NativeAdlxGpuDeclaration { StructSize=1040,AbiVersion=1,UniqueId=id,AvailableFlags=15 };
        Write(ref row,nameof(NativeAdlxGpuDeclaration.PnpString),pnp);
        Write(ref row,nameof(NativeAdlxGpuDeclaration.VbiosVersion),"020.003"); Write(ref row,nameof(NativeAdlxGpuDeclaration.VramType),"GDDR6");
        Write(ref row,nameof(NativeAdlxGpuDeclaration.VbiosPartNumber),"113-TEST"); Write(ref row,nameof(NativeAdlxGpuDeclaration.VbiosDateRaw),"04/31/2024"); return row;
    }
    private static void Write<T>(ref T value,string field,string text) where T:struct
    {
        var bytes=MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value,1));
        var offset=Marshal.OffsetOf<T>(field).ToInt32(); var length=field=="PnpString" ? 512 : 128;
        bytes.Slice(offset,length).Clear(); Encoding.UTF8.GetBytes(text).CopyTo(bytes.Slice(offset,length));
    }
    private static WindowsGpuAdapterInventoryRead Inventory()=>new(SamplingObservationStatus.Current,1,DateTimeOffset.UtcNow.UtcTicks,2,0,0,1,[Adapter(9,PnpA),Adapter(3,PnpB)]);
    private static WindowsGpuAdapter Adapter(int index,string pnp)=>new(index,"AMD",0x1002,0x744C,0,new() { LowPart=(uint)index+1 },false,8UL<<30,WindowsGpuAdapterKind.Dedicated)
    { PhysicalIdentityStatus=SamplingObservationStatus.Current,PhysicalIdentities=[new(0,pnp,WindowsGpuPnpKind.Pci,true,false,0,(uint)index,0,0,0x1002,0x744C,0,0,1,
        WindowsGpuIdentityEvidence.KmtLuidOpen|WindowsGpuIdentityEvidence.KmtHardwarePnpKey|WindowsGpuIdentityEvidence.CmExactDevNode|
        WindowsGpuIdentityEvidence.CmCanonicalInstanceRoundTrip|WindowsGpuIdentityEvidence.KmtDeviceIds|WindowsGpuIdentityEvidence.CmPciAddress,WindowsGpuPhysicalIdentityStatus.Bound,0)] };
    private sealed class Bridge:IAmdAdlxNativeBridge
    {
        public uint Abi=5,DeclarationAbi=1; public int Size=1040,Calls; public int? Count; public Exception? Failure;
        public NativeAdlxGpuMetrics[] Metadata=[]; public NativeAdlxGpuDeclaration[] Values=[]; public NativeAdlxGpuSelection[] Selections=[];
        public uint GetAbiVersion() { Calls++; if(Failure is not null) throw Failure; return Abi; }
        public int GetMetricsSize()=>840; public int GetSelectionSize()=>520;
        public uint GetDeclarationAbiVersion()=>DeclarationAbi; public int GetDeclarationSize()=>Size;
        public int ReadMetadata(NativeAdlxGpuMetrics[] output,StringBuilder message) { Metadata.CopyTo(output,0); return Count ?? Metadata.Length; }
        public int ReadSelected(NativeAdlxGpuMetrics[] output,NativeAdlxGpuSelection[] selections,StringBuilder message)=>throw new InvalidOperationException("No metrics demanded");
        public int ReadDeclarations(NativeAdlxGpuDeclaration[] output,NativeAdlxGpuSelection[] selections,StringBuilder message)
        { Selections=selections; Values.CopyTo(output,0); return Values.Length; }
    }
}

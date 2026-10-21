using System.Text;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.App.Infrastructure.DeviceTopology.Snapshots;
using ResourceManager.App.Infrastructure.DeviceTopology.Pci;
using ResourceManager.App.Infrastructure.Monitoring;

namespace Resource_Manager_APP.Tests;

public sealed class NvidiaGpuDeclarationCollectorTests
{
    internal const string PnpA = @"PCI\VEN_10DE&DEV_2684&SUBSYS_000010DE\A";
    internal const string PnpB = @"PCI\VEN_10DE&DEV_2684&SUBSYS_000010DE\B";
    internal const string IdentityA = "windows-pnp|"+PnpA;
    internal static DeviceTopologyNvidiaGpuInformation[] Declarations() => [new(PnpA,"95.00.01.00.02","8.10")];

    [Fact]
    public void ExactPhysicalMembersReceiveIndependentVersionsWithoutOrdinalFallback()
    {
        var inventory=Inventory(Adapter(9,1,PnpA),Adapter(3,2,PnpB),Adapter(7,3,@"PCI\VEN_1002&DEV_2684\C",0x1002));
        var api=new Api();
        var data=Assert.IsAssignableFrom<IReadOnlyList<DeviceTopologyNvidiaGpuInformation>>(NvidiaNvmlReader.ReadDeclarations(inventory,api));
        Assert.Equal(2,data.Count);
        var a=Assert.Single(data,static gpu=>gpu.PnpInstanceId==PnpA);
        var b=Assert.Single(data,static gpu=>gpu.PnpInstanceId==PnpB);
        Assert.Equal("95.00.01.00.02",a.VbiosVersion); Assert.Equal("8.10",a.CudaComputeCapability);
        Assert.Equal("96.00.02",b.VbiosVersion); Assert.Equal("9.0",b.CudaComputeCapability);
        Assert.Equal([2,1],api.QueriedVbios.Select(static d=>(int)d).ToArray());
        Assert.Equal(api.QueriedVbios,api.QueriedCuda);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void GlobalFailureRetainsWindowsDeclarationsWithoutReadingValues(int failure)
    {
        var api=new Api { Init=failure!=0, CountStatus=failure==1?3:0, DeviceCount=failure==2?129U:2U,
            PciStatus=failure==3?3:0, HandleStatus=failure==4?3:0 };
        var result=NvidiaNvmlReader.ReadDeclarations(Inventory(Adapter(9,1,PnpA)),api)!;
        var gpu=Assert.Single(result); Assert.Equal(PnpA,gpu.PnpInstanceId);
        Assert.Null(gpu.VbiosVersion); Assert.Null(gpu.CudaComputeCapability); Assert.Empty(api.QueriedVbios); Assert.Empty(api.QueriedCuda);
        if (failure==2) Assert.Equal(0,api.HandleCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PerFieldFailureOrMissingExportNeverDiscardsTheOtherQuery(int mode)
    {
        var api=new Api { VbiosStatus=mode==0?3:0, CudaStatus=mode==1?3:0, MissingVbios=mode==2, MissingCuda=mode==3 };
        var gpu=Assert.Single(NvidiaNvmlReader.ReadDeclarations(Inventory(Adapter(9,1,PnpA)),api)!);
        Assert.Equal(mode is 0 or 2?null:"95.00.01.00.02",gpu.VbiosVersion);
        Assert.Equal(mode is 1 or 3?null:"8.10",gpu.CudaComputeCapability);
    }

    [Theory]
    [InlineData(-1,0)]
    [InlineData(8,-1)]
    [InlineData(0,0)]
    [InlineData(8,10)]
    [InlineData(int.MaxValue,int.MaxValue)]
    public void CudaMajorMinorNeverPassesThroughFloatOrModelGuess(int major,int minor)
    {
        var api=new Api { Major=major,Minor=minor };
        var gpu=Assert.Single(NvidiaNvmlReader.ReadDeclarations(Inventory(Adapter(9,1,PnpA)),api)!);
        Assert.Equal(major<0||minor<0?null:$"{major}.{minor}",gpu.CudaComputeCapability);
    }

    [Fact]
    public void AmbiguousOrWrongPciEvidenceNeverQueriesEitherDeclaration()
    {
        var inventory=Inventory(Adapter(9,1,PnpA));
        foreach (var api in new[] { new Api { Duplicate=true },new Api { WrongPci=true } })
        {
            var gpu=Assert.Single(NvidiaNvmlReader.ReadDeclarations(inventory,api)!);
            Assert.Null(gpu.VbiosVersion); Assert.Null(gpu.CudaComputeCapability); Assert.Empty(api.QueriedVbios); Assert.Empty(api.QueriedCuda);
        }
    }

    [Fact]
    public void EmptyOrNonNvidiaWindowsInventoryNeverInitializesNvmlAndInvalidInventoryIsUnavailable()
    {
        var api=new Api();
        Assert.Empty(NvidiaNvmlReader.ReadDeclarations(Inventory(),api)!);
        Assert.Empty(NvidiaNvmlReader.ReadDeclarations(Inventory(Adapter(0,1,PnpA,0x1002)),api)!);
        Assert.Equal(0,api.InitCalls);
        Assert.Null(NvidiaNvmlReader.ReadDeclarations(Inventory(Adapter(9,1,PnpA)) with { Status=SamplingObservationStatus.Unavailable },api));
        Assert.Equal(0,api.InitCalls);
        var known=Assert.Single(NvidiaNvmlReader.ReadDeclarations(Inventory(Adapter(9,1,PnpA)),null)!);
        Assert.Null(known.VbiosVersion); Assert.Null(known.CudaComputeCapability);
    }

    [Fact]
    public void OriginalNvmlInitializationRetriesOncePerDemandThenReusesSuccess()
    {
        var attempts=0;
        var reader=new NvidiaNvmlReader(()=>++attempts==1?3:0);
        Assert.False(reader.EnsureInitialized()); Assert.Equal(1,attempts);
        Assert.True(reader.EnsureInitialized()); Assert.Equal(2,attempts);
        Assert.True(reader.EnsureInitialized()); Assert.Equal(2,attempts);
        var missingAttempts=0;
        var missing=new NvidiaNvmlReader(()=>{ missingAttempts++; throw new DllNotFoundException(); });
        Assert.False(missing.EnsureInitialized()); Assert.Equal(1,missingAttempts);
        Assert.False(missing.EnsureInitialized()); Assert.Equal(2,missingAttempts);
    }

    [Theory]
    [InlineData("version",true)]
    [InlineData("                               ",false)]
    [InlineData("1234567890123456789012345678901",true)]
    [InlineData("12345678901234567890123456789012",false)]
    [InlineData("",false)]
    [InlineData("bad\nvalue",false)]
    [InlineData("badé",false)]
    [InlineData(" version ",true)]
    public void VbiosRequiresCompleteBoundedAsciiString(string text,bool valid)
    {
        var bytes=new byte[32]; var raw=Encoding.Latin1.GetBytes(text); Array.Copy(raw,bytes,Math.Min(raw.Length,bytes.Length));
        Assert.Equal(valid?text:null,NvidiaNvmlReader.DecodeVbios(bytes));
        Assert.Null(NvidiaNvmlReader.DecodeVbios(new byte[31]));
    }

    [Fact]
    public void TypedProjectionMergesPciDeviceAndClearsPersistedValues()
    {
        var port=DeviceInformationTestData.Port(PnpA) with {
            PciIdentification=PciDeviceIdentificationReader.Read([@"PCI\VEN_10DE&DEV_2684&SUBSYS_000010DE"],[]) };
        var snapshot=DeviceInformationTestData.Snapshot(port) with { NvidiaGpuInformation=Declarations() };
        var projected=DeviceTopologyInformationProjection.Create(snapshot,DateTimeOffset.UtcNow);
        var gpu=Assert.Single(projected.Devices,static d=>d.DeviceIdentity==IdentityA);
        Assert.Equal("gpu_adapter",gpu.ObjectType);
        Assert.Equal("10DE",Assert.Single(gpu.Fields,static f=>f.Definition.Name=="pci_all_vendor_id").Value!.Value.GetString());
        Assert.Equal("8.10",Assert.Single(gpu.Fields,static f=>f.Definition.Name=="gpu_nvidia_cuda_compute_capability").Value!.Value.GetString());
        var persisted=DeviceTopologyInformationProjection.Create(snapshot,null);
        Assert.All(Assert.Single(persisted.Devices,static d=>d.DeviceIdentity==IdentityA).Fields,static field=>Assert.Null(field.Value));
        var standalone=DeviceTopologyInformationProjection.Create(snapshot with { Ports=[] },DateTimeOffset.UtcNow);
        Assert.Equal(2,Assert.Single(standalone.Devices,static d=>d.DeviceIdentity==IdentityA).Fields.Count);
    }

    private static WindowsGpuAdapterInventoryRead Inventory(params WindowsGpuAdapter[] adapters) =>
        new(SamplingObservationStatus.Current,1,DateTimeOffset.UtcNow.UtcTicks,(uint)adapters.Length,0,0,1,adapters);
    private static WindowsGpuAdapter Adapter(int index,uint bus,string pnp,uint vendor=0x10DE) =>
        new(index,"Fixture GPU",vendor,0x2684,0x10DE,new() { LowPart=(uint)index+1 },false,8UL<<30,WindowsGpuAdapterKind.Dedicated) {
            PhysicalIdentityStatus=SamplingObservationStatus.Current,PhysicalIdentities=[new(0,pnp,WindowsGpuPnpKind.Pci,true,true,0,bus,0,0,
                vendor,0x2684,0x10DE,0,1,WindowsGpuIdentityEvidence.CmPciAddress,WindowsGpuPhysicalIdentityStatus.Bound,0)] };
    private sealed class Api : INvmlGpuDeclarationApi
    {
        internal bool Init=true,Duplicate,WrongPci,MissingVbios,MissingCuda;
        internal int CountStatus,HandleStatus,PciStatus,VbiosStatus,CudaStatus,InitCalls,HandleCalls,Major=8,Minor=10;
        internal uint DeviceCount=2;
        internal List<IntPtr> QueriedVbios=[],QueriedCuda=[];
        public bool Initialize() { InitCalls++; return Init; }
        public int Count(out uint count) { count=DeviceCount; return CountStatus; }
        public int Handle(uint index,out IntPtr device) { HandleCalls++; device=(IntPtr)(index==0?2:1); return HandleStatus; }
        public int Pci(IntPtr device,out NvmlPciInfo pci)
        {
            var bus=WrongPci?7U:Duplicate?1U:(uint)(int)device;
            pci=new() { Domain=0,Bus=bus,Device=0,BusId=$"00000000:{bus:X2}:00.0",BusIdLegacy=$"0000:{bus:X2}:00.0",PciDeviceId=0x268410DE,PciSubSystemId=0x10DE };
            return PciStatus;
        }
        public int Vbios(IntPtr device,byte[] buffer)
        {
            QueriedVbios.Add(device); if (MissingVbios) throw new EntryPointNotFoundException();
            Assert.Equal(32,buffer.Length); Encoding.ASCII.GetBytes((int)device==1?"95.00.01.00.02":"96.00.02").CopyTo(buffer,0); return VbiosStatus;
        }
        public int Cuda(IntPtr device,out int major,out int minor)
        {
            QueriedCuda.Add(device); if (MissingCuda) throw new EntryPointNotFoundException();
            major=(int)device==1?Major:9; minor=(int)device==1?Minor:0; return CudaStatus;
        }
    }
}

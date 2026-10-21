using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Infrastructure.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class DiskLayoutInformationCollectorTests
{
    [Fact]
    public void SameReadOnlyHandleBracketsLayoutWithDiskNumbersAndClosesAfterBoundedGrowth()
    {
        var calls=new NativeCalls();var result=new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskA)!;
        Assert.Equal(2U,result.DiskNumber);Assert.Equal(2,result.Layout!.Partitions.Count);Assert.Equal(new[]{192,384},calls.LayoutSizes);
        Assert.Equal(new[]{"number","layout","layout","alignment","identity","number"},calls.Order);Assert.Equal(DiskA.DevicePath,calls.OpenedPath);Assert.True(calls.Handle!.IsClosed);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void DirtyFailedTruncatedOrChangedDiskNumberDoesNotPublishLayout(int failure)
    {
        var calls=new NativeCalls{NumberFailure=failure};Assert.Null(new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskA));Assert.True(calls.Handle!.IsClosed);
        if(failure!=3)Assert.Empty(calls.LayoutSizes);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void FailedLayoutDoesNotLoseVerifiedNumberAndNeverUsesFailureBufferCount(int failure)
    {
        var calls=new NativeCalls{LayoutFailure=failure};var result=new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskA)!;
        Assert.Equal(2U,result.DiskNumber);Assert.Null(result.Layout);Assert.True(calls.Handle!.IsClosed);Assert.Equal(2,calls.NumberReads);
        if(failure==2)Assert.True(calls.LayoutSizes.Count<=WindowsDiskLayoutInformationNativeApi.MaximumAttempts);
    }

    [Fact]
    public void ThrowingLayoutStillClosesAndInvalidOpenNeverQueries()
    {
        var calls=new NativeCalls{LayoutException=true};Assert.Null(new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskA)!.Layout);Assert.True(calls.Handle!.IsClosed);
        calls=new NativeCalls{InvalidOpen=true};Assert.Null(new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskA));Assert.Empty(calls.Order);Assert.True(calls.Handle!.IsClosed);
    }

    [Fact]
    public void NativeDirectoryHasOneSetupApiListAndBothPathAndPnpIdentityAndCloses()
    {
        var calls=new DirectoryCalls();var result=WindowsDeviceTopologyNativeReader.ReadDiskInterfaces(calls)!;
        Assert.Equal(new[]{DiskA,DiskB},result);Assert.Equal(1,calls.Opens);Assert.Equal(1,calls.Closes);Assert.Equal(4,calls.Details);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void IncompleteOrMalformedNativeInterfaceDirectoriesAreNotPublishedAndStillClose(int failure)
    {
        var calls=new DirectoryCalls{Failure=failure};Assert.Null(WindowsDeviceTopologyNativeReader.ReadDiskInterfaces(calls));Assert.Equal(1,calls.Closes);
    }

    [Fact]
    public void ThrowingInterfaceEnumerationStillClosesSetupApiList()
    {
        var calls=new DirectoryCalls{Failure=8};Assert.Throws<IOException>(()=>WindowsDeviceTopologyNativeReader.ReadDiskInterfaces(calls));Assert.Equal(1,calls.Closes);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public void InvalidSetupApiHandleIsNotClosed(int handle)
    {
        var calls=new DirectoryCalls{OpenHandle=handle};Assert.Null(WindowsDeviceTopologyNativeReader.ReadDiskInterfaces(calls));Assert.Equal(0,calls.Closes);Assert.Equal(0,calls.NextReads);
    }

    [Fact]
    public void ExactDirectoryLimitCompletesButNonTerminatingEnumerationIsBounded()
    {
        var calls=new DirectoryCalls{Count=WindowsDiskLayoutInformationReader.MaximumDisks};var result=WindowsDeviceTopologyNativeReader.ReadDiskInterfaces(calls)!;
        Assert.Equal(calls.Count,result.Count);Assert.Equal(1,calls.Closes);
        calls=new DirectoryCalls{Count=int.MaxValue};Assert.Null(WindowsDeviceTopologyNativeReader.ReadDiskInterfaces(calls));
        Assert.Equal(WindowsDiskLayoutInformationReader.MaximumDisks+1,calls.NextReads);Assert.Equal(1,calls.Closes);
        calls=new DirectoryCalls{Count=0};Assert.Empty(WindowsDeviceTopologyNativeReader.ReadDiskInterfaces(calls)!);Assert.Equal(1,calls.Closes);
    }

    private sealed class NativeCalls:IWindowsDiskLayoutNativeCalls
    {
        internal SafeFileHandle? Handle;internal string? OpenedPath;internal int NumberReads;
        internal int NumberFailure=-1,LayoutFailure=-1;internal bool LayoutException,InvalidOpen;
        internal List<int> LayoutSizes=[];internal List<string> Order=[];
        private int error=122;
        public SafeFileHandle Open(string path){OpenedPath=path;return Handle=new(new IntPtr(InvalidOpen?-1:42),false);}
        public bool Number(SafeFileHandle handle,byte[] buffer,out uint returned)
        {
            Assert.Same(Handle,handle);Assert.False(handle.IsClosed);NumberReads++;Order.Add("number");NumberFrame(NumberFailure==3&&NumberReads==2?3U:2U).CopyTo(buffer,0);
            returned=NumberFailure==1?11U:NumberFailure==2?13U:12U;return NumberFailure!=0;
        }
        public bool Layout(SafeFileHandle handle,byte[] buffer,out uint returned)
        {
            Assert.Same(Handle,handle);Assert.False(handle.IsClosed);LayoutSizes.Add(buffer.Length);Order.Add("layout");if(LayoutException)throw new IOException("failed");
            var frame=Frame("gpt");Array.Copy(frame,buffer,Math.Min(frame.Length,buffer.Length));returned=(uint)Math.Min(frame.Length,buffer.Length);
            if(LayoutFailure==0){error=5;return false;}
            if(LayoutFailure==1){returned=(uint)buffer.Length+1;return true;}
            if(LayoutFailure==2){Array.Fill(buffer,(byte)0xff);returned=0;return false;}
            if(LayoutFailure==3)return true; // Dirty successful truncation must still be rejected.
            if(LayoutFailure==4){returned=4;return true;}
            return buffer.Length>=frame.Length;
        }
        public int LastError()=>error;
        public bool AccessAlignment(SafeFileHandle handle,byte[] query,byte[] buffer,out uint returned)
        {
            Assert.Same(Handle,handle);Assert.False(handle.IsClosed);Order.Add("alignment");returned=0;error=50;return false;
        }
        public bool DeviceIdentity(SafeFileHandle handle,byte[] query,byte[] buffer,out uint returned)
        {
            Assert.Same(Handle,handle);Assert.False(handle.IsClosed);Order.Add("identity");returned=0;error=50;return false;
        }
    }

    private sealed class DirectoryCalls:IWindowsDiskInterfaceDirectoryCalls
    {
        internal int Opens,Closes,NextReads,Details,Failure,Count=2,OpenHandle=42;
        private int error=122;
        private int current;
        public nint Open(){Opens++;return new(OpenHandle);}
        public bool Next(nint set,uint index,ref WindowsDeviceTopologyNativeReader.DiskInterfaceData entry)
        {
            Assert.Equal(new IntPtr(42),set);Assert.Equal((uint)Marshal.SizeOf<WindowsDeviceTopologyNativeReader.DiskInterfaceData>(),entry.CbSize);NextReads++;current=(int)index;
            if(Failure==8)throw new IOException("failed");if(Failure==1){error=5;return false;}if(index>=Count){error=259;return false;}return true;
        }
        public bool Detail(nint set,ref WindowsDeviceTopologyNativeReader.DiskInterfaceData entry,nint buffer,uint capacity,out uint required,ref WindowsDeviceTopologyNativeReader.SpDevInfoData info)
        {
            Details++;Assert.Equal((uint)Marshal.SizeOf<WindowsDeviceTopologyNativeReader.SpDevInfoData>(),info.CbSize);
            var path=current==0?DiskA.DevicePath:current==1?DiskB.DevicePath:@"\\?\disk-"+current;
            var payload=Encoding.Unicode.GetBytes(path+"\0");required=(uint)(4+payload.Length);
            if(buffer==0){error=Failure==2?5:122;if(Failure==3)required=16385;return false;}
            Assert.Equal(IntPtr.Size==8?8:6,Marshal.ReadInt32(buffer));Assert.True(capacity>=required);
            Marshal.Copy(payload,0,buffer+4,payload.Length);if(Failure==4)required=capacity+1;if(Failure==5){Marshal.WriteInt16(buffer,4,unchecked((short)0xd800));Marshal.WriteInt16(buffer,6,0);}
            return true;
        }
        public string? Identity(nint set,ref WindowsDeviceTopologyNativeReader.SpDevInfoData info)=>Failure==6?null:current==0?DiskA.PnpInstanceId:current==1?DiskB.PnpInstanceId:@"SCSI\DISK&"+current;
        public bool Close(nint set){Assert.Equal(new IntPtr(42),set);Closes++;return Failure!=7;}
        public int LastError()=>error;
    }
}

using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Infrastructure.DiskUsage;

namespace Resource_Manager_APP.Tests;

public sealed class DiskUsageVolumeKindTests
{
    // StorageBusType values as MSFT_Disk reports them.
    private const uint Usb = 7;
    private const uint Ieee1394 = 4;
    private const uint Nvme = 17;
    private const uint Sata = 11;
    private const uint Mmc = 13;
    private const uint FileBackedVirtual = 15;

    [Fact]
    public void UsbSolidStateDriveReportedAsFixedIsExternal()
    {
        // A USB portable SSD such as SanDisk Extreme reports DriveType.Fixed on bus 7.
        Assert.Equal(DiskUsageVolumeKinds.External, WindowsDiskUsageVolumeCatalog.ClassifyVolumeKind(DriveType.Fixed, false, Usb));
        Assert.Equal(DiskUsageVolumeKinds.External, WindowsDiskUsageVolumeCatalog.ClassifyVolumeKind(DriveType.Fixed, false, Ieee1394));
    }

    [Fact]
    public void RemovableDriveTypeIsExternal()
    {
        Assert.Equal(DiskUsageVolumeKinds.External, WindowsDiskUsageVolumeCatalog.ClassifyVolumeKind(DriveType.Removable, false, Usb));
        Assert.Equal(DiskUsageVolumeKinds.External, WindowsDiskUsageVolumeCatalog.ClassifyVolumeKind(DriveType.Removable, false, null));
    }

    [Fact]
    public void InternalFixedDisksStayPhysicalIncludingEmbeddedMmc()
    {
        Assert.Equal(DiskUsageVolumeKinds.Physical, WindowsDiskUsageVolumeCatalog.ClassifyVolumeKind(DriveType.Fixed, false, Nvme));
        Assert.Equal(DiskUsageVolumeKinds.Physical, WindowsDiskUsageVolumeCatalog.ClassifyVolumeKind(DriveType.Fixed, false, Sata));
        Assert.Equal(DiskUsageVolumeKinds.Physical, WindowsDiskUsageVolumeCatalog.ClassifyVolumeKind(DriveType.Fixed, false, Mmc));
    }

    [Fact]
    public void VirtualSubstitutedAndUnreadableBusesKeepTheirKinds()
    {
        Assert.Equal(DiskUsageVolumeKinds.Virtual, WindowsDiskUsageVolumeCatalog.ClassifyVolumeKind(DriveType.Fixed, false, FileBackedVirtual));
        Assert.Equal(DiskUsageVolumeKinds.Virtual, WindowsDiskUsageVolumeCatalog.ClassifyVolumeKind(DriveType.Fixed, true, Usb));
        Assert.Equal(DiskUsageVolumeKinds.Unknown, WindowsDiskUsageVolumeCatalog.ClassifyVolumeKind(DriveType.Fixed, false, null));
        Assert.Equal(DiskUsageVolumeKinds.Network, WindowsDiskUsageVolumeCatalog.ClassifyVolumeKind(DriveType.Network, false, null));
        Assert.Equal(DiskUsageVolumeKinds.Optical, WindowsDiskUsageVolumeCatalog.ClassifyVolumeKind(DriveType.CDRom, false, null));
    }
}

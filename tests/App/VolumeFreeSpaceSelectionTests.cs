using ResourceManager.ReadWrite.Windows.Devices;

namespace Resource_Manager_APP.Tests;

public sealed class VolumeFreeSpaceSelectionTests
{
    [Fact]
    public void SelectedVolumeReadsDoNotEnumerateVolumesOrReadAnotherVolume()
    {
        var api = new VolumeSpaceInformationCollectorTests.Api();
        var reader = new WindowsVolumeSpaceInformationReader(api);
        var first = Assert.Single(reader.ReadSelected([VolumeSpaceInformationCollectorTests.IdA, VolumeSpaceInformationCollectorTests.IdA]));
        Assert.Equal(0, api.Directories);
        Assert.Equal([VolumeSpaceInformationCollectorTests.RootA], api.Requested);
        Assert.Equal(VolumeSpaceInformationCollectorTests.IdA, first.DeviceIdentity);
        Assert.NotNull(first.AvailableBytes);
        api.FirstStatus = 5;
        Assert.Null(Assert.Single(reader.ReadSelected([VolumeSpaceInformationCollectorTests.IdA])).AvailableBytes);
        Assert.Equal(0, api.Directories);
    }
    [Theory]
    [InlineData("C:\\")]
    [InlineData("windows-volume|invalid")]
    public void InvalidAddressesAreRejectedBeforeReading(string identity)
    {
        var api = new VolumeSpaceInformationCollectorTests.Api();
        Assert.Throws<ArgumentException>(() => new WindowsVolumeSpaceInformationReader(api).ReadSelected([identity]));
        Assert.Equal(0, api.Directories);
        Assert.Empty(api.Requested);
    }
}
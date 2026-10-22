using LibreHardwareMonitor.Hardware;
using ResourceManager.ReadWrite.Windows.Platform;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareMonitorCoreReaderTests
{
    [Fact]
    public void OfficialComputerOnlyEnablesMotherboardAndMemoryWithoutOpeningHardware()
    {
        var computer = HardwareMonitorCoreReader.CreateComputer();
        Assert.True(computer.IsMotherboardEnabled);
        Assert.True(computer.IsMemoryEnabled);
        Assert.False(computer.IsCpuEnabled);
        Assert.False(computer.IsGpuEnabled);
        Assert.False(computer.IsStorageEnabled);
        Assert.False(computer.IsNetworkEnabled);
        Assert.False(computer.IsBatteryEnabled);
        Assert.False(computer.IsControllerEnabled);
        Assert.False(computer.IsPsuEnabled);
        Assert.False(computer.IsPowerMonitorEnabled);
        Assert.Empty(computer.Hardware);
    }

    [Fact]
    public void OneOwnedSessionServesSelectedReadsAndClosesOnce()
    {
        var session = new Session();
        var creations = 0;
        var reader = new HardwareMonitorCoreReader(() => { creations++; return session; });
        Assert.Equal(0, creations);
        Assert.True(reader.Read(TimeSpan.FromSeconds(1)).CatalogComplete);
        Assert.True(reader.ReadFanIdentities(TimeSpan.FromSeconds(1)).Complete);
        Assert.Equal(1, creations);
        Assert.Equal(1, session.Opens);
        reader.Dispose();
        reader.Dispose();
        Assert.Equal(1, session.Closes);
        Assert.Throws<ObjectDisposedException>(() => reader.Read(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void FailedOpenClosesPartialSessionAndReportsIncompleteCatalogWithoutFallback()
    {
        var session = new Session { FailOpen = true };
        using var reader = new HardwareMonitorCoreReader(() => session);
        var value = reader.Read(TimeSpan.FromSeconds(1));
        Assert.False(value.CatalogComplete);
        Assert.Null(value.MemoryTemperatureCelsius);
        Assert.Null(value.CpuTemperatureCelsius);
        Assert.Empty(value.SystemFans);
        Assert.Equal(HardwareMonitorCoreReader.ProviderName, value.ProviderState.Provider);
        Assert.Equal(1, session.Closes);
    }

    [Fact]
    public void InvalidBudgetDoesNotCreateOrOpenSession()
    {
        using var reader = new HardwareMonitorCoreReader(() => throw new Xunit.Sdk.XunitException("Unexpected open"));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(TimeSpan.FromSeconds(31)));
    }

    private sealed class Session : IHardwareMonitorCoreSession
    {
        public int Opens, Closes;
        public bool FailOpen;
        public IList<IHardware> Hardware => Array.Empty<IHardware>();
        public void Open() { Opens++; if (FailOpen) throw new IOException("driver unavailable"); }
        public void Close() => Closes++;
    }
}

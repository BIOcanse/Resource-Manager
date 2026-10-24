using LibreHardwareMonitor.Hardware;
using ResourceManager.ReadWrite.Windows.Platform;

namespace Resource_Manager_APP.Tests;

public sealed class HardwareMonitorCoreReaderMemoryIsolationTests
{
    [Fact]
    public void FanIdentityReadSkipsMemoryUpdatesWhileFullReadRetainsMemoryAndFanCatalogs()
    {
        var motherboard = new FakeHardware("Board", HardwareType.Motherboard, "mainboard", null);
        var memory = new FakeHardware("Memory", HardwareType.Memory, "memory", motherboard);
        var memoryChild = new FakeHardware("Memory child", HardwareType.SuperIO, "memory-child", memory);
        memory.SubHardware = [memoryChild];
        memory.Sensors = [new FakeSensor(memory, "DIMM temperature", "memory", "dimm", "temperature", "0", SensorType.Temperature, 42f)];

        var superIo = new FakeHardware("SuperIO", HardwareType.SuperIO, "lpc", motherboard);
        superIo.Sensors = [new FakeSensor(superIo, "System Fan #1", "lpc", "fan", "1", "0", SensorType.Fan, 1200f)];
        motherboard.SubHardware = [memory, superIo];

        using var reader = new HardwareMonitorCoreReader(() => new Session([motherboard]));
        var fanCatalog = reader.ReadFanIdentities(TimeSpan.FromSeconds(1));

        Assert.True(fanCatalog.Complete);
        Assert.Single(fanCatalog.Identities);
        Assert.Equal(0, memory.UpdateCount);
        Assert.Equal(0, memoryChild.UpdateCount);

        var snapshot = reader.Read(TimeSpan.FromSeconds(1));

        Assert.Equal(42d, snapshot.MemoryTemperatureCelsius);
        Assert.Equal(1, memory.UpdateCount);
        Assert.Equal(1, memoryChild.UpdateCount);
        Assert.Equal(snapshot.SystemFans.Select(fan => fan.IdentityKey),
            fanCatalog.Identities.Select(fan => fan.IdentityKey));
    }

    private sealed class Session(IList<IHardware> hardware) : IHardwareMonitorCoreSession
    {
        public IList<IHardware> Hardware { get; } = hardware;
        public void Open() { }
        public void Close() { }
    }

    private sealed class FakeHardware(string name, HardwareType hardwareType, string identifier, IHardware? parent) : IHardware
    {
        public int UpdateCount { get; private set; }
        public HardwareType HardwareType { get; } = hardwareType;
        public Identifier Identifier { get; } = new(identifier);
        public string Name { get; set; } = name;
        public IHardware Parent { get; } = parent!;
        public ISensor[] Sensors { get; set; } = [];
        public IHardware[] SubHardware { get; set; } = [];
        public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();
        public event SensorEventHandler SensorAdded { add { } remove { } }
        public event SensorEventHandler SensorRemoved { add { } remove { } }

        public string GetReport() => string.Empty;
        public void Update() => UpdateCount++;
        public void Accept(IVisitor visitor) { }
        public void Traverse(IVisitor visitor) { }
    }

    private sealed class FakeSensor(
        IHardware hardware,
        string name,
        string hardwareIdentifier,
        string category,
        string item,
        string index,
        SensorType sensorType,
        float value) : ISensor
    {
        public IControl Control => null!;
        public IHardware Hardware { get; } = hardware;
        public Identifier Identifier { get; } = new(new Identifier(hardwareIdentifier), category, item, index);
        public int Index => 0;
        public bool IsDefaultHidden => false;
        public float? Max => value;
        public float? Min => value;
        public string Name { get; set; } = name;
        public IReadOnlyList<IParameter> Parameters { get; } = [];
        public SensorType SensorType { get; } = sensorType;
        public float? Value => value;
        public IEnumerable<SensorValue> Values { get; } = [];
        public TimeSpan ValuesTimeWindow { get; set; }

        public void ResetMin() { }
        public void ResetMax() { }
        public void ClearValues() { }
        public void Accept(IVisitor visitor) { }
        public void Traverse(IVisitor visitor) { }
    }
}

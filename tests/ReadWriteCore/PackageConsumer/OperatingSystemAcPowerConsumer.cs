using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.SystemInformation;

internal static class OperatingSystemAcPowerConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var reader = new RecordedReader(new WindowsAcPowerStatusReading(1));
        using var owner = new WindowsOperatingSystemInformationOwner(reader, TimeSpan.FromSeconds(2));
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        if (!core.Catalog.Fields.IsEmpty || reader.Reads != 0)
            throw new InvalidDataException("OS AC-power construction/catalog access sampled the source.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.AcPower, timeout.Token);
        var capability = core.Catalog.Fields.Single(field => field.Definition.Name == "machine_all_ac_power_actual");
        if (capability.Field.DeviceIdentity != WindowsOperatingSystemInformationOwner.MachineObjectIdentity
            || capability.Definition.Unit != "state" || capability.Definition.ValueKind != "boolean"
            || !capability.Read.Supported || !capability.Read.Dependencies.IsEmpty || capability.Write.Supported
            || capability.ReadSource != "windows.get-system-power-status")
            throw new InvalidDataException("Machine AC-power canonical field contract mismatch.");
        if (reader.Reads != 1) throw new InvalidDataException("Catalog access sampled the recorded AC source again.");

        reader.Value = new WindowsAcPowerStatusReading(0);
        var reading = (await core.CollectAsync([capability.Field], timeout.Token)).Single();
        if (reading.Status != FieldObservationStatus.Current || reading.Value?.Boolean != false
            || reading.ObservedAt is null || core.ReadCached(capability.Field) != reading || reader.Reads != 2)
            throw new InvalidDataException("Explicit AC-only collection did not publish the recorded offline state to the same current.");
        Console.WriteLine($"Actual ReadWriteCore package AC field: value={reading.Value!.Boolean}; status={reading.Status}; source={reading.SourceId}; same-current=yes; no WMI/real native fallback.");
    }

    private sealed class RecordedReader(WindowsAcPowerStatusReading? value) : IWindowsAcPowerStatusReader
    {
        private readonly object gate = new();
        private WindowsAcPowerStatusReading? current = value;
        private int reads;
        internal WindowsAcPowerStatusReading? Value
        {
            get { lock (gate) return current; }
            set { lock (gate) current = value; }
        }
        internal int Reads => Volatile.Read(ref reads);

        public WindowsAcPowerStatusReading? Read()
        {
            Interlocked.Increment(ref reads);
            return Value;
        }
    }
}

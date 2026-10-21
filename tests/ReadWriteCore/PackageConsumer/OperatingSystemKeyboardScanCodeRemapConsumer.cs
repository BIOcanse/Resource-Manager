using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Dependencies;
using ResourceManager.ReadWrite.Windows.SystemInformation;

internal static class OperatingSystemKeyboardScanCodeRemapConsumer
{
    internal static async Task RunWindowsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        var reader = new RecordedReader(new(true, [new(0x003A, 0x001D)]));
        using var owner = WindowsOperatingSystemInformationOwner.CreateWithKeyboardScanCodeRemapReader(reader, TimeSpan.FromSeconds(2));
        using var http = new HttpClient();
        var core = new ReadWriteCore(owner.CreateBackend(), new DependencyActions(http, []));
        if (!core.Catalog.Fields.IsEmpty || reader.Reads != 0)
            throw new InvalidDataException("OS keyboard remap construction/catalog access sampled a source.");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await owner.CaptureSelectedInformationAsync(WindowsOperatingSystemInformationFields.KeyboardRemap, timeout.Token);
        var capability = core.Catalog.Fields.Single(field => field.Definition.Name == "keyboard_all_scan_code_remap_configured");
        if (capability.Field.DeviceIdentity != WindowsOperatingSystemInformationOwner.MachineObjectIdentity
            || capability.Definition.ValueKind != "structured" || capability.Definition.Unit != "map"
            || !capability.Read.Supported || !capability.Read.Dependencies.IsEmpty || capability.Write.Supported
            || capability.ReadSource != "windows.registry.keyboard-layout.scancode-map")
            throw new InvalidDataException("Machine keyboard-remap canonical field contract mismatch.");
        if (reader.Reads != 1) throw new InvalidDataException("Catalog access sampled the recorded registry source again.");

        reader.Value = new(true, [new(0x003A, 0x001E), new(0xE052, 0x0000)]);
        var reading = (await core.CollectAsync([capability.Field], timeout.Token)).Single();
        var map = reading.Value?.Structure;
        if (reading.Status != FieldObservationStatus.Current || reading.ObservedAt is null
            || !map.HasValue
            || map.Value.GetProperty("configured").GetBoolean() != true
            || map.Value.GetProperty("entries").GetArrayLength() != 2
            || map.Value.GetProperty("entries")[0].GetProperty("targetScanCode").GetString() != "0x001E"
            || map.Value.GetProperty("entries")[1].GetProperty("sourceScanCode").GetString() != "0xE052"
            || core.ReadCached(capability.Field) != reading || reader.Reads != 2)
            throw new InvalidDataException("Explicit keyboard-only collection did not publish the recorded map to the same current.");
        Console.WriteLine($"Actual ReadWriteCore package keyboard-remap field: entries={map!.Value.GetProperty("entries").GetArrayLength()}; status={reading.Status}; source={reading.SourceId}; same-current=yes; no registry/WMI fallback.");
    }

    private sealed class RecordedReader(WindowsKeyboardScanCodeRemapReading? value) : IWindowsKeyboardScanCodeRemapReader
    {
        private WindowsKeyboardScanCodeRemapReading? current = value;
        private int reads;
        internal WindowsKeyboardScanCodeRemapReading? Value
        {
            get => Volatile.Read(ref current);
            set => Volatile.Write(ref current, value);
        }
        internal int Reads => Volatile.Read(ref reads);
        public WindowsKeyboardScanCodeRemapReading? Read()
        {
            Interlocked.Increment(ref reads);
            return Value;
        }
    }
}

using System.Text.Json;
using ResourceManager.ReadWrite.Vendors.Amd;

internal static class AmdDesktopGpuControlOwnerConsumer
{
    // Recorded setters only: does not Probe real hardware, install drivers, or invoke native tuning.
    internal static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rm-amd-owner-consumer-" + Guid.NewGuid());
        var path = Path.Combine(folder, "baseline.json");
        var gpu = new AmdDesktopGpuControlTarget(1, AmdDesktopGpuControlRoute.WindowsAmdFixedDiscrete, ExpectedPnp: "PCI\\AMD");
        var io = new RecordedHardware();
        try
        {
            using (var passiveReal = new AmdDesktopGpuControlResource(new AdlxTuningHardware(), new(path))) { }
            using (var owner = new AmdDesktopGpuControlResource(io, new(path)))
            {
                Require(io.Reads == 0 && io.Writes == 0 && !File.Exists(path), "Construction must stay passive.");
                var field = AmdDesktopGpuControlField.PowerLimitOffset;
                Require(!owner.Probe(gpu with { Route = AmdDesktopGpuControlRoute.Rejected }, field).IsAdmitted && io.Reads == 0, "Rejected route touched hardware.");
                Require(owner.Probe(gpu, field).Range == new AmdDesktopGpuControlRange(-10, 14, 2, "%"), "Driver range/units changed.");
                Require((await owner.WriteAsync(gpu, field, new(Number: 3))).Message == AmdDesktopGpuControlMessage.InvalidRangeOrStep && io.Writes == 0, "Step rejection changed.");
                io.BeforeWrite = () => Require(JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path))!["PCI\\AMD\n0"] == 0, "Baseline must be persisted before setter.");
                Require((await owner.WriteAsync(gpu, field, new(Number: 6))).Status == AmdDesktopGpuControlStatus.Applied, "Recorded write failed.");
                Require((await owner.ReadAsync(gpu, field)) is { Number: 6, Pnp: "PCI\\AMD", Unit: "%" }, "Typed readback failed.");
            }
            io.BeforeWrite = null;
            using (var restarted = new AmdDesktopGpuControlResource(io, new(path)))
            {
                io.Pnp = "PCI\\OTHER"; var writes = io.Writes;
                Require(!await restarted.RestoreAsync(gpu, AmdDesktopGpuControlField.PowerLimitOffset, new HashSet<AmdDesktopGpuControlField>()) && io.Writes == writes, "PNP mismatch allowed restore.");
                io.Pnp = "PCI\\AMD";
                Require(await restarted.RestoreAsync(gpu, AmdDesktopGpuControlField.PowerLimitOffset, new HashSet<AmdDesktopGpuControlField>()) && io.Values[0] == 0, "Restart lost original baseline.");
                var fan = gpu with { Kind = AmdDesktopGpuControlObjectKind.Fan };
                Require((await restarted.WriteAsync(fan, AmdDesktopGpuControlField.FanZeroRpm, new(Toggle: false))).Status == AmdDesktopGpuControlStatus.Applied, "Toggle write failed.");
                Require((await restarted.ReadAsync(fan, AmdDesktopGpuControlField.FanZeroRpm)) is { Toggle: false, Number: null }, "Toggle became numeric.");
                Require(await restarted.RestoreAsync(fan, AmdDesktopGpuControlField.FanZeroRpm, new HashSet<AmdDesktopGpuControlField>()), "Toggle restore failed.");
                io.Values[1] = 1600;
                Require((await restarted.WriteAsync(gpu, AmdDesktopGpuControlField.CoreClockMinimum, new(Number: 500))).Status == AmdDesktopGpuControlStatus.Applied, "Minimum set failed.");
                Require((await restarted.WriteAsync(gpu, AmdDesktopGpuControlField.CoreClockMaximum, new(Number: 1000))).Status == AmdDesktopGpuControlStatus.Applied, "Maximum set failed.");
                Require(!await restarted.RestoreAsync(gpu, AmdDesktopGpuControlField.CoreClockMinimum, new HashSet<AmdDesktopGpuControlField> { AmdDesktopGpuControlField.CoreClockMaximum }), "Configured sibling was changed.");
                io.SetterFields.Clear();
                Require(await restarted.RestoreAsync(gpu, AmdDesktopGpuControlField.CoreClockMinimum, new HashSet<AmdDesktopGpuControlField>()), "Paired restore failed.");
                Require(io.SetterFields.SequenceEqual(new[] { 2, 1 }), "Blocking sibling restore order changed.");
                Require(await restarted.RestoreAsync(gpu, AmdDesktopGpuControlField.CoreClockMaximum, new HashSet<AmdDesktopGpuControlField>()), "Sibling baseline was erased too early.");
            }
            var occupied = Path.Combine(folder, "occupied"); File.WriteAllText(occupied, "file"); var originalWrites = io.Writes;
            using (var saveFailure = new AmdDesktopGpuControlResource(io, new(Path.Combine(occupied, "baseline.json"))))
                Require((await saveFailure.WriteAsync(gpu, AmdDesktopGpuControlField.PowerLimitOffset, new(Number: 6))).Status == AmdDesktopGpuControlStatus.Failed && io.Writes == originalWrites, "Failed persistence allowed setter.");
            io.Mode = AdlxGfxMode.Offset;
            using (var offset = new AmdDesktopGpuControlResource(io, new(path)))
            {
                Require(!offset.Probe(gpu, AmdDesktopGpuControlField.CoreClockMaximum).CanWrite, "Offset generation accepted absolute maximum.");
                Require((await offset.WriteAsync(gpu, AmdDesktopGpuControlField.CoreClockOffset, new(Number: 150))).Status == AmdDesktopGpuControlStatus.Applied, "Offset write failed.");
                Require(await offset.RestoreAsync(gpu, AmdDesktopGpuControlField.CoreClockOffset, new HashSet<AmdDesktopGpuControlField>()) && io.Values[8] == 100, "Offset restore changed semantics.");
            }
            Require(io.Disposals == 0, "Borrowed hardware was disposed.");
            Console.WriteLine("AMD desktop GPU control owner consumer passed: recorded admission/range/step/toggle/PNP, persistence-before-set, restart restore, strict sibling order and borrowed ADLX lifetime; no real tuning/probe/driver installation.");
        }
        finally { if (Directory.Exists(folder))
        {
            // These fixtures create only files and empty immediate child directories.
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            foreach (var child in Directory.EnumerateDirectories(folder)) Directory.Delete(child);
            Directory.Delete(folder);
        } }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    private sealed class RecordedHardware : IAdlxTuningHardware, IDisposable
    {
        public int[] Values = [0, 500, 2400, 1000, 1800, 20, 1000, 1, 100, -50];
        public string Pnp = "PCI\\AMD"; public int Reads, Writes, Disposals;
        public AdlxGfxMode Mode = AdlxGfxMode.Absolute;
        public Action? BeforeWrite; public List<int> SetterFields = [];
        public AdlxTuningValue Read(int? adapterIndex, int field)
        {
            Require(adapterIndex == 1, "Original Windows adapter index changed."); Reads++;
            if ((field is 2 or 3 && Mode != AdlxGfxMode.Absolute) || (field >= 8 && Mode != AdlxGfxMode.Offset)) throw new IOException("wrong mode");
            return new(Pnp, Values[field], field == 7 ? 0 : field is 1 or 2 ? 100 : -10,
                field == 7 ? 1 : field is 1 or 2 ? 3000 : field >= 8 ? 200 : 14,
                field == 7 || field is 1 or 2 || field >= 8 ? 1 : 2, Mode);
        }
        public void Write(string pnp, int field, int value)
        {
            Require(pnp == Pnp, "Setter PNP changed."); BeforeWrite?.Invoke();
            if (Mode == AdlxGfxMode.Absolute && (field == 1 && value > Values[2] || field == 2 && value < Values[1])) throw new IOException("inverted clocks");
            SetterFields.Add(field); Writes++; Values[field] = value;
        }
        public void Dispose() => Disposals++;
    }
}

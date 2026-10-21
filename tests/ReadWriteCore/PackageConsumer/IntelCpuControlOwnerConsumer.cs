using System.Text.Json;
using ResourceManager.ReadWrite.Vendors.Intel;

internal static class IntelCpuControlOwnerConsumer
{
    // Records the low-level module boundary only; never opens a real driver or sends an actual MSR/OC command.
    internal static async Task RunAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "rm-intel-owner-consumer-" + Guid.NewGuid().ToString("N"));
        var baseline = Path.Combine(folder, "baseline.json");
        var state = new RecordedState();
        var original = state.Limits;
        var opens = 0;
        var target = new IntelCpuControlTarget("cpu:package-consumer-recorded");
        var capability = new IntelCpuControlCapability("cpu.power-limit", new(1, 300, 1, "W"));
        try
        {
            using (var actualFactory = IntelCpuControlResource.CreateDefault(new(Path.Combine(folder, "missing-module.bin"),
                Path.Combine(folder, "missing-library.dll"), baseline)))
                Require(!Directory.Exists(folder), "Actual default factory must be lazy; no driver/persistence opened at construction.");
            using var resource = new IntelCpuControlResource(() => { opens++; return new RecordedHardware(state); }, baseline);
            Require(opens == 0, "Recorded lower resource opens lazily.");
            Require(resource.Probe(target, capability).CanWrite && state.Writes == 0 && !File.Exists(baseline),
                "Original probe performs no setting or baseline publication.");
            Require((await resource.WriteAsync(target, capability, new(capability.Id, 45, "W"), default)).Status == "applied",
                "SDK original owner performs the recorded setting and verified readback.");
            Require(IntelRaplEncoding.ReadPower(state.Limits, state.Units, false) == 45
                && (state.Limits & ~0xffffUL) == (original & ~0xffffUL), "Preserve original sibling and reserved bits.");
            var read = await resource.ReadAsync(target, capability, default);
            Require(read is not null && read.Number == 45 && read.Unit == "W", "Raw public read uses the same actual recorded register.");
            resource.ResetDetection();
            Require(state.Closes == 1, "Reset closes one owned lower session.");
            Require(await resource.RestoreAsync(target, capability, new HashSet<string>(), default), "Same owner restores original durable baseline.");
            Require(opens == 2 && state.Limits == original
                && JsonSerializer.Deserialize<Dictionary<string, ulong>>(File.ReadAllText(baseline))!.Count == 0,
                "Reopen after reset and remove only verified restore baseline.");
            resource.Dispose(); resource.Dispose();
            Require(state.Closes == 2, "Each owned session closes once; terminal disposal is idempotent.");
            Console.WriteLine("Intel CPU owner recorded lower-module contract: setting=45 W; masked-readback=verified; reset=reopened; restore=verified; ownedSessions=2; closedSessions=2; actualFactory=constructed-only; actualDriver=not-invoked; canonicalWrites=not-added.");
        }
        finally { if (Directory.Exists(folder))
        {
            // These fixtures create only files and empty immediate child directories.
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            foreach (var child in Directory.EnumerateDirectories(folder)) Directory.Delete(child);
            Directory.Delete(folder);
        } }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed class RecordedState
    { internal ulong Units = 3UL | (10UL << 16), Limits = 0x1234abcd923480a0; internal int Writes, Closes; }
    private sealed class RecordedHardware(RecordedState state) : IIntelCpuHardware
    {
        private bool disposed;
        public ulong ReadMsr(uint register)
        { ObjectDisposedException.ThrowIf(disposed, this); return register switch { 0x606 => state.Units, 0x610 => state.Limits, _ => throw new IOException() }; }
        public void WriteMsr(uint register, ulong value)
        { ObjectDisposedException.ThrowIf(disposed, this); if (register != 0x610) throw new IOException(); state.Writes++; state.Limits = value; }
        public bool CanWriteMsr(uint register) => register == 0x610;
        public void Dispose() { if (disposed) return; disposed = true; state.Closes++; }
    }
}

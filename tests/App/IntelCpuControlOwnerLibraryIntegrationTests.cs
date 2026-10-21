using System.Text.Json;
using ResourceManager.ReadWrite;
using ResourceManager.ReadWrite.Vendors.Intel;

namespace Resource_Manager_APP.Tests;

public sealed class IntelCpuControlOwnerLibraryIntegrationTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "rm-intel-owner-" + Guid.NewGuid().ToString("N"));
    private string Baseline => Path.Combine(folder, "baseline.json");
    private static readonly IntelCpuControlTarget Target = new("cpu:recorded");
    private static IntelCpuControlCapability Power => new("cpu.power-limit", new(1, 300, 1, "W"));

    [Fact]
    public void ActualDefaultFactoryAndUnsupportedRoutesDoNotOpenDrivers()
    {
        Assert.Same(typeof(ReadWriteCore).Assembly, typeof(IntelCpuControlResource).Assembly);
        using var resource = IntelCpuControlResource.CreateDefault(new(Path.Combine(folder, "missing-module.bin"),
            Path.Combine(folder, "missing-library.dll"), Baseline));
        Assert.False(Directory.Exists(folder));
        Assert.Equal("not-implemented", resource.Probe(Target, new("cpu.power-limit-mmio")).UnavailableKind);
        Assert.False(resource.Probe(new("other-cpu", false), Power).CanWrite);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task OriginalPersistencePrecedesSettingsAndRestoreKeepsSiblingBits()
    {
        var state = new RecordedState();
        var original = state.Limits;
        state.BeforeSetting = () => Assert.True(File.Exists(Baseline));
        using (var resource = new IntelCpuControlResource(() => new RecordedHardware(state), Baseline))
        {
            Assert.True(resource.Probe(Target, Power).CanWrite);
            Assert.False(File.Exists(Baseline));
            Assert.Equal("applied", (await resource.WriteAsync(Target, Power, new(Power.Id, 45, "W"), default)).Status);
            Assert.Equal(original & ~0xffffUL, state.Limits & ~0xffffUL);
        }
        state.Limits = IntelRaplEncoding.SetPower(state.Limits, state.Units, true, 90);
        var sibling = state.Limits & ~0xffffUL;
        using var restarted = new IntelCpuControlResource(() => new RecordedHardware(state), Baseline);
        Assert.True(await restarted.RestoreAsync(Target, Power, new HashSet<string> { "cpu.short-power-limit" }, default));
        Assert.Equal(original & 0xffffUL, state.Limits & 0xffffUL);
        Assert.Equal(sibling, state.Limits & ~0xffffUL);
        Assert.Empty(JsonSerializer.Deserialize<Dictionary<string, ulong>>(File.ReadAllText(Baseline))!);
    }

    [Fact]
    public async Task FailedBaselinePublicationPreventsAnySettingWrite()
    {
        Directory.CreateDirectory(folder);
        var blocker = Path.Combine(folder, "not-a-directory");
        File.WriteAllText(blocker, "original file");
        var state = new RecordedState();
        using var resource = new IntelCpuControlResource(() => new RecordedHardware(state), Path.Combine(blocker, "baseline.json"));
        Assert.Equal("failed", (await resource.WriteAsync(Target, Power, new(Power.Id, 45, "W"), default)).Status);
        Assert.Equal(0, state.SettingWrites);
        Assert.Equal("original file", File.ReadAllText(blocker));
    }

    [Fact]
    public async Task ResetReopensOneOwnedSessionAndTerminalDisposeClosesExactlyOnce()
    {
        var state = new RecordedState();
        var opens = 0;
        var original = state.Limits;
        var resource = new IntelCpuControlResource(() => { opens++; return new RecordedHardware(state); }, Baseline);
        Assert.Equal(0, opens);
        Assert.Equal("applied", (await resource.WriteAsync(Target, Power, new(Power.Id, 45), default)).Status);
        resource.ResetDetection();
        Assert.Equal(1, state.Closes);
        Assert.True(await resource.RestoreAsync(Target, Power, new HashSet<string>(), default));
        Assert.Equal(2, opens);
        Assert.Equal(original, state.Limits);
        resource.Dispose();
        resource.Dispose();
        Assert.Equal(2, state.Closes);
        Assert.Throws<ObjectDisposedException>(() => resource.Probe(Target, Power));
        Assert.Throws<ObjectDisposedException>(resource.ResetDetection);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => resource.ReadAsync(Target, Power, default));
        Assert.Equal(2, opens);
    }

    [Fact]
    public async Task DisposalDrainsAnAcceptedOriginalGateOperationBeforeClosingItsSession()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var disposalRequested = new ManualResetEventSlim();
        var state = new RecordedState();
        state.BeforeRead = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Recorded read barrier did not release.");
        };
        var resource = new IntelCpuControlResource(() => new RecordedHardware(state), Baseline);
        var probe = Task.Run(() => resource.Probe(Target, Power));
        Task? disposal = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            disposal = Task.Run(() => { disposalRequested.Set(); resource.Dispose(); });
            Assert.True(disposalRequested.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, state.Closes);
        }
        finally { release.Set(); }
        await probe;
        if (disposal is not null) await disposal;
        Assert.Equal(1, state.Closes);
        Assert.Throws<ObjectDisposedException>(() => resource.Probe(Target, Power));
        resource.Dispose();
        Assert.Equal(1, state.Closes);
    }
    [Theory]
    [InlineData(0, "cpu.core-voltage-offset")]
    [InlineData(1, "cpu.igpu-voltage-offset")]
    [InlineData(2, "cpu.cache-voltage-offset")]
    [InlineData(3, "cpu.system-agent-voltage-offset")]
    public async Task OriginalMailboxPlaneAndRestorePreserveCurrentNonOffsetBits(int plane, string id)
    {
        var state = new RecordedState();
        state.Voltage[plane] = IntelOcMailbox.Encode(-20) | 0x12U;
        using var resource = new IntelCpuControlResource(() => new RecordedHardware(state), Baseline);
        var capability = new IntelCpuControlCapability(id, new(-250, 100, 1, "mV"));
        Assert.True(resource.Probe(Target, capability).CanWrite);
        Assert.Equal(0, state.SettingWrites); // Query command 0x10 is not a setting command 0x11.
        Assert.Equal("applied", (await resource.WriteAsync(Target, capability, new(id, -100, "mV"), default)).Status);
        state.Voltage[plane] = (state.Voltage[plane] & ~0x1fffffU) | 0x345U;
        Assert.True(await resource.RestoreAsync(Target, capability, new HashSet<string>(), default));
        Assert.Equal(IntelOcMailbox.Encode(-20) | 0x345U, state.Voltage[plane]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyNormalizationKeepsOriginalAmbiguityRefusal(bool conflict)
    {
        Directory.CreateDirectory(folder);
        var state = new RecordedState();
        var original = state.Limits;
        var originalMasked = original & IntelRaplEncoding.FieldMask(false, false);
        var stored = JsonSerializer.Serialize(new Dictionary<string, ulong>
        { [$"{Target.Id}\nold name\n{Power.Id}"] = originalMasked,
          [$"{Target.Id}\nother name\n{Power.Id}"] = conflict ? 0 : originalMasked });
        File.WriteAllText(Baseline, stored);
        state.Limits = IntelRaplEncoding.SetPower(original, state.Units, false, 45);
        using var resource = new IntelCpuControlResource(() => new RecordedHardware(state), Baseline);
        Assert.Equal(!conflict, await resource.RestoreAsync(Target, Power, new HashSet<string>(), default));
        if (conflict) { Assert.Equal(stored, File.ReadAllText(Baseline)); Assert.Equal(0, state.SettingWrites); }
        else { Assert.Equal(original, state.Limits); Assert.Empty(JsonSerializer.Deserialize<Dictionary<string, ulong>>(File.ReadAllText(Baseline))!); }
    }

    [Fact]
    public async Task CancellationAndFailedReadbackCannotReportApplied()
    {
        var state = new RecordedState { IgnoreWrites = true };
        using var resource = new IntelCpuControlResource(() => new RecordedHardware(state), Baseline);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resource.WriteAsync(Target, Power, new(Power.Id, 45), cancellation.Token));
        Assert.Equal(0, state.Reads);
        Assert.Equal("failed", (await resource.WriteAsync(Target, Power, new(Power.Id, 45), default)).Status);
        Assert.NotEmpty(JsonSerializer.Deserialize<Dictionary<string, ulong>>(File.ReadAllText(Baseline))!);
    }

    [Fact]
    public void HostVerifiedMessagesRetainOriginalG6Formatting()
    {
        var arguments = new object?[] { 45.125, "W" };
        Assert.Equal($"已写入并回读；实际硬件档位 {45.125:G6} W。",
            ResourceManager.App.Infrastructure.Control.Writers.IntelCpuControlCopy.Describe(IntelCpuControlMessage.PowerVerified, arguments));
        Assert.Equal($"已写入并回读 {-100.125:G6} mV。",
            ResourceManager.App.Infrastructure.Control.Writers.IntelCpuControlCopy.Describe(IntelCpuControlMessage.VoltageVerified, new object?[] { -100.125 }));
    }

    private sealed class RecordedState
    {
        internal ulong Units = 3UL | (10UL << 16), Limits = 0x1234abcd923480a0, Mailbox;
        internal uint[] Voltage = new uint[4];
        internal int Reads, SettingWrites, Closes;
        internal bool IgnoreWrites;
        internal Action? BeforeSetting, BeforeRead;
        internal bool Reading;
    }
    private sealed class RecordedHardware(RecordedState state) : IIntelCpuHardware
    {
        private bool disposed;
        public ulong ReadMsr(uint register)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            state.Reading = true;
            try
            {
                state.BeforeRead?.Invoke(); state.Reads++;
                return register switch { 0x606 => state.Units, 0x610 => state.Limits, 0x150 => state.Mailbox, _ => throw new IOException() };
            }
            finally { state.Reading = false; }
        }
        public void WriteMsr(uint register, ulong value)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (register == 0x610) { state.BeforeSetting?.Invoke(); state.SettingWrites++; if (!state.IgnoreWrites) state.Limits = value; return; }
            if (register != 0x150) throw new IOException();
            var plane = (int)((value >> 40) & 0xff);
            if (((value >> 32) & 0xff) == 0x11)
            { state.BeforeSetting?.Invoke(); state.SettingWrites++; if (!state.IgnoreWrites) state.Voltage[plane] = (uint)value; }
            state.Mailbox = state.Voltage[plane];
        }
        public bool CanWriteMsr(uint register) => register is 0x150 or 0x610;
        public void Dispose() { Assert.False(state.Reading); if (disposed) return; disposed = true; state.Closes++; }
    }
    public void Dispose() { if (Directory.Exists(folder))
        {
            // These fixtures create only files and empty immediate child directories.
            foreach (var file in Directory.EnumerateFiles(folder)) File.Delete(file);
            foreach (var child in Directory.EnumerateDirectories(folder)) Directory.Delete(child);
            Directory.Delete(folder);
        } }
}

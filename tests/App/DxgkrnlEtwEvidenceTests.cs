using Microsoft.Extensions.Logging.Abstractions;
using ResourceManager.App.Infrastructure.Telemetry.Etw;
using ResourceManager.ReadWrite.Windows.Devices;

namespace Resource_Manager_APP.Tests;

public sealed class DxgkrnlEtwEvidenceTests
{
    [Fact]
    public void ProducerRejectsPreBirthEventsAndReusedEvidenceWithoutClearingOtherProcesses()
    {
        ulong birth = 100;
        using var source = new DxgkrnlVidMmEtwTelemetryZone(NullLogger<DxgkrnlVidMmEtwTelemetryZone>.Instance,
            pid => new ProcessLease(pid, pid == 41 ? birth : 300));
        var now = DateTimeOffset.UtcNow;
        source.ObserveProcessMemory(41, "old.exe", 4096, "usage", 100, now);
        source.ObserveProcessMemory(42, "healthy.exe", 2048, "usage", 301, now);
        Assert.Equal(2, source.ReadCurrentEvidence(now, TimeSpan.FromSeconds(45)).Count);
        birth = 200;
        source.ObserveProcessMemory(41, "late-old.exe", 9000, "usage", 199, now);
        var healthy = Assert.Single(source.ReadCurrentEvidence(now, TimeSpan.FromSeconds(45)));
        Assert.Equal(42, healthy.ProcessId);
        source.ObserveProcessMemory(41, "replacement.exe", 1024, "usage", 201, now);
        var replacement = source.ReadCurrentEvidence(now, TimeSpan.FromSeconds(45)).Single(row => row.ProcessId == 41);
        Assert.Equal(200UL, replacement.ProcessStartKey);
        Assert.Equal("replacement", replacement.ProcessName);
        Assert.Equal(1024, replacement.ValueBytes);
    }

    private sealed class ProcessLease(uint pid, ulong birth) : IWindowsNetworkEndpointProcessLease
    {
        public uint ProcessId => pid;
        public bool TryReadCreationFileTime(out ulong result) { result = birth; return true; }
        public void Dispose() { }
    }
}

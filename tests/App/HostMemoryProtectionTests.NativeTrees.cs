using System.Collections.Concurrent;
using System.Diagnostics;
using ResourceManager.App.Application.MemoryProtection;
using ResourceManager.App.Domain.GpuPlacement;
using ResourceManager.App.Infrastructure.MemoryProtection;

namespace Resource_Manager_APP.Tests;

public sealed partial class HostMemoryProtectionTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public async Task OwnedParentTreeTerminatesLeavesFirstAndPreservesWhitelistedProcesses(bool protectRoot, bool protectChild, bool exitedRoot)
    {
        var executor = new TrackingExecutor();
        await using var fixture = await Fixture.StartAsync(executor);
        await fixture.BuildReserve();
        var signal = Path.Combine(fixture.ReserveDirectory, "spawn.signal");
        var receipt = Path.Combine(fixture.ReserveDirectory, "owned-children.txt");
        var ping = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ping.exe");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var script = $"while (-not [IO.File]::Exists('{Escape(signal)}')) {{ Start-Sleep -Milliseconds 25 }}; "
            + $"$children = @(1..3 | ForEach-Object {{ Start-Process -FilePath '{Escape(ping)}' -ArgumentList '-t','127.0.0.1' -WindowStyle Hidden -PassThru }}); "
            + $"[IO.File]::WriteAllText('{Escape(receipt)}', (($children | ForEach-Object {{ $_.Id.ToString() + ':' + $_.StartTime.ToUniversalTime().ToFileTimeUtc().ToString() }}) -join ',')); Start-Sleep -Seconds 120";
        var info = new ProcessStartInfo(powershell) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-Command"); info.ArgumentList.Add(script);
        using var parent = System.Diagnostics.Process.Start(info)!;
        var ownedChildren = new List<Process>();
        try
        {
            var root = HostMemoryProtectionExecutorTests.Identity(parent) with { ParentProcessId = Environment.ProcessId,
                ExecutablePath = powershell, Name = "powershell", PrivateCommitBytes = 100 };
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var ancestor = HostMemoryProtectionExecutorTests.Identity(self) with { IsResourceManager = true, PrivateCommitBytes = 100 };
            fixture.Source.Publish(200, 20, [ancestor, root]);
            await fixture.CreatePoint(ensureReserve: false);
            File.WriteAllText(signal, "spawn");
            await Until(() => File.Exists(receipt) && new FileInfo(receipt).Length > 0);
            foreach (var value in File.ReadAllText(receipt).Split(','))
            {
                var identity = value.Split(':');
                var child = System.Diagnostics.Process.GetProcessById(int.Parse(identity[0]));
                if (HostMemoryProtectionExecutorTests.Identity(child).ProcessStartKey != identity[1])
                { child.Dispose(); throw new InvalidOperationException("The owned child's PID was reused."); }
                ownedChildren.Add(child);
            }
            Assert.Equal(3, ownedChildren.Count);
            var children = ownedChildren.Select(child => HostMemoryProtectionExecutorTests.Identity(child) with
                { ParentProcessId = parent.Id, ExecutablePath = ping, PrivateCommitBytes = 10 }).ToArray();
            if (protectChild)
            {
                children[0] = children[0] with { SoftwareId = "protected-test-child" };
                await fixture.SoftwarePolicies.SaveSoftwarePolicyAsync(GpuPlacementPolicyDefaults.CreateSoftwarePolicy("protected-test-child", "Owned child")
                    with { HostMemoryProtectionNeverTerminate = true }, CancellationToken.None);
            }
            if (exitedRoot) HostMemoryProtectionExecutorTests.StopOwnedChild(parent);
            using (var enabled = await fixture.Patch(new { enabled = true, minimumProcessGrowthBytes = 1073741824L,
                protectedProcesses = protectRoot ? new[] { powershell } : new[] { "explorer.exe" } })) enabled.EnsureSuccessStatusCode();
            await Until(() => fixture.Source.Active == 1);
            fixture.Source.Publish(960, 96, exitedRoot ? [ancestor, .. children] : [ancestor, root, .. children]);
            await Until(async () => (await fixture.Status()).CurrentActionId is null && (await fixture.Status()).DecisionReason == "process-tree-handled");
            var records = (await fixture.Status()).Actions;
            var output = Environment.GetEnvironmentVariable("RM_MEMORY_PROTECTION_WIRE_REPORT");
            if (output is not null && protectRoot && !protectChild && !exitedRoot)
                await File.WriteAllTextAsync(output + ".tree.json", await fixture.Client.GetStringAsync("/api/memory-protection"));
            Assert.Equal(exitedRoot ? 3 : 4, records.Count);
            Assert.All(records, record => { Assert.Equal(parent.Id, record.TreeRoot!.ProcessId); Assert.Equal("processTree", record.TargetKind); Assert.Empty(record.DumpPaths); });
            Assert.Single(records.Select(record => record.ActionId).Distinct());
            Assert.Equal(0, executor.SuspendCalls);
            if (!exitedRoot && !protectRoot) Assert.Equal(parent.Id, executor.Executed.Last());
            Assert.DoesNotContain(Environment.ProcessId, executor.Executed);
            Assert.Equal(protectRoot && !exitedRoot, !parent.HasExited);
            for (var index = 0; index < ownedChildren.Count; index++)
                Assert.Equal(protectChild && index == 0, !ownedChildren[index].HasExited);
        }
        finally
        {
            // Only PIDs returned by the owned parent, with retained Process handles, are touched.
            foreach (var child in ownedChildren) { HostMemoryProtectionExecutorTests.StopOwnedChild(child); child.Dispose(); }
            HostMemoryProtectionExecutorTests.StopOwnedChild(parent);
        }
        static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuntimeReserveFailureTerminatesOwnedProcessAndPublishesReserveError()
    {
        using var child = HostMemoryProtectionExecutorTests.StartOwnedChild();
        var reserve = new FaultingReserve();
        try
        {
            await using var fixture = await Fixture.StartAsync(new WindowsHostMemoryProtectionExecutor(), reserve);
            await fixture.CreatePoint();
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await using var changes = fixture.Protection.SubscribeAsync(lifetime.Token).GetAsyncEnumerator();
            Assert.True(await changes.MoveNextAsync());
            using (var enabled = await fixture.Patch(new { enabled = true })) enabled.EnsureSuccessStatusCode();
            await Until(() => fixture.Source.Active == 2);
            reserve.Failed = true;
            fixture.Source.Publish(960, 96, [HostMemoryProtectionExecutorTests.Identity(child)]);
            await Until(async () => (await fixture.Status()).Actions.Count == 1);
            Assert.True(child.WaitForExit(5000));
            var status = await fixture.Status();
            Assert.True(status.Active); Assert.False(status.Reserve.Built);
            Assert.Equal("reserve-release-failed", Assert.Single(status.Actions).Reason);
            while (await changes.MoveNextAsync())
                if (changes.Current.Events.Any(change => change.Kind == "reserve-error")) break;
            Assert.Contains(changes.Current.Events, change => change.Kind == "reserve-error" && change.Reason == "reserve-release-failed");
        }
        finally { HostMemoryProtectionExecutorTests.StopOwnedChild(child); }
    }

    private sealed class TrackingExecutor : IHostMemoryProtectionExecutor
    {
        public int SuspendCalls;
        public ConcurrentQueue<int> Executed = new();
        private readonly WindowsHostMemoryProtectionExecutor native;
        public TrackingExecutor() => native = new(handle => { SuspendCalls++; return HostMemoryProtectionNative.NtSuspendProcess(handle); });
        public IHostMemoryProtectionTarget Open(HostMemoryProcess process) => new Target(this, process.ProcessId, native.Open(process));
        private sealed class Target(TrackingExecutor owner, int pid, IHostMemoryProtectionTarget inner) : IHostMemoryProtectionTarget
        {
            public string? ExclusionReason => inner.ExclusionReason;
            public HostMemoryActionResult Execute(IHostMemoryProtectionReserve reserve, HostMemoryActionOptions? options = null)
            { owner.Executed.Enqueue(pid); return inner.Execute(reserve, options); }
            public void Dispose() => inner.Dispose();
        }
    }
}

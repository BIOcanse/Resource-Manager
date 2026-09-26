using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.Optimization;
using ResourceManager.App.Infrastructure.GpuPlacement;
using ResourceManager.App.Infrastructure.GpuPlacement.WindowExecution;
using ResourceManager.App.Infrastructure.Optimization;

namespace Resource_Manager_APP.Tests;

internal static class GpuWindowLedgerTestData
{
    internal static GpuWindowActionPrepared Prepared() => new(
        new(new(19608, 134332600000000001, 0x10020, GpuWindowActionMethod.Resize), 100,
            new(10, 20, 80, 60, true, false, false)),
        new(19612, 134332600000000002, Path.Combine(AppContext.BaseDirectory, "WindowWorker.exe")));

    internal static GpuWindowActionResult Restored(GpuWindowActionPrepared prepared) => new(
        GpuWindowActionOutcome.WindowRestored, prepared,
        new(new(GpuWindowCallState.Accepted, null),
            new(GpuWindowReadState.Available, null, prepared.Window.Before with { Width = 81 }),
            new(GpuWindowCallState.Accepted, null),
            new(GpuWindowReadState.Available, null, prepared.Window.Before)),
        null, true, false, new(true, true, 0, false, null, 0, true, null), null);

    internal static GpuWindowActionResult Unknown(GpuWindowActionPrepared prepared) => new(
        GpuWindowActionOutcome.Unresolved, prepared, null, null, true, false,
        new(true, true, 995, true, null, 0, true, null), "Work deadline expired.");

    internal static HostManagerAppliedPlacementReceipt Placement(params HostManagerAppliedRecord[] records)
        => GpuShimPolicyLedgerTests.Placement(records[0]) with { Records = records };

    internal static HostManagerAppliedRecord Policy(GpuWindowActionPrepared prepared)
    {
        var record = GpuShimPolicyRecord.Create("window-ledger-policy", [1], [2]);
        return record with
        {
            Metadata = new Dictionary<string, string>(record.Metadata!)
            {
                ["processId"] = prepared.Window.Request.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["processStartKey"] = prepared.Window.Request.CreationFileTimeUtc.ToString(System.Globalization.CultureInfo.InvariantCulture)
            }
        };
    }

    internal static string NewRoot(string name)
    {
        var parent = Environment.GetEnvironmentVariable("RM_GPU_WINDOW_LEDGER_TEST_ROOT")
            ?? Path.Combine(AppContext.BaseDirectory, "TestArtifacts", "GpuWindowLedger");
        var root = Path.Combine(Path.GetFullPath(parent), name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    internal static HostManagerRollbackStateDocument ReadCanonical(string path)
        => HostManagerRollbackStateEnvelopeCodec.Decode(File.ReadAllBytes(path)).State;

    internal sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
    }

    internal sealed class ControlledCommitter : IHostManagerRollbackStateFileCommitter
    {
        internal HostManagerRollbackStateCommitOutcome? FailNext { get; set; }
        internal int Calls { get; private set; }

        public void Commit(string temporaryPath, string canonicalPath, ReadOnlySpan<byte> expectedImage, bool replaceExisting)
        {
            Calls++;
            var failure = FailNext;
            FailNext = null;
            if (failure != HostManagerRollbackStateCommitOutcome.NotCommitted)
                WindowsHostManagerRollbackStateFileCommitter.Instance.Commit(temporaryPath, canonicalPath, expectedImage, replaceExisting);
            if (failure is { } outcome)
                throw new HostManagerRollbackStateCommitException(outcome, new IOException("Window ledger commit fixture."));
        }
    }
}

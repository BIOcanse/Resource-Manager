using System.Diagnostics;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.Windows.Processes;

namespace Resource_Manager_APP.Tests;

public sealed class ProcessCreationAbsenceTests
{
    [Fact]
    public void SamplingExitIsExcludedWhileUnreadableLiveIdentityBlocksOnlyExitProof()
    {
        using var exited = StartExitedProcess();
        using var live = Process.GetCurrentProcess();
        using var owner = WindowsProcessFieldOwner.CreateDefault();
        owner.Inventory = new Inventory([exited, live]);
        var exceptions = 0;
        void OnException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
        {
            if (args.Exception.StackTrace?.Contains("WindowsProcessFieldOwner.ReadCreation", StringComparison.Ordinal) == true)
                exceptions++;
        }
        AppDomain.CurrentDomain.FirstChanceException += OnException;
        try
        {
            Assert.Equal(ProcessReadStatus.Exited, WindowsProcessFieldOwner.ReadCreation(exited).Status);
            Assert.Null(WindowsProcessFieldOwner.ReadCreation(exited).Creation);
            Assert.Equal(ProcessReadStatus.Current, WindowsProcessFieldOwner.ReadCreation(live).Status);
            bool Capture(Process process, ProcessCreationRead read, out int sample, out TimeSpan cpu, out ProcessInstanceKey? key)
            {
                sample = process.Id; cpu = default;
                key = read.Creation is { } birth ? new(sample, birth) : null;
                return read.Status != ProcessReadStatus.Exited;
            }
            var batch = owner.CaptureBatch<int>(false, new(), Capture, static _ => false);
            Assert.Equal(2U, batch.EnumeratedCount);
            Assert.Equal(1U, batch.ExcludedCount);
            Assert.Equal(0U, batch.SkippedCount);
            Assert.Single(batch.Samples);
            Assert.False(batch.HasUnidentifiedProcesses);
            Assert.Equal(0, exceptions);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= OnException; }

        using var unreadable = Process.GetCurrentProcess();
        owner.Inventory = new Inventory([unreadable]);
        bool Missing(Process process, ProcessCreationRead read, out int sample, out TimeSpan cpu, out ProcessInstanceKey? key)
        { sample = process.Id; cpu = default; key = null; return true; }
        var partial = owner.CaptureBatch<int>(false, new(), Missing, static _ => false);
        Assert.True(partial.HasUnidentifiedProcesses);
        Assert.Equal(0U, partial.ExcludedCount);
        Assert.Single(partial.Samples);
    }

    private static Process StartExitedProcess()
    {
        var process = Process.Start(new ProcessStartInfo("cmd.exe")
        { Arguments = "/d /c exit 0", UseShellExecute = false, CreateNoWindow = true })!;
        Assert.True(process.WaitForExit(10_000));
        return process;
    }
    private sealed class Inventory(Process[] processes) : IWindowsProcessInventoryReader
    { public Process[] GetProcesses() => processes; }
}

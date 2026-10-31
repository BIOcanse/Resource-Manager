using System.ComponentModel;
using System.IO.Pipes;
using System.Text;
using ResourceManager.NativeUi;
using ResourceManager.Shared.Localization;

namespace ResourceManager.NativeWindow.Tests;

// Started without the resident's pipe, the window process hands its command to the resident and exits.
// Every test uses its own pipe and mutex name and a fixture executable, never the installed resident.
public sealed class ResidentHandoffTests
{
    [Fact]
    public void MissingResidentAndUnansweredPipeShowTheExistingLocalizedLaunchFailure()
    {
        foreach (var language in AppLanguage.SupportedIds)
        {
            var text = ToolText.For(language);
            var shown = new List<string>();
            var missing = new ResidentHandoff(TestPipeName(), Path.Combine(FixtureRoot(), "missing", ResidentHandoff.ResidentExecutableName));
            Assert.Equal(2, ResidentHandoffRunner.Run(() => missing.Run("show"), text, shown.Add));
            Assert.False(string.IsNullOrWhiteSpace(Assert.Single(shown)));
            shown.Clear();
            Assert.Equal(2, ResidentHandoffRunner.Run(() => ResidentHandoffOutcome.Unanswered, text, shown.Add));
            Assert.Equal(text.Format(text.LauncherFailedFormat, text.LauncherUiStartFailed), Assert.Single(shown));
            shown.Clear();
            Assert.Equal(0, ResidentHandoffRunner.Run(() => ResidentHandoffOutcome.Signaled, text, shown.Add));
            Assert.Empty(shown);
        }
    }

    [Fact]
    public async Task RunningResidentReceivesExactlyTheCommandOnItsPipe()
    {
        var pipe = TestPipeName();
        var handoff = new ResidentHandoff(pipe, Path.Combine(FixtureRoot(), "absent", ResidentHandoff.ResidentExecutableName));
        using var mutex = new Mutex(true, handoff.MutexName, out var created);
        Assert.True(created);
        await using var server = new NamedPipeServerStream(pipe, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var connected = server.WaitForConnectionAsync(timeout.Token);

        // The fake resident reads while the hand-off writes: a pipe write completes only once it is read.
        var run = Task.Run(() => handoff.Run("show-passive"), timeout.Token);
        await connected;
        using var reader = new StreamReader(server, Encoding.UTF8);
        var received = await reader.ReadToEndAsync(timeout.Token);

        Assert.Equal(ResidentHandoffOutcome.Signaled, await run.WaitAsync(timeout.Token));
        Assert.Equal("show-passive\n", received);
    }

    [Fact]
    public void NoResidentStartsTheFixtureResident()
    {
        var root = FixtureRoot();
        var fixture = Path.Combine(root, ResidentHandoff.ResidentExecutableName);
        // A harmless system program under the resident's name stands in for it; it exits on its own.
        File.Copy(Path.Combine(Environment.SystemDirectory, "whoami.exe"), fixture);
        var handoff = new ResidentHandoff(TestPipeName(), fixture);

        Assert.Equal(ResidentHandoffOutcome.Started, handoff.Run("show"));
    }

    [Fact]
    public void NoResidentStartsExactlyTheConfiguredProgram()
    {
        // The start names the configured path: a missing fixture fails instead of reaching any other resident.
        var handoff = new ResidentHandoff(TestPipeName(),
            Path.Combine(FixtureRoot(), "missing", ResidentHandoff.ResidentExecutableName));
        Assert.Throws<Win32Exception>(() => handoff.Run("show"));
    }

    [Fact]
    public void ExitWithoutAResidentStartsNothing()
    {
        var handoff = new ResidentHandoff(TestPipeName(),
            Path.Combine(FixtureRoot(), "missing", ResidentHandoff.ResidentExecutableName));
        Assert.Equal(ResidentHandoffOutcome.NothingToExit, handoff.Run("exit"));
    }

    [Fact]
    public void ResidentHoldingTheMutexWithoutAPipeIsUnanswered()
    {
        var handoff = new ResidentHandoff(TestPipeName(),
            Path.Combine(FixtureRoot(), "missing", ResidentHandoff.ResidentExecutableName));
        using var mutex = new Mutex(true, handoff.MutexName, out var created);
        Assert.True(created);
        Assert.Equal(ResidentHandoffOutcome.Unanswered, handoff.Run("show"));
    }

    [Theory]
    [InlineData("show", "--show-existing")]
    [InlineData("show-passive", "--show-existing-passive")]
    [InlineData("show-passive-secondary", "--show-existing-passive-secondary")]
    [InlineData("ensure-running", "--background-startup")]
    [InlineData("exit", "--exit-existing")]
    public void ResidentCommandLineNamesTheTestPipeBeforeTheCommand(string command, string flag)
    {
        var pipe = TestPipeName();
        var handoff = new ResidentHandoff(pipe, Path.Combine(FixtureRoot(), ResidentHandoff.ResidentExecutableName));
        Assert.Equal(["--instance-pipe", pipe, flag], handoff.ResidentArguments(command));
        Assert.Equal(@"Local\" + pipe, handoff.MutexName);
    }

    [Fact]
    public void ProductPipeKeepsTheResidentsOwnNames()
    {
        var handoff = new ResidentHandoff(ResidentHandoff.DefaultPipeName,
            Path.Combine(FixtureRoot(), ResidentHandoff.ResidentExecutableName));
        Assert.Equal(["--show-existing"], handoff.ResidentArguments("show"));
        Assert.Equal(@"Local\ResourceManager.NativeUi", handoff.MutexName);
    }

    [Fact]
    public void UnknownCommandIsRejectedBeforeAnySideEffect()
    {
        var handoff = new ResidentHandoff(TestPipeName(),
            Path.Combine(FixtureRoot(), "missing", ResidentHandoff.ResidentExecutableName));
        Assert.Throws<ArgumentException>(() => handoff.Run("open"));
    }

    private static string TestPipeName() => "ResourceManager.Desktop.Test." + Guid.NewGuid().ToString("N");

    private static string FixtureRoot()
    {
        var scratch = Environment.GetEnvironmentVariable("RM_REVIEW_SCRATCH")
            ?? throw new InvalidOperationException("A registered validation scratch root is required.");
        var root = Path.Combine(scratch, "resident-handoff-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root; // The validation workset owns lifecycle and bounded retention.
    }
}

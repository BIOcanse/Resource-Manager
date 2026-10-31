using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace ResourceManager.NativeUi;

internal enum ResidentHandoffOutcome
{
    /// <summary>A running resident accepted the command on its single-instance pipe.</summary>
    Signaled,
    /// <summary>No resident was running, so this process started one with the same command.</summary>
    Started,
    /// <summary>The command was <c>exit</c> and no resident was running.</summary>
    NothingToExit,
    /// <summary>A resident holds the instance mutex but its pipe did not accept the command in time.</summary>
    Unanswered
}

/// <summary>
/// Started without the resident's pipe (a persistent entry or script that still names this program: the logon task,
/// the Task Manager IFEO value, an old shortcut, a development script), the window process owns nothing: it hands the
/// single-instance command its command line names to <c>ResourceManager.Desktop.exe</c> and exits. A running resident
/// (it holds the instance mutex) receives the command over the existing single-instance pipe; when none is running,
/// the resident is started with the same command, which it executes as its own startup command. This is the
/// persistent-entry compatibility of docs/frontend_block_trees_2026-10-26.md; its deletion condition is recorded once,
/// in <see cref="SystemIntegration.LoginStartupRegistration"/>.
/// </summary>
internal sealed class ResidentHandoff
{
    internal const string DefaultPipeName = "ResourceManager.NativeUi.Instance";
    internal const string ResidentExecutableName = "ResourceManager.Desktop.exe";
    private const string DefaultMutexName = @"Local\ResourceManager.NativeUi";
    // Matches the resident's own client: the primary may be between two pipe instances.
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1500);
    private readonly string pipeName;
    private readonly string residentExecutable;

    /// <param name="pipeName">The resident's single-instance pipe; tests pass their own name, never the product's.</param>
    /// <param name="residentExecutable">The resident program started when none is running.</param>
    internal ResidentHandoff(string pipeName, string residentExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentException.ThrowIfNullOrWhiteSpace(residentExecutable);
        this.pipeName = pipeName;
        this.residentExecutable = Path.GetFullPath(residentExecutable);
    }

    /// <summary>The product layout: the resident is the sibling of this program (the resident's own default layout).</summary>
    internal static ResidentHandoff ForCurrentProcess()
        => new(DefaultPipeName, Path.Combine(AppContext.BaseDirectory, ResidentExecutableName));

    /// <summary>The resident names its mutex after a test pipe name, and keeps the product name for the default pipe.</summary>
    internal string MutexName => pipeName == DefaultPipeName ? DefaultMutexName : @"Local\" + pipeName;

    internal ResidentHandoffOutcome Run(string command)
    {
        _ = ResidentArguments(command); // Rejects an unknown command before any side effect.
        if (ResidentRunning()) return Signal(command) ? ResidentHandoffOutcome.Signaled : ResidentHandoffOutcome.Unanswered;
        if (command == "exit") return ResidentHandoffOutcome.NothingToExit;
        var start = new ProcessStartInfo(residentExecutable)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(residentExecutable)!
        };
        foreach (var argument in ResidentArguments(command)) start.ArgumentList.Add(argument);
        using var resident = Process.Start(start)
            ?? throw new InvalidOperationException("The resident process did not start.");
        return ResidentHandoffOutcome.Started;
    }

    /// <summary>The resident's command line for a command: its own flag, plus the pipe name when it is not the product's.</summary>
    internal IReadOnlyList<string> ResidentArguments(string command)
    {
        var flag = command switch
        {
            "show" => "--show-existing",
            "show-passive" => "--show-existing-passive",
            "show-passive-secondary" => "--show-existing-passive-secondary",
            "ensure-running" => "--background-startup",
            "exit" => "--exit-existing",
            _ => throw new ArgumentException("Unknown single-instance command.", nameof(command))
        };
        return pipeName == DefaultPipeName ? [flag] : ["--instance-pipe", pipeName, flag];
    }

    private bool ResidentRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexName, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // The mutex exists; only its access is refused.
        }
    }

    private bool Signal(string command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(ConnectTimeout);
            client.Write(Encoding.UTF8.GetBytes(command + "\n"));
            client.Flush();
            return true;
        }
        catch (Exception error) when (error is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("The resident did not accept the single-instance command ({0}).", error.GetType().Name);
            return false;
        }
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ResourceManager.ReadWrite.Windows.Processes;
using System.Text.Json;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

public sealed partial class WindowsExternalGpuPlacementRuntime
{
    internal const int DiagnosticSummaryLimit = 32;
    private int executingAttempts;
    private readonly Dictionary<(string Directory, bool Root), GpuPlacementProcessInstance> exitedAttemptProcesses = [];

    // Called under gate. The two real owners in an attempt are its controller and
    // root; a new identity replaces that slot and can be probed independently.
    private bool IsAttemptProcessAlive(string directory, bool root, GpuPlacementProcessInstance identity,
        Func<GpuPlacementProcessInstance, bool> probe)
    {
        var key = (directory, root);
        if (exitedAttemptProcesses.TryGetValue(key, out var exited) && exited == identity) return false;
        if (probe(identity)) { exitedAttemptProcesses.Remove(key); return true; }
        exitedAttemptProcesses[key] = identity;
        return false;
    }

    private void PruneExitedAttemptProcesses(IReadOnlyList<string> directories)
    {
        var current = directories.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in exitedAttemptProcesses.Keys.ToArray())
            if (!current.Contains(key.Directory)) exitedAttemptProcesses.Remove(key);
    }

    // Called only at admission boundaries. Receipt facts are read from their durable owner,
    // never cached here. A running Execute also owns files after its controller has exited.
    internal void RetireFinishedAttempts(Func<GpuPlacementProcessInstance, bool>? isAlive = null)
    {
        lock (gate)
        {
            if (executingAttempts != 0 || activeCompletion is { IsCompleted: false }
                || !Directory.Exists(evidenceRoot)) return;
            if (File.GetAttributes(evidenceRoot).HasFlag(FileAttributes.ReparsePoint)) return;
            var references = stateStore.ReadExternalGpuAttemptReferences();
            if (references is null)
            {
                logger.LogDebug("External GPU attempt retirement awaits durable receipt authority.");
                return;
            }
            var probe = isAlive ?? IsAlive;
            var directories = Directory.EnumerateDirectories(evidenceRoot).ToArray();
            PruneExitedAttemptProcesses(directories);
            var retiredRoot = Path.Combine(evidenceRoot, "Retired");
            if (Directory.Exists(retiredRoot))
            {
                if (File.GetAttributes(retiredRoot).HasFlag(FileAttributes.ReparsePoint)) return;
                foreach (var directory in Directory.EnumerateDirectories(retiredRoot)) DeleteRetired(directory);
            }
            foreach (var directory in directories)
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id)
                    || references.Contains(directory)) continue;
                try
                {
                    RequireFlatDirectory(directory);
                    var controllerPath = Path.Combine(directory, "controller.json");
                    if (!File.Exists(controllerPath)) continue; // No identity cannot prove control is released.
                    var controller = JsonSerializer.Deserialize(File.ReadAllText(controllerPath), ExternalGpuPlacementJsonContext.Default.GpuPlacementProcessInstance);
                    if (controller is null || IsAttemptProcessAlive(directory, false, controller, probe)) continue;
                    var failed = HasFailedAttemptForDirectory(directory);
                    var root = failed ? ReadAttemptRoot(directory) : null;
                    if (root is not null && failed && IsAttemptProcessAlive(directory, true, root, probe)) continue;

                    var diagnostics = Path.Combine(evidenceRoot, "Diagnostics");
                    Directory.CreateDirectory(diagnostics);
                    RequireFlatDirectory(diagnostics);
                    // Fixed slots bound both count and bytes; never copy unbounded controller logs/results.
                    var slot = id.ToByteArray()[0] % DiagnosticSummaryLimit;
                    File.WriteAllText(Path.Combine(diagnostics, $"{slot:D2}.json"), JsonSerializer.Serialize(new
                    {
                        attemptId = id.ToString("N"), retiredAtUtc = DateTimeOffset.UtcNow,
                        failed, rootProcessId = root?.ProcessId, rootProcessStartKey = root?.ProcessStartKey
                    }));
                    Directory.CreateDirectory(retiredRoot);
                    if (File.GetAttributes(retiredRoot).HasFlag(FileAttributes.ReparsePoint))
                        throw new IOException("External GPU retirement directory is a reparse point.");
                    var retired = Path.Combine(retiredRoot, Path.GetFileName(directory));
                    Directory.Move(directory, retired);
                    DeleteRetired(retired);
                }
                catch (Exception error) when (IsRetirementError(error))
                { logger.LogWarning(error, "Cannot retire external GPU attempt {Directory}; evidence retained.", directory); }
            }
        }
    }

    internal static bool IsAlive(GpuPlacementProcessInstance identity)
    {
        if (identity.ProcessId <= 0 || identity.ProcessStartKey == 0 || !Path.IsPathFullyQualified(identity.ExecutablePath))
            throw new InvalidDataException("External GPU process identity is unresolved.");
        var process = WindowsProcessBasicMetricNativeApi.OpenProcess(0x1000, false, identity.ProcessId);
        if (process == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 87) return false; // A disappeared PID is an ordinary lifetime fact.
            throw new Win32Exception(error);
        }
        try
        {
            if (!WindowsProcessBasicMetricNativeApi.GetProcessTimes(process, out var birth, out var exit, out _, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (exit.ToUInt64() != 0 || birth.ToUInt64() != identity.ProcessStartKey) return false;
            var path = new WindowsProcessBasicMetricNativeApi().ImagePath(process);
            if (path is null) throw new Win32Exception(Marshal.GetLastWin32Error());
            return StringComparer.OrdinalIgnoreCase.Equals(path, identity.ExecutablePath);
        }
        finally { WindowsProcessBasicMetricNativeApi.CloseHandle(process); }
    }

    private static GpuPlacementProcessInstance? ReadAttemptRoot(string directory)
    {
        var path = Path.Combine(directory, "request.json");
        if (!File.Exists(path)) return null;
        using var request = JsonDocument.Parse(File.ReadAllText(path));
        var expected = request.RootElement.GetProperty("expected");
        // Retained Chromium attempts predate the renderer-neutral Root field.
        return (expected.TryGetProperty("Root", out var root) ? root : expected.GetProperty("Browser"))
            .Deserialize(ExternalGpuPlacementJsonContext.Default.GpuPlacementProcessInstance)
            ?? throw new InvalidDataException("External GPU attempt root is unresolved.");
    }

    private static bool HasFailedAttemptForDirectory(string directory)
    {
        try
        {
            var resultPath = Path.Combine(directory, "result.json");
            return !File.Exists(resultPath)
                || JsonSerializer.Deserialize(File.ReadAllText(resultPath), ExternalGpuPlacementJsonContext.Default.RunningGpuPlacementActionResult)?.Status
                    != RunningGpuPlacementActionStatuses.RecreateRequested;
        }
        catch (Exception error) when (IsRetirementError(error)) { return true; }
    }

    private void DeleteRetired(string directory)
    {
        try
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _)) return;
            RequireFlatDirectory(directory);
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
        catch (Exception error) when (IsRetirementError(error))
        { logger.LogWarning(error, "Cannot delete retired external GPU attempt {Directory}.", directory); }
    }

    private static void RequireFlatDirectory(string directory)
    {
        if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)
            || Directory.EnumerateDirectories(directory).Any()
            || Directory.EnumerateFiles(directory).Any(file => File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)))
            throw new IOException("External GPU attempt storage must be flat and contain no reparse points.");
    }

    private static bool IsRetirementError(Exception error)
        => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or Win32Exception
            or InvalidOperationException or KeyNotFoundException;
}

using System.Diagnostics;
using System.Text.Json;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement.External;

internal static class ExternalGpuControllerRecovery
{
    internal static async Task RecoverAsync(string evidenceRoot, IReadOnlyList<string> controllerImages,
        TimeSpan budget, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(evidenceRoot)) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        RejectLink(evidenceRoot);
        foreach (var directory in Directory.EnumerateDirectories(evidenceRoot))
        {
            deadline.Token.ThrowIfCancellationRequested();
            RejectLink(directory);
            var record = Path.Combine(directory, "controller.json");
            if (!File.Exists(record)) continue;
            RejectLink(record);
            var identity = JsonSerializer.Deserialize<GpuPlacementProcessInstance>(File.ReadAllText(record));
            if (identity is null || !controllerImages.Any(image => StringComparer.OrdinalIgnoreCase.Equals(
                Path.GetFullPath(image), Path.GetFullPath(identity.ExecutablePath))))
                throw new InvalidDataException("The recovery controller is not an owned product image.");
            Process process;
            try { process = Process.GetProcessById(identity.ProcessId); }
            catch (ArgumentException) { continue; }
            using (process)
            {
                if (!ChromiumGpuProcessIdentity.Matches(process, identity)) continue;
                // Pin the exact controller handle before signalling; PID reuse cannot redirect a wait.
                _ = process.SafeHandle;
                if (!ChromiumGpuProcessIdentity.Matches(process, identity)) continue;
                var stop = Path.Combine(directory, Path.GetFileName(identity.ExecutablePath).Equals(
                    WindowsExternalGpuPlacementRuntime.RendererFileName, StringComparison.OrdinalIgnoreCase) ? "controller.stop" : "stop");
                Signal(stop + ".cancel");
                Signal(stop + ".recover");
                await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            }
        }
    }

    private static void Signal(string path)
    {
        if (File.Exists(path)) { RejectLink(path); return; }
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
    }
    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Recovery does not follow reparse points.");
    }
}

using System.Diagnostics;

namespace ResourceManager.Shared.Packages;

/// <summary>Deployment's existing frontend shutdown deadlines, shared by update and uninstall.</summary>
public sealed record ProductDesktopShutdownSettings(int GracefulWaitMilliseconds = 5000, int ForcedWaitMilliseconds = 10_000);

public static class ProductDesktopLifetime
{
    /// <summary>Stop only this installation's resident/window images. Ask the owner to exit first, then join
    /// both images before allowing deployment to replace any file. Retained handles prevent PID reuse.</summary>
    public static void Stop(string root, ProductDesktopShutdownSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.GracefulWaitMilliseconds < 0 || settings.ForcedWaitMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(settings));
        var paths = new[] { ProductDesktopLayout.ResidentFor(root), ProductDesktopLayout.WindowFor(root) };
        var owned = new List<(Process Process, string Path)>();
        try
        {
            foreach (var path in paths)
                foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
                {
                    var keep = false;
                    try
                    {
                        _ = process.SafeHandle;
                        if (!string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase)) continue;
                        keep = true;
                        owned.Add((process, path));
                    }
                    catch (InvalidOperationException) { /* It exited before capture. */ }
                    finally { if (!keep) process.Dispose(); }
                }
            var hasResident = owned.Any(item => !item.Process.HasExited && string.Equals(item.Path, paths[0], StringComparison.OrdinalIgnoreCase));
            if (hasResident)
            {
                using var request = Process.Start(new ProcessStartInfo(paths[0])
                {
                    UseShellExecute = true, Arguments = "--exit-existing", WorkingDirectory = Path.GetDirectoryName(paths[0])!
                }) ?? throw new IOException("Cannot request resident shutdown.");
                if (!request.WaitForExit(settings.GracefulWaitMilliseconds))
                {
                    request.Kill();
                    if (!request.WaitForExit(settings.ForcedWaitMilliseconds)) throw new TimeoutException("Resident shutdown request did not exit.");
                }
            }
            else foreach (var item in owned) if (!item.Process.HasExited) item.Process.CloseMainWindow();
            var waiting = Stopwatch.StartNew();
            foreach (var item in owned)
            {
                var process = item.Process;
                if (process.HasExited) continue;
                var remaining = Math.Max(0, settings.GracefulWaitMilliseconds - (int)Math.Min(waiting.ElapsedMilliseconds, int.MaxValue));
                if (process.WaitForExit(remaining)) continue;
                process.Kill(); // The resident's Job also owns its window child.
                if (!process.WaitForExit(settings.ForcedWaitMilliseconds)) throw new TimeoutException("The product interface did not exit.");
            }
        }
        finally { foreach (var item in owned) item.Process.Dispose(); }
    }
}

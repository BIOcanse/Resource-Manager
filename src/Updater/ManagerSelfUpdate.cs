using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.Shared.Packages;
using ResourceManager.Shared.Localization;

namespace ResourceManager.Updater;

internal static class ManagerSelfUpdate
{
    public static bool Schedule(ReleasePackageLayout package, string installRoot, string installedExecutable,
        ToolText? text = null)
        => Schedule(package, installRoot, installedExecutable, start =>
        {
            using var helper = Process.Start(start)
                ?? throw new InvalidOperationException((text ?? ToolText.FromInstallRoot(installRoot)).ManagerHelperStartFailed);
        }, text);

    internal static bool Schedule(ReleasePackageLayout package, string installRoot, string installedExecutable,
        Action<ProcessStartInfo> launch, ToolText? text = null)
    {
        // ApplyAsync has committed this protected copy. Never execute the user's download tree.
        var source = UpdateManagerPaths.PackagedExecutable(Path.GetFullPath(installRoot));
        using var verifiedSource = OpenVerifiedManager(installRoot, package.Version, text);
        if (SameFileContents(verifiedSource, installedExecutable)) return false;
        var start = new ProcessStartInfo(source)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(source),
            ArgumentList = { "--replace-manager", installRoot, Environment.ProcessId.ToString() }
        };
        // Keep the verified bytes read-only until CreateProcess has opened the image.
        launch(start);
        return true;
    }

    public static async Task<string> ReplaceAsync(string executablePath, string installRoot,
        int oldProcessId, CancellationToken cancellationToken, ToolText? text = null)
    {
        var target = UpdatePlan.RequireRegisteredTarget(installRoot);
        text ??= ToolText.FromInstallRoot(target);
        var managerDirectory = UpdateManagerPaths.InstalledDirectory(target);
        var installedExecutable = UpdateManagerPaths.InstalledExecutable(target);
        ReleasePackageLayout.RejectReparse(managerDirectory);
        ReleasePackageLayout.RejectReparse(installedExecutable);
        var resultPath = Path.Combine(managerDirectory, "self-update-result.json");
        try
        {
            var source = Path.GetFullPath(executablePath);
            if (!source.Equals(UpdateManagerPaths.PackagedExecutable(target), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(text.ManagerSourceInvalid);
            await WaitForOldManagerExitAsync(installedExecutable, oldProcessId, cancellationToken, text);
            using var operationLock = UpdateManagerCommand.AcquireOperationLock(target);
            using var verifiedSource = OpenVerifiedManager(target, text: text);
            var expectedHash = SHA256.HashData(verifiedSource);
            verifiedSource.Position = 0;
            var staged = Path.Combine(managerDirectory, "next-" + Guid.NewGuid().ToString("N") + ".exe");
            var backup = Path.Combine(managerDirectory, "previous-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                using (var stagedOutput = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    verifiedSource.CopyTo(stagedOutput);
                byte[] stagedHash;
                using (var stagedStream = File.OpenRead(staged)) stagedHash = SHA256.HashData(stagedStream);
                if (!expectedHash.AsSpan().SequenceEqual(stagedHash))
                    throw new InvalidDataException(text.ManagerCopyInvalid);
                File.Replace(staged, installedExecutable, backup);
            }
            finally
            {
                if (File.Exists(staged)) File.Delete(staged);
            }
            WriteResult(resultPath, true, text.Format(text.ManagerSelfUpdatedBackupFormat, backup));
            return text.ManagerSelfUpdated;
        }
        catch (Exception exception)
        {
            WriteResult(resultPath, false, exception.ToString());
            throw;
        }
    }

    public static string? ReadLastFailure(string installRoot)
    {
        var path = Path.Combine(UpdateManagerPaths.InstalledDirectory(installRoot), "self-update-result.json");
        if (!File.Exists(path)) return null;
        ReleasePackageLayout.RejectReparse(path);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("success").GetBoolean()
            ? null : document.RootElement.GetProperty("message").GetString()?.Split('\n')[0];
    }

    private static async Task WaitForOldManagerExitAsync(string installedExecutable, int oldProcessId,
        CancellationToken cancellationToken, ToolText text)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = false;
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.Id == Environment.ProcessId) continue;
                    try
                    {
                        if (process.Id != oldProcessId &&
                            !process.ProcessName.StartsWith("ResourceManager", StringComparison.OrdinalIgnoreCase))
                            continue;
                        var path = process.MainModule?.FileName;
                        if (process.Id == oldProcessId ||
                            string.Equals(path, installedExecutable, StringComparison.OrdinalIgnoreCase))
                            active = true;
                    }
                    catch (System.ComponentModel.Win32Exception)
                    {
                        if (process.Id == oldProcessId || process.ProcessName.StartsWith("ResourceManager.UpdateManager",
                                StringComparison.OrdinalIgnoreCase))
                            active = true;
                    }
                    catch (InvalidOperationException) { }
                }
            }
            if (!active) return;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        throw new TimeoutException(text.ManagerExitTimeout);
    }

    internal static FileStream OpenVerifiedManager(string installRoot, string? expectedVersion = null, ToolText? text = null)
    {
        text ??= ToolText.FromInstallRoot(installRoot);
        var root = Path.GetFullPath(installRoot);
        foreach (var directory in new[] { root, Path.Combine(root, "Internal"), Path.Combine(root, "Internal", "UpdateManager") })
            ReleasePackageLayout.RejectReparse(directory);
        var manifestPath = Path.Combine(root, "release-manifest.json");
        ReleasePackageLayout.RejectReparse(manifestPath);
        using var manifestStream = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var manifest = JsonDocument.Parse(manifestStream);
        var top = manifest.RootElement;
        var version = top.GetProperty("version").GetString();
        if (string.IsNullOrWhiteSpace(version) || (expectedVersion is not null &&
            !string.Equals(version, expectedVersion, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(text.ManagerPayloadInvalid);
        const string relative = "Internal/UpdateManager/ResourceManager.UpdateManager.exe";
        var entries = top.GetProperty("files").EnumerateArray()
            .Where(entry => string.Equals(entry.GetProperty("path").GetString(), relative, StringComparison.Ordinal)).ToArray();
        if (entries.Length != 1) throw new InvalidDataException(text.ManagerPayloadInvalid);
        var source = UpdateManagerPaths.PackagedExecutable(root);
        ReleasePackageLayout.RejectReparse(source);
        var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length != entries[0].GetProperty("length").GetInt64() ||
                !Convert.ToHexString(SHA256.HashData(stream)).Equals(entries[0].GetProperty("sha256").GetString(),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(text.ManagerPayloadInvalid);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static bool SameFileContents(Stream source, string second)
    {
        using var target = File.OpenRead(second);
        if (source.Length != target.Length) return false;
        return SHA256.HashData(source).AsSpan().SequenceEqual(SHA256.HashData(target));
    }

    private static void WriteResult(string path, bool success, string message)
    {
        var temp = path + ".writing";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(new
        {
            success, message, finishedAt = DateTimeOffset.UtcNow
        }));
        File.Move(temp, path, overwrite: true);
    }
}

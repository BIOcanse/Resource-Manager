using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.Shared.Packages;
using ResourceManager.Shared.Localization;

namespace ResourceManager.Updater;

internal static class ManagerSelfUpdate
{
    public static bool Schedule(ReleasePackageLayout package, string installRoot,
        ToolText? text = null)
        => Schedule(package, installRoot, start =>
        {
            using var helper = Process.Start(start)
                ?? throw new InvalidOperationException((text ?? ToolText.FromInstallRoot(installRoot)).ManagerHelperStartFailed);
        }, text);

    internal static bool Schedule(ReleasePackageLayout package, string installRoot,
        Action<ProcessStartInfo> launch, ToolText? text = null)
    {
        // ApplyAsync has committed this protected copy. Never execute the user's download tree.
        var source = UpdateManagerPaths.PackagedExecutable(Path.GetFullPath(installRoot));
        using var verifiedSource = OpenVerifiedManager(installRoot, package.Version, text);
        if (SameFileContents(verifiedSource, UpdateManagerPaths.InstalledExecutable(installRoot))) return false;
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
        var target = UpdatePlan.RequireProductTarget(installRoot);
        var registeredManager = ManagerRelocationRegistration.RequireOwned(target);
        return await ReplaceVerifiedAsync(executablePath, target, oldProcessId, registeredManager,
            () => ManagerRelocationRegistration.Rewrite(target), cancellationToken, text);
    }

    // The public entry verifies machine registration. This seam records the same
    // file transaction without modifying the test machine's registration.
    internal static async Task<string> ReplaceVerifiedAsync(string executablePath, string target,
        int oldProcessId, string registeredManager, Action relocateRegistration,
        CancellationToken cancellationToken, ToolText? text = null,
        Func<string, int, CancellationToken, ToolText, Task>? waitForExit = null)
    {
        text ??= ToolText.FromInstallRoot(target);
        registeredManager = UpdateManagerPaths.RequireOwnedManager(target, registeredManager);
        var managerDirectory = UpdateManagerPaths.InstalledDirectory(target);
        var installedExecutable = UpdateManagerPaths.InstalledExecutable(target);
        foreach (var path in new[] { target, Path.Combine(target, "Internal") }) ReleasePackageLayout.RejectReparse(path);
        RejectOptionalReparse(managerDirectory);
        Directory.CreateDirectory(managerDirectory);
        ReleasePackageLayout.RejectReparse(managerDirectory);
        RejectOptionalReparse(installedExecutable);
        var resultPath = Path.Combine(managerDirectory, "self-update-result.json");
        try
        {
            using var verifiedSource = OpenVerifiedManager(target, text: text);
            // 0.2.8 starts the new helper in the download directory. Its bytes
            // must match the protected, committed payload; never copy from it.
            var source = Path.GetFullPath(executablePath);
            if (!source.Equals(UpdateManagerPaths.PackagedExecutable(target), StringComparison.OrdinalIgnoreCase))
            {
                ReleasePackageLayout.RejectReparse(source);
                if (!SameFileContents(verifiedSource, source))
                    throw new InvalidOperationException(text.ManagerSourceInvalid);
                verifiedSource.Position = 0;
            }
            await (waitForExit ?? WaitForOldManagerExitAsync)(registeredManager, oldProcessId, cancellationToken, text);
            using var operationLock = UpdateManagerCommand.AcquireOperationLock(target);
            var relocating = !registeredManager.Equals(installedExecutable, StringComparison.OrdinalIgnoreCase);
            // Prepare every stable entry before any desktop identity is switched.
            var payloads = new List<(FileStream Input, string Destination)> { (verifiedSource, installedExecutable) };
            try
            {
                if (relocating)
                    foreach (var name in new[] { "Start.exe", UninstallationPlan.ExecutableName })
                        payloads.Add((OpenVerifiedPayload(target, name, text: text), Path.Combine(managerDirectory, name)));
                string? backup = null;
                foreach (var payload in payloads)
                    backup = Promote(payload.Input, payload.Destination, text);
                if (relocating) relocateRegistration();
                WriteResult(resultPath, true, text.Format(text.ManagerSelfUpdatedBackupFormat, backup ?? registeredManager));
                return text.ManagerSelfUpdated;
            }
            finally
            {
                foreach (var payload in payloads.Skip(1)) payload.Input.Dispose();
            }
        }
        catch (Exception exception)
        {
            WriteResult(resultPath, false, exception.ToString());
            throw;
        }
    }

    internal static void RejectOptionalReparse(string path)
    {
        try { ReleasePackageLayout.RejectReparse(path); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static string? Promote(FileStream source, string destination, ToolText text)
    {
        RejectOptionalReparse(destination);
        source.Position = 0;
        var expectedHash = SHA256.HashData(source);
        source.Position = 0;
        var staged = Path.Combine(Path.GetDirectoryName(destination)!, "next-" + Guid.NewGuid().ToString("N") + ".exe");
        var backup = Path.Combine(Path.GetDirectoryName(destination)!, "previous-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { source.CopyTo(output); output.Flush(flushToDisk: true); }
            using (var stream = File.OpenRead(staged))
                if (!expectedHash.AsSpan().SequenceEqual(SHA256.HashData(stream)))
                    throw new InvalidDataException(text.ManagerCopyInvalid);
            if (File.Exists(destination)) File.Replace(staged, destination, backup);
            else { File.Move(staged, destination); backup = null; }
            return backup;
        }
        finally { if (File.Exists(staged)) File.Delete(staged); }
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
        => OpenVerifiedPayload(installRoot, "Internal/UpdateManager/ResourceManager.UpdateManager.exe", expectedVersion, text);

    internal static FileStream OpenVerifiedPayload(string installRoot, string relative,
        string? expectedVersion = null, ToolText? text = null)
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
        var entries = top.GetProperty("files").EnumerateArray()
            .Where(entry => string.Equals(entry.GetProperty("path").GetString(), relative, StringComparison.Ordinal)).ToArray();
        if (entries.Length != 1) throw new InvalidDataException(text.ManagerPayloadInvalid);
        var source = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
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

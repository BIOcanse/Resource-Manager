using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;

namespace ResourceManager.ReadWrite.Vendors.Amd.Smu;

public sealed partial class AmdSmuPawnIoSession
{
    // Both PM-table and read-only SMN sessions use the original executor and
    // explicit ordered library/direct-device selection. No new native ABI.
    private static T OpenModule<T>(string declaredPath, IReadOnlyList<string> libraryPaths,
        bool useDirectDevice, TimeSpan pciLockTimeout,
        Func<IAmdSmuPawnIoExecutor, string, T> initialize)
    {
        if (pciLockTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(pciLockTimeout));
        ArgumentNullException.ThrowIfNull(libraryPaths);
        var modulePath = Path.GetFullPath(declaredPath);
        if (!File.Exists(modulePath)) throw new AmdSmuProviderUnavailableException(
            $"The selected PawnIO module is missing: {modulePath}");
        var module = File.ReadAllBytes(modulePath);
        var errors = new List<string>();
        foreach (var libraryPath in libraryPaths)
        {
            try
            {
                return InitializeOwned(PawnIoLibraryExecutor.Open(libraryPath, module), modulePath, initialize);
            }
            catch (Exception ex) when (ex is AmdSmuProviderUnavailableException or DllNotFoundException
                or BadImageFormatException or EntryPointNotFoundException)
            {
                errors.Add($"{Path.GetFileName(libraryPath)}: {ex.Message}");
            }
        }
        if (useDirectDevice)
        {
            try
            {
                return InitializeOwned(DirectPawnIoDeviceExecutor.Open(module), modulePath, initialize);
            }
            catch (Exception ex) when (ex is AmdSmuProviderUnavailableException or Win32Exception
                or IOException or UnauthorizedAccessException)
            {
                errors.Add($"PawnIO device: {ex.Message}");
            }
        }
        throw new AmdSmuProviderUnavailableException(errors.Count == 0
            ? "No selected PawnIO execution path is available." : string.Join("; ", errors));
    }

    private static T InitializeOwned<T>(IAmdSmuPawnIoExecutor executor, string path,
        Func<IAmdSmuPawnIoExecutor, string, T> initialize)
    {
        try { return initialize(executor, path); }
        catch { executor.Dispose(); throw; }
    }
}

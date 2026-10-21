using ResourceManager.Shared.Packages;
using ResourceManager.Updater;

namespace ResourceManager.Installer;

internal interface IInstallationReplacementRuntime
{
    void RequireOwned();
    void StopProduct();
    void RemoveIdentity();
    void RestoreIdentity();
}

/// <summary>Replaces registration while leaving the previous program and original data intact.</summary>
internal sealed class InstallationReplacement(UninstallationPlan previous, IInstallationReplacementRuntime runtime)
{
    internal static void RequireSeparateDirectories(UninstallationPlan previous, PackageLayout source, PackageLayout target)
    {
        foreach (var old in new[] { previous.Root, previous.ManagerDirectory })
            foreach (var next in new[] { source.Root, target.Root })
                if (Contains(old, next) || Contains(next, old))
                    throw new InvalidOperationException("The release and new installation must be separate from the previous installation.");
        if (Contains(source.Root, target.Root) || Contains(target.Root, source.Root))
            throw new InvalidOperationException("The release and installation directories must be separate.");
        foreach (var path in new[] { previous.Root, Path.GetDirectoryName(previous.Root)!, Path.GetDirectoryName(target.Root)! })
            ReleasePackageLayout.RejectReparse(path);
        if (!Directory.Exists(previous.Root)) throw new DirectoryNotFoundException(previous.Root);
        if (Directory.Exists(target.Root) || File.Exists(target.Root))
            throw new IOException("The new installation directory is occupied: " + target.Root);
        var manager = UpdateManagerPaths.InstalledDirectory(target.Root);
        if (!string.Equals(manager, previous.ManagerDirectory, StringComparison.OrdinalIgnoreCase)
            && (Directory.Exists(manager) || File.Exists(manager)))
            throw new IOException("The new update manager directory is occupied: " + manager);
    }

    private static bool Contains(string parent, string child)
    {
        parent = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar);
        child = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar);
        return child.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal void Run(string preparedRoot, Action installPrepared)
    {
        runtime.RequireOwned();
        var stopped = false;
        var managerMoved = false;
        var preservedManager = Path.Combine(previous.Root, "Internal", "PreviousUpdateManager-" + Guid.NewGuid().ToString("N"));
        FileStream? operationLock = null;
        try
        {
            if (Directory.Exists(previous.ManagerDirectory)) operationLock = PackageOperationLock.AcquireDirectory(previous.ManagerDirectory);
            runtime.RequireOwned();
            if (PackageSwitchTransaction.ListPending(previous.Root).Count != 0)
                throw new InvalidOperationException("Recover the interrupted update before reinstalling.");
            stopped = true; // Stop can fail after stopping part of the product.
            runtime.StopProduct();
            PackageSwitchTransaction.CopyUserData(previous.Root, preparedRoot);
            UpdatePlan.CheckCompatibility(preparedRoot, preparedRoot);
            PackageUserDirectoryAccess.Prepare(preparedRoot);
            runtime.RequireOwned();
            runtime.RemoveIdentity();
            operationLock?.Dispose();
            operationLock = null;
            if (Directory.Exists(previous.ManagerDirectory)
                && !Contains(previous.Root, previous.ManagerDirectory))
            {
                ReleasePackageLayout.RejectReparse(previous.ManagerDirectory);
                var internalRoot = Path.GetDirectoryName(preservedManager)!;
                if (Directory.Exists(internalRoot) || File.Exists(internalRoot)) ReleasePackageLayout.RejectReparse(internalRoot);
                Directory.CreateDirectory(internalRoot);
                ReleasePackageLayout.RejectReparse(internalRoot);
                Directory.Move(previous.ManagerDirectory, preservedManager);
                managerMoved = true;
            }
            installPrepared(); // Its rollback removes only the new installation before we restore old registration.
        }
        catch (Exception failure)
        {
            operationLock?.Dispose();
            operationLock = null;
            if (stopped)
            {
                try
                {
                    if (managerMoved) Directory.Move(preservedManager, previous.ManagerDirectory);
                    runtime.RestoreIdentity();
                }
                catch (Exception recoveryFailure)
                {
                    throw new InvalidOperationException($"Reinstallation failed: {failure.Message}\nPrevious registration could not be restored: {recoveryFailure.Message}\nPreserved files: {previous.Root}", failure);
                }
            }
            throw;
        }
        finally { operationLock?.Dispose(); }
    }
}

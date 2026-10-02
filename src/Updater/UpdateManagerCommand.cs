using System.Text.Json;
using Microsoft.Win32;
using ResourceManager.Shared.Packages;
using ResourceManager.Shared.Localization;

namespace ResourceManager.Updater;

public static class UpdateManagerCommand
{
    public static string GetInstalledRoot(ToolText? text = null)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"Software\ResourceManager");
        var root = key?.GetValue("InstallRoot") as string
            ?? throw new InvalidOperationException((text ?? ToolText.For(AppLanguage.System)).NotInstalled);
        return UpdatePlan.RequireRegisteredTarget(root);
    }

    public static IReadOnlyList<string> GetPendingRecoveryDescriptions(string installRoot)
        => PackageSwitchTransaction.ListPending(UpdatePlan.RequireRegisteredTarget(installRoot))
            .Select(entry => $"{entry.PreviousVersion} → {entry.TargetVersion}（{entry.Stage}）")
            .ToArray();

    public static string? GetManagerUpdateFailure(string installRoot)
        => ManagerSelfUpdate.ReadLastFailure(UpdatePlan.RequireRegisteredTarget(installRoot));

    public static string DescribePackageAction(string packageRoot, string installRoot, bool repair, ToolText? text = null)
    {
        var plan = UpdatePlan.Create(packageRoot, installRoot,
            repair ? UpdateOperation.Repair : UpdateOperation.Update);
        var copy = text ?? ToolText.FromInstallRoot(installRoot);
        return repair
            ? copy.Format(copy.RepairSummaryFormat, plan.Package.Version)
            : copy.Format(copy.UpdateSummaryFormat, plan.PreviousVersion, plan.Package.Version);
    }

    public static async Task<string> ExecuteAsync(string[] args, string executablePath,
        CancellationToken cancellationToken = default, ToolText? text = null)
    {
        text ??= ToolText.For(AppLanguage.System);
        if (args.Length == 3 && args[0] == "--replace-manager"
            && int.TryParse(args[2], out var oldProcessId))
            return await ManagerSelfUpdate.ReplaceAsync(executablePath, args[1],
                oldProcessId, cancellationToken);
        if (args.Length == 2 && args[0] == "--verify-package")
        {
            var package = ReleasePackageLayout.Verify(args[1]);
            return text.Format(text.VerifiedPackageFormat, package.Version);
        }
        if (args.Length == 3 && args[0] is "--legacy-plan" or "--adopt-and-apply")
        {
            var adoption = LegacyInstallationAdoption.Plan(args[1], args[2], executablePath);
            if (args[0] == "--legacy-plan")
                return text.Format(text.LegacyPlanFormat, adoption.PreviousVersion, adoption.Package.Version);
            return await LegacyInstallationAdoption.AdoptAndApplyAsync(adoption, executablePath, cancellationToken);
        }
        if (args.Length == 3 && args[0] == "--resume-legacy")
            return await LegacyInstallationAdoption.ResumeAndApplyAsync(args[1], args[2],
                executablePath, cancellationToken);
        if (args.Length == 2 && args[0] == "--finish-legacy-registration")
            return LegacyInstallationAdoption.FinishDesktopRegistration(args[1]);
        if (args.Length == 2 && args[0] == "--recover")
        {
            var target = UpdatePlan.RequireRegisteredTarget(args[1]);
            RequireExternalManager(executablePath, target, text);
            using var operationLock = AcquireOperationLock(target, text);
            var recovered = await PackageSwitchTransaction.RecoverPendingAsync(target,
                new WindowsUpdateRuntime(), cancellationToken);
            return recovered.Count == 0 ? text.NoRecovery : text.Format(text.RecoveredCountFormat, recovered.Count);
        }
        if (args.Length != 3 || args[0] is not ("--plan-only" or "--apply" or "--repair"))
            throw new ArgumentException(text.InvalidCommand);
        var operation = args[0] == "--repair" ? UpdateOperation.Repair : UpdateOperation.Update;
        var plan = UpdatePlan.Create(args[1], args[2], operation);
        if (args[0] == "--plan-only") return text.Format(text.PreflightFormat, plan.Package.Version);
        RequireExternalManager(executablePath, plan.InstallRoot, text);
        using var updateLock = AcquireOperationLock(plan.InstallRoot, text);
        var pending = PackageSwitchTransaction.ListPending(plan.InstallRoot);
        if (pending.Count > 0)
            throw new InvalidOperationException(text.PendingTransaction);
        var workspace = await PackageSwitchTransaction.ApplyAsync(plan,
            new WindowsUpdateRuntime(), cancellationToken);
        var result = operation == UpdateOperation.Repair
            ? text.Format(text.RepairedFormat, workspace)
            : text.Format(text.UpdatedFormat, plan.Package.Version, workspace);
        try
        {
            var desktopResult = LegacyInstallationAdoption.FinishDesktopRegistration(plan.InstallRoot);
            if (desktopResult != "桌面登记无需旧版收尾。") result += " " + desktopResult;
        }
        catch (Exception exception)
        {
            result += " " + text.Format(text.RegistrationFailedFormat, exception.Message);
        }
        try
        {
            if (ManagerSelfUpdate.Schedule(plan.Package, plan.InstallRoot, executablePath))
                result += " " + text.SelfReplaceNotice;
        }
        catch (Exception exception)
        {
            result += " " + text.Format(text.SelfUpdateFailedFormat, exception.Message);
        }
        return result;
    }

    public static void WriteResult(string[] args, bool success, string message)
    {
        if (args.Length == 3 && args[0] is "--apply" or "--repair" or "--adopt-and-apply" or "--resume-legacy")
        {
            try
            {
                var target = UpdatePlan.RequireRegisteredTarget(args[2]);
                var managerDirectory = UpdateManagerPaths.InstalledDirectory(target);
                ReleasePackageLayout.RejectReparse(managerDirectory);
                var resultName = args[0] is "--adopt-and-apply" or "--resume-legacy"
                    ? "last-legacy-result.json" : "last-operation-result.json";
                var resultPath = Path.Combine(managerDirectory, resultName);
                if (File.Exists(resultPath)) ReleasePackageLayout.RejectReparse(resultPath);
                var tempPath = resultPath + ".writing";
                File.WriteAllText(tempPath,
                    JsonSerializer.Serialize(new { success, message, finishedAt = DateTimeOffset.UtcNow }));
                File.Move(tempPath, resultPath, overwrite: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (InvalidOperationException) { }
        }
        if (args.Length != 3 || args[0] != "--apply") return;
        var directory = Path.GetDirectoryName(Path.GetFullPath(args[1]));
        if (directory is null || !Directory.Exists(directory)) return;
        try
        {
            var installedParent = Path.GetDirectoryName(UpdatePlan.RequireRegisteredTarget(args[2]));
            if (!string.Equals(Path.GetDirectoryName(directory), installedParent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(directory).StartsWith("ResourceManager-update-download-", StringComparison.Ordinal))
                return;
            ReleasePackageLayout.RejectReparse(directory);
            var result = Path.Combine(directory, "last-update-result.json");
            if (File.Exists(result)) ReleasePackageLayout.RejectReparse(result);
            File.WriteAllText(result,
                JsonSerializer.Serialize(new { success, message, finishedAt = DateTimeOffset.UtcNow }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidOperationException) { }
    }

    private static void RequireExternalManager(string executablePath, string installRoot, ToolText text)
    {
        var expected = UpdateManagerPaths.InstalledExecutable(installRoot);
        if (!Path.GetFullPath(executablePath).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(text.ExternalManagerRequired);
        if (!File.Exists(expected)) throw new FileNotFoundException(text.MissingManager, expected);
        ReleasePackageLayout.RejectReparse(expected);
        ReleasePackageLayout.RejectReparse(UpdateManagerPaths.InstalledDirectory(installRoot));
    }

    internal static FileStream AcquireOperationLock(string installRoot, ToolText? text = null)
    {
        var path = Path.Combine(UpdateManagerPaths.InstalledDirectory(installRoot), "operation.lock");
        if (File.Exists(path)) ReleasePackageLayout.RejectReparse(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception)
        {
            throw new InvalidOperationException((text ?? ToolText.FromInstallRoot(installRoot)).BusyOperation, exception);
        }
    }
}

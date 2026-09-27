using System.Text.Json;
using Microsoft.Win32;
using ResourceManager.Shared.Packages;

namespace ResourceManager.Updater;

public static class UpdateManagerCommand
{
    public static string GetInstalledRoot()
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"Software\ResourceManager");
        var root = key?.GetValue("InstallRoot") as string
            ?? throw new InvalidOperationException("未找到已安装的 Resource Manager。请先使用 Install.exe 首次安装。");
        return UpdatePlan.RequireRegisteredTarget(root);
    }

    public static IReadOnlyList<string> GetPendingRecoveryDescriptions(string installRoot)
        => PackageSwitchTransaction.ListPending(UpdatePlan.RequireRegisteredTarget(installRoot))
            .Select(entry => $"{entry.PreviousVersion} → {entry.TargetVersion}（{entry.Stage}）")
            .ToArray();

    public static string? GetManagerUpdateFailure(string installRoot)
        => ManagerSelfUpdate.ReadLastFailure(UpdatePlan.RequireRegisteredTarget(installRoot));

    public static string DescribePackageAction(string packageRoot, string installRoot, bool repair)
    {
        var plan = UpdatePlan.Create(packageRoot, installRoot,
            repair ? UpdateOperation.Repair : UpdateOperation.Update);
        return repair
            ? $"将使用 {plan.Package.Version} 的完整发行包修复主程序，保留现有设置和用户数据。"
            : $"将 Resource Manager 从 {plan.PreviousVersion} 更新到 {plan.Package.Version}，保留现有设置和用户数据。";
    }

    public static async Task<string> ExecuteAsync(string[] args, string executablePath,
        CancellationToken cancellationToken = default)
    {
        if (args.Length == 3 && args[0] == "--replace-manager"
            && int.TryParse(args[2], out var oldProcessId))
            return await ManagerSelfUpdate.ReplaceAsync(executablePath, args[1],
                oldProcessId, cancellationToken);
        if (args.Length == 2 && args[0] == "--verify-package")
        {
            var package = ReleasePackageLayout.Verify(args[1]);
            return $"发行包 {package.Version} 校验通过。";
        }
        if (args.Length == 3 && args[0] is "--legacy-plan" or "--adopt-and-apply")
        {
            var adoption = LegacyInstallationAdoption.Plan(args[1], args[2], executablePath);
            if (args[0] == "--legacy-plan")
                return $"旧版 {adoption.PreviousVersion} 可接入外置管理器并更新至 {adoption.Package.Version}。";
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
            RequireExternalManager(executablePath, target);
            using var operationLock = AcquireOperationLock(target);
            var recovered = await PackageSwitchTransaction.RecoverPendingAsync(target,
                new WindowsUpdateRuntime(), cancellationToken);
            return recovered.Count == 0 ? "没有待恢复的更新事务。" : $"已恢复 {recovered.Count} 个中断事务。";
        }
        if (args.Length != 3 || args[0] is not ("--plan-only" or "--apply" or "--repair"))
            throw new ArgumentException("更新管理器命令无效。");
        var operation = args[0] == "--repair" ? UpdateOperation.Repair : UpdateOperation.Update;
        var plan = UpdatePlan.Create(args[1], args[2], operation);
        if (args[0] == "--plan-only") return $"{plan.Package.Version} 更新预检通过。";
        RequireExternalManager(executablePath, plan.InstallRoot);
        using var updateLock = AcquireOperationLock(plan.InstallRoot);
        var pending = PackageSwitchTransaction.ListPending(plan.InstallRoot);
        if (pending.Count > 0)
            throw new InvalidOperationException("存在中断的更新事务，请先使用恢复功能。");
        var workspace = await PackageSwitchTransaction.ApplyAsync(plan,
            new WindowsUpdateRuntime(), cancellationToken);
        var result = operation == UpdateOperation.Repair
            ? $"主程序已修复。旧文件保留在：{workspace}"
            : $"已更新到 {plan.Package.Version}。旧版备份保留在：{workspace}";
        try
        {
            var desktopResult = LegacyInstallationAdoption.FinishDesktopRegistration(plan.InstallRoot);
            if (desktopResult != "桌面登记无需旧版收尾。") result += " " + desktopResult;
        }
        catch (Exception exception)
        {
            result += $" 旧版桌面登记收尾未完成，可重试：{exception.Message}";
        }
        try
        {
            if (ManagerSelfUpdate.Schedule(plan.Package, plan.InstallRoot, executablePath))
                result += " 更新管理器将在旧进程退出后完成自身替换。";
        }
        catch (Exception exception)
        {
            result += $" 管理器自身更新未启动：{exception.Message}";
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

    private static void RequireExternalManager(string executablePath, string installRoot)
    {
        var expected = UpdateManagerPaths.InstalledExecutable(installRoot);
        if (!Path.GetFullPath(executablePath).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("更新、恢复和修复必须由已安装的外置更新管理器执行。");
        if (!File.Exists(expected)) throw new FileNotFoundException("外置更新管理器不存在。", expected);
        ReleasePackageLayout.RejectReparse(expected);
        ReleasePackageLayout.RejectReparse(UpdateManagerPaths.InstalledDirectory(installRoot));
    }

    internal static FileStream AcquireOperationLock(string installRoot)
    {
        var path = Path.Combine(UpdateManagerPaths.InstalledDirectory(installRoot), "operation.lock");
        if (File.Exists(path)) ReleasePackageLayout.RejectReparse(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception)
        {
            throw new InvalidOperationException("另一个更新、恢复或修复操作正在进行。", exception);
        }
    }
}

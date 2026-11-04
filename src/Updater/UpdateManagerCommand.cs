using System.Text.Json;
using Microsoft.Win32;
using ResourceManager.Shared.Packages;
using ResourceManager.Shared.Localization;
using ResourceManager.Shared.ServiceHosting;

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
        => PackageSwitchTransaction.ListPending(UpdatePlan.RequireProductTarget(installRoot))
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
                oldProcessId, cancellationToken, text);
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
            RequireInstalledManager(executablePath, target, text);
            using var operationLock = AcquireOperationLock(target, text);
            ProvisionPackageUse(target);
            var recovered = await PackageSwitchTransaction.RecoverPendingAsync(target,
                new WindowsUpdateRuntime(), cancellationToken);
            return recovered.Count == 0 ? text.NoRecovery : text.Format(text.RecoveredCountFormat, recovered.Count);
        }
        if (args.Length == 3 && args[0] == "--import-user-data")
        {
            var target = UpdatePlan.RequireRegisteredTarget(args[2]);
            RequireInstalledManager(executablePath, target, text);
            using var operationLock = AcquireOperationLock(target, text);
            ProvisionPackageUse(target);
            if (PackageSwitchTransaction.ListPending(target).Count > 0) throw new InvalidOperationException(text.PendingTransaction);
            UserDataArchive.Verify(args[1]);
            var runtime = new WindowsUpdateRuntime();
            var service = WindowsServiceRegistration.Read(WindowsServiceRegistration.ProductServiceName)
                ?? throw new InvalidOperationException(text.LauncherServiceStartFailed);
            using var maintenance = await runtime.AcquirePackageMaintenanceAsync(target, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            runtime.StopService(target);
            try
            {
                UserDataArchive.Import(
                    target,
                    args[1],
                    staged => UpdatePlan.CheckCompatibility(target, staged),
                    createCredentialFreeLegacyDatabaseCopy: LegacyAiCredentialArchiveSanitizer.CreateCredentialFreeCopy);
            }
            finally { if (service.Running) runtime.StartService(target); }
            return text.ImportUserDataCompleted;
        }
        if (args.Length != 3 || args[0] is not ("--plan-only" or "--apply" or "--repair"))
            throw new ArgumentException(text.InvalidCommand);
        var operation = args[0] == "--repair" ? UpdateOperation.Repair : UpdateOperation.Update;
        var plan = UpdatePlan.Create(args[1], args[2], operation);
        if (args[0] == "--plan-only") return text.Format(text.PreflightFormat, plan.Package.Version);
        RequireApplyManager(executablePath, plan, text);
        using var updateLock = AcquireOperationLock(plan.InstallRoot, text);
        ProvisionPackageUse(plan.InstallRoot);
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
            DesktopEntryRegistration.Refresh(plan.InstallRoot);
        }
        catch (Exception exception)
        {
            result += " " + text.Format(text.RegistrationFailedFormat, exception.Message);
        }
        try
        {
            if (ManagerSelfUpdate.Schedule(plan.Package, plan.InstallRoot, text))
                result += " " + text.SelfReplaceNotice;
        }
        catch (Exception exception)
        {
            result += " " + text.Format(text.SelfUpdateFailedFormat, exception.Message);
        }
        try
        {
            PackageSwitchTransaction.RetainLatestBackup(plan.InstallRoot, workspace);
            ProductUpdateDownload.RemoveCompleted(plan.Package.Root, plan.InstallRoot, plan.Package.Version);
        }
        catch (Exception exception)
        {
            result += " " + text.Format(text.UpdateCleanupFailedFormat, exception.Message);
        }
        return result;
    }

    public static string FormatFailure(Exception exception, ToolText text)
        => exception is UpdatePlanException failure ? failure.Format(text) : text.OperationFailed;

    public static void WriteFailure(string[] args, Exception exception, ToolText text)
    {
        System.Diagnostics.Trace.WriteLine(exception);
        var failure = exception as UpdatePlanException;
        WriteResult(args, false, FormatFailure(exception, text), failure?.Code ?? "update.applyFailed",
            failure?.Parameters, exception.ToString());
    }

    public static void WriteResult(string[] args, bool success, string message,
        string? failureCode = null, string[]? failureParameters = null, string? diagnosticDetail = null)
    {
        var targetVersion = ReadResultTargetVersion(args, success);
        if ((args.Length == 3 && args[0] is "--apply" or "--repair" or "--adopt-and-apply" or "--resume-legacy" or "--import-user-data")
            || (args.Length == 2 && args[0] == "--recover"))
        {
            try
            {
                var target = UpdatePlan.RequireRegisteredTarget(args[^1]);
                var managerDirectory = UpdateManagerPaths.InstalledDirectory(target);
                ReleasePackageLayout.RejectReparse(managerDirectory);
                var resultName = args[0] is "--adopt-and-apply" or "--resume-legacy"
                    ? "last-legacy-result.json" : "last-operation-result.json";
                var resultPath = Path.Combine(managerDirectory, resultName);
                if (File.Exists(resultPath)) ReleasePackageLayout.RejectReparse(resultPath);
                var tempPath = resultPath + ".writing";
                File.WriteAllText(tempPath,
                    JsonSerializer.Serialize(new OperationResult(success, message, DateTimeOffset.UtcNow, args,
                        failureCode, failureParameters, diagnosticDetail, targetVersion)));
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
                JsonSerializer.Serialize(new { success, message, finishedAt = DateTimeOffset.UtcNow,
                    failureCode, failureParameters, diagnosticDetail, targetVersion }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (InvalidOperationException) { }
    }

    public sealed record OperationResult(bool Success, string Message, DateTimeOffset FinishedAt, string[] Arguments,
        string? FailureCode = null, string[]? FailureParameters = null, string? DiagnosticDetail = null,
        string? TargetVersion = null);

    private static string? ReadResultTargetVersion(string[] args, bool success)
    {
        if (args.Length != 3 || args[0] is not ("--apply" or "--repair" or "--adopt-and-apply" or "--resume-legacy"))
            return null;
        try
        {
            // A successful apply may already have removed its downloaded package.
            var path = Path.Combine(success ? args[2] : args[1], "release-manifest.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 1_048_576) return null;
            using var manifest = JsonDocument.Parse(File.ReadAllText(path));
            return manifest.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.String ? version.GetString() : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    public static OperationResult? ReadResult(string[] args, DateTimeOffset startedAt)
    {
        var target = UpdatePlan.RequireRegisteredTarget(args[^1]);
        var name = args[0] is "--adopt-and-apply" or "--resume-legacy" ? "last-legacy-result.json" : "last-operation-result.json";
        var path = Path.Combine(UpdateManagerPaths.InstalledDirectory(target), name);
        if (!File.Exists(path)) return null;
        ReleasePackageLayout.RejectReparse(path);
        if (new FileInfo(path).Length > 1_048_576) throw new InvalidDataException("Invalid update result.");
        var result = JsonSerializer.Deserialize<OperationResult>(File.ReadAllText(path));
        return result is not null && result.FinishedAt >= startedAt
            && result.Arguments is not null && result.Arguments.SequenceEqual(args, StringComparer.Ordinal)
            ? result : null;
    }

    private static void RequireInstalledManager(string executablePath, string installRoot, ToolText text)
    {
        var expected = UpdateManagerPaths.InstalledExecutable(installRoot);
        if (!Path.GetFullPath(executablePath).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(text.ExternalManagerRequired);
        if (!File.Exists(expected)) throw new FileNotFoundException(text.MissingManager, expected);
        ReleasePackageLayout.RejectReparse(expected);
        ReleasePackageLayout.RejectReparse(UpdateManagerPaths.InstalledDirectory(installRoot));
    }

    // Installs from before the package use guard have none, and maintenance never creates one on its own.
    // The elevated manager provisions it here, as the installer does for a new install; an existing guard is
    // only verified. Clients of such an install predate the guard, so a new guard has no holders to wait for.
    internal static void ProvisionPackageUse(string installRoot) => PackageUseGuard.Prepare(installRoot);

    internal static void RequireApplyManager(string executablePath, UpdatePlan plan, ToolText text)
    {
        var actual = Path.GetFullPath(executablePath);
        if (actual.Equals(UpdateManagerPaths.InstalledExecutable(plan.InstallRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            RequireInstalledManager(actual, plan.InstallRoot, text);
            return;
        }
        if (!actual.Equals(UpdateManagerPaths.PackagedExecutable(plan.Package.Root),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(text.ExternalManagerRequired);
        // UpdatePlan already verified this exact selected package. Recheck its
        // manager at the execution boundary, not an arbitrary matching filename.
        using var verified = ManagerSelfUpdate.OpenVerifiedManager(plan.Package.Root, plan.Package.Version, text);
    }

    internal static FileStream AcquireOperationLock(string installRoot, ToolText? text = null)
    {
        try { return PackageOperationLock.Acquire(installRoot); }
        catch (IOException exception)
        {
            throw new InvalidOperationException((text ?? ToolText.FromInstallRoot(installRoot)).BusyOperation, exception);
        }
    }
}

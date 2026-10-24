using System.ComponentModel;
using Microsoft.Win32;
using ResourceManager.App.Application.GpuPlacement;
using ResourceManager.App.Domain.GpuPlacement;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

public sealed partial class WindowsIfeoGpuLaunchInterceptionRegistry : IGpuLaunchInterceptionRegistry
{
    internal const string BrokerFileName = "ResourceManager.GpuLaunchBroker.exe";
    internal const string OwnerValue = "ResourceManager.GpuLaunchInterception.v1";

    private const string IfeoRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const string UseFilterValueName = "UseFilter";
    private const string ParentOwnerValueName = "ResourceManagerUseFilterOwner";
    private const string FilterFullPathValueName = "FilterFullPath";
    private const string DebuggerValueName = "Debugger";
    private const string OwnerValueName = "ResourceManagerOwner";
    private const string RuleIdValueName = "ResourceManagerRuleId";

    private static readonly RegistryView[] RegistryViews =
    [
        RegistryView.Registry64,
        RegistryView.Registry32
    ];

    private static readonly object RegistryOperationGate = new();

    private readonly string brokerPath;
    private readonly IfeoOwnedRuleCleanupCoordinator ownedRuleCleanup;

    public WindowsIfeoGpuLaunchInterceptionRegistry()
        : this(Path.Combine(AppContext.BaseDirectory, BrokerFileName))
    {
    }

    internal WindowsIfeoGpuLaunchInterceptionRegistry(string brokerPath)
        : this(brokerPath, new WindowsIfeoOwnedRuleStore(BuildDebuggerCommand(brokerPath)))
    {
    }

    internal WindowsIfeoGpuLaunchInterceptionRegistry(
        string brokerPath,
        IIfeoOwnedRuleStore ownedRuleStore)
    {
        this.brokerPath = Path.GetFullPath(brokerPath);
        ownedRuleCleanup = new IfeoOwnedRuleCleanupCoordinator(ownedRuleStore, OwnerValue);
    }

    public GpuLaunchInterceptionStatus Apply(GpuPlacementProcessPolicy policy)
    {
        lock (RegistryOperationGate)
        {
            return ApplyUnderGate(policy);
        }
    }

    private GpuLaunchInterceptionStatus ApplyUnderGate(GpuPlacementProcessPolicy policy)
    {
        try { return ApplyPolicyUnderGate(policy); }
        catch (Exception error) when (IfeoOwnedRuleCleanupCoordinator.IsRegistryFailure(error))
        {
            return Failed(policy, CleanupFailureStatus(error), error.Message);
        }
    }

    private GpuLaunchInterceptionStatus ApplyPolicyUnderGate(GpuPlacementProcessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (!policy.StartupInterceptionEnabled)
        {
            RemoveOwnedRulesForPath(policy.ExecutablePath);
            return Disabled(policy);
        }

        var eligibility = EvaluateEligibility(policy.ExecutablePath, brokerPath);
        if (!eligibility.Eligible)
        {
            RemoveOwnedRulesForPath(policy.ExecutablePath);
            return Failed(policy, eligibility.Status, eligibility.Message);
        }

        var executablePath = eligibility.ExecutablePath!;
        var imageName = Path.GetFileName(executablePath);
        var ruleName = CreateRuleName(executablePath);
        var debuggerCommand = BuildDebuggerCommand(brokerPath);

        try
        {
            foreach (var view in RegistryViews)
            {
                var inspection = InspectView(view, imageName, executablePath, ruleName, debuggerCommand);
                if (!string.IsNullOrWhiteSpace(inspection.Conflict))
                {
                    return Failed(
                        policy,
                        GpuLaunchInterceptionStatuses.ThirdPartyConflict,
                        inspection.Conflict);
                }
            }

            foreach (var view in RegistryViews)
            {
                WriteRule(view, imageName, executablePath, ruleName, debuggerCommand);
            }

            return GetStatusUnderGate(policy);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryRollbackOwnedRules(executablePath);
            return Failed(policy, GpuLaunchInterceptionStatuses.RegistryAccessDenied, ex.Message);
        }
        catch (System.Security.SecurityException ex)
        {
            TryRollbackOwnedRules(executablePath);
            return Failed(policy, GpuLaunchInterceptionStatuses.RegistryAccessDenied, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            TryRollbackOwnedRules(executablePath);
            return Failed(policy, GpuLaunchInterceptionStatuses.RegistryError, ex.Message);
        }
    }

    public GpuLaunchInterceptionStatus GetStatus(GpuPlacementProcessPolicy policy)
    {
        lock (RegistryOperationGate)
        {
            return GetStatusUnderGate(policy);
        }
    }

    private GpuLaunchInterceptionStatus GetStatusUnderGate(GpuPlacementProcessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (!policy.StartupInterceptionEnabled)
        {
            return Disabled(policy);
        }

        var eligibility = EvaluateEligibility(policy.ExecutablePath, brokerPath);
        if (!eligibility.Eligible)
        {
            return Failed(policy, eligibility.Status, eligibility.Message);
        }

        var executablePath = eligibility.ExecutablePath!;
        var imageName = Path.GetFileName(executablePath);
        var ruleName = CreateRuleName(executablePath);
        var debuggerCommand = BuildDebuggerCommand(brokerPath);
        var registeredViews = new List<string>(RegistryViews.Length);

        try
        {
            foreach (var view in RegistryViews)
            {
                var inspection = InspectView(view, imageName, executablePath, ruleName, debuggerCommand);
                if (!string.IsNullOrWhiteSpace(inspection.Conflict))
                {
                    return Failed(
                        policy,
                        GpuLaunchInterceptionStatuses.ThirdPartyConflict,
                        inspection.Conflict,
                        registeredViews);
                }

                if (inspection.Registered)
                {
                    registeredViews.Add(ViewName(view));
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failed(policy, GpuLaunchInterceptionStatuses.RegistryAccessDenied, ex.Message, registeredViews);
        }
        catch (System.Security.SecurityException ex)
        {
            return Failed(policy, GpuLaunchInterceptionStatuses.RegistryAccessDenied, ex.Message, registeredViews);
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            return Failed(policy, GpuLaunchInterceptionStatuses.RegistryError, ex.Message, registeredViews);
        }

        if (registeredViews.Count == RegistryViews.Length)
        {
            return new GpuLaunchInterceptionStatus(
                policy.ProcessKey,
                executablePath,
                true,
                true,
                GpuLaunchInterceptionStatuses.Registered,
                "完整路径启动拦截已在 64 位和 32 位注册表视图生效。",
                registeredViews);
        }

        return Failed(
            policy,
            GpuLaunchInterceptionStatuses.PartialRegistration,
            "启动拦截尚未在全部注册表视图生效。",
            registeredViews);
    }

    public IReadOnlyList<GpuLaunchInterceptionStatus> Reconcile(
        GpuPlacementPolicyDocument document,
        IReadOnlyCollection<string> overlayExecutablePaths)
    {
        lock (RegistryOperationGate)
        {
            ArgumentNullException.ThrowIfNull(document);
            ArgumentNullException.ThrowIfNull(overlayExecutablePaths);

            var desiredPolicies = SelectDesiredPolicies(document, overlayExecutablePaths);
            var desiredPaths = desiredPolicies
                .Select(static policy => NormalizePath(policy.ExecutablePath))
                .Where(static path => !string.IsNullOrWhiteSpace(path))
                .Select(static path => path!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var failures = ownedRuleCleanup.RemoveStale(RegistryViews, desiredPaths);
            return failures.Select(failure => new GpuLaunchInterceptionStatus(
                    failure.ImageName, null, false, false,
                    CleanupFailureStatus(failure.Error),
                    $"{failure.View}/{failure.ImageName}: {failure.Error.Message}", []))
                .Concat(desiredPolicies.Select(ApplyUnderGate))
                .ToArray();
        }
    }

    private static string CleanupFailureStatus(Exception error)
    {
        var accessDenied = error is UnauthorizedAccessException or System.Security.SecurityException
            || error is AggregateException aggregate && aggregate.Flatten().InnerExceptions.Any(
                item => item is UnauthorizedAccessException or System.Security.SecurityException);
        return accessDenied ? GpuLaunchInterceptionStatuses.RegistryAccessDenied : GpuLaunchInterceptionStatuses.RegistryError;
    }

    internal static IReadOnlyList<GpuPlacementProcessPolicy> SelectDesiredPolicies(
        GpuPlacementPolicyDocument document,
        IReadOnlyCollection<string> overlayExecutablePaths)
    {
        var policies = document.ProcessPolicies
            .Where(static policy => policy.StartupInterceptionEnabled)
            .ToList();
        var desiredPaths = policies
            .Select(static policy => NormalizePath(policy.ExecutablePath))
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in overlayExecutablePaths)
        {
            var normalized = NormalizePath(path);
            if (normalized is null || !desiredPaths.Add(normalized))
            {
                continue;
            }

            var existing = document.ProcessPolicies
                .Where(policy => string.Equals(NormalizePath(policy.ExecutablePath), normalized,
                    StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static policy => policy.UpdatedAt)
                .FirstOrDefault();
            policies.Add(existing is null
                ? new GpuPlacementProcessPolicy(
                    "overlay", normalized, Path.GetFileName(normalized), normalized,
                    true, [], null, DateTimeOffset.MinValue, true)
                : existing with { StartupInterceptionEnabled = true });
        }

        return policies;
    }

    public GpuLaunchInterceptionCleanupResult RemoveAllOwnedRules()
    {
        lock (RegistryOperationGate)
        {
            try
            {
                var removed = ownedRuleCleanup.RemoveAll(RegistryViews);
                return new GpuLaunchInterceptionCleanupResult(
                    true,
                    removed,
                    $"已从 IFEO 注册表视图中移除 {removed} 条 Resource Manager 启动拦截规则。");
            }
            catch (Exception ex) when (IfeoOwnedRuleCleanupCoordinator.IsRegistryFailure(ex))
            {
                return new GpuLaunchInterceptionCleanupResult(false, 0, ex.Message);
            }
        }
    }

    internal static string BuildDebuggerCommand(string path)
    {
        return $"\"{Path.GetFullPath(path)}\" --resource-manager-ifeo";
    }

    private void TryRollbackOwnedRules(string executablePath)
    {
        try
        {
            RemoveOwnedRulesForPath(executablePath);
        }
        catch (Exception ex) when (IfeoOwnedRuleCleanupCoordinator.IsRegistryFailure(ex))
        {
            // The original registration error is the actionable result. Reconciliation retries cleanup later.
        }
    }

    private static GpuLaunchInterceptionStatus Disabled(GpuPlacementProcessPolicy policy)
    {
        return new GpuLaunchInterceptionStatus(
            policy.ProcessKey,
            NormalizePath(policy.ExecutablePath),
            false,
            false,
            GpuLaunchInterceptionStatuses.Disabled,
            "该进程未启用固定启动拦截。",
            []);
    }

    private static GpuLaunchInterceptionStatus Failed(
        GpuPlacementProcessPolicy policy,
        string status,
        string message,
        IReadOnlyList<string>? registeredViews = null)
    {
        return new GpuLaunchInterceptionStatus(
            policy.ProcessKey,
            NormalizePath(policy.ExecutablePath),
            policy.StartupInterceptionEnabled,
            false,
            status,
            message,
            registeredViews ?? []);
    }

    private sealed record ViewInspection(bool Registered, string? Conflict);

    private sealed record EligibilityResult(
        bool Eligible,
        string Status,
        string Message,
        string? ExecutablePath);
}

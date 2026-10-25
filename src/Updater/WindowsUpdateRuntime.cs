using System.Diagnostics;
using Microsoft.Win32;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Updater;

internal interface IUpdateRuntime
{
    Task<IDisposable> AcquirePackageMaintenanceAsync(string installRoot, CancellationToken cancellationToken);
    void StopService(string installRoot);
    void StartService(string installRoot);
    Task WaitForHealthAsync(CancellationToken cancellationToken);
    void WriteInstalledVersion(string root, string version);
}

internal sealed partial class WindowsUpdateRuntime : IUpdateRuntime
{
    public void StartService(string root) => WindowsServiceRegistration.StartExisting(
        WindowsServiceRegistration.ProductServiceName, Backend(root), restart: false);

    private static string Backend(string root)
        => Path.Combine(root, "Bin", "ResourceManager", "ResourceManager.exe");

    public Task<IDisposable> AcquirePackageMaintenanceAsync(string installRoot, CancellationToken cancellationToken)
        => ResourceManager.Shared.Packages.PackageUseGuard.AcquireMaintenanceAsync(installRoot, cancellationToken);

    public async Task WaitForHealthAsync(CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var capabilities = await client.GetAsync(
                    "http://127.0.0.1:9321/api/runtime/capabilities", cancellationToken);
                using var settings = await client.GetAsync(
                    "http://127.0.0.1:9321/api/settings/app", cancellationToken);
                if (capabilities.IsSuccessStatusCode && settings.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { }
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        throw new TimeoutException("新版本服务未通过运行时与设置数据健康检查。");
    }

    public void WriteInstalledVersion(string root, string version)
    {
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\ResourceManager", writable: true)
            ?? throw new InvalidOperationException("产品版本注册项不存在。");
        if (key.GetValue("InstallationContract") as string != "resource-manager-directory-registration-v1"
            || !string.Equals(key.GetValue("InstallRoot") as string, root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("产品版本注册项不属于当前安装。");
        key.SetValue("DisplayVersion", version);
    }
}

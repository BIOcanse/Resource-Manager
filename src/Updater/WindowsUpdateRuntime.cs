using Microsoft.Win32;
using ResourceManager.Shared.ServiceHosting;

namespace ResourceManager.Updater;

internal interface IUpdateRuntime
{
    Task WaitForNativeUiExitAsync(string installRoot, CancellationToken cancellationToken);
    void StopService(string installRoot);
    void StartService(string installRoot);
    Task WaitForHealthAsync(CancellationToken cancellationToken);
    void WriteInstalledVersion(string root, string version);
}

internal sealed class WindowsUpdateRuntime : IUpdateRuntime
{
    public void StopService(string root) => WindowsServiceRegistration.StopExisting(
        WindowsServiceRegistration.ProductServiceName, Backend(root));

    public void StartService(string root) => WindowsServiceRegistration.StartExisting(
        WindowsServiceRegistration.ProductServiceName, Backend(root), restart: false);

    private static string Backend(string root)
        => Path.Combine(root, "Bin", "ResourceManager", "ResourceManager.exe");

    public async Task WaitForNativeUiExitAsync(string installRoot, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = false;
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("ResourceManager.NativeUi"))
            {
                using (process)
                {
                    try
                    {
                        var path = process.MainModule?.FileName;
                        if (path is not null && IsUnder(path, installRoot)) active = true;
                    }
                    catch (System.ComponentModel.Win32Exception) { active = true; }
                    catch (InvalidOperationException) { /* Process exited while inspected. */ }
                }
            }
            if (!active) return;
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
        throw new TimeoutException("等待桌面界面退出超时；请从托盘退出产品后重试。");
    }

    private static bool IsUnder(string path, string root)
        => Path.GetFullPath(path).StartsWith(
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

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

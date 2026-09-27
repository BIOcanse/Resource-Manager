using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Security.Principal;
using Microsoft.Win32;
using ResourceManager.App.Application.Settings;
using ResourceManager.App.Domain.Updates;
using ResourceManager.App.Infrastructure.Paths;
using ResourceManager.Shared.Packages;

namespace ResourceManager.App.Infrastructure.Updates;

public sealed record ProductUpdateState(
    string Stage,
    string? TargetVersion,
    string? Workspace,
    string? Error,
    DateTimeOffset UpdatedAt);

public sealed class ProductUpdateCoordinator(
    ProductVersionCatalogService catalog,
    IHttpClientFactory httpClientFactory,
    IHostEnvironment environment,
    ILogger<ProductUpdateCoordinator> logger)
{
    private readonly object gate = new();
    private ProductUpdateState state = new("idle", null, null, null, DateTimeOffset.UtcNow);

    public ProductUpdateState Status
    {
        get { lock (gate) return state; }
    }

    public ProductUpdateState Submit(string choice)
    {
        if (string.IsNullOrWhiteSpace(choice) || !choice.StartsWith("version:", StringComparison.Ordinal))
            throw new ArgumentException("必须选择目录中的具体目标版本。", nameof(choice));
        lock (gate)
        {
            if (state.Stage is "checking" or "downloading" or "verifying" or "waitingForExit")
                throw new InvalidOperationException("已有更新任务正在进行。");
            state = new ProductUpdateState("checking", null, null, null, DateTimeOffset.UtcNow);
        }
        _ = Task.Run(() => PrepareAndLaunchAsync(choice));
        return Status;
    }

    private async Task PrepareAndLaunchAsync(string choice)
    {
        string? workspace = null;
        try
        {
            var root = PackagePathResolver.ResolvePackageRoot(environment.ContentRootPath);
            using var identity = WindowsIdentity.GetCurrent();
            if (identity.User?.IsWellKnown(WellKnownSidType.LocalSystemSid) != true)
                throw new InvalidOperationException("更新必须由已安装的 LocalSystem 产品服务准备。");
            var manifest = Path.Combine(root, "release-manifest.json");
            if (!File.Exists(manifest))
                throw new InvalidOperationException("当前运行目录不是正式安装的发行包。");
            var listing = await catalog.ReadAsync(CancellationToken.None);
            var option = listing.Options.FirstOrDefault(item => item.Choice == choice && item.Selectable)
                ?? throw new InvalidOperationException("目标版本不可选，或目录已发生变化。");
            if (listing.Status != "loaded" || !listing.Complete)
                throw new InvalidOperationException("版本目录不完整，不能开始更新。");
            var parent = Path.GetDirectoryName(root)
                ?? throw new InvalidOperationException("无法定位安装目录父目录。");
            workspace = Path.Combine(parent, "ResourceManager-update-download-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            SetState("downloading", option.Version, workspace);

            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(10);
            var archivePath = Path.Combine(workspace, option.AssetName);
            var expected = await ReadChecksumAsync(client, option, CancellationToken.None);
            await DownloadAndVerifyAsync(client, option.DownloadUrl, archivePath, expected, CancellationToken.None);
            SetState("verifying", option.Version, workspace);
            var packageRoot = Path.Combine(workspace, "package");
            ExtractChecked(archivePath, packageRoot);
            var package = ReleasePackageLayout.Verify(packageRoot);
            if (!string.Equals(package.Version.TrimStart('v'), option.Version.TrimStart('v'), StringComparison.Ordinal))
                throw new InvalidDataException("发行包版本与目录目标不一致。");
            var updater = ResolveInstalledManager(root);
            var process = Process.Start(new ProcessStartInfo(updater)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workspace,
                ArgumentList = { "--apply", packageRoot, root }
            }) ?? throw new InvalidOperationException("独立更新器未能启动。");
            SetState("waitingForExit", option.Version, workspace);
            _ = ObserveAsync(process, option.Version, workspace);
            // The updater waits for every Native UI session to exit before stopping the service.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Product update preparation failed.");
            lock (gate) state = new ProductUpdateState(
                "error", state.TargetVersion, workspace, exception.Message, DateTimeOffset.UtcNow);
        }
    }

    private static string ResolveInstalledManager(string installRoot)
    {
        var expected = UpdateManagerPaths.InstalledExecutable(installRoot);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(@"Software\ResourceManager");
        if (key?.GetValue("InstallationContract") as string != "resource-manager-directory-registration-v1"
            || !string.Equals(key.GetValue("InstallRoot") as string, installRoot, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(key.GetValue("UpdateManagerPath") as string, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("没有已登记的外置更新管理器。");
        if (!File.Exists(expected)) throw new FileNotFoundException("外置更新管理器不存在。", expected);
        ReleasePackageLayout.RejectReparse(UpdateManagerPaths.InstalledDirectory(installRoot));
        ReleasePackageLayout.RejectReparse(expected);
        return expected;
    }

    private async Task ObserveAsync(Process process, string version, string workspace)
    {
        using (process)
        {
            try
            {
                await process.WaitForExitAsync();
                lock (gate)
                {
                    if (state.Stage == "waitingForExit" && state.Workspace == workspace)
                        state = new ProductUpdateState(
                            process.ExitCode == 0 ? "completed" : "error",
                            version, workspace,
                            process.ExitCode == 0 ? null : "更新器失败；详情见工作目录中的结果文件。",
                            DateTimeOffset.UtcNow);
                }
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Updater observation failed.");
            }
        }
    }

    private void SetState(string stage, string version, string workspace)
    {
        lock (gate) state = new ProductUpdateState(stage, version, workspace, null, DateTimeOffset.UtcNow);
    }

    private static async Task<string> ReadChecksumAsync(
        HttpClient client, ProductVersionOption option, CancellationToken cancellationToken)
    {
        var text = await client.GetStringAsync(option.ChecksumUrl, cancellationToken);
        var match = Regex.Match(text.Trim(), @"^([0-9a-fA-F]{64})\s\s([A-Za-z0-9._-]+)$",
            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success || match.Groups[2].Value != option.AssetName)
            throw new InvalidDataException("发行包校验文件格式或资产名称不匹配。");
        return match.Groups[1].Value;
    }

    private static async Task DownloadAndVerifyAsync(
        HttpClient client, string url, string path, string expected, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 2_147_483_648L)
            throw new InvalidDataException("发行包超过更新大小上限。");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long total = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            total += read;
            if (total > 2_147_483_648L) throw new InvalidDataException("发行包超过更新大小上限。");
            hash.AppendData(buffer.AsSpan(0, read));
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        await output.FlushAsync(cancellationToken);
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("发行包 SHA-256 校验失败。");
    }

    private static void ExtractChecked(string archivePath, string target)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 50_000 || archive.Entries.Sum(entry => entry.Length) > 4_294_967_296L)
            throw new InvalidDataException("发行包解压规模超过上限。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName;
            if (relative.Contains('\\') || relative.StartsWith('/')
                || relative.Split('/').Any(segment => segment is "." or "..")
                || !seen.Add(relative))
                throw new InvalidDataException("发行包包含不安全或重复路径。");
            var mode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (mode == 0xA000) throw new InvalidDataException("发行包不能包含符号链接。");
        }
        Directory.CreateDirectory(target);
        archive.ExtractToDirectory(target);
    }
}

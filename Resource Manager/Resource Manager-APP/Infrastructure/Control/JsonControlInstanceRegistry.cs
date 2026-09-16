using System.Text.Json;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>
/// 登记表存成一个 JSON 文件。
///
/// 只增不减：认到新设备就登记，认不到的**不删**，只是这次不在场。
/// 删除永远是用户的动作（界面上那个删除按钮），不是程序自己判断的。
/// 这就是「配置不会因为卡拔了就丢」的兑现方式。
/// </summary>
public sealed class JsonControlInstanceRegistry(
    IHostEnvironment environment,
    IControlObjectCatalog catalog,
    IControlDesiredStateStore desiredStates,
    ILogger<JsonControlInstanceRegistry>? logger = null) : IControlInstanceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string registryPath = Path.Combine(
        environment.ContentRootPath,
        "UserData",
        "Control",
        "instances.json");
    private ControlInstanceRegistryFile? cached;

    public Task<ControlInstanceCatalog> ReadAsync(CancellationToken cancellationToken)
        => SynchronizeAsync(cancellationToken);

    public Task<ControlInstanceCatalog> RefreshAsync(CancellationToken cancellationToken)
        => SynchronizeAsync(cancellationToken);

    public async Task<ControlInstanceCatalog> ForgetAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var present = catalog.ReadObjects().Objects
            .Any(entry => string.Equals(entry.Id, instanceId, StringComparison.Ordinal));
        if (present)
        {
            // 在场的删不掉：下一次读取又会把它登记回来，等于什么都没发生。
            // 与其让用户点了没反应，不如直接说清楚。
            throw new InvalidOperationException("设备正在使用，先断开再删除它的记录。");
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = Load();
            var kept = file.Instances
                .Where(entry => !string.Equals(entry.Id, instanceId, StringComparison.Ordinal))
                .ToArray();
            await SaveAsync(new ControlInstanceRegistryFile(kept), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        // 设定跟着实例一起走：实例没了，它的期望状态也不该继续留着。
        await ForgetDesiredStateAsync(instanceId, cancellationToken).ConfigureAwait(false);
        return await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 把当前认到的设备并进登记表，然后连同"谁在场"一起返回。
    ///
    /// 新设备登记时自带默认配置（取各能力的默认值），这样「恢复默认」立刻可用。
    /// 已登记的只更新名字和最后见到的时间 —— **不覆盖默认配置**，
    /// 那是用户可能已经用过的基准。
    /// </summary>
    private async Task<ControlInstanceCatalog> SynchronizeAsync(CancellationToken cancellationToken)
    {
        var objects = catalog.ReadObjects().Objects;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ControlInstanceRegistryFile file;
        try
        {
            file = Load();
            var byId = file.Instances.ToDictionary(
                static entry => entry.Id,
                StringComparer.Ordinal);
            var now = DateTimeOffset.UtcNow;
            var changed = false;

            foreach (var controlObject in objects)
            {
                if (byId.TryGetValue(controlObject.Id, out var existing))
                {
                    var refreshed = existing with
                    {
                        DisplayName = controlObject.DisplayName,
                        LastSeenAt = now
                    };
                    if (refreshed != existing)
                    {
                        byId[controlObject.Id] = refreshed;
                        changed = true;
                    }
                    continue;
                }

                byId[controlObject.Id] = new ControlInstance(
                    controlObject.Id,
                    controlObject.Kind,
                    controlObject.DisplayName,
                    controlObject.Platform,
                    controlObject.GpuAttachment,
                    IdentityIsUnique(controlObject),
                    now,
                    now,
                    DefaultSettingsOf(controlObject));
                changed = true;
            }

            if (changed)
            {
                file = new ControlInstanceRegistryFile(
                    byId.Values.OrderBy(static entry => entry.FirstSeenAt).ToArray());
                await SaveAsync(file, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }

        var presentIds = objects.Select(static entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        return new ControlInstanceCatalog(
            file.Instances
                .Select(entry => new ControlInstanceView(entry, presentIds.Contains(entry.Id)))
                .ToArray(),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// 这个身份能不能唯一认出这台设备。
    ///
    /// 显卡的 id 里带着 PCI 标识和实例路径，能。像 <c>fan:cpu</c> 这种只有位置和名字，
    /// 换一个同名的上去我们分辨不出来，所以如实标成不唯一。
    /// </summary>
    private static bool IdentityIsUnique(ControlObject controlObject)
        => controlObject.Id.Contains("PCI\\", StringComparison.OrdinalIgnoreCase)
            || controlObject.Id.Contains("VEN_", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<ControlSetting> DefaultSettingsOf(ControlObject controlObject)
        => controlObject.Capabilities
            .Where(static capability => capability.Range?.DefaultValue is not null)
            .Select(static capability => new ControlSetting(
                capability.Id,
                Number: capability.Range!.DefaultValue))
            .ToArray();

    private async Task ForgetDesiredStateAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        var desired = await desiredStates.LoadAsync(cancellationToken).ConfigureAwait(false);
        var kept = desired.Objects
            .Where(entry => !string.Equals(entry.ObjectId, instanceId, StringComparison.Ordinal))
            .ToArray();
        if (kept.Length != desired.Objects.Count)
        {
            await desiredStates.SaveAsync(new ControlDesiredState(kept), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private ControlInstanceRegistryFile Load()
    {
        if (cached is not null)
        {
            return cached;
        }
        if (!File.Exists(registryPath))
        {
            cached = ControlInstanceRegistryFile.Empty;
            return cached;
        }
        try
        {
            var json = File.ReadAllText(registryPath);
            cached = JsonSerializer.Deserialize<ControlInstanceRegistryFile>(json, JsonOptions)
                ?? ControlInstanceRegistryFile.Empty;
        }
        catch (Exception error) when (error is JsonException or IOException
            or UnauthorizedAccessException)
        {
            // 读不动就当还没登记过。设备会在下一次同步时重新登记回来，
            // 丢的只是"第一次见到的时间"和用户删过的记录。
            logger?.LogWarning(error, "控制实例登记表读取失败，按空表处理。");
            cached = ControlInstanceRegistryFile.Empty;
        }
        return cached;
    }

    private async Task SaveAsync(
        ControlInstanceRegistryFile file,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(registryPath)
            ?? throw new InvalidOperationException("控制实例登记表的存放路径没有父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{registryPath}.{Environment.ProcessId}.tmp";
        var json = JsonSerializer.Serialize(file, JsonOptions);
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, registryPath, overwrite: true);
        cached = file;
    }
}

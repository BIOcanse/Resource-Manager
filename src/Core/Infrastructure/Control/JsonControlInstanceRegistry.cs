using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Text.RegularExpressions;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;
using ResourceManager.App.Hosting.StartupCapabilities;

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
    IControlPlane plane,
    ILogger<JsonControlInstanceRegistry>? logger = null,
    StartupCapabilitySet? capabilities = null) : IControlInstanceRegistry
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web) { WriteIndented = true, TypeInfoResolver = ControlInstanceJsonContext.Default };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string registryPath = ControlDataPaths.FilePath(
        environment.ContentRootPath,
        "instances.json", capabilities);
    private ControlInstanceRegistryFile? cached;
    // A failed read cannot authorize replacing the unknown persisted registrations.
    private bool persistenceReadFailed;

    public Task<ControlInstanceCatalog> ReadAsync(CancellationToken cancellationToken)
        => SynchronizeAsync(cancellationToken, recoverUnreadable: false);

    public Task<ControlInstanceCatalog> RefreshAsync(CancellationToken cancellationToken)
        => SynchronizeAsync(cancellationToken, recoverUnreadable: true);

    public async Task<ControlInstanceCatalog> ForgetAsync(
        string instanceId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        var objects = catalog.ReadObjects().Objects;
        var present = objects
            .Any(entry => string.Equals(entry.Id, instanceId, StringComparison.Ordinal));
        if (present)
        {
            // 在场的删不掉：下一次读取又会把它登记回来，等于什么都没发生。
            // 与其让用户点了没反应，不如直接说清楚。
            throw new ControlRequestException(new("control-instance-present"));
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = Load(objects);
            var recovering = persistenceReadFailed;
            if (recovering && capabilities is not null && !capabilities.Allows(StartupCapability.MutablePersistence))
                return new([], DateTimeOffset.UtcNow);
            var kept = file.Instances
                .Where(entry => !string.Equals(entry.Id, instanceId, StringComparison.Ordinal))
                .ToArray();
            if (recovering)
            {
                if (!await SaveAsync(new ControlInstanceRegistryFile(kept), cancellationToken).ConfigureAwait(false))
                    return new([], DateTimeOffset.UtcNow);
            }
            await plane.ForgetInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false);
            if (!recovering)
                await SaveAsync(new ControlInstanceRegistryFile(kept), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }

        // 设定跟着实例一起走：实例没了，它的期望状态也不该继续留着。
        return await SynchronizeAsync(cancellationToken, recoverUnreadable: false).ConfigureAwait(false);
    }

    /// <summary>
    /// 把当前认到的设备并进登记表，然后连同"谁在场"一起返回。
    ///
    /// 新设备登记时自带默认配置（取各能力的默认值），这样「恢复默认」立刻可用。
    /// 已登记的只更新名字和最后见到的时间 —— **不覆盖默认配置**，
    /// 那是用户可能已经用过的基准。
    /// </summary>
    private async Task<ControlInstanceCatalog> SynchronizeAsync(CancellationToken cancellationToken, bool recoverUnreadable)
    {
        var objects = catalog.ReadObjects().Objects;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ControlInstanceRegistryFile file;
        try
        {
            file = Load(objects);
            if (persistenceReadFailed && (!recoverUnreadable
                || capabilities is not null && !capabilities.Allows(StartupCapability.MutablePersistence)))
                return new([], DateTimeOffset.UtcNow);
            var byId = file.Instances.ToDictionary(
                static entry => entry.Id,
                StringComparer.Ordinal);
            var now = DateTimeOffset.UtcNow;
            var changed = persistenceReadFailed;

            foreach (var controlObject in objects)
            {
                if (byId.TryGetValue(controlObject.Id, out var existing))
                {
                    var refreshed = existing with
                    {
                        ReportedName = controlObject.ReportedName ?? existing.ReportedName,
                        Ordinal = controlObject.Ordinal ?? existing.Ordinal,
                        AdapterIndex = controlObject.AdapterIndex ?? existing.AdapterIndex,
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
                    controlObject.ReportedName,
                    controlObject.Platform,
                    controlObject.GpuAttachment,
                    IdentityIsUnique(controlObject),
                    now,
                    now,
                    DefaultSettingsOf(controlObject), controlObject.Ordinal, controlObject.AdapterIndex);
                changed = true;
            }

            if (changed)
            {
                file = new ControlInstanceRegistryFile(
                    byId.Values.OrderBy(static entry => entry.FirstSeenAt).ToArray());
                if (capabilities is null || capabilities.Allows(StartupCapability.MutablePersistence))
                {
                    if (!await SaveAsync(file, cancellationToken).ConfigureAwait(false))
                        return new([], DateTimeOffset.UtcNow);
                }
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
    /// 身份是按出厂唯一标识定的（id 里带 <c>uid:</c>）才算能 ——
    /// 那种标识不随槽位、驱动、系统重装而变。退到型号的那些标不唯一：
    /// 换一块一模一样的卡上去我们分辨不出来，**如实标出来，不假装能唯一识别**。
    /// </summary>
    private static bool IdentityIsUnique(ControlObject controlObject)
        => controlObject.Id.Contains(":uid:", StringComparison.Ordinal);

    private static IReadOnlyList<ControlSetting> DefaultSettingsOf(ControlObject controlObject)
        => controlObject.Capabilities
            .Where(static capability => capability.Range?.DefaultValue is not null)
            .Select(static capability => new ControlSetting(
                capability.Id,
                Number: capability.Range!.DefaultValue))
            .ToArray();

    private ControlInstanceRegistryFile Load(IReadOnlyList<ControlObject> objects)
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
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException("control-instance-file-invalid");
            if (!document.RootElement.TryGetProperty("formatVersion", out var version))
            {
                cached = ConvertLegacy(json, objects);
                if (capabilities is null || capabilities.Allows(StartupCapability.MutablePersistence))
                    ReplacePreservingOriginal(cached, ".legacy-v1.bak");
                else logger?.LogInformation("Control instance legacy conversion is read-only; the original file was retained.");
            }
            else
            {
                if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 2)
                    throw new JsonException("control-instance-format-unsupported");
                cached = JsonSerializer.Deserialize<ControlInstanceRegistryFile>(json, JsonOptions)
                    ?? throw new JsonException("control-instance-file-invalid");
                Validate(cached);
            }
        }
        catch (Exception error) when (error is JsonException or IOException
            or UnauthorizedAccessException)
        {
            // Ordinary reads return empty for this owner lifetime and keep the
            // original untouched. Only explicit Refresh/Forget may recover it,
            // first preserving those exact bytes beside the new facts-only file.
            logger?.LogWarning(error, "Control instance registry could not be loaded or migrated; returning an empty catalog and preserving the original file.");
            persistenceReadFailed = true;
            cached = ControlInstanceRegistryFile.Empty;
        }
        return cached;
    }

    // One-time reader for supported pre-v2 releases. Delete this whole migration
    // (legacy DTOs/context registration included) when no supported upgrade source
    // writes the unversioned displayName format. Do not expand it into a runtime
    // display-text compatibility path.
    private static ControlInstanceRegistryFile ConvertLegacy(string json, IReadOnlyList<ControlObject> objects)
    {
        var legacy = JsonSerializer.Deserialize<LegacyControlInstanceRegistryFile>(json, JsonOptions)
            ?? throw new JsonException("control-instance-legacy-file-invalid");
        if (legacy.Instances is null) throw new JsonException("control-instance-legacy-file-invalid");
        var instances = new List<ControlInstance>(legacy.Instances.Count);
        foreach (var old in legacy.Instances)
        {
            if (old is null) throw new JsonException("control-instance-legacy-entry-invalid");
            var current = objects.FirstOrDefault(item => string.Equals(item.Id, old.Id, StringComparison.Ordinal));
            var ordinal = current?.Ordinal ?? old.Ordinal;
            var adapterIndex = current?.AdapterIndex ?? old.AdapterIndex;
            if (old.Kind == ControlObjectKinds.Fan)
            {
                ordinal ??= IndexFromId(old.Id, "fan:oem");
                adapterIndex ??= IndexFromId(old.Id, "fan:gpu");
            }
            // Only the old writer's proven generated patterns are discarded. A
            // current hardware-reported name is authoritative even if it looks
            // similar. An optional label cannot supply an otherwise absent index.
            var generatedName = old.Kind == ControlObjectKinds.Fan
                && Regex.IsMatch(old.DisplayName ?? string.Empty, @"^(?:风扇\s*\d+|显卡风扇控制器（GPU\d+）)$", RegexOptions.CultureInvariant);
            var name = current?.ReportedName ?? (generatedName ? null : old.DisplayName);
            instances.Add(new(old.Id, old.Kind, name, old.Platform, old.GpuAttachment,
                old.IdentityIsUnique, old.FirstSeenAt, old.LastSeenAt, old.DefaultSettings, ordinal, adapterIndex));
        }
        var file = new ControlInstanceRegistryFile(instances);
        Validate(file);
        return file;
    }

    private static int? IndexFromId(string? id, string prefix)
        => id?.StartsWith(prefix, StringComparison.Ordinal) == true
            && int.TryParse(id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var index)
            && index >= 0 ? index : null;

    private static void Validate(ControlInstanceRegistryFile file)
    {
        if (file.Instances is null) throw new JsonException("control-instance-file-invalid");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in file.Instances)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id)
                || string.IsNullOrWhiteSpace(item.Kind) || string.IsNullOrWhiteSpace(item.Platform.OperatingSystem)
                || string.IsNullOrWhiteSpace(item.Platform.Vendor) || item.DefaultSettings is null
                || item.Ordinal is < 0 || item.AdapterIndex is < 0
                || item.DefaultSettings.Any(setting => setting is null || string.IsNullOrWhiteSpace(setting.CapabilityId)))
                throw new JsonException("control-instance-entry-invalid");
        }
    }

    private void ReplacePreservingOriginal(ControlInstanceRegistryFile file, string backupSuffix)
    {
        var backupPath = registryPath + backupSuffix;
        if (File.Exists(backupPath))
            backupPath = $"{registryPath}{backupSuffix[..^4]}.{Guid.NewGuid():N}.bak";
        var temporaryPath = $"{registryPath}.{Environment.ProcessId}.migration.tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(file, JsonOptions));
            File.Replace(temporaryPath, registryPath, backupPath);
            logger?.LogInformation("Control instance registry replaced with format 2; original backup: {BackupPath}", backupPath);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { logger?.LogWarning(error, "Control instance migration temporary file could not be removed: {Path}", temporaryPath); }
        }
    }

    private async Task<bool> SaveAsync(
        ControlInstanceRegistryFile file,
        CancellationToken cancellationToken)
    {
        if (persistenceReadFailed)
        {
            try { ReplacePreservingOriginal(file, ".unconverted.bak"); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                logger?.LogWarning(error, "Control instance recovery could not preserve the original file; returning an empty catalog without changing it.");
                return false;
            }
            persistenceReadFailed = false;
            cached = file;
            return true;
        }
        var directory = Path.GetDirectoryName(registryPath)
            ?? throw new InvalidOperationException("控制实例登记表的存放路径没有父目录。");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{registryPath}.{Environment.ProcessId}.tmp";
        var json = JsonSerializer.Serialize(file, JsonOptions);
        await File.WriteAllTextAsync(temporaryPath, json, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, registryPath, overwrite: true);
        cached = file;
        return true;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true, WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ControlInstanceRegistryFile))]
[JsonSerializable(typeof(LegacyControlInstanceRegistryFile))]
internal partial class ControlInstanceJsonContext : JsonSerializerContext;

internal sealed record LegacyControlInstanceRegistryFile(
    [property: JsonRequired] IReadOnlyList<LegacyControlInstance> Instances);

internal sealed record LegacyControlInstance(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Kind,
    string? DisplayName,
    [property: JsonRequired] ControlObjectPlatform Platform,
    string? GpuAttachment,
    [property: JsonRequired] bool IdentityIsUnique,
    [property: JsonRequired] DateTimeOffset FirstSeenAt,
    [property: JsonRequired] DateTimeOffset LastSeenAt,
    [property: JsonRequired] IReadOnlyList<ControlSetting> DefaultSettings,
    int? Ordinal = null,
    int? AdapterIndex = null);

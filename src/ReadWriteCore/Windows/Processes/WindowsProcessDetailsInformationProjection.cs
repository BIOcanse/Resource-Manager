using System.Globalization;
using System.Text.Json;
using ResourceManager.App.Infrastructure.ResourceBreakdown;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Processes;

internal static class WindowsProcessDetailsIdentity
{
    private const string Prefix = "windows-process|";

    internal static string Process(ProcessInstanceKey key)
        => $"{Prefix}{key.ProcessId}:{key.StartKey.ToString(CultureInfo.InvariantCulture)}";

    internal static string Module(ProcessInstanceKey key, ulong baseAddress)
        => $"{Process(key)}|module|{baseAddress.ToString("X", CultureInfo.InvariantCulture)}";

    internal static string Thread(ProcessInstanceKey key, uint threadId)
        => $"{Process(key)}|thread|{threadId.ToString(CultureInfo.InvariantCulture)}";

    internal static bool TryParse(string identity, out ProcessInstanceKey key, out string kind)
    {
        key = default;
        kind = string.Empty;
        if (!identity.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var parts = identity[Prefix.Length..].Split('|');
        if (parts.Length is not (1 or 3)) return false;
        var identityParts = parts[0].Split(':');
        if (identityParts.Length != 2
            || !int.TryParse(identityParts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || pid <= 0
            || !long.TryParse(identityParts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var creation)
            || creation <= 0) return false;
        if (parts.Length == 3)
        {
            kind = parts[1];
            if (kind is not ("module" or "thread") || parts[2].Length == 0) return false;
            if (kind == "module" && !ulong.TryParse(parts[2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _)) return false;
            if (kind == "thread" && (!uint.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var tid) || tid == 0)) return false;
        }
        key = new(pid, creation);
        return true;
    }
}

internal static class WindowsProcessDetailsInformationProjection
{
    internal static DeviceInformationSnapshot Apply(DeviceInformationSnapshot previous,
        bool inventoryComplete, IReadOnlyList<ProcessInstanceKey> currentProcesses, bool allProcesses,
        IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsFields> selected,
        IReadOnlyDictionary<ProcessInstanceKey, WindowsProcessDetailsReadResult> results,
        DateTimeOffset observedAt)
    {
        var objects = previous.Devices.ToDictionary(static device => device.DeviceIdentity,
            static device => new MutableDevice(device.ObjectType, device.Fields));
        var affected = new Dictionary<ProcessInstanceKey, WindowsProcessDetailsFields>();
        if (allProcesses)
        {
            foreach (var device in previous.Devices)
                if (WindowsProcessDetailsIdentity.TryParse(device.DeviceIdentity, out var key, out _))
                    affected[key] = WindowsProcessDetailsFields.All;
            foreach (var key in currentProcesses) affected[key] = WindowsProcessDetailsFields.All;
        }
        else
        {
            foreach (var pair in selected) affected[pair.Key] = pair.Value;
        }

        var current = currentProcesses.ToHashSet();
        foreach (var pair in affected)
        {
            var key = pair.Key;
            var fields = pair.Value;
            var result = results.GetValueOrDefault(key) ?? WindowsProcessDetailsReadResult.Unavailable;
            var isCurrent = current.Contains(key);

            if (!isCurrent)
            {
                if (inventoryComplete) RetireSelected(objects, key, fields);
                else ClearSelected(objects, key, fields);
                continue;
            }

            if ((fields & WindowsProcessDetailsFields.ImageSignature) != 0)
            {
                var identity = WindowsProcessDetailsIdentity.Process(key);
                var device = GetOrAdd(objects, identity, "process");
                device.Fields[DeviceInformationFieldRegistry.ProcessAllImageSignatureReported.Name] =
                    result.SignatureSucceeded && result.SignatureStatus is { } signature
                        ? new(DeviceInformationFieldRegistry.ProcessAllImageSignatureReported,
                            JsonSerializer.SerializeToElement(signature.ToString(CultureInfo.InvariantCulture))) { ObservedAt = observedAt }
                        : new(DeviceInformationFieldRegistry.ProcessAllImageSignatureReported, null);
            }

            if ((fields & WindowsProcessDetailsFields.LoadedModules) != 0)
            {
                if (result.ModulesSucceeded)
                {
                    RetireChildren(objects, key, "module");
                    foreach (var module in result.Modules)
                    {
                        var identity = WindowsProcessDetailsIdentity.Module(key, module.BaseAddress);
                        GetOrAdd(objects, identity, "process_module").Fields[DeviceInformationFieldRegistry.ProcessAllLoadedModuleActual.Name]
                            = new(DeviceInformationFieldRegistry.ProcessAllLoadedModuleActual,
                                JsonSerializer.SerializeToElement(module.Path)) { ObservedAt = observedAt };
                    }
                }
                else ClearSelected(objects, key, WindowsProcessDetailsFields.LoadedModules);
            }

            if ((fields & WindowsProcessDetailsFields.ThreadState) != 0)
            {
                if (result.ThreadsSucceeded)
                {
                    RetireChildren(objects, key, "thread");
                    foreach (var thread in result.Threads)
                    {
                        var identity = WindowsProcessDetailsIdentity.Thread(key, thread.ThreadId);
                        GetOrAdd(objects, identity, "process_thread").Fields[DeviceInformationFieldRegistry.ProcessAllThreadStateActual.Name]
                            = new(DeviceInformationFieldRegistry.ProcessAllThreadStateActual,
                                JsonSerializer.SerializeToElement(new { state = thread.State, waitReason = thread.WaitReason }))
                            { ObservedAt = observedAt };
                    }
                }
                else ClearSelected(objects, key, WindowsProcessDetailsFields.ThreadState);
            }
        }

        var projected = objects.Where(static pair => pair.Value.Fields.Count > 0)
            .Select(static pair => new DeviceInformationObject(pair.Key, pair.Value.ObjectType, pair.Value.Fields.Values.ToArray()))
            .ToArray();
        var hasValue = projected.SelectMany(static device => device.Fields).Any(static field => field.Value is not null);
        return new(hasValue ? observedAt : null, projected);
    }

    private static void ClearSelected(Dictionary<string, MutableDevice> objects, ProcessInstanceKey key,
        WindowsProcessDetailsFields selected)
    {
        foreach (var pair in objects.ToArray())
        {
            if (!WindowsProcessDetailsIdentity.TryParse(pair.Key, out var owner, out var kind) || owner != key) continue;
            if ((selected & WindowsProcessDetailsFields.ImageSignature) != 0 && kind.Length == 0)
                Clear(pair.Value, DeviceInformationFieldRegistry.ProcessAllImageSignatureReported);
            if ((selected & WindowsProcessDetailsFields.LoadedModules) != 0 && kind == "module")
                Clear(pair.Value, DeviceInformationFieldRegistry.ProcessAllLoadedModuleActual);
            if ((selected & WindowsProcessDetailsFields.ThreadState) != 0 && kind == "thread")
                Clear(pair.Value, DeviceInformationFieldRegistry.ProcessAllThreadStateActual);
        }
    }

    private static void RetireSelected(Dictionary<string, MutableDevice> objects, ProcessInstanceKey key,
        WindowsProcessDetailsFields selected)
    {
        foreach (var pair in objects.ToArray())
        {
            if (!WindowsProcessDetailsIdentity.TryParse(pair.Key, out var owner, out var kind) || owner != key) continue;
            if ((selected & WindowsProcessDetailsFields.ImageSignature) != 0 && kind.Length == 0)
                pair.Value.Fields.Remove(DeviceInformationFieldRegistry.ProcessAllImageSignatureReported.Name);
            if ((selected & WindowsProcessDetailsFields.LoadedModules) != 0 && kind == "module")
                pair.Value.Fields.Remove(DeviceInformationFieldRegistry.ProcessAllLoadedModuleActual.Name);
            if ((selected & WindowsProcessDetailsFields.ThreadState) != 0 && kind == "thread")
                pair.Value.Fields.Remove(DeviceInformationFieldRegistry.ProcessAllThreadStateActual.Name);
        }
    }

    private static void RetireChildren(Dictionary<string, MutableDevice> objects, ProcessInstanceKey key, string kind)
    {
        var definition = kind == "module"
            ? DeviceInformationFieldRegistry.ProcessAllLoadedModuleActual
            : DeviceInformationFieldRegistry.ProcessAllThreadStateActual;
        foreach (var pair in objects.ToArray())
        {
            if (WindowsProcessDetailsIdentity.TryParse(pair.Key, out var owner, out var childKind)
                && owner == key && childKind == kind)
                pair.Value.Fields.Remove(definition.Name);
        }
    }

    private static void Clear(MutableDevice device, DeviceInformationDefinition definition)
    {
        if (device.Fields.ContainsKey(definition.Name))
            device.Fields[definition.Name] = new(definition, null);
    }

    private static MutableDevice GetOrAdd(Dictionary<string, MutableDevice> objects, string identity, string type)
    {
        if (!objects.TryGetValue(identity, out var device)) objects.Add(identity, device = new(type, []));
        return device;
    }

    private sealed class MutableDevice(string objectType, IEnumerable<DeviceInformationField> fields)
    {
        internal string ObjectType { get; } = objectType;
        internal Dictionary<string, DeviceInformationField> Fields { get; } = fields.ToDictionary(
            static field => field.Definition.Name, StringComparer.OrdinalIgnoreCase);
    }
}

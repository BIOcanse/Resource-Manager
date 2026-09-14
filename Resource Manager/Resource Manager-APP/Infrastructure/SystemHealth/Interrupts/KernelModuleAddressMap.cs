namespace ResourceManager.App.Infrastructure.SystemHealth.Interrupts;

internal sealed class KernelModuleAddressMap
{
    internal const string UnknownModuleName = "未知内核模块";

    private readonly object gate = new();
    private readonly Dictionary<ulong, KernelModuleRange> modules = [];

    public void Upsert(ulong imageBase, int imageSize, string? fileName)
    {
        if (imageBase == 0 || imageSize <= 0)
        {
            return;
        }

        var path = string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim();
        var name = ModuleName(path);
        lock (gate)
        {
            modules[imageBase] = new KernelModuleRange(imageBase, (ulong)imageSize, name, path);
        }
    }

    public void Remove(ulong imageBase)
    {
        lock (gate)
        {
            modules.Remove(imageBase);
        }
    }

    public KernelModuleIdentity Resolve(ulong routineAddress)
    {
        if (routineAddress == 0)
        {
            return KernelModuleIdentity.Unknown;
        }

        lock (gate)
        {
            KernelModuleRange? best = null;
            foreach (var module in modules.Values)
            {
                if (routineAddress < module.ImageBase
                    || routineAddress - module.ImageBase >= module.ImageSize
                    || (best is not null && module.ImageBase <= best.ImageBase))
                {
                    continue;
                }

                best = module;
            }

            return best is null
                ? KernelModuleIdentity.Unknown
                : new KernelModuleIdentity(best.Name, best.Path);
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            modules.Clear();
        }
    }

    private static string ModuleName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return UnknownModuleName;
        }

        try
        {
            return Path.GetFileName(path);
        }
        catch
        {
            return path;
        }
    }

    private sealed record KernelModuleRange(
        ulong ImageBase,
        ulong ImageSize,
        string Name,
        string? Path);
}

internal sealed record KernelModuleIdentity(string Name, string? Path)
{
    public static KernelModuleIdentity Unknown { get; } = new(KernelModuleAddressMap.UnknownModuleName, null);
}

namespace ResourceManager.ReadWrite.Vendors.Amd.Smu;

public sealed partial class AmdSmuPawnIoSession : IAmdSmuSession
{
    private const int PmTableQwordCount = 512;
    private readonly IAmdSmuPawnIoExecutor executor;
    private readonly TimeSpan pciLockTimeout;

    private AmdSmuPawnIoSession(
        IAmdSmuPawnIoExecutor executor,
        string modulePath,
        uint tableVersion,
        ulong tableBase,
        uint codeName,
        uint smuVersion,
        TimeSpan pciLockTimeout)
    {
        this.executor = executor;
        this.pciLockTimeout = pciLockTimeout;
        ModulePath = modulePath;
        TableVersion = tableVersion;
        TableBase = tableBase;
        CodeName = codeName;
        SmuVersion = smuVersion;
    }

    public string ExecutorName => executor.Name;

    public string ModulePath { get; }

    public uint TableVersion { get; }

    public ulong TableBase { get; }

    public uint CodeName { get; }

    public uint SmuVersion { get; }

    public static AmdSmuPawnIoSession Open(AmdSmuPawnIoOpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return OpenModule(options.ModulePath, options.LibraryPaths, options.UseDirectDevice,
            options.PciLockTimeout, (executor, path) => Initialize(executor, path, options.PciLockTimeout));
    }

    public float[] UpdateAndReadPmTable()
    {
        WithPciLock(pciLockTimeout, () => executor.Execute("ioctl_update_pm_table", [], 0));
        var raw = executor.Execute("ioctl_read_pm_table", [], PmTableQwordCount);
        var byteCount = raw.Length * sizeof(ulong);
        if (byteCount == 0)
        {
            return [];
        }

        var bytes = new byte[byteCount];
        Buffer.BlockCopy(raw, 0, bytes, 0, byteCount);
        var table = new float[byteCount / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, table, 0, table.Length * sizeof(float));
        return table;
    }

    public void Dispose()
    {
        executor.Dispose();
    }

    private static AmdSmuPawnIoSession Initialize(IAmdSmuPawnIoExecutor executor, string modulePath, TimeSpan pciLockTimeout)
    {
        var resolved = WithPciLock(pciLockTimeout, () => executor.Execute("ioctl_resolve_pm_table", [], 2));
        if (resolved.Length < 2 || resolved[0] == 0)
        {
            throw new AmdSmuProviderUnavailableException("RyzenSMU 模块未能解析 PM table。");
        }

        var codeName = ReadOptionalU32(executor, "ioctl_get_code_name");
        var smuVersion = WithPciLock(pciLockTimeout, () => ReadOptionalU32(executor, "ioctl_get_smu_version"));
        return new AmdSmuPawnIoSession(
            executor,
            modulePath,
            (uint)(resolved[0] & 0xFFFFFFFFUL),
            resolved[1],
            codeName,
            smuVersion,
            pciLockTimeout);
    }
}

using System.ComponentModel;

namespace ResourceManager.App.Infrastructure.Monitoring.AmdSmu;

internal sealed partial class AmdSmuPawnIoSession : IDisposable
{
    private const string ComponentId = "amd-smu-pawnio-provider";
    private const string ModuleFileName = "RyzenSMU.bin";
    private const int PmTableQwordCount = 512;
    private readonly IAmdSmuPawnIoExecutor executor;

    private AmdSmuPawnIoSession(
        IAmdSmuPawnIoExecutor executor,
        string modulePath,
        uint tableVersion,
        ulong tableBase,
        uint codeName,
        uint smuVersion)
    {
        this.executor = executor;
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

    public static AmdSmuPawnIoSession Open(string contentRootPath)
    {
        var modulePath = LocateModulePath(contentRootPath)
            ?? throw new AmdSmuProviderUnavailableException(
                $"缺少 {ModuleFileName}；请在组件安装页安装/验证 AMD SMU / PawnIO Provider。");
        var module = File.ReadAllBytes(modulePath);
        var errors = new List<string>();

        foreach (var libraryPath in CandidatePawnIoLibraryPaths())
        {
            try
            {
                var executor = PawnIoLibraryExecutor.Open(libraryPath, module);
                return Initialize(executor, modulePath);
            }
            catch (Exception ex) when (ex is AmdSmuProviderUnavailableException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
            {
                errors.Add($"{Path.GetFileName(libraryPath)}: {ex.Message}");
            }
        }

        try
        {
            var executor = DirectPawnIoDeviceExecutor.Open(module);
            return Initialize(executor, modulePath);
        }
        catch (Exception ex) when (ex is AmdSmuProviderUnavailableException or Win32Exception or IOException or UnauthorizedAccessException)
        {
            errors.Add($"PawnIO device: {ex.Message}");
        }

        var detail = errors.Count == 0 ? "未找到 PawnIOLib.dll，且 PawnIO 设备不可用。" : string.Join("；", errors);
        throw new AmdSmuProviderUnavailableException(detail);
    }

    public float[] UpdateAndReadPmTable()
    {
        WithPciLock(() => executor.Execute("ioctl_update_pm_table", [], 0));
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

    private static AmdSmuPawnIoSession Initialize(IAmdSmuPawnIoExecutor executor, string modulePath)
    {
        try
        {
            var resolved = WithPciLock(() => executor.Execute("ioctl_resolve_pm_table", [], 2));
            if (resolved.Length < 2 || resolved[0] == 0)
            {
                throw new AmdSmuProviderUnavailableException("RyzenSMU 模块未能解析 PM table。");
            }

            var codeName = ReadOptionalU32(executor, "ioctl_get_code_name");
            var smuVersion = WithPciLock(() => ReadOptionalU32(executor, "ioctl_get_smu_version"));
            return new AmdSmuPawnIoSession(
                executor,
                modulePath,
                (uint)(resolved[0] & 0xFFFFFFFFUL),
                resolved[1],
                codeName,
                smuVersion);
        }
        catch
        {
            executor.Dispose();
            throw;
        }
    }
}

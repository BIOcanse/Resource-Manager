namespace ResourceManager.App.Infrastructure.Operations;

internal static class HostManagerOperationRequestSchemas
{
    public const uint KindName = 0x0001;
    public const uint DomainKey = 0x0002;
    public const uint Subject = 0x0003;
    public const uint SubjectVersion = 2;
    // 0x01x1 / 0x02x1 是只带"是否确认条款"的旧载荷；0x01x2 / 0x02x2 在其后追加了版本选择。
    // 载荷格式按 schema id 区分，其余动作仍用布尔载荷，不受影响。
    public const uint ComponentDownload = 0x0111;
    public const uint ComponentInstall = 0x0112;
    public const uint UserComponentInstall = 0x0113;
    public const uint DependencyDownload = 0x0211;
    public const uint DependencyInstaller = 0x0212;
    public const uint UserDependencyInstaller = 0x0213;
    public const uint SoftwareUninstall = 0x0301;
    public const uint UserSoftwareUninstall = 0x0302;
    public const uint DiskUsageScan = 0x0601;
    public const uint ProgressStage = 0x1001;
    public const uint ProgressDetailFact = 0x1002;
    public const uint OperationCheckpoint = 0x1003;
    public const uint OperationResult = 0x2001;
    public const uint OperationError = 0x2002;
    public const uint EffectExpectedBefore = 0x3001;
    public const uint EffectObservation = 0x3002;
    public const uint Version = 1;
}

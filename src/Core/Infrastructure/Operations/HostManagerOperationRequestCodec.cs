using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Application.DiskUsage;
using System.Buffers.Binary;
using System.Text;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Operations;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Operations;

internal static class HostManagerOperationKinds
{
    public const string ComponentDownload = "component.download";
    public const string ComponentInstall = "component.install";
    public const string DependencyDownload = "dependency.download";
    public const string DependencyLaunchInstaller = "dependency.launch-installer";
    public const string SoftwareUninstall = "software.uninstall";
    public const string DiskUsageScan = "disk-usage.scan";

    internal static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [
            ComponentDownload,
            ComponentInstall,
            DependencyDownload,
            DependencyLaunchInstaller,
            SoftwareUninstall,
            DiskUsageScan
        ],
        StringComparer.Ordinal);
}

internal static class HostManagerOperationRequestCodec
{
    private const int MaximumStringByteCount = 32 * 1024;
    private const int MaximumListCount = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static HostManagerOperationSubmitCommand ComponentDownload(
        string id,
        ComponentActionRequest request)
        => CreateAcquisitionRequest(
            HostManagerOperationKinds.ComponentDownload,
            $"下载组件 {ValidateText(id, nameof(id))}",
            $"component:{id}",
            HostManagerOperationRequestSchemas.ComponentDownload,
            id,
            request.AcknowledgeExternalTerms,
            request.VersionChoice);

    internal static HostManagerOperationSubmitCommand ComponentInstall(
        string id,
        ComponentActionRequest request)
        => CreateAcquisitionRequest(
            HostManagerOperationKinds.ComponentInstall,
            $"安装组件 {ValidateText(id, nameof(id))}",
            $"component:{id}",
            HostManagerOperationRequestSchemas.ComponentInstall,
            id,
            request.AcknowledgeExternalTerms,
            request.VersionChoice);

    internal static HostManagerOperationSubmitCommand ComponentInstall(
        string id,
        ComponentActionRequest request,
        SoftwareOperationCaller caller)
        => CreateUserAcquisitionRequest(
            HostManagerOperationKinds.ComponentInstall,
            $"安装组件 {ValidateText(id, nameof(id))}",
            $"component:{id}",
            HostManagerOperationRequestSchemas.UserComponentInstall,
            id,
            request.AcknowledgeExternalTerms,
            request.VersionChoice,
            caller);

    internal static HostManagerOperationSubmitCommand DependencyDownload(
        string id,
        OptionalDependencyDownloadRequest request)
        => CreateAcquisitionRequest(
            HostManagerOperationKinds.DependencyDownload,
            $"下载依赖 {ValidateText(id, nameof(id))}",
            $"dependency:{id}",
            HostManagerOperationRequestSchemas.DependencyDownload,
            id,
            request.AcknowledgeExternalTerms,
            request.VersionChoice);

    internal static HostManagerOperationSubmitCommand DependencyInstaller(
        string id,
        OptionalDependencyInstallRequest request)
        => CreateAcquisitionRequest(
            HostManagerOperationKinds.DependencyLaunchInstaller,
            $"启动依赖安装器 {ValidateText(id, nameof(id))}",
            $"dependency:{id}",
            HostManagerOperationRequestSchemas.DependencyInstaller,
            id,
            request.AcknowledgeExternalTerms,
            request.VersionChoice);

    internal static HostManagerOperationSubmitCommand DependencyInstaller(
        string id,
        OptionalDependencyInstallRequest request,
        SoftwareOperationCaller caller)
        => CreateUserAcquisitionRequest(
            HostManagerOperationKinds.DependencyLaunchInstaller,
            $"启动依赖安装器 {ValidateText(id, nameof(id))}",
            $"dependency:{id}",
            HostManagerOperationRequestSchemas.UserDependencyInstaller,
            id,
            request.AcknowledgeExternalTerms,
            request.VersionChoice,
            caller);

    internal static HostManagerOperationSubmitCommand SoftwareUninstall(
        SoftwareOperationRequest request, SoftwareOperationCaller caller)
        => new(
            HostManagerOperationKinds.SoftwareUninstall,
            $"卸载软件 {ValidateText(request.Id, nameof(request.Id))}",
            $"software:{request.Id}",
            HostManagerOperationRequestSchemas.UserSoftwareUninstall,
            HostManagerOperationRequestSchemas.Version,
            Encode(writer =>
            {
                WriteString(writer, request.Id);
                writer.Write(request.ConfirmOperation);
                WriteString(writer, caller.WindowsSid);
                writer.Write(caller.WindowsSessionId);
            }));

    internal static (SoftwareOperationRequest Request, SoftwareOperationCaller Caller) DecodeUserSoftwareUninstall(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        var request = new SoftwareOperationRequest(ReadString(reader), reader.ReadBoolean());
        var caller = new SoftwareOperationCaller(ReadString(reader), reader.ReadUInt32());
        RequireEnd(stream);
        return (request, caller);
    }

    internal static HostManagerOperationSubmitCommand DiskUsageScan(
        DiskUsageScanRequest request)
        => CreateDiskUsageScan(request, null);

    internal static HostManagerOperationSubmitCommand DiskUsageScan(
        DiskUsageScanRequest request,
        DiskUsageResultOwner owner)
    {
        owner.Validate();
        return CreateDiskUsageScan(request, owner);
    }

    private static HostManagerOperationSubmitCommand CreateDiskUsageScan(
        DiskUsageScanRequest request,
        DiskUsageResultOwner? owner)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = Encode(writer =>
        {
            WriteString(writer, request.Scope);
            WriteString(writer, request.Mode);
            // 全局扫描没有目标路径，写个占位，读回来再还原成空串。
            WriteString(writer, string.IsNullOrEmpty(request.Target) ? "*" : request.Target);
            if (owner is { } caller)
            {
                WriteString(writer, caller.WindowsSid);
                writer.Write(checked((uint)caller.WindowsSessionId));
            }
        });
        // 同一时刻只允许一个磁盘扫描：域键固定，协调器自己会挡掉重复提交。
        return new HostManagerOperationSubmitCommand(
            HostManagerOperationKinds.DiskUsageScan,
            "扫描磁盘占用",
            "disk-usage:scan",
            HostManagerOperationRequestSchemas.DiskUsageScan,
            HostManagerOperationRequestSchemas.Version,
            payload);
    }

    internal static (DiskUsageScanRequest Request, DiskUsageResultOwner? Owner) DecodeDiskUsageScan(
        ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        var scope = ReadString(reader);
        var mode = ReadString(reader);
        var target = ReadString(reader);
        DiskUsageResultOwner? owner = null;
        if (stream.Position < stream.Length)
        {
            var caller = new DiskUsageResultOwner(ReadString(reader), checked((int)reader.ReadUInt32()));
            caller.Validate();
            owner = caller;
        }
        RequireEnd(stream);
        return (new DiskUsageScanRequest(scope, mode, target == "*" ? string.Empty : target), owner);
    }

    internal static (string Id, bool Confirm, string? VersionChoice) DecodeAcquisitionRequest(
        ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        var id = ReadString(reader);
        var confirm = reader.ReadBoolean();
        var versionChoice = reader.ReadBoolean() ? ReadString(reader) : null;
        RequireEnd(stream);
        return (id, confirm, versionChoice);
    }

    internal static (string Id, bool Confirm, string? VersionChoice, SoftwareOperationCaller Caller)
        DecodeUserAcquisitionRequest(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        var id = ReadString(reader);
        var confirm = reader.ReadBoolean();
        var versionChoice = reader.ReadBoolean() ? ReadString(reader) : null;
        var caller = new SoftwareOperationCaller(ReadString(reader), reader.ReadUInt32());
        RequireEnd(stream);
        if (caller.WindowsSessionId == 0)
            throw new InvalidDataException("The interactive installer request has no Windows session.");
        return (id, confirm, versionChoice, caller);
    }

    internal static (string Id, bool Confirm) DecodeBooleanRequest(
        ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        var id = ReadString(reader);
        var confirm = reader.ReadBoolean();
        RequireEnd(stream);
        return (id, confirm);
    }

    internal static byte[] EncodeText(string value)
        => Encode(writer => WriteString(writer, value));

    internal static byte[] EncodeEffectExpectedBefore(NativeOperationHandle128 requestHandle)
        => HostManagerOperationEffectEvidenceCodec.EncodeEffectExpectedBefore(requestHandle);

    internal static NativeOperationHandle128 DecodeEffectExpectedBefore(ReadOnlySpan<byte> payload)
        => HostManagerOperationEffectEvidenceCodec.DecodeEffectExpectedBefore(payload);

    internal static string DecodeText(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        var value = ReadString(reader);
        RequireEnd(stream);
        return value;
    }

    /// <summary>组件与依赖的获取动作：除了条款确认，还带用户选择的版本（null 表示已验证版本）。</summary>
    private static HostManagerOperationSubmitCommand CreateAcquisitionRequest(
        string kind,
        string title,
        string domainKey,
        uint schemaId,
        string id,
        bool value,
        string? versionChoice)
        => new(
            kind,
            title,
            domainKey,
            schemaId,
            HostManagerOperationRequestSchemas.Version,
            Encode(writer =>
            {
                WriteString(writer, id);
                writer.Write(value);
                // 版本是可选字段：请求里要么没有这一项（已验证版本），要么是一段非空文本。
                // 载荷里的字符串一律不允许为空，所以用一个存在位来表达「没有选择」。
                writer.Write(versionChoice is not null);
                if (versionChoice is not null)
                {
                    WriteString(writer, versionChoice);
                }
            }));

    private static HostManagerOperationSubmitCommand CreateUserAcquisitionRequest(
        string kind,
        string title,
        string domainKey,
        uint schemaId,
        string id,
        bool value,
        string? versionChoice,
        SoftwareOperationCaller caller)
    {
        ArgumentNullException.ThrowIfNull(caller);
        if (caller.WindowsSessionId == 0)
            throw new ArgumentOutOfRangeException(nameof(caller), "An interactive request must be bound to a user session.");
        _ = new System.Security.Principal.SecurityIdentifier(caller.WindowsSid);
        return new HostManagerOperationSubmitCommand(
            kind,
            title,
            domainKey,
            schemaId,
            HostManagerOperationRequestSchemas.Version,
            Encode(writer =>
            {
                WriteString(writer, id);
                writer.Write(value);
                writer.Write(versionChoice is not null);
                if (versionChoice is not null) WriteString(writer, versionChoice);
                WriteString(writer, caller.WindowsSid);
                writer.Write(caller.WindowsSessionId);
            }));
    }

    private static HostManagerOperationSubmitCommand CreateBooleanRequest(
        string kind,
        string title,
        string domainKey,
        uint schemaId,
        string id,
        bool value)
        => new(
            kind,
            title,
            domainKey,
            schemaId,
            HostManagerOperationRequestSchemas.Version,
            Encode(writer =>
            {
                WriteString(writer, id);
                writer.Write(value);
            }));

    private static byte[] Encode(Action<BinaryWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, StrictUtf8, leaveOpen: true))
        {
            write(writer);
        }
        return stream.ToArray();
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        value = ValidateText(value, nameof(value));
        var bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length > MaximumStringByteCount)
        {
            throw new ArgumentException(
                "An operation request string exceeds the explicit byte bound.");
        }
        writer.Write(checked((uint)bytes.Length));
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        var byteCount = reader.ReadUInt32();
        if (byteCount == 0 || byteCount > MaximumStringByteCount)
        {
            throw new InvalidDataException(
                "An operation request string length is invalid.");
        }
        var bytes = reader.ReadBytes(checked((int)byteCount));
        if (bytes.Length != byteCount)
        {
            throw new EndOfStreamException();
        }
        return ValidateText(StrictUtf8.GetString(bytes), "payload");
    }

    private static string ValidateText(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
        {
            throw new ArgumentException(
                "An operation request string is empty or contains NUL.",
                parameterName);
        }
        _ = StrictUtf8.GetByteCount(value);
        return value;
    }

    private static void RequireEnd(Stream stream)
    {
        if (stream.Position != stream.Length)
        {
            throw new InvalidDataException(
                "An operation request contains trailing bytes.");
        }
    }
}

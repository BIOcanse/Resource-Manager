using System.Buffers.Binary;
using System.Text;
using ResourceManager.App.Domain.Components;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Migration;
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
    public const string MigrationExecute = "migration.execute";
    public const string MigrationRestore = "migration.restore";
    public const string DiscoveryStart = "discovery.start";

    internal static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [
            ComponentDownload,
            ComponentInstall,
            DependencyDownload,
            DependencyLaunchInstaller,
            SoftwareUninstall,
            MigrationExecute,
            MigrationRestore,
            DiscoveryStart
        ],
        StringComparer.Ordinal);
}

internal static class HostManagerOperationRequestSchemas
{
    public const uint KindName = 0x0001;
    public const uint DomainKey = 0x0002;
    public const uint UserTitle = 0x0003;
    public const uint ComponentDownload = 0x0101;
    public const uint ComponentInstall = 0x0102;
    public const uint DependencyDownload = 0x0201;
    public const uint DependencyInstaller = 0x0202;
    public const uint SoftwareUninstall = 0x0301;
    public const uint MigrationExecute = 0x0401;
    public const uint MigrationRestore = 0x0402;
    public const uint DiscoveryStart = 0x0501;
    public const uint ProgressStage = 0x1001;
    public const uint ProgressMessage = 0x1002;
    public const uint OperationCheckpoint = 0x1003;
    public const uint OperationResult = 0x2001;
    public const uint OperationError = 0x2002;
    public const uint EffectExpectedBefore = 0x3001;
    public const uint EffectObservation = 0x3002;
    public const uint Version = 1;
}

internal static class HostManagerOperationRequestCodec
{
    private const int MaximumStringByteCount = 32 * 1024;
    private const int MaximumListCount = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static HostManagerOperationSubmitCommand ComponentDownload(
        string id,
        ComponentActionRequest request)
        => CreateBooleanRequest(
            HostManagerOperationKinds.ComponentDownload,
            $"下载组件 {ValidateText(id, nameof(id))}",
            $"component:{id}",
            HostManagerOperationRequestSchemas.ComponentDownload,
            id,
            request.AcknowledgeExternalTerms);

    internal static HostManagerOperationSubmitCommand ComponentInstall(
        string id,
        ComponentActionRequest request)
        => CreateBooleanRequest(
            HostManagerOperationKinds.ComponentInstall,
            $"安装组件 {ValidateText(id, nameof(id))}",
            $"component:{id}",
            HostManagerOperationRequestSchemas.ComponentInstall,
            id,
            request.AcknowledgeExternalTerms);

    internal static HostManagerOperationSubmitCommand DependencyDownload(
        string id,
        OptionalDependencyDownloadRequest request)
        => CreateBooleanRequest(
            HostManagerOperationKinds.DependencyDownload,
            $"下载依赖 {ValidateText(id, nameof(id))}",
            $"dependency:{id}",
            HostManagerOperationRequestSchemas.DependencyDownload,
            id,
            request.AcknowledgeExternalTerms);

    internal static HostManagerOperationSubmitCommand DependencyInstaller(
        string id,
        OptionalDependencyInstallRequest request)
        => CreateBooleanRequest(
            HostManagerOperationKinds.DependencyLaunchInstaller,
            $"启动依赖安装器 {ValidateText(id, nameof(id))}",
            $"dependency:{id}",
            HostManagerOperationRequestSchemas.DependencyInstaller,
            id,
            request.AcknowledgeExternalTerms);

    internal static HostManagerOperationSubmitCommand SoftwareUninstall(
        SoftwareOperationRequest request)
        => CreateBooleanRequest(
            HostManagerOperationKinds.SoftwareUninstall,
            $"卸载软件 {ValidateText(request.Id, nameof(request.Id))}",
            $"software:{request.Id}",
            HostManagerOperationRequestSchemas.SoftwareUninstall,
            request.Id,
            request.ConfirmOperation);

    internal static HostManagerOperationSubmitCommand MigrationExecute(
        SoftwareDataMigrationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = Encode(writer =>
        {
            WriteString(writer, request.SoftwareName);
            WriteStringList(writer, request.SourcePaths);
            WriteString(writer, request.TargetCategory);
            WriteString(writer, request.MigrationKind);
            writer.Write(request.ConfirmExecution);
            writer.Write(request.AllowMediumRisk);
        });
        return new HostManagerOperationSubmitCommand(
            HostManagerOperationKinds.MigrationExecute,
            $"迁移 {ValidateText(request.SoftwareName, nameof(request.SoftwareName))}",
            $"migration:{request.SoftwareName}",
            HostManagerOperationRequestSchemas.MigrationExecute,
            HostManagerOperationRequestSchemas.Version,
            payload);
    }

    internal static HostManagerOperationSubmitCommand MigrationRestore(
        SoftwareDataRestoreRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CreateBooleanRequest(
            HostManagerOperationKinds.MigrationRestore,
            $"还原迁移 {ValidateText(request.Id, nameof(request.Id))}",
            $"migration-restore:{request.Id}",
            HostManagerOperationRequestSchemas.MigrationRestore,
            request.Id,
            request.ConfirmExecution);
    }

    internal static HostManagerOperationSubmitCommand DiscoveryStart(
        SoftwareDataDiscoveryStartRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var payload = Encode(writer =>
        {
            WriteString(writer, request.SoftwareName);
            WriteStringList(writer, request.ProcessNames);
            WriteStringList(writer, request.ProgramRootPaths);
        });
        return new HostManagerOperationSubmitCommand(
            HostManagerOperationKinds.DiscoveryStart,
            $"发现数据 {ValidateText(request.SoftwareName, nameof(request.SoftwareName))}",
            $"discovery:{request.SoftwareName}",
            HostManagerOperationRequestSchemas.DiscoveryStart,
            HostManagerOperationRequestSchemas.Version,
            payload);
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

    internal static byte[] EncodeEffectExpectedBefore(
        NativeOperationHandle128 requestHandle)
    {
        if (requestHandle.IsZero)
        {
            throw new ArgumentException(
                "The operation request evidence handle is zero.",
                nameof(requestHandle));
        }
        var payload = new byte[16];
        HostManagerOperationPayloadCatalog.WriteHandle(payload, requestHandle);
        return payload;
    }

    internal static NativeOperationHandle128 DecodeEffectExpectedBefore(
        ReadOnlySpan<byte> payload)
    {
        if (payload.Length != 16)
        {
            throw new InvalidDataException(
                "The operation request evidence payload length is invalid.");
        }
        var result = new NativeOperationHandle128(
            BinaryPrimitives.ReadUInt64LittleEndian(payload),
            BinaryPrimitives.ReadUInt64LittleEndian(payload[8..]));
        return result.IsZero
            ? throw new InvalidDataException(
                "The operation request evidence handle is zero.")
            : result;
    }

    internal static string DecodeText(ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        var value = ReadString(reader);
        RequireEnd(stream);
        return value;
    }

    internal static SoftwareDataMigrationRequest DecodeMigrationExecute(
        ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        var result = new SoftwareDataMigrationRequest(
            ReadString(reader),
            ReadStringList(reader),
            ReadString(reader),
            ReadString(reader),
            reader.ReadBoolean(),
            reader.ReadBoolean());
        RequireEnd(stream);
        return result;
    }

    internal static SoftwareDataDiscoveryStartRequest DecodeDiscoveryStart(
        ReadOnlySpan<byte> payload)
    {
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, StrictUtf8, leaveOpen: true);
        var result = new SoftwareDataDiscoveryStartRequest(
            ReadString(reader),
            ReadStringList(reader),
            ReadStringList(reader));
        RequireEnd(stream);
        return result;
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

    private static void WriteStringList(
        BinaryWriter writer,
        IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count > MaximumListCount)
        {
            throw new ArgumentException(
                "An operation request list exceeds its explicit count bound.");
        }
        writer.Write(checked((uint)values.Count));
        foreach (var value in values)
        {
            WriteString(writer, value);
        }
    }

    private static string[] ReadStringList(BinaryReader reader)
    {
        var count = reader.ReadUInt32();
        if (count > MaximumListCount)
        {
            throw new InvalidDataException(
                "An operation request list count is invalid.");
        }
        var values = new string[checked((int)count)];
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = ReadString(reader);
        }
        return values;
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

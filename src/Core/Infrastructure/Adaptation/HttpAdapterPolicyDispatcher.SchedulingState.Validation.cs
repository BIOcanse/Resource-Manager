using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;

namespace ResourceManager.App.Infrastructure.Adaptation;

public sealed partial class HttpAdapterPolicyDispatcher
{
    private static AdapterSchedulingStateExportResult ValidateExportResponse(
        AdapterSchedulingStateExportWireResponse? wire,
        Guid requestId,
        AdapterSoftwareRegistration registration,
        int maximumPayloadBytes)
    {
        if (wire is null
            || !string.Equals(wire.Operation, ExportOperation, StringComparison.Ordinal)
            || wire.RequestId != requestId
            || !Enum.IsDefined(wire.Status))
        {
            return ExportFailure(
                AdapterSchedulingStateExportStatus.Conflict,
                "适配器状态导出响应没有回显精确请求身份或返回了未知状态。");
        }

        if (wire.Status != AdapterSchedulingStateExportStatus.Exported)
        {
            if (wire.Identity is not null
                || wire.Payload is not null
                || wire.PayloadVersion != 0
                || wire.PayloadLength != 0
                || !string.IsNullOrEmpty(wire.PayloadDigestSha256)
                || wire.CurrentCpuGrade.HasValue
                || wire.CurrentGpuGrade.HasValue)
            {
                return ExportFailure(
                    AdapterSchedulingStateExportStatus.Conflict,
                    "适配器状态导出失败响应携带了互相冲突的状态载荷。");
            }

            return ExportFailure(wire.Status, CleanMessage(wire.Message, "适配器拒绝导出当前调度状态。"));
        }

        if (!TryProjectIdentity(wire.Identity, registration, out var identity)
            || wire.Payload is null
            || wire.PayloadLength != wire.Payload.Length
            || wire.PayloadLength <= 0
            || wire.PayloadLength > maximumPayloadBytes
            || wire.PayloadVersion == 0
            || !IsCanonicalSha256(wire.PayloadDigestSha256)
            || !DigestMatches(wire.Payload, wire.PayloadDigestSha256!)
            || !GradesMatchCapabilities(registration, wire.CurrentCpuGrade, wire.CurrentGpuGrade)
            || wire.ObservedAt == default)
        {
            return ExportFailure(
                AdapterSchedulingStateExportStatus.Conflict,
                "适配器状态导出响应的身份、载荷描述、摘要、档位或时间无效。");
        }

        return new AdapterSchedulingStateExportResult(
            AdapterSchedulingStateExportStatus.Exported,
            identity,
            new AdapterSchedulingStatePayload(
                wire.PayloadVersion,
                wire.PayloadDigestSha256!,
                wire.Payload.ToArray()),
            wire.CurrentCpuGrade,
            wire.CurrentGpuGrade,
            wire.ObservedAt,
            CleanMessage(wire.Message, "适配器已导出当前调度状态。"));
    }

    private static AdapterSchedulingStateRestoreResult ValidateRestoreResponse(
        AdapterSchedulingStateRestoreWireResponse? wire,
        Guid requestId,
        AdapterSoftwareRegistration registration,
        AdapterSchedulingStateRestoreCommand command)
    {
        if (wire is null
            || !string.Equals(wire.Operation, RestoreOperation, StringComparison.Ordinal)
            || wire.RequestId != requestId
            || !Enum.IsDefined(wire.Status)
            || !Enum.IsDefined(wire.OwnershipResult)
            || !TryProjectIdentity(wire.Identity, registration, out var identity)
            || identity != command.Identity
            || wire.PayloadVersion != command.Payload.Version
            || wire.PayloadLength != command.Payload.Bytes.Length
            || !string.Equals(wire.PayloadDigestSha256, command.Payload.Sha256Digest, StringComparison.Ordinal))
        {
            return RestoreFailure(
                AdapterSchedulingStateRestoreStatus.Conflict,
                AdapterSchedulingStateOwnershipResult.OwnershipLost,
                "适配器状态恢复响应没有回显精确请求、所有权身份或载荷描述。");
        }

        var statusShapeValid = wire.Status switch
        {
            AdapterSchedulingStateRestoreStatus.Restored =>
                wire.OwnershipResult is AdapterSchedulingStateOwnershipResult.Restored
                    or AdapterSchedulingStateOwnershipResult.AlreadyRestored
                && wire.ObservedAt != default
                && wire.ObservedCpuGrade == command.ExpectedCpuGrade
                && wire.ObservedGpuGrade == command.ExpectedGpuGrade,
            AdapterSchedulingStateRestoreStatus.Conflict =>
                wire.OwnershipResult is AdapterSchedulingStateOwnershipResult.NotEvaluated
                    or AdapterSchedulingStateOwnershipResult.OwnershipLost,
            AdapterSchedulingStateRestoreStatus.Unsupported or AdapterSchedulingStateRestoreStatus.Unavailable =>
                wire.OwnershipResult == AdapterSchedulingStateOwnershipResult.NotEvaluated,
            _ => false
        };
        if (!statusShapeValid)
        {
            return RestoreFailure(
                AdapterSchedulingStateRestoreStatus.Conflict,
                AdapterSchedulingStateOwnershipResult.OwnershipLost,
                "适配器状态恢复响应的状态、所有权结果或实际档位互相冲突。");
        }

        return new AdapterSchedulingStateRestoreResult(
            wire.Status,
            wire.OwnershipResult,
            identity,
            wire.ObservedCpuGrade,
            wire.ObservedGpuGrade,
            wire.ObservedAt,
            CleanMessage(wire.Message, "适配器已返回调度状态恢复结果。"));
    }

    private static (AdapterSchedulingStateExportStatus Status, string Message)? ValidateSchedulingStateRegistration(
        AdapterSoftwareRegistration? registration)
    {
        if (registration is null)
        {
            return (AdapterSchedulingStateExportStatus.Unavailable, "适配软件未找到持久注册记录。");
        }

        if (!UsesLoopbackHttp(registration))
        {
            return (AdapterSchedulingStateExportStatus.Unsupported, "当前仅支持 loopback-http 调度状态通道。");
        }

        if (registration.SchedulingCapabilities?.HasAnyDimension != true)
        {
            return (AdapterSchedulingStateExportStatus.Unsupported, "适配软件未声明 SDK 软件级调度器能力。");
        }

        return null;
    }

    private static bool GradesMatchCapabilities(
        AdapterSoftwareRegistration registration,
        AdapterCpuSchedulingGrade? cpuGrade,
        AdapterGpuSchedulingGrade? gpuGrade)
    {
        var cpuCapabilities = registration.SchedulingCapabilities?.Cpu;
        var gpuCapabilities = registration.SchedulingCapabilities?.Gpu;
        var cpuMatches = cpuCapabilities is null
            ? !cpuGrade.HasValue
            : cpuGrade.HasValue && cpuCapabilities.SupportedGrades.Contains(cpuGrade.Value);
        var gpuMatches = gpuCapabilities is null
            ? !gpuGrade.HasValue
            : gpuGrade.HasValue && gpuCapabilities.SupportedGrades.Contains(gpuGrade.Value);
        return cpuMatches && gpuMatches;
    }

    private static bool IdentityMatchesRegistration(
        AdapterSchedulingStateIdentity identity,
        AdapterSoftwareRegistration registration)
    {
        return string.Equals(identity.SoftwareId, registration.Id, StringComparison.Ordinal)
            && string.Equals(identity.AdapterId, registration.AdapterId, StringComparison.Ordinal)
            && string.Equals(identity.ApplicationId, registration.AppId, StringComparison.Ordinal);
    }

    private static bool IsValidIdentity(AdapterSchedulingStateIdentity identity)
    {
        return identity.AdapterInstanceId.IsValid
            && identity.LeaseId.IsValid
            && identity.LeaseGeneration != 0;
    }

    private static bool TryProjectIdentity(
        AdapterSchedulingStateIdentityWire? wire,
        AdapterSoftwareRegistration registration,
        out AdapterSchedulingStateIdentity identity)
    {
        identity = null!;
        if (wire is null
            || !TryParseHex(wire.AdapterInstanceIdHigh, out var instanceHigh)
            || !TryParseHex(wire.AdapterInstanceIdLow, out var instanceLow)
            || !TryParseHex(wire.LeaseIdHigh, out var leaseHigh)
            || !TryParseHex(wire.LeaseIdLow, out var leaseLow)
            || !TryParseHex(wire.LeaseGeneration, out var leaseGeneration))
        {
            return false;
        }

        var candidate = new AdapterSchedulingStateIdentity(
            new AdapterInstanceId(instanceHigh, instanceLow),
            new AdapterInstanceLeaseId(leaseHigh, leaseLow),
            leaseGeneration,
            wire.SoftwareId,
            wire.AdapterId,
            wire.ApplicationId);
        if (!IsValidIdentity(candidate) || !IdentityMatchesRegistration(candidate, registration))
        {
            return false;
        }

        identity = candidate;
        return true;
    }

    private static AdapterSchedulingStateIdentityWire ToWireIdentity(AdapterSchedulingStateIdentity identity)
    {
        return new AdapterSchedulingStateIdentityWire(
            FormatHex(identity.AdapterInstanceId.High),
            FormatHex(identity.AdapterInstanceId.Low),
            FormatHex(identity.LeaseId.High),
            FormatHex(identity.LeaseId.Low),
            FormatHex(identity.LeaseGeneration),
            identity.SoftwareId,
            identity.AdapterId,
            identity.ApplicationId);
    }

    private static bool TryValidatePayload(
        AdapterSchedulingStatePayload? payload,
        int maximumPayloadBytes,
        out string message)
    {
        if (payload is null
            || payload.Version == 0
            || payload.Bytes is null
            || payload.Bytes.Length == 0
            || payload.Bytes.Length > maximumPayloadBytes
            || !IsCanonicalSha256(payload.Sha256Digest)
            || !DigestMatches(payload.Bytes, payload.Sha256Digest))
        {
            message = "恢复请求的载荷版本、显式长度上限或 SHA-256 摘要无效。";
            return false;
        }

        message = string.Empty;
        return true;
    }

    private static bool IsCanonicalSha256(string? value)
    {
        if (value is null || value.Length != Sha256HexLength)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool DigestMatches(byte[] payload, string expectedDigest)
    {
        var expected = Convert.FromHexString(expectedDigest);

        Span<byte> actual = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(payload, actual);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string FormatHex(ulong value)
    {
        return value.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static bool TryParseHex(string? value, out ulong result)
    {
        result = 0;
        return value is { Length: 16 }
            && value.All(static character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f')
            && ulong.TryParse(value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result);
    }

    private static AdapterSchedulingStateExportStatus ExportStatusFromHttp(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented =>
                AdapterSchedulingStateExportStatus.Unsupported,
            HttpStatusCode.Conflict => AdapterSchedulingStateExportStatus.Conflict,
            _ => AdapterSchedulingStateExportStatus.Unavailable
        };
    }

    private static AdapterSchedulingStateRestoreStatus RestoreStatusFromHttp(HttpStatusCode statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented =>
                AdapterSchedulingStateRestoreStatus.Unsupported,
            HttpStatusCode.Conflict => AdapterSchedulingStateRestoreStatus.Conflict,
            _ => AdapterSchedulingStateRestoreStatus.Unavailable
        };
    }

    private static AdapterSchedulingStateOwnershipResult OwnershipFromHttp(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.Conflict
            ? AdapterSchedulingStateOwnershipResult.OwnershipLost
            : AdapterSchedulingStateOwnershipResult.NotEvaluated;
    }

    private static AdapterSchedulingStateRestoreStatus RestoreStatusFromExportStatus(
        AdapterSchedulingStateExportStatus status)
    {
        return status switch
        {
            AdapterSchedulingStateExportStatus.Unsupported => AdapterSchedulingStateRestoreStatus.Unsupported,
            AdapterSchedulingStateExportStatus.Conflict => AdapterSchedulingStateRestoreStatus.Conflict,
            _ => AdapterSchedulingStateRestoreStatus.Unavailable
        };
    }

    private static AdapterSchedulingStateExportResult ExportFailure(
        AdapterSchedulingStateExportStatus status,
        string message)
    {
        return new AdapterSchedulingStateExportResult(
            status,
            null,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            message);
    }

    private static AdapterSchedulingStateRestoreResult RestoreFailure(
        AdapterSchedulingStateRestoreStatus status,
        AdapterSchedulingStateOwnershipResult ownershipResult,
        string message)
    {
        return new AdapterSchedulingStateRestoreResult(
            status,
            ownershipResult,
            null,
            null,
            null,
            DateTimeOffset.UtcNow,
            message);
    }

    private static string CleanMessage(string? message, string fallback)
    {
        return string.IsNullOrWhiteSpace(message) ? fallback : message.Trim();
    }

}

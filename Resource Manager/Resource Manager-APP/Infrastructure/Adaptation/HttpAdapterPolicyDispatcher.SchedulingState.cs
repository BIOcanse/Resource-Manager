using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.Adaptation.Scheduling;
using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Infrastructure.Adaptation;

public sealed partial class HttpAdapterPolicyDispatcher
{
    private const string ExportOperation = "scheduling-state-export-v1";
    private const string RestoreOperation = "scheduling-state-restore-v1";
    private const int Sha256HexLength = 64;

    public async Task<AdapterSchedulingStateExportResult> ExportSoftwareSchedulingStateAsync(
        string softwareId,
        int maximumPayloadBytes,
        DateTimeOffset deadline,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(softwareId) || maximumPayloadBytes <= 0)
        {
            return ExportFailure(
                AdapterSchedulingStateExportStatus.Conflict,
                "导出请求缺少有效软件身份或显式载荷上限。");
        }

        if (deadline <= DateTimeOffset.UtcNow)
        {
            return ExportFailure(
                AdapterSchedulingStateExportStatus.Unavailable,
                "导出请求已超过显式截止时间。");
        }

        if (softwareId.Equals(RuntimeAttributionIds.ResourceManagerSelf, StringComparison.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return selfSchedulingControl.ExportCoordinatorSchedulingState(maximumPayloadBytes, deadline);
        }

        var registration = await FindRegistrationAsync(softwareId, cancellationToken);
        var registrationFailure = ValidateSchedulingStateRegistration(registration);
        if (registrationFailure is not null)
        {
            return ExportFailure(registrationFailure.Value.Status, registrationFailure.Value.Message);
        }

        var requestId = Guid.NewGuid();
        var request = new AdapterSchedulingStateExportWireRequest(
            ExportOperation,
            requestId,
            registration!.Id,
            registration.AdapterId,
            registration.AppId,
            maximumPayloadBytes,
            deadline);

        await using var deadlineCancellation = new AdapterSchedulingDeadlineCancellation(
            deadline,
            cancellationToken);
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                registration.ResourceMarkerEndpoint.Address,
                request,
                JsonOptions,
                deadlineCancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                return ExportFailure(
                    ExportStatusFromHttp(response.StatusCode),
                    $"适配器状态导出端点返回 HTTP {(int)response.StatusCode}。");
            }

            if (DateTimeOffset.UtcNow > deadline)
            {
                return ExportFailure(
                    AdapterSchedulingStateExportStatus.Unavailable,
                    "适配器状态导出响应超过显式截止时间。");
            }

            var wire = await response.Content.ReadFromJsonAsync<AdapterSchedulingStateExportWireResponse>(
                JsonOptions,
                deadlineCancellation.Token);
            return ValidateExportResponse(wire, requestId, registration, maximumPayloadBytes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (
            deadlineCancellation.DeadlineExpired
            && !cancellationToken.IsCancellationRequested)
        {
            return ExportFailure(
                AdapterSchedulingStateExportStatus.Unavailable,
                "适配器状态导出未在显式截止时间内完成。");
        }
        catch (OperationCanceledException ex)
        {
            return ExportFailure(
                AdapterSchedulingStateExportStatus.Unavailable,
                $"适配器状态导出传输被提前取消：{ex.Message}");
        }
        catch (JsonException ex)
        {
            return ExportFailure(
                AdapterSchedulingStateExportStatus.Conflict,
                $"适配器状态导出响应不符合严格 wire 合同：{ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            return ExportFailure(
                AdapterSchedulingStateExportStatus.Unavailable,
                $"适配器状态导出不可用：{ex.Message}");
        }
    }

    public async Task<AdapterSchedulingStateRestoreResult> RestoreSoftwareSchedulingStateAsync(
        string softwareId,
        AdapterSchedulingStateRestoreCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(softwareId) || command.MaximumPayloadBytes <= 0)
        {
            return RestoreFailure(
                AdapterSchedulingStateRestoreStatus.Conflict,
                AdapterSchedulingStateOwnershipResult.NotEvaluated,
                "恢复请求缺少有效软件身份或显式载荷上限。");
        }

        if (command.Deadline <= DateTimeOffset.UtcNow)
        {
            return RestoreFailure(
                AdapterSchedulingStateRestoreStatus.Unavailable,
                AdapterSchedulingStateOwnershipResult.NotEvaluated,
                "恢复请求已超过显式截止时间。");
        }

        if (softwareId.Equals(RuntimeAttributionIds.ResourceManagerSelf, StringComparison.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return selfSchedulingControl.RestoreCoordinatorSchedulingState(command);
        }

        if (command.Payload?.Bytes is not { } sourcePayload)
        {
            return RestoreFailure(
                AdapterSchedulingStateRestoreStatus.Conflict,
                AdapterSchedulingStateOwnershipResult.NotEvaluated,
                "恢复请求缺少不透明状态载荷。");
        }

        command = command with
        {
            Payload = command.Payload with { Bytes = sourcePayload.ToArray() }
        };

        var registration = await FindRegistrationAsync(softwareId, cancellationToken);
        var registrationFailure = ValidateSchedulingStateRegistration(registration);
        if (registrationFailure is not null)
        {
            return RestoreFailure(
                RestoreStatusFromExportStatus(registrationFailure.Value.Status),
                AdapterSchedulingStateOwnershipResult.NotEvaluated,
                registrationFailure.Value.Message);
        }

        if (!IdentityMatchesRegistration(command.Identity, registration!)
            || !IsValidIdentity(command.Identity))
        {
            return RestoreFailure(
                AdapterSchedulingStateRestoreStatus.Conflict,
                AdapterSchedulingStateOwnershipResult.OwnershipLost,
                "恢复请求的适配器实例、租约或软件身份与当前注册不一致。");
        }

        if (!TryValidatePayload(command.Payload, command.MaximumPayloadBytes, out var payloadMessage))
        {
            return RestoreFailure(
                AdapterSchedulingStateRestoreStatus.Conflict,
                AdapterSchedulingStateOwnershipResult.NotEvaluated,
                payloadMessage);
        }

        if (!GradesMatchCapabilities(
                registration!,
                command.ExpectedCpuGrade,
                command.ExpectedGpuGrade))
        {
            return RestoreFailure(
                AdapterSchedulingStateRestoreStatus.Conflict,
                AdapterSchedulingStateOwnershipResult.NotEvaluated,
                "恢复请求的 CPU/GPU 档位与适配器声明的调度维度不一致。");
        }

        var requestId = Guid.NewGuid();
        var request = new AdapterSchedulingStateRestoreWireRequest(
            RestoreOperation,
            requestId,
            ToWireIdentity(command.Identity),
            command.Payload.Version,
            command.Payload.Bytes.Length,
            command.Payload.Sha256Digest,
            command.Payload.Bytes,
            command.ExpectedCpuGrade,
            command.ExpectedGpuGrade,
            command.MaximumPayloadBytes,
            command.Deadline);

        await using var deadlineCancellation = new AdapterSchedulingDeadlineCancellation(
            command.Deadline,
            cancellationToken);
        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                registration!.ResourceMarkerEndpoint.Address,
                request,
                JsonOptions,
                deadlineCancellation.Token);
            if (!response.IsSuccessStatusCode)
            {
                return RestoreFailure(
                    RestoreStatusFromHttp(response.StatusCode),
                    OwnershipFromHttp(response.StatusCode),
                    $"适配器状态恢复端点返回 HTTP {(int)response.StatusCode}。");
            }

            if (DateTimeOffset.UtcNow > command.Deadline)
            {
                return RestoreFailure(
                    AdapterSchedulingStateRestoreStatus.Unavailable,
                    AdapterSchedulingStateOwnershipResult.NotEvaluated,
                    "适配器状态恢复响应超过显式截止时间。");
            }

            var wire = await response.Content.ReadFromJsonAsync<AdapterSchedulingStateRestoreWireResponse>(
                JsonOptions,
                deadlineCancellation.Token);
            return ValidateRestoreResponse(wire, requestId, registration, command);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (
            deadlineCancellation.DeadlineExpired
            && !cancellationToken.IsCancellationRequested)
        {
            return RestoreFailure(
                AdapterSchedulingStateRestoreStatus.Unavailable,
                AdapterSchedulingStateOwnershipResult.NotEvaluated,
                "适配器状态恢复未在显式截止时间内完成。");
        }
        catch (OperationCanceledException ex)
        {
            return RestoreFailure(
                AdapterSchedulingStateRestoreStatus.Unavailable,
                AdapterSchedulingStateOwnershipResult.NotEvaluated,
                $"适配器状态恢复传输被提前取消：{ex.Message}");
        }
        catch (JsonException ex)
        {
            return RestoreFailure(
                AdapterSchedulingStateRestoreStatus.Conflict,
                AdapterSchedulingStateOwnershipResult.OwnershipLost,
                $"适配器状态恢复响应不符合严格 wire 合同：{ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            return RestoreFailure(
                AdapterSchedulingStateRestoreStatus.Unavailable,
                AdapterSchedulingStateOwnershipResult.NotEvaluated,
                $"适配器状态恢复不可用：{ex.Message}");
        }
    }

}

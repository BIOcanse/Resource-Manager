using System.Net.Http.Json;
using System.Text.Json;
using ResourceManager.App.Application.Adaptation;
using ResourceManager.App.Domain.Adaptation;
using ResourceManager.App.Domain.ProcessAttribution;

namespace ResourceManager.App.Infrastructure.Adaptation;

public sealed partial class HttpAdapterPolicyDispatcher(
    HttpClient httpClient,
    IAdapterSoftwareRegistry adapterRegistry,
    IResourceManagerSelfSchedulingControl selfSchedulingControl) : IAdapterPolicyDispatcher
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<AdapterSoftwareSchedulingResult> ApplySoftwareSchedulingAsync(
        string softwareId,
        AdapterSoftwareSchedulingEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (softwareId.Equals(RuntimeAttributionIds.ResourceManagerSelf, StringComparison.OrdinalIgnoreCase))
        {
            return selfSchedulingControl.ApplyScheduling(envelope);
        }

        var registration = await FindRegistrationAsync(softwareId, cancellationToken);
        if (registration is null)
        {
            return RejectedScheduling(envelope, "适配软件未找到持久注册记录。");
        }

        if (!UsesLoopbackHttp(registration))
        {
            return RejectedScheduling(envelope, "当前仅支持 loopback-http 适配策略通道。");
        }

        if (registration.SchedulingCapabilities?.HasAnyDimension != true)
        {
            return RejectedScheduling(envelope, "适配软件未声明 SDK 软件级调度器能力。");
        }

        var supportedCpuGrades = registration.SchedulingCapabilities.Cpu?.SupportedGrades ?? [];
        var supportedGpuGrades = registration.SchedulingCapabilities.Gpu?.SupportedGrades ?? [];
        if (envelope.CpuGrade.HasValue && !supportedCpuGrades.Contains(envelope.CpuGrade.Value))
        {
            return RejectedScheduling(envelope, "适配软件未声明请求的 CPU 调度档位。");
        }

        if (envelope.GpuGrade.HasValue && !supportedGpuGrades.Contains(envelope.GpuGrade.Value))
        {
            return RejectedScheduling(envelope, "适配软件未声明请求的 GPU 调度档位。");
        }

        try
        {
            using var response = await httpClient.PostAsJsonAsync(
                registration.ResourceMarkerEndpoint.Address,
                envelope,
                JsonOptions,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return RejectedScheduling(envelope, $"适配软件调度端点返回 HTTP {(int)response.StatusCode}。");
            }

            var result = await response.Content.ReadFromJsonAsync<AdapterSoftwareSchedulingResult>(JsonOptions, cancellationToken);
            return result ?? RejectedScheduling(envelope, "适配软件调度端点返回空结果。");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException or JsonException)
        {
            return RejectedScheduling(envelope, $"适配软件调度下发失败：{ex.Message}");
        }
    }

    private async Task<AdapterSoftwareRegistration?> FindRegistrationAsync(
        string softwareId,
        CancellationToken cancellationToken)
    {
        return (await adapterRegistry.GetAllAsync(cancellationToken))
            .FirstOrDefault(item => item.Id.Equals(softwareId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool UsesLoopbackHttp(AdapterSoftwareRegistration registration)
    {
        return string.Equals(
            registration.ResourceMarkerEndpoint.Transport,
            AdapterResourceMarkerTransports.LoopbackHttp,
            StringComparison.OrdinalIgnoreCase);
    }

    private static AdapterSoftwareSchedulingResult RejectedScheduling(
        AdapterSoftwareSchedulingEnvelope envelope,
        string message)
    {
        return new AdapterSoftwareSchedulingResult(
            envelope.PolicyId,
            false,
            null,
            null,
            false,
            false,
            message,
            DateTimeOffset.Now);
    }
}

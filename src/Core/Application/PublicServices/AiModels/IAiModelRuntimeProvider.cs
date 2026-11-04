using ResourceManager.App.Domain.PublicServices.AiModels;

namespace ResourceManager.App.Application.PublicServices.AiModels;

public interface IAiModelRuntimeProvider
{
    Task<AiModelRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AiModelDescriptor>> ListModelsIfRunningAsync(
        CancellationToken cancellationToken);

    Task<AiModelLoadResult> LoadAsync(AiModelLoadRequest request, CancellationToken cancellationToken);

    Task<AiModelUnloadResult> UnloadAsync(AiModelUnloadRequest request, CancellationToken cancellationToken);

    Task ForwardAsync(HttpContext context, string relativePath, CancellationToken cancellationToken);
}

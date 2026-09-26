using ResourceManager.App.Domain.PublicServices.AiModels;

namespace ResourceManager.App.Application.PublicServices.AiModels;

public interface ILocalAiModelService
{
    Task<AiModelRuntimeStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<AiModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken);

    Task<AiModelLoadResult> LoadAsync(
        AiModelLoadRequest request,
        CancellationToken cancellationToken);

    Task<AiModelUnloadResult> UnloadAsync(
        AiModelUnloadRequest request,
        CancellationToken cancellationToken);

    Task ForwardAsync(
        HttpContext context,
        string relativePath,
        CancellationToken cancellationToken);
}

public sealed class AiModelRuntimeUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);

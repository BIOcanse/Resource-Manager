namespace ResourceManager.App.Application.PublicServices.AiGateway;

public interface IAiGatewayLocalModelPolicy
{
    Task<bool> IsAllowedAsync(string model, CancellationToken cancellationToken);
}

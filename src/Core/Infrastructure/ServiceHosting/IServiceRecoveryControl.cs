using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Infrastructure.ServiceHosting;

public interface IServiceRecoveryControl
{
    string? GetSkipReason();
    void Write(ServiceRecoveryConfiguration configuration);
    ServiceRecoveryConfiguration Read();
}

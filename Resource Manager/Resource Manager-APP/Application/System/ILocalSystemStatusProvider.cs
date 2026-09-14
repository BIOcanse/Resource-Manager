using ResourceManager.App.Domain.LocalSystem;

namespace ResourceManager.App.Application.LocalSystem;

public interface ILocalSystemStatusProvider
{
    LocalSystemStatus GetStatus();
}

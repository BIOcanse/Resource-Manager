using ResourceManager.App.Domain.SystemHealth;

namespace ResourceManager.App.Application.SystemHealth;

public interface IPowerProfileReader
{
    PowerProfileSnapshot Read();
}

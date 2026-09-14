using ResourceManager.App.Domain.LocalSystem;

namespace ResourceManager.App.Application.LocalSystem;

public interface ILocalPathOpener
{
    LocalPathOpenResult Open(LocalPathOpenRequest request);
}

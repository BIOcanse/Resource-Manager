using ResourceManager.App.Domain.Controlled;

namespace ResourceManager.App.Application.Controlled;

public interface IControlledSoftwareRegistry
{
    IReadOnlyList<ControlledSoftwareRegistration> GetAll();

    ControlledSoftwareRegistration Register(ControlledSoftwareRegistrationRequest request);

    bool Remove(Guid id);
}

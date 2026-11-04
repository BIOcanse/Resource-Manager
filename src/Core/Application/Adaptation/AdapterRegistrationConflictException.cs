namespace ResourceManager.App.Application.Adaptation;

public sealed class AdapterRegistrationConflictException : InvalidOperationException
{
    public AdapterRegistrationConflictException()
        : base("The adapter or application ID belongs to another registration. Remove that registration explicitly before changing its identity.") { }
}

namespace ResourceManager.App.Domain.ExternalInvocation;

public enum ExternalInvocationAccessClass : byte
{
    Open = 0,
    Authenticated = 1
}

public enum ExternalInvocationTransport : byte
{
    Internal = 0,
    Http = 1,
    NamedPipe = 2,
    CommandLine = 3,
    AdapterSdk = 4
}

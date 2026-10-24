namespace ResourceManager.Shared.Runtime;

// Explicit UI opening shares the authenticated user-session pipe, without issuing an API credential.
public static class NativeUiOpenSessionContract
{
    public const byte OpenOperation = 4;
    public const byte OpenForeground = 0;
    public const byte OpenBackground = 1;
    public const byte Accepted = 1;

    public static async Task<bool> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var mode = new byte[1];
        await stream.ReadExactlyAsync(mode, cancellationToken);
        return mode[0] switch
        {
            OpenForeground => false,
            OpenBackground => true,
            _ => throw new InvalidDataException("Invalid Native UI open mode.")
        };
    }
}

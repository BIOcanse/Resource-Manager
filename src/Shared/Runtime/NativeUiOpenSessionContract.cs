namespace ResourceManager.Shared.Runtime;

// Explicit UI opening has its own user-session pipe and carries no API credential.
public static class NativeUiOpenSessionContract
{
    public static string PipeName(uint sessionId) => $"ResourceManager.NativeUi.OpenSession.{sessionId}.v1";
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

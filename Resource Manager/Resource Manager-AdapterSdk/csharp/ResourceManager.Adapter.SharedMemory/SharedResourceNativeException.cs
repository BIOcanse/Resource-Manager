namespace ResourceManager.Adapter.SharedMemory;

public sealed class SharedResourceNativeException : InvalidOperationException
{
    internal SharedResourceNativeException(string operation, int resultCode)
        : base(CreateMessage(operation, resultCode))
    {
        ResultCode = resultCode;
        IsKnownResultCode = Enum.IsDefined(typeof(NativeSharedResourceResult), resultCode);
    }

    public int ResultCode { get; }

    public bool IsKnownResultCode { get; }

    private static string CreateMessage(string operation, int resultCode)
    {
        var label = Enum.IsDefined(typeof(NativeSharedResourceResult), resultCode)
            ? ((NativeSharedResourceResult)resultCode).ToString()
            : $"Unknown({resultCode})";
        return $"Native shared-resource operation '{operation}' failed with {label}.";
    }
}

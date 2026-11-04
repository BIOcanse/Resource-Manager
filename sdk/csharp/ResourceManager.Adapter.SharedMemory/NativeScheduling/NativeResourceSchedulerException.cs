namespace ResourceManager.Adapter.NativeScheduling;

public sealed class NativeResourceSchedulerException : InvalidOperationException
{
    internal NativeResourceSchedulerException(string operation, int resultCode)
        : base(CreateMessage(operation, resultCode))
    {
        ResultCode = resultCode;
        IsKnownResultCode = Enum.IsDefined(typeof(NativeResourceSchedulerResultCode), resultCode);
    }

    public int ResultCode { get; }

    public bool IsKnownResultCode { get; }

    private static string CreateMessage(string operation, int resultCode)
    {
        var label = Enum.IsDefined(typeof(NativeResourceSchedulerResultCode), resultCode)
            ? ((NativeResourceSchedulerResultCode)resultCode).ToString()
            : $"Unknown({resultCode})";
        return $"Native resource scheduler operation '{operation}' failed with {label}.";
    }
}

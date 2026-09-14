namespace ResourceManager.Adapter.NativeLedger;

public sealed class NativeAdapterResourceLedgerException : InvalidOperationException
{
    internal NativeAdapterResourceLedgerException(string operation, int resultCode)
        : base(CreateMessage(operation, resultCode))
    {
        ResultCode = resultCode;
        IsKnownResultCode = Enum.IsDefined(typeof(NativeAdapterResourceLedgerResultCode), resultCode);
    }

    public int ResultCode { get; }

    public bool IsKnownResultCode { get; }

    private static string CreateMessage(string operation, int resultCode)
    {
        var label = Enum.IsDefined(typeof(NativeAdapterResourceLedgerResultCode), resultCode)
            ? ((NativeAdapterResourceLedgerResultCode)resultCode).ToString()
            : $"Unknown({resultCode})";
        return $"Native private-resource ledger operation '{operation}' failed with {label}.";
    }
}

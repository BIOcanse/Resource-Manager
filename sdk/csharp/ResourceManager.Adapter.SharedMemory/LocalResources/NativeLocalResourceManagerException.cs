namespace ResourceManager.Adapter.LocalResources;

public enum LocalResourceManagerError
{
    InvalidArgument = 1,
    InvalidState = 2,
    BufferTooSmall = 3,
    TableFull = 4,
    ResourceFull = 5,
    CapabilityFull = 6,
    PendingFull = 7,
    DuplicateTable = 8,
    DuplicateResource = 9,
    DuplicateCapability = 10,
    StaleTable = 11,
    StaleResource = 12,
    StaleCapability = 13,
    StaleIntent = 14,
    ResourceBusy = 15,
    EffectMismatch = 16,
    IntentConflict = 17,
    GenerationExhausted = 18,
    NumericOverflow = 19,
    PartitionFull = 20,
    StalePartition = 21,
    PartitionBusy = 22,
    SlotOccupied = 23,
    AdmissionStale = 24,
    OperationActive = 25,
    OperationRequired = 26,
    StaleOperation = 27,
    RecoveryRequired = 28,
    InvalidOperationPhase = 29
}

public sealed class NativeLocalResourceManagerException : InvalidOperationException
{
    internal NativeLocalResourceManagerException(string operation, int code)
        : base($"Native local resource manager operation '{operation}' failed with result {code}.")
    {
        Operation = operation;
        ResultCode = code;
    }

    public string Operation { get; }
    public int ResultCode { get; }
    public LocalResourceManagerError? Error
        => Enum.IsDefined(typeof(LocalResourceManagerError), ResultCode)
            ? (LocalResourceManagerError)ResultCode
            : null;
}

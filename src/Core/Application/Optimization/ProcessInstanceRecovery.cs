namespace ResourceManager.App.Application.Optimization;

internal static class ProcessInstanceRecovery
{
    internal static bool IsConfirmedExited(
        RecoveryReadResult<ProcessInstanceRecoverySnapshot> read,
        uint expectedProcessId,
        ulong expectedProcessStartKey)
    {
        if (expectedProcessId == 0 || expectedProcessId > int.MaxValue)
        {
            throw new InvalidDataException(
                "The applied ownership process identity is invalid.");
        }
        RequireCanonicalProcessStartKey(expectedProcessStartKey);

        return read.Status switch
        {
            RecoveryReadStatus.NotFoundOrExited when read.Value is null => true,
            RecoveryReadStatus.Found when read.Value is not null =>
                IsDifferentProcessIncarnation(
                    read.Value,
                    expectedProcessId,
                    expectedProcessStartKey),
            RecoveryReadStatus.Unavailable when read.Value is null => false,
            _ => throw new InvalidDataException(
                "The process recovery identity result has an invalid shape.")
        };
    }

    private static void RequireCanonicalProcessStartKey(ulong processStartKey)
    {
        if (processStartKey == 0)
        {
            throw new InvalidDataException(
                "The applied ownership process start key must be nonzero.");
        }

        try
        {
            _ = DateTimeOffset.FromFileTime(checked((long)processStartKey));
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException(
                "The applied ownership process start key is not a canonical Windows process identity.",
                exception);
        }
    }

    private static bool IsDifferentProcessIncarnation(
        ProcessInstanceRecoverySnapshot observed,
        uint expectedProcessId,
        ulong expectedProcessStartKey)
    {
        if (observed.ProcessId != checked((int)expectedProcessId))
        {
            throw new InvalidDataException(
                "The process recovery identity does not match the requested process ID.");
        }

        ulong observedProcessStartKey;
        try
        {
            observedProcessStartKey = checked((ulong)observed.StartedAt.ToFileTime());
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or OverflowException)
        {
            throw new InvalidDataException(
                "The process recovery start time is not a canonical Windows process identity.",
                exception);
        }
        if (observedProcessStartKey == 0)
        {
            throw new InvalidDataException(
                "The process recovery start key must be nonzero.");
        }

        return observedProcessStartKey != expectedProcessStartKey;
    }
}

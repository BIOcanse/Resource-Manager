using ResourceManager.ReadWrite;

internal static class FieldObservationConsumerAssertions
{
    internal static void RequireOriginalOwnerObservation(FieldReading delivered, FieldReading current)
    {
        if (delivered.Field != current.Field || delivered.SourceId != current.SourceId)
            throw new InvalidDataException("Subscription observation does not use the original field/source.");

        // The owner can finish its next collection while this asynchronous consumer resumes.
        // Equal observation times still require exact equality; a later current may have a new value.
        if (delivered.ObservedAt == current.ObservedAt)
        {
            if (delivered != current)
                throw new InvalidDataException("Subscription differs from the original owner at the same observation time.");
        }
        else if (delivered.ObservedAt is null || current.ObservedAt is null
            || current.ObservedAt < delivered.ObservedAt)
        {
            throw new InvalidDataException("Subscription observation is newer than the original owner current.");
        }
    }
}

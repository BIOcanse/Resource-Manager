namespace Resource_Manager_APP.Tests;

// GC.GetTotalMemory is process-wide, so a retained-heap bound only holds when no other test runs beside it.
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessHeapMeasurementCollection
{
    public const string Name = "Process heap measurement";
}

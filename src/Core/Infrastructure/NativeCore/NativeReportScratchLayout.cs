using System.Runtime.CompilerServices;

namespace ResourceManager.App.Infrastructure.NativeCore;

internal sealed class NativeReportScratchLayout
{
    internal NativeReportScratchLayout(uint reportCapacity, uint persistenceCapacity)
    {
        ReportCapacity = checked((int)reportCapacity);
        PersistenceCapacity = checked((int)persistenceCapacity);
        Persistence = checked((ulong)ReportCapacity * (ulong)Unsafe.SizeOf<NativeReportOutput>());
        Feedback = checked(Persistence + (ulong)PersistenceCapacity *
            (ulong)Unsafe.SizeOf<NativeReportPersistenceOperation>());
        Length = checked(Feedback + (ulong)PersistenceCapacity *
            (ulong)Unsafe.SizeOf<NativeReportPersistenceFeedback>());
    }

    internal int ReportCapacity { get; }
    internal int PersistenceCapacity { get; }
    internal ulong Reports => 0;
    internal ulong Persistence { get; }
    internal ulong Feedback { get; }
    internal ulong Length { get; }
}

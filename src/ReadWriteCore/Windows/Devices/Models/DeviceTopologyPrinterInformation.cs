using ResourceManager.ReadWrite.Windows.Devices.Printers;

namespace ResourceManager.ReadWrite.Windows.Devices.Models;

[Flags]
public enum WindowsPrinterQueueStatusFlags : uint
{
    None = 0,
    Paused = 0x00000001,
    Error = 0x00000002,
    PendingDeletion = 0x00000004,
    PaperJam = 0x00000008,
    PaperOut = 0x00000010,
    ManualFeed = 0x00000020,
    PaperProblem = 0x00000040,
    Offline = 0x00000080,
    IoActive = 0x00000100,
    Busy = 0x00000200,
    Printing = 0x00000400,
    OutputBinFull = 0x00000800,
    NotAvailable = 0x00001000,
    Waiting = 0x00002000,
    Processing = 0x00004000,
    Initializing = 0x00008000,
    WarmingUp = 0x00010000,
    TonerLow = 0x00020000,
    NoToner = 0x00040000,
    PagePunt = 0x00080000,
    UserIntervention = 0x00100000,
    OutOfMemory = 0x00200000,
    DoorOpen = 0x00400000,
    ServerUnknown = 0x00800000,
    PowerSave = 0x01000000,
    ServerOffline = 0x02000000,
    DriverUpdateNeeded = 0x04000000
}

/// <summary>Values returned by one explicitly scoped Winspool directory read.
/// A nullable queue status distinguishes unavailable from the valid zero mask.</summary>
public sealed record DeviceTopologyPrinterInformation(
    string PrinterName,
    string? ServerName,
    string? PortNameDeclared,
    WindowsPrinterQueueStatusFlags? QueueStateActual,
    ulong? DriverVersionRaw);

/// <summary>A failed selected read for one known printer, or a whole-directory
/// failure when both identity fields are null.</summary>
public sealed record WindowsPrinterInformationReadFailure(
    string? PrinterName,
    string? ServerName,
    WindowsPrinterInformationFields FailedFields,
    string Code);

/// <summary>The result of one complete printer-name directory read. Failed
/// directories return no partial printer objects; failed object fields are listed
/// alongside their known identity so the original current can be cleared.</summary>
public sealed record WindowsPrinterInformationReadResult(
    bool DirectorySucceeded,
    IReadOnlyList<DeviceTopologyPrinterInformation> Printers,
    IReadOnlyList<WindowsPrinterInformationReadFailure> Failures);

/// <summary>Caller-owned local producer for an explicit selected printer read.
/// It is called only from explicit discovery/collection, never from cached reads.</summary>
public interface IWindowsPrinterInformationReader
{
    WindowsPrinterInformationReadResult Read(WindowsPrinterInformationScope scope,
        WindowsPrinterInformationFields selectedFields, CancellationToken cancellationToken);
}

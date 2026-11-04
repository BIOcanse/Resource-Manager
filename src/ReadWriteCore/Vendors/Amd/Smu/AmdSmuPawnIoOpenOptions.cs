namespace ResourceManager.ReadWrite.Vendors.Amd.Smu;

/// <summary>Explicit ordered library selection and optional direct device access. No paths or installation are inferred.</summary>
public sealed record AmdSmuPawnIoOpenOptions(string ModulePath, IReadOnlyList<string> LibraryPaths,
    bool UseDirectDevice, TimeSpan PciLockTimeout);

/// <summary>One selected SMU session. Read owns the existing table update; the caller owns disposal.</summary>
public interface IAmdSmuSession : IDisposable
{
    string ExecutorName { get; }
    string ModulePath { get; }
    uint TableVersion { get; }
    float[] UpdateAndReadPmTable();
}

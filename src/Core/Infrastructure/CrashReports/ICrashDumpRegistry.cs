using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Infrastructure.CrashReports;

// The desired WER LocalDumps key of one image (HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\<image>).
public sealed record CrashDumpRegistryKey(string Image, string DumpFolder, int DumpType, int? CustomDumpFlags, int DumpCount);

// The seam between CrashDumpSettings and the machine: the registry keys and the dump folder.
// Tests use a fake; only WindowsCrashDumpRegistry touches HKLM.
public interface ICrashDumpRegistry
{
    IReadOnlyList<CrashDumpRegistryEntry> Read(IReadOnlyList<string> images);

    // Writes the key and its owner marker, replacing what the key held.
    void Write(CrashDumpRegistryKey key);

    // Deletes the key only when it carries the owner marker; a key someone else wrote stays.
    void RemoveOwned(string image);

    // Creates the dump folder; when usersMayWrite, interactive users may create files in it (the window process
    // runs as the user and WER writes its dump under that identity).
    void PrepareFolder(string path, bool usersMayWrite);
}

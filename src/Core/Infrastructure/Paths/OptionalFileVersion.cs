namespace ResourceManager.App.Infrastructure.Paths;

// Metadata is checked at the existing settings read boundary. Missing optional
// files have a version too; their contents are opened only when that version changes.
internal readonly record struct OptionalFileVersion(DateTime WrittenUtc, long Length)
{
    internal static OptionalFileVersion? Read(string path)
    {
        var written = File.GetLastWriteTimeUtc(path);
        // GetLastWriteTimeUtc returns the FILETIME epoch for a missing path,
        // while access and other I/O failures still propagate to the caller.
        if (written == DateTime.FromFileTimeUtc(0) && !File.Exists(path)) return null;
        try { return new(written, new FileInfo(path).Length); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}

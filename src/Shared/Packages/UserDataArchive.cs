using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using ResourceManager.App.Infrastructure.Optimization;

namespace ResourceManager.Shared.Packages;

/// <summary>Product data only; no program payload, dependencies, ACLs or service registration.</summary>
public static class UserDataArchive
{
    private const string Contract = "resource-manager-user-data-v1";
    private const string ManifestName = "user-data-manifest.json";
    private const long MaximumBytes = 256L * 1024 * 1024 * 1024;
    public static IReadOnlyList<string> DirectoryNames { get; } = ["Config", "UserData", "Misc"];
    private sealed record Member(string Path, long Length, string Sha256);
    private sealed record Manifest(string Contract, string[] Directories, Member[] Files);

    public static string? RetainedArchiveAt(string root)
    {
        if (!Directory.Exists(root)) return null;
        ReleasePackageLayout.RejectReparse(root);
        var entries = Directory.GetFileSystemEntries(root);
        if (entries.Length != 1 || !File.Exists(entries[0])
            || !Path.GetFileName(entries[0]).StartsWith("ResourceManager-user-data-", StringComparison.Ordinal)
            || !entries[0].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Installation folder already contains files other than a retained user data archive.");
        ReleasePackageLayout.RejectReparse(entries[0]);
        Verify(entries[0]);
        return entries[0];
    }

    public static void Create(string installRoot, string archivePath)
    {
        var files = new List<Member>();
        var directories = new List<string>();
        var count = 0;
        long bytes = 0;
        ReleasePackageLayout.RejectReparse(installRoot);
        using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
        {
            foreach (var name in DirectoryNames)
            {
                var source = Path.Combine(installRoot, name);
                if (!Directory.Exists(source)) continue;
                foreach (var path in OwnedPackageTree.Read(source))
                {
                    if (++count > 100_000) throw new IOException("User data archive exceeds its entry bound.");
                    var relative = Path.GetRelativePath(installRoot, path).Replace('\\', '/');
                    RequireDataPath(relative);
                    if (Directory.Exists(path)) { directories.Add(relative); continue; }
                    using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    bytes = checked(bytes + input.Length);
                    if (bytes > MaximumBytes) throw new IOException("User data archive exceeds its size bound.");
                    var entry = zip.CreateEntry(relative, CompressionLevel.Fastest);
                    using (var destination = entry.Open()) input.CopyTo(destination);
                    input.Position = 0;
                    files.Add(new(relative, input.Length, Convert.ToHexString(SHA256.HashData(input))));
                }
            }
            var manifest = zip.CreateEntry(ManifestName);
            using var writer = manifest.Open();
            JsonSerializer.Serialize(writer, new Manifest(Contract, directories.ToArray(), files.ToArray()));
        }
        Verify(archivePath);
    }

    public static void Verify(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        _ = ReadVerified(zip);
    }

    public static void Import(string installRoot, string archivePath, Action<string>? validateData = null,
        IHostManagerUserDataArrival? hostManagerArrival = null)
    {
        installRoot = Path.GetFullPath(installRoot).TrimEnd(Path.DirectorySeparatorChar);
        ReleasePackageLayout.RejectReparse(installRoot);
        foreach (var name in DirectoryNames)
            if (Directory.Exists(Path.Combine(installRoot, name))) _ = OwnedPackageTree.Read(Path.Combine(installRoot, name));
        var workspace = Path.Combine(Path.GetDirectoryName(installRoot)!, "ResourceManager-import-" + Guid.NewGuid().ToString("N"));
        var next = Path.Combine(workspace, "next");
        var previous = Path.Combine(workspace, "previous");
        Directory.CreateDirectory(next);
        Directory.CreateDirectory(previous);
        var moved = new List<string>();
        var promoted = new List<string>();
        var complete = false;
        try
        {
            using (var zip = ZipFile.OpenRead(archivePath))
            {
                var manifest = ReadVerified(zip);
                foreach (var name in DirectoryNames) Directory.CreateDirectory(Path.Combine(next, name));
                foreach (var directory in manifest.Directories)
                    Directory.CreateDirectory(Path.Combine(next, directory.Replace('/', Path.DirectorySeparatorChar)));
                foreach (var member in manifest.Files)
                {
                    var target = Path.Combine(next, member.Path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var source = zip.GetEntry(member.Path)!.Open();
                    using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    source.CopyTo(destination);
                }
            }
            validateData?.Invoke(next);
            (hostManagerArrival ?? HostManagerUserDataArrival.Instance).Prepare(next, installRoot);
            // The target-bound authority is prepared locally, never read from ZIP.
            var promotedDirectories = DirectoryNames.Append(HostManagerUserDataArrival.AuthorityDirectory);
            foreach (var name in promotedDirectories)
            {
                if (!Directory.Exists(Path.Combine(next, name))) continue;
                var target = Path.Combine(installRoot, name);
                if (Directory.Exists(target)) { Directory.Move(target, Path.Combine(previous, name)); moved.Add(name); }
                Directory.Move(Path.Combine(next, name), target);
                promoted.Add(name);
            }
            PackageUserDirectoryAccess.Prepare(installRoot);
            PackageUserDirectoryAccess.PrepareRestoredUserCache(installRoot);
            complete = true;
        }
        catch
        {
            // Retain this workspace if rollback itself fails; never erase the previous data in that case.
            foreach (var name in promoted.AsEnumerable().Reverse()) OwnedPackageTree.Delete(Path.Combine(installRoot, name));
            foreach (var name in moved.AsEnumerable().Reverse()) Directory.Move(Path.Combine(previous, name), Path.Combine(installRoot, name));
            complete = true;
            throw;
        }
        finally { if (complete) OwnedPackageTree.Delete(workspace); }
    }

    private static Manifest ReadVerified(ZipArchive zip)
    {
        if (zip.Entries.Count > 100_001) throw new InvalidDataException("User data ZIP exceeds its entry bound.");
        var manifestEntry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("Missing user data archive manifest.");
        if (manifestEntry.Length > 32 * 1024 * 1024) throw new InvalidDataException("User data manifest is too large.");
        using var manifestInput = manifestEntry.Open();
        var manifest = JsonSerializer.Deserialize<Manifest>(manifestInput) ?? throw new InvalidDataException("Invalid user data archive manifest.");
        if (manifest.Contract != Contract || manifest.Files is null || manifest.Directories is null
            || manifest.Files.Length + manifest.Directories.Length > 100_000)
            throw new InvalidDataException("Invalid user data archive contract.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ManifestName };
        foreach (var directory in manifest.Directories)
        {
            RequireDataPath(directory);
            if (!seen.Add(directory)) throw new InvalidDataException("Duplicate user data path.");
        }
        long bytes = 0;
        foreach (var member in manifest.Files)
        {
            RequireDataPath(member.Path);
            if (!seen.Add(member.Path) || member.Length < 0 || member.Length > MaximumBytes)
                throw new InvalidDataException("Invalid user data file.");
            bytes = checked(bytes + member.Length);
            if (bytes > MaximumBytes) throw new InvalidDataException("User data ZIP exceeds its size bound.");
            var entry = zip.GetEntry(member.Path) ?? throw new InvalidDataException("Missing user data file.");
            if (entry.Length != member.Length || (entry.ExternalAttributes >> 16 & 0xF000) == 0xA000)
                throw new InvalidDataException("Invalid user data ZIP member.");
            using var source = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            long length = 0;
            int read;
            while ((read = source.Read(buffer)) != 0)
            {
                length = checked(length + read);
                if (length > member.Length) throw new InvalidDataException("User data member exceeds its declared length.");
                hash.AppendData(buffer, 0, read);
            }
            if (length != member.Length || !Convert.ToHexString(hash.GetHashAndReset()).Equals(member.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("User data archive checksum failed.");
        }
        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
            if (!actual.Add(entry.FullName)) throw new InvalidDataException("Duplicate ZIP member.");
        if (!actual.SetEquals(manifest.Files.Select(member => member.Path).Append(ManifestName)))
            throw new InvalidDataException("Unexpected user data ZIP member.");
        return manifest;
    }

    private static void RequireDataPath(string path)
    {
        var parts = path.Split('/');
        if (parts.Length > 64 || !DirectoryNames.Contains(parts[0], StringComparer.Ordinal)
            || path.Contains('\\') || parts.Any(part => part.Length == 0 || part is "." or ".."
                || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || IsDeviceName(part)))
            throw new InvalidDataException("Unsafe user data archive path.");
    }

    private static bool IsDeviceName(string part)
    {
        var name = part.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" || name.Length == 4
            && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal))
            && name[3] is >= '1' and <= '9';
    }
}

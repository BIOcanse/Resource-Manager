using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ResourceManager.App.Domain.Dependencies;
using ResourceManager.App.Domain.Messages;
using ResourceManager.App.Domain.Software;
using ResourceManager.App.Infrastructure.NativeCore;

namespace ResourceManager.App.Infrastructure.Dependencies;

/// <summary>The managed boundary borrows the cache owner's registered tail once per synchronous native probe.</summary>
internal sealed unsafe partial class NativeDependencyProbe
{
    private readonly NativeDependencyCacheWorkspace workspace;
    private readonly uint maximumBytes;

    internal NativeDependencyProbe(NativeDependencyCacheWorkspace workspace)
        : this(workspace, DependencyNativeSettings.MaximumWorkspaceBytes) { }

    internal NativeDependencyProbe(NativeDependencyCacheWorkspace workspace, uint maximumBytes)
    {
        this.workspace = workspace;
        this.maximumBytes = maximumBytes;
    }

    internal OptionalDependencyStatus BuildStatus(OptionalDependencyDefinition definition,
        IReadOnlyList<InstalledSoftwareEntry> installedSoftware, string packageRoot, ILogger? logger = null)
        => Build(definition, installedSoftware, packageRoot, null, null, null, logger);

    internal OptionalDependencyStatus BuildStatus(OptionalDependencyDefinition definition,
        IReadOnlyList<InstalledSoftwareEntry> installedSoftware, string dependencyRoot, string installerCacheRoot,
        IReadOnlyList<string> commonCandidates, ILogger? logger = null)
        => Build(definition, installedSoftware, null, dependencyRoot, installerCacheRoot, commonCandidates, logger);

    private OptionalDependencyStatus Build(OptionalDependencyDefinition definition,
        IReadOnlyList<InstalledSoftwareEntry> software, string? packageRoot, string? dependencyRoot,
        string? installerRoot, IReadOnlyList<string>? candidates, ILogger? logger)
    {
        var measure = new RegionWriter(IntPtr.Zero, uint.MaxValue);
        _ = BuildRequest(ref measure, definition, software, packageRoot, dependencyRoot, installerRoot, candidates);
        CheckCapacity(measure.Position);
        uint warnings = 0, readAttempts = 0;
        int readStatus = 0;
        var status = workspace.UseProbe<OptionalDependencyStatus>(measure.Position, (region, available) =>
        {
            var writer = new RegionWriter(region, Math.Min(available, maximumBytes));
            var request = BuildRequest(ref writer, definition, software, packageRoot, dependencyRoot, installerRoot, candidates);
            var result = Execute(region, available, ref writer, request, 0);
            if (result.Status == 2) { CheckCapacity(result.RequiredBytes); return (default!, result.RequiredBytes); }
            warnings = result.Warnings;
            readAttempts = result.ReadAttempts;
            readStatus = result.ReadStatus;
            string Get(TextSpan span) => Read(region, available, span);
            string? Maybe(TextSpan span) => span.Offset == 0 ? null : Get(span);
            var installDirectory = Get(result.InstallDirectory);
            var externalDirectory = Maybe(result.ExternalDirectory);
            var state = result.State switch
            {
                1 => "update_available", 2 => "payload_missing", 3 => "installed",
                4 => "readyToInstall", 5 => "downloadable", 6 => "manualDownloadRequired",
                _ => throw new InvalidDataException("Invalid native dependency status state.")
            };
            var sourceKind = result.SourceKind switch
            {
                0 => DependencyInstallerSourceKinds.Manual, 1 => DependencyInstallerSourceKinds.Direct,
                2 => DependencyInstallerSourceKinds.GitHubRelease,
                _ => throw new InvalidDataException("Invalid native dependency installer source.")
            };
            var message = result.Message == BackendMessageCodes.Dependency.ReusingExternalInstall
                ? BackendMessage.Create(BackendMessageDomains.Dependency, checked((byte)result.Message), externalDirectory!)
                : BackendMessage.Create(BackendMessageDomains.Dependency, checked((byte)result.Message));
            return (new OptionalDependencyStatus(definition, state, Get(result.DependencyRoot),
                Get(result.InstallerDirectory), installDirectory, Maybe(result.InstallerPath),
                (result.Flags & 1) != 0, (result.Flags & 2) != 0, (result.Flags & 4) != 0,
                (result.Flags & 8) != 0, externalDirectory ?? installDirectory, externalDirectory,
                Get(result.Detection), message, sourceKind, (result.Flags & 16) != 0, (result.Flags & 32) != 0), 0UL);
        });

        // Projection is complete and the borrowed view/lease has ended before arbitrary sinks can reenter.
        if (readAttempts > 1 || readStatus < 0)
            System.Diagnostics.Trace.WriteLine($"AtomicFileCommit operation=open status=0x{readStatus:X8} attempts={readAttempts} component={definition.Id}");
        if ((warnings & 1) != 0)
            logger?.LogWarning("Component release record is unavailable or invalid: {RecordPath}",
                Path.Combine(status.InstallDirectory, DependencyReleaseRecordStore.FileName));
        if ((warnings & 2) != 0)
            logger?.LogWarning("Could not inspect interrupted component payload backups for {ComponentId}.", definition.Id);
        return status;
    }

    private static Request BuildRequest(ref RegionWriter writer, OptionalDependencyDefinition definition,
        IReadOnlyList<InstalledSoftwareEntry> software, string? packageRoot, string? dependencyRoot,
        string? installerRoot, IReadOnlyList<string>? candidates)
    {
        var request = writer.Definition(definition);
        request.PackageRoot = writer.Text(packageRoot);
        request.DependencyRoot = writer.Text(dependencyRoot);
        request.InstallerRoot = writer.Text(installerRoot);
        request.Software = writer.Software(software);
        request.Candidates = writer.Texts(candidates);
        request.ExplicitCandidates = candidates is null ? 0u : 1u;
        return request;
    }
    internal bool IsDependencyInstalledAt(OptionalDependencyDefinition definition, string directory)
        => Run(1, definition, directory, null, (region, length, result) => (result.Flags & 1) != 0);

    private T Run<T>(uint operation, OptionalDependencyDefinition? definition, string? left, string? right,
        Func<IntPtr, ulong, NativeResult, T> project)
    {
        var measure = new RegionWriter(IntPtr.Zero, uint.MaxValue);
        _ = HelperRequest(ref measure, definition, left, right);
        CheckCapacity(measure.Position);
        return workspace.UseProbe<T>(measure.Position, (region, available) =>
        {
            var writer = new RegionWriter(region, Math.Min(available, maximumBytes));
            var request = HelperRequest(ref writer, definition, left, right);
            var result = Execute(region, available, ref writer, request, operation);
            if (result.Status == 2) { CheckCapacity(result.RequiredBytes); return (default!, result.RequiredBytes); }
            return (project(region, available, result), 0UL);
        });
    }

    private static Request HelperRequest(ref RegionWriter writer, OptionalDependencyDefinition? definition, string? left, string? right)
    {
        var request = definition is null ? default : writer.Definition(definition);
        request.Left = writer.Text(left);
        request.Right = writer.Text(right);
        request.MaximumEntries = DependencyInstallPaths.MaximumEntries;
        request.UnicodeProfile = NativeDependencyProbeUnicodeProfile.Current;
        return request;
    }
    private void CheckCapacity(ulong required)
    {
        if (required > maximumBytes) throw new OutOfMemoryException("The dependency probe exceeds MaximumWorkspaceBytes.");
    }

    private static NativeResult Execute(IntPtr region, ulong available, ref RegionWriter writer, Request request, uint operation)
    {
        *(Request*)region = request;
        ExecuteNative(region, checked((nuint)writer.Capacity), writer.Position, operation, out var result);
        if (result.Status is not (0 or 2))
            throw result.Status switch
            {
                2 => new OutOfMemoryException("The dependency probe exceeded its registered workspace bound."),
                4 => new UnauthorizedAccessException("Native dependency path access was denied."),
                1 => new InvalidDataException("Invalid native dependency probe input."),
                _ => new IOException($"Native dependency probe failed ({result.Status}, OS {result.OsError}).")
            };
        return result;
    }

    private static string Read(IntPtr region, ulong available, TextSpan text)
    {
        var bytes = checked((ulong)text.Count * sizeof(char));
        if (text.Offset > available || bytes > available - text.Offset || (text.Offset & 1) != 0)
            throw new InvalidDataException("Invalid native dependency text region.");
        return new string((char*)((byte*)region + text.Offset), 0, checked((int)text.Count));
    }

    // These scalar calls use only OS-owned temporary path memory; they do not create another Ledger owner.
    internal static bool IsRegularFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            fixed (char* pointer = full)
                return PathFactNative(pointer, checked((uint)full.Length), checked((uint)Path.GetPathRoot(full)!.Length), 1, out _) == 1;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or Win32Exception)
        { return false; }
    }

    internal static void RejectLinks(string path)
    {
        var full = Path.GetFullPath(path);
        uint status, osError;
        fixed (char* pointer = full)
            status = PathFactNative(pointer, checked((uint)full.Length), checked((uint)Path.GetPathRoot(full)!.Length), 0, out osError);
        if (status == 0) return;
        var rejected = osError > 0 && osError <= full.Length ? full[..checked((int)osError)] : full;
        if (status == 3) throw new IOException($"Component installation rejects a reparse point: {rejected}");
        if (status == 4) throw new IOException($"Component installation rejects a hard-linked file: {rejected}");
        if (osError == 5) throw new UnauthorizedAccessException($"Cannot validate component path: {full}");
        throw new IOException($"Cannot validate the component file identity: {full}", new Win32Exception(checked((int)osError)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TextSpan { internal uint Offset, Count; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SoftwareRow { internal TextSpan Name, Publisher, Directory; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Request
    {
        internal TextSpan Id, Vendor, InstallerName, InstallName, DependencyRoot, InstallerRoot,
            ReleaseTag, ReleaseAsset, PayloadTag, PayloadAsset, Patterns, Probes, Products, PayloadFiles,
            Software, Candidates, Left, Right, DownloadUrl, PackageRoot;
        internal uint HasPayload, HasRelease, ExplicitCandidates, MaximumEntries, ReadAttemptLimit, ReadDelayMs, UnicodeProfile;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeResult
    {
        internal uint Status, State, Flags, Message;
        internal TextSpan DependencyRoot, InstallerDirectory, InstallDirectory, InstallerPath,
            ExternalDirectory, Detection, Value;
        internal uint Warnings, OsError, SourceKind, Reserved;
        internal ulong RequiredBytes;
        internal uint ReadAttempts;
        internal int ReadStatus;
    }
    private ref struct RegionWriter
    {
        private readonly byte* region;
        internal readonly ulong Capacity;
        internal uint Position;
        internal RegionWriter(IntPtr region, ulong capacity)
        {
            this.region = (byte*)region; Capacity = capacity; Position = checked((uint)sizeof(Request));
            if (capacity < Position) throw new OutOfMemoryException("Dependency probe workspace cannot hold its header.");
        }
        private uint Reserve(uint bytes, uint alignment)
        {
            var offset = checked((Position + alignment - 1) & ~(alignment - 1));
            if (offset > Capacity || bytes > Capacity - offset)
                throw new OutOfMemoryException("Dependency probe input exceeds its registered workspace.");
            Position = checked(offset + bytes);
            return offset;
        }
        internal TextSpan Text(string? value)
        {
            if (value is null) return default;
            var count = checked((uint)value.Length);
            var offset = Reserve(checked(count * 2), 2);
            if (region != null) value.AsSpan().CopyTo(new Span<char>(region + offset, value.Length));
            return new TextSpan { Offset = offset, Count = count };
        }
        internal TextSpan Texts(IReadOnlyList<string>? values)
        {
            if (values is null) return default;
            var offset = Reserve(checked((uint)values.Count * (uint)sizeof(TextSpan)), 4);
            var index = 0;
            while (index < values.Count)
            {
                var value = Text(values[index]);
                if (region != null) ((TextSpan*)(region + offset))[index] = value;
                index++;
            }
            return new TextSpan { Offset = offset, Count = checked((uint)values.Count) };
        }
        internal TextSpan Software(IReadOnlyList<InstalledSoftwareEntry> values)
        {
            var offset = Reserve(checked((uint)values.Count * (uint)sizeof(SoftwareRow)), 4);
            var index = 0;
            while (index < values.Count)
            {
                var entry = values[index];
                var row = new SoftwareRow {
                    Name = Text(entry.Name), Publisher = Text(entry.Publisher), Directory = Text(entry.InstallLocation) };
                if (region != null) ((SoftwareRow*)(region + offset))[index] = row;
                index++;
            }
            return new TextSpan { Offset = offset, Count = checked((uint)values.Count) };
        }
        internal Request Definition(OptionalDependencyDefinition value) => new()
        {
            Id = Text(value.Id), Vendor = Text(value.Vendor), InstallerName = Text(value.InstallerFileName),
            InstallName = Text(value.InstallDirectoryName), DownloadUrl = Text(value.DownloadUrl),
            ReleaseTag = Text(value.ReleaseSource?.VerifiedTag), ReleaseAsset = Text(value.ReleaseSource?.VerifiedAssetName),
            PayloadTag = Text(value.PayloadSource?.VerifiedTag), PayloadAsset = Text(value.PayloadSource?.VerifiedAssetName),
            Patterns = Texts(value.InstallerFilePatterns), Probes = Texts(value.InstalledProbeRelativePaths),
            Products = Texts(value.InstalledProductNames), PayloadFiles = Texts(value.PayloadSource?.FileNames),
            HasPayload = value.PayloadSource is null ? 0u : 1u, HasRelease = value.ReleaseSource is null ? 0u : 1u,
            MaximumEntries = DependencyInstallPaths.MaximumEntries,
            ReadAttemptLimit = WindowsNativeAtomicFileCommitter.TransientAttemptLimit,
            ReadDelayMs = WindowsNativeAtomicFileCommitter.TransientRetryDelayMilliseconds,
            UnicodeProfile = NativeDependencyProbeUnicodeProfile.Current
        };
    }

    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_dependency_probe_execute")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void ExecuteNative(IntPtr region, nuint length, uint inputLength, uint operation, out NativeResult result);
    [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_dependency_path_fact")]
    [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial uint PathFactNative(char* path, uint length, uint rootLength, uint regular, out uint osError);
}

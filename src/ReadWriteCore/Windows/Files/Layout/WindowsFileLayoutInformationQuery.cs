using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using ResourceManager.ReadWrite.Information;
using ResourceManager.ReadWrite.Publications;

namespace ResourceManager.ReadWrite.Windows.Files.Layout;

public enum WindowsFileLayoutTargetKind { File, VolumeMountPoint, VolumeGuidPath }

[Flags]
public enum WindowsFileLayoutInformationFields
{
    None = 0,
    FileExtentCount = 1,
    VolumeFragmentationMap = 2
}

/// <summary>Finite caller-selected limits for a single explicit file or volume query.</summary>
public sealed record WindowsFileLayoutQueryLimits
{
    public int InitialPageBytes { get; init; } = 16 * 1024;
    public int MaximumPageBytes { get; init; } = 64 * 1024;
    public int MaximumPageCount { get; init; } = 256;
    public int MaximumReturnedBytes { get; init; } = 8 * 1024 * 1024;
    public int MaximumExtentCount { get; init; } = 65_536;
    public int MaximumRetriesPerPage { get; init; } = 2;

    internal void Validate()
    {
        if (InitialPageBytes < 32
            || MaximumPageBytes < InitialPageBytes || MaximumPageBytes > Array.MaxLength
            || MaximumPageCount <= 0 || MaximumReturnedBytes <= 0
            || MaximumExtentCount <= 0 || MaximumRetriesPerPage < 0)
            throw new ArgumentOutOfRangeException(nameof(WindowsFileLayoutQueryLimits),
                "Choose positive finite file-layout bounds; page buffers must fit a managed byte array and include the native header.");
    }
}

/// <summary>Explicit one-file, drive-root, or volume-GUID scope. Construction performs no IOCTL.</summary>
public sealed record WindowsFileLayoutQueryScope
{
    private const string VolumeGuidPathPrefix = @"\\?\Volume{";

    public WindowsFileLayoutTargetKind TargetKind { get; }
    public string TargetPath { get; }
    public WindowsFileLayoutQueryLimits Limits { get; }

    private WindowsFileLayoutQueryScope(WindowsFileLayoutTargetKind targetKind, string targetPath,
        WindowsFileLayoutQueryLimits? limits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        TargetKind = targetKind;
        Limits = limits ?? new WindowsFileLayoutQueryLimits();
        Limits.Validate();
        if (targetKind == WindowsFileLayoutTargetKind.File)
        {
            if (!Path.IsPathFullyQualified(targetPath) || targetPath.Length > 32_767
                || targetPath.IndexOf(':', 2) >= 0
                || targetPath.EndsWith(Path.DirectorySeparatorChar)
                || targetPath.EndsWith(Path.AltDirectorySeparatorChar))
                throw new ArgumentException("Choose one fully qualified default-stream file path, without an alternate data stream.", nameof(targetPath));
            TargetPath = targetPath;
        }
        else if (targetKind == WindowsFileLayoutTargetKind.VolumeMountPoint)
        {
            if (targetPath.Length != 3 || !char.IsAsciiLetter(targetPath[0]) || targetPath[1] != ':'
                || targetPath[2] != '\\')
                throw new ArgumentException("Choose one drive-root mount point such as C:\\.", nameof(targetPath));
            TargetPath = targetPath;
        }
        else if (targetKind == WindowsFileLayoutTargetKind.VolumeGuidPath)
        {
            if (!TryNormalizeVolumeGuidPath(targetPath, out var normalized))
                throw new ArgumentException("Choose one volume GUID path in the form \\\\?\\Volume{GUID}\\.", nameof(targetPath));
            TargetPath = normalized;
        }
        else throw new ArgumentOutOfRangeException(nameof(targetKind));
    }

    public static WindowsFileLayoutQueryScope ForFile(string absoluteFilePath, WindowsFileLayoutQueryLimits? limits = null)
        => new(WindowsFileLayoutTargetKind.File, absoluteFilePath, limits);

    /// <summary>Creates a passive query for one explicit volume GUID path, including a volume without a drive letter.</summary>
    public static WindowsFileLayoutQueryScope ForVolumeGuidPath(string volumeGuidPath, WindowsFileLayoutQueryLimits? limits = null)
        => new(WindowsFileLayoutTargetKind.VolumeGuidPath, volumeGuidPath, limits);

    /// <summary>Creates a passive query for one drive-root mount point; mounted-folder paths are not accepted.</summary>
    public static WindowsFileLayoutQueryScope ForVolumeMountPoint(string driveRoot, WindowsFileLayoutQueryLimits? limits = null)
        => new(WindowsFileLayoutTargetKind.VolumeMountPoint, driveRoot, limits);

    internal static bool TryNormalizeVolumeGuidPath(string path, out string normalized)
    {
        normalized = string.Empty;
        if (path.Length != VolumeGuidPathPrefix.Length + 36 + 2
            || !path.StartsWith(VolumeGuidPathPrefix, StringComparison.OrdinalIgnoreCase)
            || !path.EndsWith(@"}\", StringComparison.Ordinal)) return false;
        if (!Guid.TryParseExact(path.Substring(VolumeGuidPathPrefix.Length, 36), "D", out var volumeGuid)) return false;
        normalized = VolumeGuidPathPrefix + volumeGuid.ToString("D") + @"}\";
        return true;
    }
}

/// <summary>A returned full-volume allocation map. Each page retains its native LCN interval and read time.</summary>
public sealed record WindowsVolumeAllocationMap(
    string VolumeGuidPath,
    ulong VolumeSerialNumber,
    ulong TotalClusters,
    uint BytesPerCluster,
    DateTimeOffset CaptureStartedAt,
    DateTimeOffset CaptureCompletedAt,
    IReadOnlyList<WindowsVolumeBitmapPage> Pages);

public sealed record WindowsVolumeBitmapPage(
    ulong ReturnedStartingLcn,
    ulong ReportedClustersFromStart,
    ulong ReturnedClusterCount,
    string LsbFirstAllocatedBitsBase64,
    DateTimeOffset ObservedAt);

/// <summary>One typed scope result; a null selected value means the source did not prove it.</summary>
public sealed record WindowsFileLayoutReadResult(
    WindowsFileLayoutTargetKind TargetKind,
    string DeviceIdentity,
    ulong? FileExtentCount,
    WindowsVolumeAllocationMap? VolumeAllocationMap);

/// <summary>Caller-supplied producer seam; injected readers remain caller-owned.</summary>
public interface IWindowsFileLayoutInformationReader
{
    WindowsFileLayoutReadResult Read(WindowsFileLayoutQueryScope scope, WindowsFileLayoutInformationFields selected,
        CancellationToken cancellationToken);
}

/// <summary>One explicit bounded query with one current, gate and publication signal. Borrowers read committed state only.</summary>
public sealed class WindowsFileLayoutInformationQuery : IDisposable
{
    public const string InformationItemId = "information.file-layout-query";
    private static readonly JsonSerializerOptions MapJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly WindowsFileLayoutQueryScope scope;
    private readonly IWindowsFileLayoutInformationReader reader;
    private readonly DeviceInformationDefinition definition;
    private readonly string objectType;
    private readonly WindowsFileLayoutInformationFields field;
    private readonly SemaphoreSlim captureGate = new(1, 1);
    private readonly object gate = new();
    private DeviceInformationSnapshot current = DeviceInformationSnapshot.Empty;
    private int disposed;

    public SamplingPublicationSignal Publications { get; } = new();
    public InformationReadWriteBackend Backend { get; }

    public WindowsFileLayoutInformationQuery(WindowsFileLayoutQueryScope scope)
        : this(scope, new WindowsFileLayoutInformationReader()) { }

    public WindowsFileLayoutInformationQuery(WindowsFileLayoutQueryScope scope,
        IWindowsFileLayoutInformationReader reader)
    {
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
        scope.Limits.Validate();
        (definition, objectType, field) = scope.TargetKind == WindowsFileLayoutTargetKind.File
            ? (DeviceInformationFieldRegistry.FileAllExtentCountActual, "file", WindowsFileLayoutInformationFields.FileExtentCount)
            : (DeviceInformationFieldRegistry.VolumeAllFragmentationMapActual, "volume", WindowsFileLayoutInformationFields.VolumeFragmentationMap);
        Backend = new(ReadInformation, token => CaptureAsync(token),
            candidate => candidate == definition ? new(true, null, []) : OperationSupport.Unsupported("field-not-in-this-explicit-query-scope"),
            CaptureSelectedAsync);
    }

    public DeviceInformationSnapshot ReadInformation()
    {
        lock (gate) return current;
    }

    /// <summary>Explicitly reads the one selected file or volume scope; no read occurs on construction or cached access.</summary>
    public async ValueTask<DeviceInformationSnapshot> CaptureAsync(CancellationToken cancellationToken = default,
        Func<Action, bool>? tryPublish = null)
        => await CaptureCoreAsync(null, true, tryPublish, cancellationToken);

    private async ValueTask<DeviceInformationSnapshot> CaptureSelectedAsync(
        ImmutableArray<InformationFieldSelection> selection, CancellationToken cancellationToken)
    {
        ValidateSelection(selection);
        return await CaptureCoreAsync(selection[0].Field.DeviceIdentity, false, null, cancellationToken);
    }

    private async ValueTask<DeviceInformationSnapshot> CaptureCoreAsync(string? expectedIdentity, bool replaceMembership,
        Func<Action, bool>? tryPublish, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        await captureGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            WindowsFileLayoutReadResult result;
            try
            {
                result = reader.Read(scope, field, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                ValidateResult(result, expectedIdentity);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch
            {
                var knownIdentity = expectedIdentity ?? ReadKnownIdentity();
                if (knownIdentity is not null)
                {
                    var failed = new DeviceInformationSnapshot(null,
                        [new DeviceInformationObject(knownIdentity, objectType, [new(definition, null)])]);
                    _ = Commit(failed, false, tryPublish, cancellationToken);
                }
                throw;
            }

            var observedAt = DateTimeOffset.UtcNow;
            JsonElement? value;
            if (field == WindowsFileLayoutInformationFields.FileExtentCount)
                value = result.FileExtentCount is { } count
                    ? JsonSerializer.SerializeToElement(count.ToString(CultureInfo.InvariantCulture)) : null;
            else
                value = result.VolumeAllocationMap is { } map
                    ? JsonSerializer.SerializeToElement(ProjectMap(map), MapJsonOptions) : null;
            var next = new DeviceInformationSnapshot(observedAt,
                [new DeviceInformationObject(result.DeviceIdentity, objectType,
                    [new DeviceInformationField(definition, value) { ObservedAt = value is null ? null : observedAt }])]);
            cancellationToken.ThrowIfCancellationRequested();
            _ = Commit(next, replaceMembership, tryPublish, cancellationToken);
            return ReadInformation();
        }
        finally { captureGate.Release(); }
    }

    private void ValidateSelection(ImmutableArray<InformationFieldSelection> selection)
    {
        if (selection.IsDefaultOrEmpty || selection.Length != 1 || selection[0].Definition != definition
            || selection[0].Field.FieldName != definition.Name)
            throw new ArgumentException("Select the one canonical field declared by this explicit file-layout query.", nameof(selection));
    }

    private void ValidateResult(WindowsFileLayoutReadResult result, string? expectedIdentity)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.TargetKind != scope.TargetKind || string.IsNullOrWhiteSpace(result.DeviceIdentity)
            || expectedIdentity is not null && !StringComparer.Ordinal.Equals(expectedIdentity, result.DeviceIdentity))
            throw new InvalidDataException("The reader returned a foreign or changed file-layout identity.");
        if (field == WindowsFileLayoutInformationFields.FileExtentCount && result.VolumeAllocationMap is not null
            || field == WindowsFileLayoutInformationFields.VolumeFragmentationMap && result.FileExtentCount is not null)
            throw new InvalidDataException("The reader returned a value outside the selected query scope.");
        if (result.VolumeAllocationMap is { } map)
        {
            if (!IsCompleteMap(map))
                throw new InvalidDataException("The volume map does not prove a valid GUID path and complete full-volume cluster coverage.");
            if (scope.TargetKind == WindowsFileLayoutTargetKind.VolumeGuidPath
                && !StringComparer.OrdinalIgnoreCase.Equals(map.VolumeGuidPath, scope.TargetPath))
                throw new InvalidDataException("The volume map belongs to a different explicit volume GUID path.");
        }
    }

    private static bool IsCompleteMap(WindowsVolumeAllocationMap map)
    {
        if (string.IsNullOrWhiteSpace(map.VolumeGuidPath)
            || !WindowsFileLayoutQueryScope.TryNormalizeVolumeGuidPath(map.VolumeGuidPath, out _)
            || map.BytesPerCluster == 0
            || map.Pages is null || map.CaptureCompletedAt < map.CaptureStartedAt) return false;
        ulong coveredUntil = 0;
        foreach (var page in map.Pages)
        {
            if (page.ReturnedStartingLcn > coveredUntil || page.ReturnedClusterCount == 0
                || page.ReturnedStartingLcn > map.TotalClusters
                || page.ReportedClustersFromStart != map.TotalClusters - page.ReturnedStartingLcn
                || page.ReturnedClusterCount > map.TotalClusters - page.ReturnedStartingLcn) return false;
            byte[] bytes;
            try { bytes = Convert.FromBase64String(page.LsbFirstAllocatedBitsBase64); }
            catch (FormatException) { return false; }
            if (page.ObservedAt < map.CaptureStartedAt || page.ObservedAt > map.CaptureCompletedAt
                || (ulong)bytes.Length != page.ReturnedClusterCount / 8 + (page.ReturnedClusterCount % 8 == 0 ? 0UL : 1UL)) return false;
            var trailingBits = (int)(page.ReturnedClusterCount & 7);
            if (trailingBits != 0 && (bytes[^1] & ~((1 << trailingBits) - 1)) != 0) return false;
            coveredUntil = Math.Max(coveredUntil, checked(page.ReturnedStartingLcn + page.ReturnedClusterCount));
        }
        return map.TotalClusters == 0 ? map.Pages.Count == 0 : coveredUntil == map.TotalClusters;
    }

    private static object ProjectMap(WindowsVolumeAllocationMap map) => new
    {
        map.VolumeGuidPath,
        VolumeSerialNumber = map.VolumeSerialNumber.ToString(CultureInfo.InvariantCulture),
        TotalClusters = map.TotalClusters.ToString(CultureInfo.InvariantCulture),
        CoverageStartingLcn = "0",
        CoverageClusterCount = map.TotalClusters.ToString(CultureInfo.InvariantCulture),
        CoverageComplete = true,
        map.BytesPerCluster,
        map.CaptureStartedAt,
        map.CaptureCompletedAt,
        BitMeaning = "1=allocated;0=free",
        BitOrder = "least-significant-bit-first",
        Pages = map.Pages.Select(page => new
        {
            ReturnedStartingLcn = page.ReturnedStartingLcn.ToString(CultureInfo.InvariantCulture),
            ReportedClustersFromStart = page.ReportedClustersFromStart.ToString(CultureInfo.InvariantCulture),
            ReturnedClusterCount = page.ReturnedClusterCount.ToString(CultureInfo.InvariantCulture),
            page.LsbFirstAllocatedBitsBase64,
            page.ObservedAt
        })
    };

    private string? ReadKnownIdentity()
    {
        lock (gate) return current.Devices.Count == 1 ? current.Devices[0].DeviceIdentity : null;
    }

    private bool Commit(DeviceInformationSnapshot next, bool replaceMembership,
        Func<Action, bool>? tryPublish, CancellationToken cancellationToken)
    {
        var committed = false;
        void Publish()
        {
            if (cancellationToken.IsCancellationRequested) return;
            lock (gate)
            {
                if (Volatile.Read(ref disposed) != 0 || cancellationToken.IsCancellationRequested) return;
                var values = replaceMembership ? new Dictionary<FieldReference, DeviceInformationField>()
                    : current.Devices.SelectMany(device => device.Fields.Select(field => KeyValuePair.Create(
                        new FieldReference(device.DeviceIdentity, field.Definition.Name),
                        field.ObservedAt is null ? field with { ObservedAt = current.ObservedAt } : field))).ToDictionary();
                foreach (var device in next.Devices)
                foreach (var fieldValue in device.Fields)
                    values[new FieldReference(device.DeviceIdentity, fieldValue.Definition.Name)] = fieldValue with
                    { ObservedAt = fieldValue.Value is null ? null : next.ObservedAt };
                var devices = values.GroupBy(pair => pair.Key.DeviceIdentity, StringComparer.Ordinal)
                    .Select(group => new DeviceInformationObject(group.Key, objectType, group.Select(pair => pair.Value).ToArray())).ToArray();
                current = new(next.ObservedAt, devices);
                committed = true;
            }
        }
        if (tryPublish is null) Publish(); else _ = tryPublish(Publish);
        cancellationToken.ThrowIfCancellationRequested();
        if (committed) Publications.Publish([InformationItemId]);
        return committed;
    }

    public IReadWriteCollectionBorrow BorrowSubscriptionCapture()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return new InformationCollectionBorrow(Backend, Publications, InformationItemId, (selection, permit, token) =>
        {
            token.ThrowIfCancellationRequested();
            ValidateSelection(selection);
            DeviceInformationSnapshot snapshot = DeviceInformationSnapshot.Empty;
            var committed = permit.TryPublish(() =>
            {
                token.ThrowIfCancellationRequested();
                snapshot = ReadInformation();
            });
            return ValueTask.FromResult(new FieldCollectionResult(InformationReadWriteBackend.Read(selection, snapshot), committed));
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        captureGate.Wait();
        try { lock (gate) current = DeviceInformationSnapshot.Empty; }
        finally { captureGate.Release(); }
        // Reader is caller-owned when injected; this query retains no handles between captures.
    }
}

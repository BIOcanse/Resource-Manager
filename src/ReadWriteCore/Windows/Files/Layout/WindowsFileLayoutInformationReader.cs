using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ResourceManager.ReadWrite.Windows.Files.Layout;

/// <summary>Read-only Win32/FSCTL implementation. Every opened handle is scoped to one Read call.</summary>
public sealed class WindowsFileLayoutInformationReader : IWindowsFileLayoutInformationReader
{
    private const uint FsctlGetNtfsVolumeData = 0x00090064;
    private const uint FsctlGetVolumeBitmap = 0x0009006F;
    private const uint FsctlGetRetrievalPointers = 0x00090073;
    private const int ErrorInsufficientBuffer = 122;
    internal const int ErrorHandleEof = 38;
    private const int ErrorMoreData = 234;
    private const int RetrievalPointersHeaderBytes = 16;
    private const int RetrievalPointerExtentBytes = 16;
    private const int VolumeBitmapHeaderBytes = 16;
    private const int NtfsVolumeDataBytes = 96;

    public WindowsFileLayoutReadResult Read(WindowsFileLayoutQueryScope scope,
        WindowsFileLayoutInformationFields selected, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Limits.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        return scope.TargetKind switch
        {
            WindowsFileLayoutTargetKind.File when selected == WindowsFileLayoutInformationFields.FileExtentCount
                => ReadFile(scope, cancellationToken),
            WindowsFileLayoutTargetKind.VolumeMountPoint or WindowsFileLayoutTargetKind.VolumeGuidPath
                when selected == WindowsFileLayoutInformationFields.VolumeFragmentationMap
                => ReadVolume(scope, cancellationToken),
            _ => throw new ArgumentException("The selected field does not match the explicit file-layout scope.", nameof(selected))
        };
    }

    private static WindowsFileLayoutReadResult ReadFile(WindowsFileLayoutQueryScope scope, CancellationToken token)
    {
        using var handle = File.OpenHandle(scope.TargetPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
        var standardInfo = new byte[24];
        if (!ReadHandleInformation(handle, 1, standardInfo))
            throw LastError("GetFileInformationByHandleEx(FileStandardInfo)");
        // FILE_STANDARD_INFO: AllocationSize, EndOfFile, NumberOfLinks, DeletePending, Directory.
        if (standardInfo[21] != 0) throw new IOException("The selected path is a directory, not a file object.");

        var fileIdInfo = new byte[24];
        if (!ReadHandleInformation(handle, 18, fileIdInfo))
            throw LastError("GetFileInformationByHandleEx(FileIdInfo)");
        var volumeSerial = BinaryPrimitives.ReadUInt64LittleEndian(fileIdInfo);
        var fileId = Convert.ToHexString(fileIdInfo.AsSpan(8, 16));
        var identity = $"windows-file-layout|file|{volumeSerial:x16}|{fileId}";
        var extentCount = ReadExtentCount(handle, scope.Limits, token);
        token.ThrowIfCancellationRequested();
        return new(WindowsFileLayoutTargetKind.File, identity, extentCount, null);
    }

    private static ulong ReadExtentCount(SafeFileHandle handle, WindowsFileLayoutQueryLimits limits,
        CancellationToken token)
    {
        long requestedVcn = 0;
        long coveredVcn = 0;
        ulong extentCount = 0;
        long returnedBytesTotal = 0;
        var pageCount = 0;
        var outputBytes = limits.InitialPageBytes;
        var input = new byte[sizeof(long)];

        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (pageCount >= limits.MaximumPageCount)
                throw new IOException("The file extent query exceeded its configured page limit.");
            pageCount++;

            long retry = 0;
            IoResult call;
            byte[] output;
            while (true)
            {
                output = new byte[outputBytes];
                BinaryPrimitives.WriteInt64LittleEndian(input, requestedVcn);
                call = DeviceIo(handle, FsctlGetRetrievalPointers, input, output);
                if (call.Success || call.Error != ErrorInsufficientBuffer) break;
                if (retry >= limits.MaximumRetriesPerPage || outputBytes >= limits.MaximumPageBytes)
                    throw new IOException("FSCTL_GET_RETRIEVAL_POINTERS could not fit a page within the configured byte limit.");
                retry++;
                outputBytes = GrowPageBuffer(outputBytes, limits.MaximumPageBytes);
            }

            if (TryCompleteRetrievalPointerQueryOnEof(call.Success, call.Error, call.BytesReturned,
                extentCount, out var completedExtentCount))
                return completedExtentCount;
            if (!call.Success && call.Error != ErrorMoreData)
                throw NativeError("FSCTL_GET_RETRIEVAL_POINTERS", call.Error);
            if (call.BytesReturned > limits.MaximumReturnedBytes - returnedBytesTotal)
                throw new IOException("The file extent query exceeded its configured returned-byte limit.");
            returnedBytesTotal += call.BytesReturned;
            if (call.BytesReturned > output.Length)
                throw new InvalidDataException("The retrieval-pointer response exceeds its allocated page buffer.");
            if (!TryParseRetrievalPointerPage(output, checked((int)call.BytesReturned), out var page))
                throw new InvalidDataException("The returned retrieval-pointer page is truncated or malformed.");
            if (page.StartingVcn < 0 || page.StartingVcn > requestedVcn)
                throw new InvalidDataException("The retrieval-pointer page starts after the requested VCN.");

            var pageCursor = page.StartingVcn;
            var madeProgress = false;
            foreach (var extent in page.Extents)
            {
                if (extent.NextVcn <= pageCursor || extent.Lcn < -1)
                    throw new InvalidDataException("The retrieval-pointer extent has invalid cluster bounds.");
                if (extent.NextVcn <= coveredVcn)
                {
                    pageCursor = extent.NextVcn; // Driver rounded the requested VCN down; ignore an already counted run.
                    continue;
                }
                if (pageCursor < coveredVcn || pageCursor > coveredVcn)
                    throw new InvalidDataException("The retrieval-pointer pages do not provide contiguous VCN coverage.");
                if (extentCount >= (ulong)limits.MaximumExtentCount)
                    throw new IOException("The file extent query exceeded its configured extent-count limit.");
                extentCount++; // Includes LCN=-1 sparse/unallocated runs as one returned run.
                coveredVcn = extent.NextVcn;
                pageCursor = extent.NextVcn;
                madeProgress = true;
            }

            if (call.Success) return extentCount; // Successful complete response, including a valid zero-extent response.
            if (!madeProgress || coveredVcn <= requestedVcn)
                throw new InvalidDataException("The retrieval-pointer continuation made no VCN progress.");
            requestedVcn = coveredVcn;
        }
    }

    private static WindowsFileLayoutReadResult ReadVolume(WindowsFileLayoutQueryScope scope, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var volumeGuidPath = ResolveVolumeGuidPath(scope, GetVolumeGuidPath);
        // Naming-a-Volume requires omitting the trailing slash to open the volume itself.
        using var handle = File.OpenHandle(volumeGuidPath.TrimEnd('\\'), FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite, FileOptions.None);
        // The serial is read from this exact opened GUID handle; the same handle is used for every bitmap page.
        var (volumeSerial, totalClusters, bytesPerCluster) = ReadNtfsVolumeData(handle);
        if (totalClusters < 0 || bytesPerCluster == 0)
            throw new InvalidDataException("The NTFS volume-data response has invalid cluster bounds.");

        var captureStartedAt = DateTimeOffset.UtcNow;
        var pages = ReadVolumeBitmap(handle, totalClusters, scope.Limits, token);
        token.ThrowIfCancellationRequested();
        var map = new WindowsVolumeAllocationMap(volumeGuidPath, volumeSerial, checked((ulong)totalClusters), bytesPerCluster,
            captureStartedAt, DateTimeOffset.UtcNow, pages);
        var identity = $"windows-file-layout|volume|{volumeGuidPath.TrimEnd('\\')}|{volumeSerial:x16}";
        return new(scope.TargetKind, identity, null, map);
    }

    private static IReadOnlyList<WindowsVolumeBitmapPage> ReadVolumeBitmap(SafeFileHandle handle, long totalClusters,
        WindowsFileLayoutQueryLimits limits, CancellationToken token)
    {
        var pages = new List<WindowsVolumeBitmapPage>();
        if (totalClusters == 0) return pages;
        if ((ulong)totalClusters > checked((ulong)limits.MaximumReturnedBytes * 8))
            throw new IOException("The complete volume bitmap exceeds the configured returned-byte bound.");

        long requestedLcn = 0;
        long coveredUntil = 0;
        long returnedBytesTotal = 0;
        var pageCount = 0;
        var outputBytes = limits.InitialPageBytes;
        var input = new byte[sizeof(long)];
        while (coveredUntil < totalClusters)
        {
            token.ThrowIfCancellationRequested();
            if (pageCount >= limits.MaximumPageCount)
                throw new IOException("The volume bitmap exceeded its configured page limit.");
            pageCount++;
            long retry = 0;
            IoResult call;
            byte[] output;
            while (true)
            {
                output = new byte[outputBytes];
                BinaryPrimitives.WriteInt64LittleEndian(input, requestedLcn);
                call = DeviceIo(handle, FsctlGetVolumeBitmap, input, output);
                if (call.Success || call.Error != ErrorInsufficientBuffer) break;
                if (retry >= limits.MaximumRetriesPerPage || outputBytes >= limits.MaximumPageBytes)
                    throw new IOException("FSCTL_GET_VOLUME_BITMAP could not fit a page within the configured byte limit.");
                retry++;
                outputBytes = GrowPageBuffer(outputBytes, limits.MaximumPageBytes);
            }
            if (!call.Success && call.Error != ErrorMoreData)
                throw NativeError("FSCTL_GET_VOLUME_BITMAP", call.Error);
            if (call.BytesReturned > limits.MaximumReturnedBytes - returnedBytesTotal)
                throw new IOException("The volume bitmap exceeded its configured returned-byte limit.");
            returnedBytesTotal += call.BytesReturned;
            if (call.BytesReturned > output.Length)
                throw new InvalidDataException("The volume-bitmap response exceeds its allocated page buffer.");
            if (!TryParseVolumeBitmapPage(output, checked((int)call.BytesReturned), out var page))
                throw new InvalidDataException("The returned volume-bitmap page is truncated or malformed.");
            if (page.StartingLcn > requestedLcn || page.StartingLcn > totalClusters
                || page.ReportedClustersFromStart != totalClusters - page.StartingLcn)
                throw new InvalidDataException("The volume bitmap page does not match the NTFS volume cluster extent.");
            if (page.ReturnedClusterCount == 0)
                throw new InvalidDataException("The volume bitmap returned no bits before the volume end.");
            var pageEnd = checked(page.StartingLcn + checked((long)page.ReturnedClusterCount));
            if (page.StartingLcn > coveredUntil || pageEnd <= coveredUntil)
                throw new InvalidDataException("The volume bitmap pages have a gap or made no forward progress.");

            pages.Add(new(checked((ulong)page.StartingLcn), checked((ulong)page.ReportedClustersFromStart),
                page.ReturnedClusterCount, Convert.ToBase64String(page.Bits), DateTimeOffset.UtcNow));
            coveredUntil = pageEnd;
            requestedLcn = pageEnd;
        }
        return pages;
    }

    private static (ulong SerialNumber, long TotalClusters, uint BytesPerCluster) ReadNtfsVolumeData(SafeFileHandle handle)
    {
        var output = new byte[NtfsVolumeDataBytes];
        var call = DeviceIo(handle, FsctlGetNtfsVolumeData, ReadOnlySpan<byte>.Empty, output);
        if (!call.Success) throw NativeError("FSCTL_GET_NTFS_VOLUME_DATA", call.Error);
        if (call.BytesReturned > output.Length)
            throw new InvalidDataException("FSCTL_GET_NTFS_VOLUME_DATA returned more bytes than its allocated SDK buffer.");
        if (!TryParseNtfsVolumeData(output, checked((int)call.BytesReturned), out var fields))
            throw new InvalidDataException("FSCTL_GET_NTFS_VOLUME_DATA returned a truncated SDK structure.");
        return (fields.VolumeSerialNumber, fields.TotalClusters, fields.BytesPerCluster);
    }

    private static string GetVolumeGuidPath(string mountPoint)
    {
        var output = new StringBuilder(50);
        if (!GetVolumeNameForVolumeMountPointW(mountPoint, output, (uint)output.Capacity))
            throw LastError("GetVolumeNameForVolumeMountPointW");
        if (!WindowsFileLayoutQueryScope.TryNormalizeVolumeGuidPath(output.ToString(), out var value))
            throw new InvalidDataException("Windows returned an unexpected volume GUID path.");
        return value;
    }

    internal static string ResolveVolumeGuidPath(WindowsFileLayoutQueryScope scope,
        Func<string, string> resolveMountPoint)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(resolveMountPoint);
        return scope.TargetKind switch
        {
            WindowsFileLayoutTargetKind.VolumeGuidPath => scope.TargetPath,
            WindowsFileLayoutTargetKind.VolumeMountPoint => resolveMountPoint(scope.TargetPath),
            _ => throw new ArgumentException("The scope does not select a volume.", nameof(scope))
        };
    }

    private static unsafe bool ReadHandleInformation(SafeFileHandle handle, int informationClass, byte[] output)
    {
        fixed (byte* pointer = output)
            return GetFileInformationByHandleEx(handle, informationClass, pointer, (uint)output.Length);
    }

    private static int GrowPageBuffer(int currentBytes, int maximumBytes)
        => currentBytes > maximumBytes - currentBytes ? maximumBytes : currentBytes * 2;

    internal static bool TryCompleteRetrievalPointerQueryOnEof(bool succeeded, int error, uint bytesReturned,
        ulong extentCount, out ulong completedExtentCount)
    {
        completedExtentCount = 0;
        if (succeeded || error != ErrorHandleEof) return false;
        if (bytesReturned != 0)
            throw new InvalidDataException("FSCTL_GET_RETRIEVAL_POINTERS returned bytes with its end-of-file status.");
        completedExtentCount = extentCount;
        return true;
    }

    internal static bool TryParseRetrievalPointerPage(ReadOnlySpan<byte> output, int bytesReturned,
        out RetrievalPointerPage page)
    {
        page = default;
        if (bytesReturned < RetrievalPointersHeaderBytes || bytesReturned > output.Length) return false;
        var extentCount = BinaryPrimitives.ReadUInt32LittleEndian(output);
        var startingVcn = BinaryPrimitives.ReadInt64LittleEndian(output[8..]);
        var availableExtents = (bytesReturned - RetrievalPointersHeaderBytes) / RetrievalPointerExtentBytes;
        if (extentCount > availableExtents || extentCount > int.MaxValue || startingVcn < 0) return false;
        var extents = ImmutableArray.CreateBuilder<RetrievalPointerExtent>((int)extentCount);
        var cursor = startingVcn;
        for (var index = 0; index < extentCount; index++)
        {
            var offset = RetrievalPointersHeaderBytes + index * RetrievalPointerExtentBytes;
            var nextVcn = BinaryPrimitives.ReadInt64LittleEndian(output[offset..]);
            var lcn = BinaryPrimitives.ReadInt64LittleEndian(output[(offset + 8)..]);
            if (nextVcn <= cursor || lcn < -1) return false;
            extents.Add(new(nextVcn, lcn));
            cursor = nextVcn;
        }
        page = new(startingVcn, extents.MoveToImmutable());
        return true;
    }

    internal static bool TryParseVolumeBitmapPage(ReadOnlySpan<byte> output, int bytesReturned,
        out VolumeBitmapPage page)
    {
        page = default;
        if (bytesReturned < VolumeBitmapHeaderBytes || bytesReturned > output.Length) return false;
        var startingLcn = BinaryPrimitives.ReadInt64LittleEndian(output);
        var clustersFromStart = BinaryPrimitives.ReadInt64LittleEndian(output[8..]);
        if (startingLcn < 0 || clustersFromStart < 0) return false;
        var availableBytes = bytesReturned - VolumeBitmapHeaderBytes;
        var returnedClusters = Math.Min(checked((long)availableBytes * 8), clustersFromStart);
        if (returnedClusters < 0) return false;
        var returnedBytes = checked((int)((returnedClusters + 7) / 8));
        var bits = output.Slice(VolumeBitmapHeaderBytes, returnedBytes).ToArray();
        var finalBitCount = (int)(returnedClusters & 7);
        if (finalBitCount is > 0 and < 8 && bits.Length > 0)
            bits[^1] &= (byte)((1 << finalBitCount) - 1);
        page = new(startingLcn, clustersFromStart, checked((ulong)returnedClusters), bits);
        return true;
    }

    internal static bool TryParseNtfsVolumeData(ReadOnlySpan<byte> output, int bytesReturned,
        out NtfsVolumeDataFields fields)
    {
        fields = default;
        if (bytesReturned < NtfsVolumeDataBytes || bytesReturned > output.Length) return false;
        var totalClusters = BinaryPrimitives.ReadInt64LittleEndian(output[16..]);
        var bytesPerCluster = BinaryPrimitives.ReadUInt32LittleEndian(output[44..]);
        if (totalClusters < 0 || bytesPerCluster == 0) return false;
        fields = new(BinaryPrimitives.ReadUInt64LittleEndian(output), totalClusters, bytesPerCluster);
        return true;
    }

    private static unsafe IoResult DeviceIo(SafeFileHandle handle, uint code, ReadOnlySpan<byte> input, byte[] output)
    {
        fixed (byte* inputPointer = input)
        fixed (byte* outputPointer = output)
        {
            var succeeded = DeviceIoControl(handle, code, input.IsEmpty ? null : inputPointer,
                checked((uint)input.Length), outputPointer, checked((uint)output.Length), out var bytesReturned, IntPtr.Zero);
            return new(succeeded, succeeded ? 0 : Marshal.GetLastWin32Error(), bytesReturned);
        }
    }

    private static IOException LastError(string operation) => NativeError(operation, Marshal.GetLastWin32Error());
    private static IOException NativeError(string operation, int error)
        => new($"{operation} failed with Win32 error {error}.", new System.ComponentModel.Win32Exception(error));

    private readonly record struct IoResult(bool Success, int Error, uint BytesReturned);
    internal readonly record struct RetrievalPointerExtent(long NextVcn, long Lcn);
    internal readonly record struct RetrievalPointerPage(long StartingVcn, ImmutableArray<RetrievalPointerExtent> Extents);
    internal readonly record struct VolumeBitmapPage(long StartingLcn, long ReportedClustersFromStart,
        ulong ReturnedClusterCount, byte[] Bits);
    internal readonly record struct NtfsVolumeDataFields(ulong VolumeSerialNumber, long TotalClusters, uint BytesPerCluster);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool DeviceIoControl(SafeFileHandle handle, uint controlCode,
        byte* inputBuffer, uint inputBufferSize, byte* outputBuffer, uint outputBufferSize,
        out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        void* fileInformation, uint bufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string volumeMountPoint,
        StringBuilder volumeName, uint bufferLength);
}

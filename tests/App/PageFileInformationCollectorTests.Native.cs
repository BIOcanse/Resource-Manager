using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Domain.DeviceTopology;
using ResourceManager.App.Infrastructure.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class PageFileInformationCollectorTests
{
    [Fact]
    public void ActualSdkLayoutsAndCurrentNativeCallbackPreserveBothSizeTArchitectures()
    {
        using var fixture = SdkFixture();
        foreach (var arch in new[] { "x86", "x64" })
        {
            var sdk = fixture.RootElement.GetProperty(arch); var width = sdk.GetProperty("pointerSize").GetInt32();
            var frame = Convert.FromHexString(sdk.GetProperty("frameHex").GetString()!);
            Assert.Equal(width == 8 ? 32 : 20, sdk.GetProperty("pageFileSize").GetInt32());
            Assert.Equal((uint)frame.Length, BinaryPrimitives.ReadUInt32LittleEndian(frame));
            Assert.Equal(123456789UL, Unsigned(frame, sdk.GetProperty("totalOffset").GetInt32(), width));
            Assert.Equal(12345UL, Unsigned(frame, sdk.GetProperty("usedOffset").GetInt32(), width));
            Assert.Equal(23456UL, Unsigned(frame, sdk.GetProperty("peakOffset").GetInt32(), width));
        }
        var current = fixture.RootElement.GetProperty(IntPtr.Size == 8 ? "x64" : "x86");
        Assert.Equal(current.GetProperty("pageFileSize").GetInt32(), Marshal.SizeOf<WindowsPageFileNativeData>());
        Assert.Equal(current.GetProperty("usedOffset").GetInt32(), Marshal.OffsetOf<WindowsPageFileNativeData>(nameof(WindowsPageFileNativeData.TotalInUse)).ToInt32());
        Assert.Equal(current.GetProperty("peakOffset").GetInt32(), Marshal.OffsetOf<WindowsPageFileNativeData>(nameof(WindowsPageFileNativeData.PeakUsage)).ToInt32());
        Assert.Equal(current.GetProperty("systemInfoSize").GetInt32(), Marshal.SizeOf<WindowsPageFileSystemInfo>());
        Assert.Equal(current.GetProperty("pageSizeOffset").GetInt32(), Marshal.OffsetOf<WindowsPageFileSystemInfo>(nameof(WindowsPageFileSystemInfo.PageSize)).ToInt32());
        var calls = new Calls { Frames = [Convert.FromHexString(current.GetProperty("frameHex").GetString()!)] };
        var file = Assert.Single(new WindowsPageFileInformationReader(new WindowsPageFileInformationNativeApi(calls)).Read()!);
        Assert.Equal(123456789UL, file.CapacityPages); Assert.Equal(12345UL, file.UsedPages); Assert.Equal(23456UL, file.PeakUsedPages);
        Assert.Equal(PathA, file.PathReported); Assert.Equal(IdA, file.DeviceIdentity); Assert.Equal(8192U, file.PageSizeBytes);
        Assert.Equal(1, calls.SizeCalls); Assert.Equal(1, calls.Enumerations);
    }

    [Fact]
    public void NativeReadDoesNotNarrowLargeCountersOrInterpretReservedBits()
    {
        var frame = NativeFrame(ulong.MaxValue, 9007199254740993UL, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), uint.MaxValue);
        var file = Assert.Single(Read(new Calls { Frames = [frame] })!);
        Assert.Equal((ulong)unchecked((nuint)ulong.MaxValue), file.CapacityPages);
        Assert.Equal((ulong)unchecked((nuint)9007199254740993UL), file.UsedPages); Assert.Equal(0UL, file.PeakUsedPages);
    }

    [Theory]
    [InlineData(0U)] [InlineData(4U)] [InlineData(8U)] [InlineData(19U)]
    public void UnsupportedCallbackSizeRetainsIdentityAndIndependentUnitButNoDirtyCounters(uint size)
    {
        var frame = NativeFrame(); BinaryPrimitives.WriteUInt32LittleEndian(frame, size);
        var files = Read(new Calls { Names = [PathA, PathB], Frames = [frame, NativeFrame()] })!;
        Assert.Null(files[0].CapacityPages); Assert.Null(files[0].UsedPages); Assert.Null(files[0].PeakUsedPages);
        Assert.Equal(8192U, files[0].PageSizeBytes); Assert.Equal(PathA, files[0].PathReported);
        Assert.Equal(1000UL, files[1].CapacityPages);
    }

    [Fact]
    public void CompleteFutureStructurePrefixAndNullInformationAreHandledIndependently()
    {
        var frame = NativeFrame(); BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)frame.Length + 16);
        Array.Resize(ref frame, frame.Length + 16);
        var calls = new Calls { Names = [PathA, PathB], Frames = [frame, null] };
        var files = Read(calls)!;
        Assert.Equal(1000UL, files[0].CapacityPages); Assert.Null(files[1].UsedPages);
        Assert.Equal(IdB, files[1].DeviceIdentity); Assert.Equal(8192U, files[1].PageSizeBytes);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public void PageSizeFailureNeverHidesRawPagesOrAssumesFourK(int failure)
    {
        var calls = new Calls { PageBytes = 0, SizeError = failure switch { 1 => new EntryPointNotFoundException(),
            2 => new System.ComponentModel.Win32Exception(5), _ => null } };
        var file = Assert.Single(Read(calls)!);
        Assert.Null(file.PageSizeBytes); Assert.Equal(200UL, file.UsedPages);
        Assert.Equal(1, calls.SizeCalls); Assert.Equal(1, calls.Enumerations);
    }

    [Fact]
    public void FailedEnumerationRejectsItsPartialCallbacksWhileSuccessfulEmptyConfirmsNoFiles()
    {
        var calls = new Calls { Complete = false }; Assert.Null(Read(calls)); Assert.Equal(1, calls.CallbackCount);
        calls.Names = []; calls.Complete = true; Assert.Empty(Read(calls)!);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void KnownNativeEnumerationFailureBecomesUnavailableWithoutEscapingReader(int failure)
    {
        var calls = new Calls { EnumError = failure switch { 0 => new EntryPointNotFoundException(),
            1 => new UnauthorizedAccessException(), 2 => new IOException(), _ => new BadImageFormatException() } };
        Assert.Null(Read(calls)); Assert.Equal(1, calls.Enumerations);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void InvalidOrIncompleteFilenameRejectsDirectoryEvenIfNativeAbortReportsSuccess(int failure)
    {
        var calls = new Calls { Names = failure switch { 0 => [null], 1 => [""],
            2 => [new string('X', WindowsPageFileInformationReader.MaximumPathCharacters + 1)], _ => [PathA] },
            Unterminated = failure == 3 };
        Assert.Null(Read(calls)); Assert.False(calls.LastCallbackResult);
    }

    [Fact]
    public void BoundedDirectoryAndExactMaximumFilenameAreAcceptedButExcessOrDuplicateIsNotPartial()
    {
        var calls = new Calls { Names = [new string('X', WindowsPageFileInformationReader.MaximumPathCharacters)] };
        Assert.Single(Read(calls)!);
        calls.Names = Enumerable.Range(0, WindowsPageFileInformationReader.MaximumFiles).Select(index => "file" + index).ToArray();
        Assert.Equal(WindowsPageFileInformationReader.MaximumFiles, Read(calls)!.Count);
        calls.Names = [.. calls.Names, "extra"]; Assert.Null(Read(calls)); Assert.False(calls.LastCallbackResult);
        calls.Names = [PathA, PathA]; Assert.Null(Read(calls));
        calls.Names = [PathA, PathA.ToUpperInvariant()]; Assert.Equal(2, Read(calls)!.Count);
    }

    internal static IReadOnlyList<DeviceTopologyPageFileInformation>? Read(Calls calls)
        => new WindowsPageFileInformationReader(new WindowsPageFileInformationNativeApi(calls)).Read();
    private static ulong Unsigned(ReadOnlySpan<byte> bytes, int offset, int width)
        => width == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]) : BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    internal static byte[] NativeFrame(ulong total = 1000, ulong used = 200, ulong peak = 300)
    {
        var frame = new byte[Marshal.SizeOf<WindowsPageFileNativeData>()];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)frame.Length);
        foreach (var (offset, value) in new[] { (8, total), (8 + IntPtr.Size, used), (8 + 2 * IntPtr.Size, peak) })
        {
            if (IntPtr.Size == 8) BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(offset), value);
            else BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(offset), (uint)value);
        }
        return frame;
    }
    private static JsonDocument SdkFixture()
    {
        var assembly = typeof(PageFileInformationCollectorTests).Assembly;
        var name = Assert.Single(assembly.GetManifestResourceNames(), item => item.EndsWith(".Fixtures.Memory.windows-sdk-pagefile.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!; return JsonDocument.Parse(stream);
    }

    internal sealed class Calls : IWindowsPageFileNativeCalls
    {
        internal string?[] Names = [PathA];
        internal byte[]?[] Frames = [NativeFrame()];
        internal uint PageBytes = 8192;
        internal bool Complete = true, Unterminated;
        internal Exception? SizeError, EnumError;
        internal int SizeCalls, Enumerations, CallbackCount;
        internal bool LastCallbackResult;
        public uint PageSize() { SizeCalls++; if (SizeError is not null) throw SizeError; return PageBytes; }
        public bool Enumerate(WindowsPageFileCallback callback)
        {
            Enumerations++; if (EnumError is not null) throw EnumError;
            for (var index = 0; index < Names.Length; index++)
            {
                var frame = Frames[Math.Min(index, Frames.Length - 1)];
                var information = frame is null ? IntPtr.Zero : Marshal.AllocHGlobal(frame.Length);
                var name = Names[index]; var filename = IntPtr.Zero;
                try
                {
                    if (frame is not null) Marshal.Copy(frame, 0, information, frame.Length);
                    if (Unterminated)
                    {
                        filename = Marshal.AllocHGlobal((WindowsPageFileInformationReader.MaximumPathCharacters + 1) * 2);
                        for (var c = 0; c <= WindowsPageFileInformationReader.MaximumPathCharacters; c++) Marshal.WriteInt16(filename, c * 2, 'X');
                    }
                    else if (name is not null) filename = Marshal.StringToHGlobalUni(name);
                    CallbackCount++; LastCallbackResult = callback(IntPtr.Zero, information, filename);
                    if (!LastCallbackResult) break;
                }
                finally { if (information != IntPtr.Zero) Marshal.FreeHGlobal(information); if (filename != IntPtr.Zero) Marshal.FreeHGlobal(filename); }
            }
            return Complete;
        }
    }
}

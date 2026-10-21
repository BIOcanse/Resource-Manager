using Microsoft.Win32.SafeHandles;
using ResourceManager.App.Infrastructure.DeviceTopology;

namespace Resource_Manager_APP.Tests;

public sealed partial class DiskAlignmentInformationCollectorTests
{
    [Fact]
    public void AlignmentQueryUsesSdkInputOnSameReadOnlyLayoutHandleInsideNumberBrackets()
    {
        var calls = new Calls(); var result = new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA)!;
        Assert.Equal(new[] { "number", "layout", "layout", "alignment", "identity", "number" }, calls.Order);
        Assert.Equal(new[] { 28 }, calls.AlignmentSizes);
        Assert.Equal(Disk().AccessAlignment, result.AccessAlignment); Assert.NotNull(result.Layout);
        Assert.Equal(2U, result.DiskNumber); Assert.True(calls.Handle!.IsClosed);
        Assert.Equal(DiskLayoutInformationCollectorTests.DiskA.DevicePath, calls.OpenedPath);
        var reader = new WindowsDiskLayoutInformationReader(new Api(calls)); var disk = Assert.Single(reader.Read()!);
        Assert.Equal(DiskLayoutInformationCollectorTests.IdA, disk.DeviceIdentity);
        Assert.Equal(Disk().AccessAlignment, disk.AccessAlignment);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void FailedOrDirtyGeometryKeepsOnlyVerifiedLayoutAndNumber(int failure)
    {
        var calls = new Calls { AlignmentFailure = failure };
        var result = new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA)!;
        Assert.Null(result.AccessAlignment); Assert.NotNull(result.Layout); Assert.Equal(2U, result.DiskNumber);
        Assert.Equal(2, calls.NumberReads); Assert.True(calls.Handle!.IsClosed);
    }

    [Fact]
    public void FailedLayoutDoesNotHideGeometryAndChangedNumberHidesBoth()
    {
        var calls = new Calls { LayoutFailure = true };
        var result = new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA)!;
        Assert.Null(result.Layout); Assert.Equal(Disk().AccessAlignment, result.AccessAlignment); Assert.True(calls.Handle!.IsClosed);
        calls = new Calls { ChangedNumber = true };
        Assert.Null(new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA));
        Assert.Contains("alignment", calls.Order); Assert.True(calls.Handle!.IsClosed);
        calls = new Calls { InvalidOpen = true };
        Assert.Null(new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA));
        Assert.Empty(calls.Order); Assert.True(calls.Handle!.IsClosed);
    }

    [Fact]
    public void GrowthAcceptsOnlyCompleteSuccessAndCannotAllocateDirtyFailureSize()
    {
        var calls = new Calls { Grow = true };
        var result = new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA)!;
        Assert.Equal(new[] { 28, 56 }, calls.AlignmentSizes); Assert.Equal(Disk().AccessAlignment, result.AccessAlignment);
        calls = new Calls { InfiniteGrowth = true };
        result = new WindowsDiskLayoutInformationNativeApi(calls).Read(DiskLayoutInformationCollectorTests.DiskA)!;
        Assert.Null(result.AccessAlignment); Assert.NotNull(result.Layout);
        Assert.Equal(WindowsDiskLayoutInformationNativeApi.MaximumAlignmentAttempts, calls.AlignmentSizes.Count);
        Assert.Equal(WindowsDiskAccessAlignmentDecoder.MaximumBytes, calls.AlignmentSizes[^1]);
        Assert.All(calls.AlignmentSizes, size => Assert.InRange(size, 28, WindowsDiskAccessAlignmentDecoder.MaximumBytes));
        Assert.True(calls.Handle!.IsClosed);
    }

    private sealed class Api(Calls calls) : IWindowsDiskLayoutInformationApi
    {
        public IReadOnlyList<WindowsDiskInterface>? Enumerate() => [DiskLayoutInformationCollectorTests.DiskA];
        public WindowsDiskLayoutCapture? Read(WindowsDiskInterface device) => new WindowsDiskLayoutInformationNativeApi(calls).Read(device);
    }

    private sealed class Calls : IWindowsDiskLayoutNativeCalls
    {
        internal SafeFileHandle? Handle;
        internal string? OpenedPath;
        internal List<string> Order = [];
        internal List<int> AlignmentSizes = [];
        internal int NumberReads, AlignmentFailure = -1;
        internal bool LayoutFailure, ChangedNumber, InvalidOpen, Grow, InfiniteGrowth;
        private int error = 122;
        public SafeFileHandle Open(string path) { NumberReads = 0; OpenedPath = path; return Handle = new(new IntPtr(InvalidOpen ? -1 : 42), false); }
        public bool Number(SafeFileHandle handle, byte[] buffer, out uint returned)
        {
            Check(handle); NumberReads++; Order.Add("number");
            DiskLayoutInformationCollectorTests.NumberFrame(ChangedNumber && NumberReads == 2 ? 3U : 2U).CopyTo(buffer, 0);
            returned = 12; return true;
        }
        public bool Layout(SafeFileHandle handle, byte[] buffer, out uint returned)
        {
            Check(handle); Order.Add("layout");
            if (LayoutFailure) { returned = 0; error = 5; throw new IOException("layout failed"); }
            var bytes = DiskLayoutInformationCollectorTests.Frame("gpt");
            Array.Copy(bytes, buffer, Math.Min(bytes.Length, buffer.Length)); returned = (uint)Math.Min(bytes.Length, buffer.Length);
            error = 122; return buffer.Length >= bytes.Length;
        }
        public bool AccessAlignment(SafeFileHandle handle, byte[] query, byte[] buffer, out uint returned)
        {
            Check(handle); Assert.Equal(Frame("query"), query); Order.Add("alignment"); AlignmentSizes.Add(buffer.Length);
            Frame().CopyTo(buffer, 0); returned = 28;
            if (AlignmentFailure == 0) { error = 50; return false; }
            if (AlignmentFailure == 1) { returned = (uint)buffer.Length + 1; return true; }
            if (AlignmentFailure == 2) { returned = 27; return true; }
            if (AlignmentFailure == 3) { Array.Fill(buffer, (byte)0xff); return true; }
            if (AlignmentFailure == 4) throw new IOException("alignment failed");
            if (AlignmentFailure == 5) { error = 5; return false; }
            if (InfiniteGrowth || Grow && buffer.Length < 56)
            { Array.Fill(buffer, (byte)0xff); returned = 0; error = 234; return false; }
            if (Grow) { System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 56); returned = 56; }
            return true;
        }
        public int LastError() => error;
        public bool DeviceIdentity(SafeFileHandle handle, byte[] query, byte[] buffer, out uint returned)
        { Check(handle); Order.Add("identity"); returned = 0; error = 50; return false; }
        private void Check(SafeFileHandle handle) { Assert.Same(Handle, handle); Assert.False(handle.IsClosed); Assert.False(handle.IsInvalid); }
    }
}

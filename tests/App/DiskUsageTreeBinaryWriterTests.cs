using System.Buffers.Binary;
using System.Text;
using ResourceManager.App.Domain.DiskUsage;
using ResourceManager.App.Endpoints;

namespace Resource_Manager_APP.Tests;

public sealed class DiskUsageTreeBinaryWriterTests
{
    [Fact]
    public void EmptyValueIsExactlyTheVersionThreeHeader()
    {
        var bytes = DiskUsageTreeBinaryWriter.WriteEmpty();
        Assert.Equal(64, bytes.Length);
        Assert.Equal(0x5544_4D52u, U32(bytes, 0));
        Assert.Equal(3u, U32(bytes, 4));
        Assert.Equal(0u, U32(bytes, 8));
        Assert.Equal(-1, I32(bytes, 12));
        Assert.Equal(-1, I32(bytes, 16));
        Assert.All(bytes.Skip(20), value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void CompleteSelectedSubtreeContainsScanFactsIncludingEmptyEntries()
    {
        var builder = new DiskUsageTreeBuilder();
        var unrelated = builder.Add(-1, "C:\\", true, 0, 0);
        _ = builder.Add(unrelated, "private.txt", false, 9000, 12000);
        var root = builder.Add(-1, "D:\\", true, 10, 10);
        var directory = builder.Add(root, "资料📁", true, 20, 20);
        var nested = builder.Add(directory, "a.txt", false, 300, 4096);
        var emptyFile = builder.Add(root, "空.txt", false, 0, 0);
        var emptyDirectory = builder.Add(root, "empty", true, 0, 0);
        var tiny = builder.Add(directory, "tiny", false, 1, 4096);
        var tree = builder.Build();
        var bytes = Write(tree, root);
        var facts = Decode(bytes);

        Assert.Equal(3u, U32(bytes, 4));
        Assert.Equal(root, I32(bytes, 12));
        Assert.Equal(-1, facts.NextCursor);
        Assert.Equal(331d, BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(24)));
        Assert.All(bytes.Skip(20).Take(4).Concat(bytes.Skip(32).Take(28)), value => Assert.Equal((byte)0, value));
        Assert.Equal("D:\\", facts.RootPath);
        Assert.Equal(new[] { root, directory, nested, tiny, emptyFile, emptyDirectory }, facts.NodeIds);
        Assert.Equal(new[] { -1, root, directory, directory, root, root }, facts.ParentIds);
        Assert.Equal(new double[] { 331, 321, 300, 1, 0, 0 }, facts.Sizes);
        Assert.Equal(new double[] { 321, 301, 0, 0, 0, 0 }, facts.ChildSizeTotals);
        Assert.Equal(new[] { 3, 2, 1, 1, 1, 0 }, facts.FileCounts);
        Assert.Equal(new byte[] { 1, 1, 0, 0, 0, 1 }, facts.DirectoryFlags);
        Assert.Equal(new[] { "D:\\", "资料📁", "a.txt", "tiny", "空.txt", "empty" }, facts.Names);
        Assert.Equal(facts.NodeIds.Select(tree.FirstChildOf), facts.FirstChildIds);
        Assert.Equal(facts.NodeIds.Select(tree.NextSiblingOf), facts.NextSiblingIds);
        AssertParentBeforeChild(tree, facts);
    }

    [Fact]
    public void SelectingASubtreePreservesItsOriginalParentAndNodeIds()
    {
        var builder = new DiskUsageTreeBuilder();
        var root = builder.Add(-1, "C:\\", true, 0, 0);
        var selected = builder.Add(root, "selected", true, 0, 0);
        var file = builder.Add(selected, "file", false, 7, 8);
        _ = builder.Add(root, "outside", false, 99, 99);
        var tree = builder.Build();
        var facts = Decode(Write(tree, selected));
        Assert.Equal(new[] { selected, file }, facts.NodeIds);
        Assert.Equal(new[] { root, selected }, facts.ParentIds);
        Assert.Equal(Path.Combine("C:\\", "selected"), facts.RootPath);
    }

    [Fact]
    public void LongCjkNamesAreConsumableAcrossByteBoundedPagesWithAncestryContext()
    {
        var builder = new DiskUsageTreeBuilder();
        var root = builder.Add(-1, "D:\\", true, 0, 0);
        var directory = builder.Add(root, "目录", true, 0, 0);
        var name = new string('界', 230) + "📁";
        for (var i = 0; i < 40; i++) builder.Add(directory, name + i, false, i, i);
        _ = builder.Add(root, "outside-directory", false, 9, 9);
        var tree = builder.Build();
        var consumed = new List<int>();
        int? cursor = null;
        var pages = 0;
        do
        {
            var bytes = DiskUsageTreeBinaryWriter.Write(tree, root, 8, 1024, cursor);
            Assert.InRange(bytes.Length, 64, 1024);
            var page = Decode(bytes);
            AssertParentBeforeChild(tree, page);
            var start = cursor.HasValue ? Array.IndexOf(page.NodeIds, cursor.Value) : 0;
            Assert.True(start >= 0);
            if (cursor.HasValue && cursor <= 41)
                Assert.Equal(new[] { root, directory }, page.NodeIds.Take(start));
            consumed.AddRange(page.NodeIds.Skip(start));
            Assert.All(page.Names.Where(n => n.StartsWith('界')), n => Assert.StartsWith(name, n));
            cursor = page.NextCursor < 0 ? null : page.NextCursor;
            Assert.True(++pages <= tree.Count);
        } while (cursor.HasValue);
        Assert.True(pages > 1);
        Assert.Equal(Enumerable.Range(0, tree.Count), consumed);
    }

    [Fact]
    public void NodeBudgetReturnsFirstUnreturnedDfsNodeAndRejectsInsufficientCursorContext()
    {
        var builder = new DiskUsageTreeBuilder();
        var root = builder.Add(-1, "C:\\", true, 0, 0);
        var directory = builder.Add(root, "d", true, 0, 0);
        var nested = builder.Add(directory, "f", false, 1, 1);
        var sibling = builder.Add(root, "s", false, 1, 1);
        var tree = builder.Build();
        var first = Decode(DiskUsageTreeBinaryWriter.Write(tree, root, 2, 1024));
        Assert.Equal(new[] { root, directory }, first.NodeIds);
        Assert.Equal(nested, first.NextCursor);
        Assert.Throws<ArgumentException>(() => DiskUsageTreeBinaryWriter.Write(tree, root, 2, 1024, nested));
        var second = Decode(DiskUsageTreeBinaryWriter.Write(tree, root, 3, 1024, nested));
        Assert.Equal(new[] { root, directory, nested }, second.NodeIds);
        Assert.Equal(sibling, second.NextCursor);
        var third = Decode(DiskUsageTreeBinaryWriter.Write(tree, root, 2, 1024, sibling));
        Assert.Equal(new[] { root, sibling }, third.NodeIds);
        Assert.Equal(-1, third.NextCursor);
    }

    [Fact]
    public void OversizedRecordAndQueryPathRejectWithoutAllocatingTheWholeTree()
    {
        var builder = new DiskUsageTreeBuilder();
        var root = builder.Add(-1, "C:\\", true, 0, 0);
        var large = builder.Add(root, new string('界', 400), false, 1, 1);
        var tree = builder.Build();
        var first = Decode(DiskUsageTreeBinaryWriter.Write(tree, root, 2, 1024));
        Assert.Equal(large, first.NextCursor);
        Assert.Throws<ArgumentException>(() => DiskUsageTreeBinaryWriter.Write(tree, root, 2, 1024, large));
        Assert.Throws<ArgumentException>(() => DiskUsageTreeBinaryWriter.Write(tree, large, 2, 1024));
    }

    [Fact]
    public void PageBytesAndScratchAllocationDoNotGrowWithWholeInput()
    {
        static DiskUsageTree Tree(int count)
        {
            var builder = new DiskUsageTreeBuilder(count + 1);
            var root = builder.Add(-1, "C:\\", true, 0, 0);
            for (var i = 0; i < count; i++) builder.Add(root, "file", false, 1, 1);
            return builder.Build();
        }
        var small = Tree(20);
        var large = Tree(20_000);
        _ = DiskUsageTreeBinaryWriter.Write(large, 0, 8, 1024);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var largeBytes = DiskUsageTreeBinaryWriter.Write(large, 0, 8, 1024);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(DiskUsageTreeBinaryWriter.Write(small, 0, 8, 1024).Length, largeBytes.Length);
        Assert.True(allocated < 16_384, $"Page allocated {allocated} bytes.");
        Assert.Equal(8, Decode(largeBytes).NextCursor);
    }

    [Fact]
    public void DeepZeroSizeSubtreeIsCompleteWithoutRecursiveTraversal()
    {
        const int count = 16_384;
        var builder = new DiskUsageTreeBuilder();
        var parent = -1;
        for (var index = 0; index < count; index++) parent = builder.Add(parent, "d", true, 0, 0);
        var facts = Decode(Write(builder.Build(), 0));
        Assert.Equal(Enumerable.Range(0, count), facts.NodeIds);
        Assert.All(facts.Sizes, size => Assert.Equal(0d, size));
        Assert.Equal(parent, facts.NodeIds[^1]);
    }

    [Theory]
    [InlineData(-1, 2, 1024)]
    [InlineData(1, 2, 1024)]
    [InlineData(0, 1, 1024)]
    [InlineData(0, 400001, 1024)]
    [InlineData(0, 2, 1023)]
    [InlineData(0, 2, DiskUsageTreeBinaryWriter.MaximumBytes + 1)]
    public void InvalidRootAndBoundsAreRejected(int root, int nodes, int bytes)
    {
        var builder = new DiskUsageTreeBuilder();
        _ = builder.Add(-1, "C:\\", true, 0, 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskUsageTreeBinaryWriter.Write(builder.Build(), root, nodes, bytes));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]
    public void CursorMustBeAValidStrictDescendant(int cursor)
    {
        var builder = new DiskUsageTreeBuilder();
        _ = builder.Add(-1, "C:\\", true, 0, 0);
        _ = builder.Add(0, "file", false, 1, 1);
        _ = builder.Add(-1, "D:\\", true, 0, 0);
        Assert.ThrowsAny<ArgumentException>(() => DiskUsageTreeBinaryWriter.Write(builder.Build(), 0, 8, 1024, cursor));
    }

    private static byte[] Write(DiskUsageTree tree, int root)
        => DiskUsageTreeBinaryWriter.Write(tree, root, 65_536, 8_388_608);

    private static void AssertParentBeforeChild(DiskUsageTree tree, Facts facts)
    {
        var positions = facts.NodeIds.Select((node, index) => (node, index)).ToDictionary(x => x.node, x => x.index);
        foreach (var node in facts.NodeIds.Skip(1)) Assert.True(positions[tree.ParentOf(node)] < positions[node]);
    }

    private static Facts Decode(byte[] bytes)
    {
        var count = checked((int)U32(bytes, 8));
        var sizes = new double[count];
        var childTotals = new double[count];
        var ids = new int[count];
        var parents = new int[count];
        var counts = new int[count];
        var firstChildren = new int[count];
        var nextSiblings = new int[count];
        var flags = new byte[count];
        var names = new string[count];
        var offset = 64;
        for (var index = 0; index < count; index++, offset += 8) sizes[index] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(offset));
        for (var index = 0; index < count; index++, offset += 8) childTotals[index] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(offset));
        foreach (var column in new[] { ids, parents, counts, firstChildren, nextSiblings })
            for (var index = 0; index < count; index++, offset += 4) column[index] = I32(bytes, offset);
        Array.Copy(bytes, offset, flags, 0, count);
        offset += count;
        while (offset % 4 != 0) Assert.Equal((byte)0, bytes[offset++]);
        var lengthsOffset = offset;
        offset += 4 * count;
        for (var index = 0; index < count; index++)
        {
            var length = checked((int)U32(bytes, lengthsOffset + index * 4));
            names[index] = Encoding.UTF8.GetString(bytes, offset, length);
            offset += length;
        }
        var pathLength = checked((int)U32(bytes, 60));
        var rootPath = Encoding.UTF8.GetString(bytes, offset, pathLength);
        Assert.Equal(bytes.Length, offset + pathLength);
        return new(sizes, childTotals, ids, parents, counts, firstChildren, nextSiblings, flags, names, rootPath, I32(bytes, 16));
    }

    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
    private static int I32(byte[] bytes, int offset) => BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
    private sealed record Facts(double[] Sizes, double[] ChildSizeTotals, int[] NodeIds, int[] ParentIds,
        int[] FileCounts, int[] FirstChildIds, int[] NextSiblingIds, byte[] DirectoryFlags, string[] Names, string RootPath, int NextCursor);
}

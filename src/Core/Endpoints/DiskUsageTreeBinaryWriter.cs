using System.Buffers.Binary;
using System.Text;
using ResourceManager.App.Domain.DiskUsage;

namespace ResourceManager.App.Endpoints;

/// <summary>
/// Writes bounded preorder pages of little-endian RMDU version 3 hierarchy facts.
/// Header: magic (0), version (4), count (8), query root (12), next cursor (16,
/// -1 when complete), root size f64 (24), root path UTF-8 byte length (60).
/// Columns: sizes f64, direct-child size totals f64, node IDs, parent IDs, file
/// counts, first-child IDs and next-sibling IDs i32, directory flags u8, padding
/// to four bytes, name lengths u32, UTF-8 names, then the query root path.
/// A continuation starts with its root-to-cursor ancestors as context. All IDs
/// and links retain their original scan values, including links outside a page.
/// </summary>
public static class DiskUsageTreeBinaryWriter
{
    public const uint Magic = 0x5544_4D52;
    public const uint Version = 3;
    public const int HeaderBytes = 64;
    public const int MaximumNodes = 400_000;
    public const int MaximumBytes = 33_554_432;
    private const int NumericBytesPerNode = 37;
    private const int BytesPerNode = NumericBytesPerNode + sizeof(uint);

    public static void ValidateBounds(int maxNodes, int maxBytes)
    {
        if (maxNodes < 2 || maxNodes > MaximumNodes)
            throw new ArgumentOutOfRangeException(nameof(maxNodes), "maxNodes must be between 2 and 400000.");
        if (maxBytes < 1024 || maxBytes > MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), "maxBytes must be between 1024 and 33554432.");
    }

    public static byte[] WriteEmpty()
    {
        var buffer = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), -1);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(16), -1);
        return buffer;
    }

    public static byte[] Write(DiskUsageTree tree, int rootNodeId, int maxNodes, int maxBytes, int? cursor = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ValidateBounds(maxNodes, maxBytes);
        ValidateNode(tree, rootNodeId);
        var first = cursor ?? rootNodeId;
        ValidateNode(tree, first);
        if (cursor == rootNodeId)
            throw new ArgumentException("The continuation cursor must be below the query root.", nameof(cursor));

        // This list never grows with the complete tree: only context and accepted records.
        var nodes = new List<int>();
        var pathBytes = RootPathByteCount(tree, rootNodeId, maxBytes);
        long namesBytes = 0;
        if (cursor.HasValue)
        {
            var ancestor = first;
            var cursorNameBytes = Encoding.UTF8.GetByteCount(tree.NameOf(first));
            while (ancestor != rootNodeId)
            {
                ancestor = Parent(tree, ancestor);
                if (ancestor < rootNodeId)
                    throw new ArgumentException("The cursor is outside the requested subtree.", nameof(cursor));
                if (nodes.Count >= maxNodes - 1)
                    throw new ArgumentException("The node budget cannot hold the cursor and its ancestry.", nameof(maxNodes));
                namesBytes += Encoding.UTF8.GetByteCount(tree.NameOf(ancestor));
                if (RequiredBytes(nodes.Count + 2, namesBytes + cursorNameBytes, pathBytes) > maxBytes)
                    throw new ArgumentException("The byte budget cannot hold the cursor and its ancestry.", nameof(maxBytes));
                nodes.Add(ancestor);
            }
            nodes.Reverse();
        }

        var current = first;
        var accepted = 0;
        long remainingSteps = 2L * tree.Count;
        while (current >= 0)
        {
            var nameBytes = Encoding.UTF8.GetByteCount(tree.NameOf(current));
            if (nodes.Count >= maxNodes || RequiredBytes(nodes.Count + 1, namesBytes + nameBytes, pathBytes) > maxBytes)
            {
                if (accepted == 0)
                    throw new ArgumentException("The budgets cannot hold the next node and its ancestry.");
                break;
            }
            nodes.Add(current);
            namesBytes += nameBytes;
            accepted++;
            current = Successor(tree, current, rootNodeId, ref remainingSteps);
        }

        var count = nodes.Count;
        var padding = (4 - NumericBytesPerNode * count % 4) % 4;
        var total = checked((int)(HeaderBytes + (long)BytesPerNode * count + padding + namesBytes + pathBytes));
        var buffer = new byte[total];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt32LittleEndian(span, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], (uint)count);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], rootNodeId);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], current);
        BinaryPrimitives.WriteDoubleLittleEndian(span[24..], tree.SizeOf(rootNodeId));
        BinaryPrimitives.WriteUInt32LittleEndian(span[60..], (uint)pathBytes);

        var offset = HeaderBytes;
        foreach (var node in nodes)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(span[offset..], tree.SizeOf(node));
            offset += sizeof(double);
        }
        foreach (var node in nodes)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(span[offset..], DirectChildSizeTotal(tree, node));
            offset += sizeof(double);
        }
        offset = WriteInt32Column(span, offset, nodes, static node => node);
        offset = WriteInt32Column(span, offset, nodes, tree.ParentOf);
        offset = WriteInt32Column(span, offset, nodes, tree.FileCountOf);
        offset = WriteInt32Column(span, offset, nodes, tree.FirstChildOf);
        offset = WriteInt32Column(span, offset, nodes, tree.NextSiblingOf);
        foreach (var node in nodes)
            span[offset++] = tree.IsDirectory(node) ? (byte)1 : (byte)0;
        offset += padding;
        foreach (var node in nodes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(span[offset..], (uint)Encoding.UTF8.GetByteCount(tree.NameOf(node)));
            offset += sizeof(uint);
        }
        foreach (var node in nodes)
            offset += Encoding.UTF8.GetBytes(tree.NameOf(node), span[offset..]);
        WriteRootPath(tree, rootNodeId, span[offset..]);
        return buffer;
    }

    // The three-byte allowance bounds alignment before any record is accepted.
    private static long RequiredBytes(int count, long namesBytes, int pathBytes)
        => HeaderBytes + 3L + (long)BytesPerNode * count + namesBytes + pathBytes;

    private static void ValidateNode(DiskUsageTree tree, int node)
    {
        if ((uint)node >= (uint)tree.Count)
            throw new ArgumentOutOfRangeException(nameof(node));
    }

    private static int Parent(DiskUsageTree tree, int node)
    {
        var parent = tree.ParentOf(node);
        if (parent < -1 || parent >= node)
            throw new InvalidOperationException("The disk-usage tree contains an invalid parent link.");
        return parent;
    }

    private static int Successor(DiskUsageTree tree, int node, int root, ref long remainingSteps)
    {
        var child = tree.FirstChildOf(node);
        if (child >= 0)
        {
            ValidateChild(tree, node, child);
            return child;
        }
        while (node != root)
        {
            if (--remainingSteps < 0)
                throw new InvalidOperationException("The disk-usage traversal exceeded its node bound.");
            var sibling = tree.NextSiblingOf(node);
            var parent = Parent(tree, node);
            if (sibling >= 0)
            {
                ValidateChild(tree, parent, sibling);
                if (sibling <= node)
                    throw new InvalidOperationException("The disk-usage tree contains an invalid sibling link.");
                return sibling;
            }
            node = parent;
        }
        return -1;
    }

    private static void ValidateChild(DiskUsageTree tree, int parent, int child)
    {
        if ((uint)child >= (uint)tree.Count || child <= parent || tree.ParentOf(child) != parent)
            throw new InvalidOperationException("The disk-usage tree contains an invalid child link.");
    }

    private static double DirectChildSizeTotal(DiskUsageTree tree, int node)
    {
        if (!tree.IsDirectory(node)) return 0;
        double total = 0;
        var previous = node;
        var remaining = tree.Count;
        for (var child = tree.FirstChildOf(node); child >= 0; child = tree.NextSiblingOf(child))
        {
            if (--remaining < 0 || child <= previous)
                throw new InvalidOperationException("The disk-usage child traversal exceeded its node bound.");
            ValidateChild(tree, node, child);
            total += Math.Max(0, tree.SizeOf(child));
            previous = child;
        }
        return total;
    }

    // Walk twice with scalar state, rather than materializing the root's ancestry or full path.
    private static int RootPathByteCount(DiskUsageTree tree, int node, int maxBytes)
    {
        long bytes = 0;
        var hasDescendant = false;
        while (node >= 0)
        {
            var name = tree.NameOf(node);
            bytes += Encoding.UTF8.GetByteCount(name);
            if (!name.IsEmpty && hasDescendant && name[^1] != Path.DirectorySeparatorChar) bytes++;
            if (bytes > maxBytes - HeaderBytes)
                throw new ArgumentException("The byte budget cannot hold the query root path.", nameof(maxBytes));
            hasDescendant = true;
            node = Parent(tree, node);
        }
        return (int)bytes;
    }

    private static void WriteRootPath(DiskUsageTree tree, int node, Span<byte> destination)
    {
        var offset = destination.Length;
        var hasDescendant = false;
        while (node >= 0)
        {
            var name = tree.NameOf(node);
            if (!name.IsEmpty && hasDescendant && name[^1] != Path.DirectorySeparatorChar)
                destination[--offset] = (byte)Path.DirectorySeparatorChar;
            var length = Encoding.UTF8.GetByteCount(name);
            offset -= length;
            Encoding.UTF8.GetBytes(name, destination.Slice(offset, length));
            hasDescendant = true;
            node = Parent(tree, node);
        }
    }

    private static int WriteInt32Column(Span<byte> span, int offset, List<int> nodes, Func<int, int> read)
    {
        foreach (var node in nodes)
        {
            BinaryPrimitives.WriteInt32LittleEndian(span[offset..], read(node));
            offset += sizeof(int);
        }
        return offset;
    }
}

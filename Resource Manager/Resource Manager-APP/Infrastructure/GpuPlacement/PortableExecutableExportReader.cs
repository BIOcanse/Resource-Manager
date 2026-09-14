using System.Buffers.Binary;
using System.Text;

namespace ResourceManager.App.Infrastructure.GpuPlacement;

internal static class PortableExecutableExportReader
{
    private const uint PortableExecutableSignature = 0x00004550;
    private const ushort Pe32Magic = 0x010b;
    private const ushort Pe32PlusMagic = 0x020b;

    public static bool TryGetExportRva(
        string path,
        string exportName,
        out uint exportRva,
        out string error)
    {
        exportRva = 0;
        error = string.Empty;
        try
        {
            var image = File.ReadAllBytes(path);
            if (!TryReadUInt32(image, 0x3c, out var peOffset)
                || !TryReadUInt32(image, checked((int)peOffset), out var signature)
                || signature != PortableExecutableSignature)
            {
                error = "GPU Provider 不是有效的 PE 文件。";
                return false;
            }

            var coffOffset = checked((int)peOffset + 4);
            if (!TryReadUInt16(image, coffOffset + 2, out var sectionCount)
                || !TryReadUInt16(image, coffOffset + 16, out var optionalHeaderSize))
            {
                error = "GPU Provider COFF 头不完整。";
                return false;
            }

            var optionalHeaderOffset = coffOffset + 20;
            if (!TryReadUInt16(image, optionalHeaderOffset, out var optionalMagic))
            {
                error = "GPU Provider 可选头不完整。";
                return false;
            }

            var dataDirectoryOffset = optionalMagic switch
            {
                Pe32Magic => optionalHeaderOffset + 96,
                Pe32PlusMagic => optionalHeaderOffset + 112,
                _ => -1
            };
            if (dataDirectoryOffset < 0
                || !TryReadUInt32(image, dataDirectoryOffset, out var exportDirectoryRva)
                || exportDirectoryRva == 0)
            {
                error = "GPU Provider 没有导出目录。";
                return false;
            }

            var sectionTableOffset = optionalHeaderOffset + optionalHeaderSize;
            var sections = ReadSections(image, sectionTableOffset, sectionCount);
            if (!TryMapRva(sections, exportDirectoryRva, out var exportDirectoryOffset)
                || !TryReadUInt32(image, exportDirectoryOffset + 24, out var nameCount)
                || !TryReadUInt32(image, exportDirectoryOffset + 28, out var functionsRva)
                || !TryReadUInt32(image, exportDirectoryOffset + 32, out var namesRva)
                || !TryReadUInt32(image, exportDirectoryOffset + 36, out var ordinalsRva)
                || !TryMapRva(sections, functionsRva, out var functionsOffset)
                || !TryMapRva(sections, namesRva, out var namesOffset)
                || !TryMapRva(sections, ordinalsRva, out var ordinalsOffset))
            {
                error = "GPU Provider 导出表不完整。";
                return false;
            }

            for (var index = 0u; index < nameCount; index++)
            {
                if (!TryReadUInt32(image, namesOffset + checked((int)index * 4), out var nameRva)
                    || !TryMapRva(sections, nameRva, out var nameOffset)
                    || !TryReadAsciiString(image, nameOffset, out var observedName)
                    || !observedName.Equals(exportName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryReadUInt16(image, ordinalsOffset + checked((int)index * 2), out var ordinal)
                    || !TryReadUInt32(image, functionsOffset + ordinal * 4, out exportRva)
                    || exportRva == 0)
                {
                    error = $"GPU Provider 导出 {exportName} 没有有效函数地址。";
                    return false;
                }

                return true;
            }

            error = $"GPU Provider 缺少导出 {exportName}。";
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OverflowException or ArgumentException)
        {
            error = ex.Message;
            return false;
        }
    }

    private static IReadOnlyList<Section> ReadSections(byte[] image, int offset, ushort count)
    {
        var sections = new List<Section>(count);
        for (var index = 0; index < count; index++)
        {
            var sectionOffset = offset + index * 40;
            if (!TryReadUInt32(image, sectionOffset + 8, out var virtualSize)
                || !TryReadUInt32(image, sectionOffset + 12, out var virtualAddress)
                || !TryReadUInt32(image, sectionOffset + 16, out var rawSize)
                || !TryReadUInt32(image, sectionOffset + 20, out var rawOffset))
            {
                break;
            }

            sections.Add(new Section(virtualAddress, Math.Max(virtualSize, rawSize), rawOffset));
        }
        return sections;
    }

    private static bool TryMapRva(
        IReadOnlyList<Section> sections,
        uint rva,
        out int offset)
    {
        foreach (var section in sections)
        {
            if (rva >= section.VirtualAddress && rva - section.VirtualAddress < section.VirtualSize)
            {
                offset = checked((int)(section.RawOffset + rva - section.VirtualAddress));
                return true;
            }
        }

        offset = 0;
        return false;
    }

    private static bool TryReadAsciiString(byte[] image, int offset, out string value)
    {
        if (offset < 0 || offset >= image.Length)
        {
            value = string.Empty;
            return false;
        }

        var end = offset;
        while (end < image.Length && image[end] != 0)
        {
            end++;
        }
        value = Encoding.ASCII.GetString(image, offset, end - offset);
        return end < image.Length;
    }

    private static bool TryReadUInt16(byte[] image, int offset, out ushort value)
    {
        if (offset < 0 || offset > image.Length - sizeof(ushort))
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset, sizeof(ushort)));
        return true;
    }

    private static bool TryReadUInt32(byte[] image, int offset, out uint value)
    {
        if (offset < 0 || offset > image.Length - sizeof(uint))
        {
            value = 0;
            return false;
        }
        value = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, sizeof(uint)));
        return true;
    }

    private readonly record struct Section(uint VirtualAddress, uint VirtualSize, uint RawOffset);
}

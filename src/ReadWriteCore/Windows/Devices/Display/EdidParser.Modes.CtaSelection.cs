using System.Collections.Immutable;

namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadCtaModeSelection(ReadOnlySpan<byte> payload, uint block, uint offset,
        byte revision, bool native, List<EdidModeSelectionDeclaration> selections)
    {
        if (payload.Length == 0)
            return false;
        byte? flags = null;
        EdidNativeImageSizeDeclaration? size = null;
        if (native)
        {
            // An omitted flags byte stays omitted. Unexplained tails are not truncated.
            if (payload.Length is not (1 or 2 or 6))
                return false;
            if (payload.Length > 1)
            {
                flags = payload[1];
                if ((flags.Value & 0x7E) != 0)
                    return false;
                var hasSize = (flags.Value & 1) != 0;
                if (hasSize ? payload.Length != 6 : payload.Length != 2)
                    return false;
                if (hasSize)
                {
                    var width = checked((ushort)(payload[2] | payload[3] << 8));
                    var height = checked((ushort)(payload[4] | payload[5] << 8));
                    if (width == 0 || height == 0)
                        return false;
                    size = new(width, height, (flags.Value & 0x80) == 0 ? 100u : 1000u);
                }
            }
        }
        // Pending references are resolved only after every declared block has been read.
        selections.Add(new(block, offset, native ? "cta-nvrdb" : "cta-vfpdb", revision,
            ModeBytes(payload), []) { Flags = flags, ImageSize = size });
        return true;
    }

    private static bool ResolveCtaModeSelections(List<EdidModeDeclaration> modes, ModeCtaReferences cta)
    {
        if (cta.Selections.Count == 0)
            return true;
        var detailed = modes.Where(static mode => mode.Encoding == "edid-detailed").ToArray();
        // The shared global VTDB ordinal counts every T7/T10 declaration in source order.
        // DMT-code T8 declarations and ordinary18B DTDs remain in their separate domains.
        var vtdbs = modes.Where(static mode => mode.Encoding is "cta-vtdb-vii" or "cta-vtdb-x").ToArray();
        for (var declarationIndex = 0; declarationIndex < cta.Selections.Count; declarationIndex++)
        {
            var declaration = cta.Selections[declarationIndex];
            var count = declaration.Encoding == "cta-nvrdb" ? 1 : declaration.RawDescriptor.Length;
            var references = ImmutableArray.CreateBuilder<EdidModeSelectionReference>(count);
            for (var index = 0; index < count; index++)
            {
                var svr = declaration.RawDescriptor[index];
                var offset = checked(declaration.ByteOffset + (uint)index);
                if (svr is >= 1 and <= 127 or >= 193 and <= 253)
                {
                    if (!TryCtaModeCode(svr, out _))
                        return false;
                    references.Add(new(offset, svr, "cta-vic", svr, null));
                    continue;
                }
                EdidModeDeclaration target;
                if (svr is >= 129 and <= 144)
                {
                    var ordinal = svr - 129;
                    if (ordinal >= detailed.Length)
                        return false;
                    target = detailed[ordinal];
                }
                else if (svr is >= 145 and <= 160)
                {
                    var ordinal = svr - 145;
                    if (ordinal >= vtdbs.Length)
                        return false;
                    target = vtdbs[ordinal];
                }
                else
                    return false;
                references.Add(new(offset, svr, target.Encoding, null,
                    new(target.BlockIndex, target.ByteOffset, target.RawDescriptor, null)));
            }
            cta.Selections[declarationIndex] = declaration with { References = references.MoveToImmutable() };
        }
        return true;
    }
}

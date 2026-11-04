using System;

namespace ResourceManager.ReadWrite.Windows.Devices.Display;

public static partial class EdidParser
{
    private static bool ReadDisplayIdTypeV(ReadOnlySpan<byte> payload, uint block, uint offset,
        byte flags, byte containerRevision, List<EdidModeDeclaration> modes)
    {
        // VESA DisplayID 1.3 Table 4-25 defines complete seven-byte entries only.
        // The EDID/DisplayID section length already supplies the natural upper bound;
        // the table's stated 53-entry maximum exceeds one section's available payload.
        if (flags != 0 || payload.IsEmpty || payload.Length % 7 != 0)
            return false;

        for (var relative = 0; relative < payload.Length; relative += 7)
        {
            var descriptor = payload.Slice(relative, 7);
            var options = descriptor[0];
            var formula = (byte)(options & 0x03);
            var stereo = (byte)((options >> 5) & 0x03);
            if ((options & 0x0C) != 0 || formula > 1 || stereo == 3)
                return false;

            // Table 4-26 has no row for byte 1. Keep it only in RawDescriptor.
            var width = checked((uint)(descriptor[2] | descriptor[3] << 8) + 1);
            var height = checked((uint)(descriptor[4] | descriptor[5] << 8) + 1);
            modes.Add(new(block, checked(offset + (uint)relative), "displayid-v", 0, ModeBytes(descriptor))
            {
                Width = width,
                Height = height,
                NominalVerticalRateHz = checked((uint)descriptor[6] + 1),
                Preferred = (options & 0x80) != 0,
                Formula = "cvt",
                ReducedBlanking = true,
                ContainerFlags = flags,
                ContainerRevision = containerRevision,
                DisplayIdTypeVOptions = new(formula, stereo, (options & 0x10) != 0)
            });
        }
        return true;
    }

    private static bool ReadDisplayIdTypeVi(ReadOnlySpan<byte> payload, uint block, uint offset,
        byte flags, byte containerRevision, List<EdidModeDeclaration> modes)
    {
        if (flags != 0)
            return false;

        for (var relative = 0; relative < payload.Length;)
        {
            if (payload.Length - relative < 3)
                return false;
            var hasAspectAndSize = (payload[relative + 2] & 0x40) != 0;
            var stride = hasAspectAndSize ? 17 : 14;
            if (payload.Length - relative < stride)
                return false;

            var descriptor = payload.Slice(relative, stride);
            var horizontalActiveFlags = descriptor[4];
            var verticalActiveFlags = descriptor[6];
            var timingFlags = descriptor[13];
            var stereo = (byte)((timingFlags >> 5) & 0x03);
            if ((horizontalActiveFlags & 0x40) != 0 || (verticalActiveFlags & 0x40) != 0
                || (timingFlags & 0x10) != 0 || stereo == 3)
                return false;

            var clockCode = checked((uint)(descriptor[0] | descriptor[1] << 8 | (descriptor[2] & 0x3F) << 16));
            // Table 4-28's aggregate maximum (0x1FFFFF) conflicts with its six-bit high field
            // and stated 4,194.303 MP/s ceiling. Bit 21 alone exceeds that aggregate maximum.
            // Preserve the mode and its geometry while withholding only its unsupported clock/rates.
            ulong? clockHz = (descriptor[2] & 0x20) == 0
                ? checked(((ulong)clockCode + 1) * 1000UL)
                : null;

            var width = checked((uint)(descriptor[3] | (horizontalActiveFlags & 0x0F) << 8) + 1);
            var height = checked((uint)(descriptor[5] | (verticalActiveFlags & 0x3F) << 8) + 1);
            var horizontalBlanking = checked((uint)(descriptor[7] | (descriptor[9] & 0xF0) << 4) + 1);
            var horizontalFrontPorch = checked((uint)(descriptor[8] | (descriptor[9] & 0x0F) << 8) + 1);
            var horizontalSyncWidth = checked((uint)descriptor[10] + 1);
            var verticalBlanking = checked((uint)descriptor[11] + 1);
            var verticalFrontPorch = checked((uint)descriptor[12] + 1);
            var verticalSyncWidth = checked((uint)(timingFlags & 0x0F) + 1);
            if (checked(horizontalFrontPorch + horizontalSyncWidth) > horizontalBlanking
                || checked(verticalFrontPorch + verticalSyncWidth) > verticalBlanking)
                return false;

            var positiveHorizontalSync = (horizontalActiveFlags & 0x80) != 0;
            var positiveVerticalSync = (verticalActiveFlags & 0x80) != 0;
            var interlaced = (timingFlags & 0x80) != 0;
            EdidDetailedTiming? timing = null;
            if (clockHz is { } clock)
            {
                var horizontalTotal = checked((ulong)width + horizontalBlanking);
                var verticalTotal = checked((ulong)height + verticalBlanking);
                var denominator = checked(horizontalTotal * verticalTotal);
                timing = new(clock, width, horizontalBlanking, horizontalFrontPorch, horizontalSyncWidth,
                    height, verticalBlanking, verticalFrontPorch, verticalSyncWidth, timingFlags,
                    positiveHorizontalSync, positiveVerticalSync, ModeRate(clock, denominator), null);
            }

            EdidAspectRatioDeclaration? aspect = null;
            EdidDisplayIdTypeViImageSize? imageSize = null;
            if (hasAspectAndSize)
            {
                var multiplier = descriptor[14];
                aspect = new(multiplier, checked((uint)multiplier * 3), 256);
                var verticalBaseSize = checked((uint)(descriptor[15] | (descriptor[16] & 0x0F) << 8) + 1);
                var sizeMultiplier = checked((byte)((descriptor[16] >> 4) + 1));
                imageSize = new(multiplier, verticalBaseSize, sizeMultiplier,
                    checked(verticalBaseSize * sizeMultiplier));
            }

            modes.Add(new(block, checked(offset + (uint)relative), "displayid-vi", 0, ModeBytes(descriptor))
            {
                Width = width,
                Height = height,
                Interlaced = interlaced,
                Preferred = (descriptor[2] & 0x80) != 0,
                AspectRatioDeclared = aspect,
                ContainerFlags = flags,
                ContainerRevision = containerRevision,
                Timing = timing,
                DisplayIdTypeViTiming = new(clockHz, horizontalBlanking, horizontalFrontPorch,
                    horizontalSyncWidth, verticalBlanking, verticalFrontPorch, verticalSyncWidth,
                    positiveHorizontalSync, positiveVerticalSync, stereo),
                DisplayIdTypeViImageSize = imageSize
            });
            relative += stride;
        }
        return true;
    }
}

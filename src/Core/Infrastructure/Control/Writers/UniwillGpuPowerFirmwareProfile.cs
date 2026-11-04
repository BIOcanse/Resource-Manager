using ResourceManager.App.Domain.RuntimeSpecialization;

namespace ResourceManager.App.Infrastructure.Control.Writers;

/// <summary>Known 0x0743–0x0746 layouts, identified independently of register readback.</summary>
internal static class UniwillGpuPowerFirmwareProfile
{
    // Hardware facts from Linux uniwill-acpi.c at 5b05bb3f6c5716fab6911e12d60dd1f43ad9806a.
    // These are exact SMBIOS board strings; the letter x is not a wildcard.
    private static readonly HashSet<string> TuxedoBoards = new(StringComparer.Ordinal)
    {
        "PHxTQx1", "PH6AG01_PH6AQ71_PH6AQI1", "PH4PG31", "PH6PG01_PH6PG71",
        "GMxMGxx", "GMxNGxx", "GMxZGxx", "GMxTGxx", "GMxRGxx", "GMxAGxx",
        "GMxXGxx", "GM6XGxX", "GMxPXxx", "GMxHGxx", "GM5IXxA", "GM6IXxB_MB1",
        "GM6IXxB_MB2", "GM7IXxN", "X6FR5xxY", "X6AR5xxY", "X6AR5xxY_mLED",
        "X5KK45xS_X5SP45xS", "X6HP45xU", "X6KK45xU_X6SP45xU", "X5AR45xS", "X6AR55xU"
    };

    internal static bool Supports(
        FirmwareIdentitySnapshot identity, bool hasAcpiDevice, Func<ushort, byte?> read)
        => Detect(identity, () => hasAcpiDevice, read) is true;

    internal static bool? Detect(
        FirmwareIdentitySnapshot identity, Func<bool?> hasAcpiDevice, Func<ushort, byte?> read)
    {
        ArgumentNullException.ThrowIfNull(hasAcpiDevice);
        return IsProfileCandidate(identity)
            ? Detect(identity, hasAcpiDevice(), read)
            : false;
    }

    /// <summary>
    /// Returns null only when the shared-board discriminator could not be read yet.
    /// The hardware owner retains the first result, including unknown, for its lifetime.
    /// </summary>
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    internal static bool? Detect(
        FirmwareIdentitySnapshot identity, bool? hasAcpiDevice, Func<ushort, byte?> read)
    {
        if (!IsProfileCandidate(identity)) return false;
        if (hasAcpiDevice is null) return null;
        if (!hasAcpiDevice.Value) return false;

        var manufacturer = identity.SystemManufacturer!;
        var board = identity.BaseBoardProduct!;

        if (manufacturer.Contains("MACHENIKE", StringComparison.Ordinal) && board == "L16P") return true;
        if (TuxedoBoards.Contains(board)) return true;

        // The same Gen7 board identifies variants with and without a discrete GPU.
        if (board != "PHxARX1_PHxAQF1") return false;
        return read(UniwillEcRegisters.SystemId) is { } systemId
            ? (systemId & 0x80) != 0
            : null;
    }

    private static bool IsProfileCandidate(FirmwareIdentitySnapshot identity)
    {
        if (identity.SystemManufacturer is not { } manufacturer
            || identity.BaseBoardProduct is not { } board) return false;
        return (manufacturer.Contains("MACHENIKE", StringComparison.Ordinal) && board == "L16P")
            || (manufacturer.Contains("TUXEDO", StringComparison.Ordinal)
                && (TuxedoBoards.Contains(board) || board == "PHxARX1_PHxAQF1"));
    }

    internal static bool IsAcpiDeviceId(string? deviceId)
        => deviceId?.StartsWith(UniwillEcBridge.AcpiDeviceId + "\\", StringComparison.OrdinalIgnoreCase) == true;
}

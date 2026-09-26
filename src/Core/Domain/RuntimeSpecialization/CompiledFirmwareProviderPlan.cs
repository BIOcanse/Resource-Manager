namespace ResourceManager.App.Domain.RuntimeSpecialization;

public enum NotebookOemFanProviderKind : byte
{
    None = 0,
    MechrevoUwAcpi = 1
}

public sealed record FirmwareIdentitySnapshot(
    string? BiosManufacturer,
    string? BiosVersion,
    string? SystemManufacturer,
    string? SystemProductName,
    string? BaseBoardManufacturer,
    string? BaseBoardProduct)
{
    public static FirmwareIdentitySnapshot Unknown { get; } = new(null, null, null, null, null, null);

    public string SearchText => string.Join(
        ' ',
        new[]
        {
            BiosManufacturer,
            BiosVersion,
            SystemManufacturer,
            SystemProductName,
            BaseBoardManufacturer,
            BaseBoardProduct
        }.Where(static item => !string.IsNullOrWhiteSpace(item)));

    public string Describe()
    {
        return string.Join(
            " / ",
            new[]
            {
                SystemManufacturer,
                SystemProductName,
                BaseBoardManufacturer,
                BaseBoardProduct,
                BiosManufacturer,
                BiosVersion
            }.Where(static item => !string.IsNullOrWhiteSpace(item)))
            is { Length: > 0 } description
            ? description
            : "Unknown firmware";
    }
}

public sealed record CompiledFirmwareProviderPlan(
    FirmwareIdentitySnapshot Identity,
    NotebookOemFanProviderKind NotebookOemFanProvider)
{
    public static CompiledFirmwareProviderPlan Default { get; } = new(
        FirmwareIdentitySnapshot.Unknown,
        NotebookOemFanProviderKind.None);
}

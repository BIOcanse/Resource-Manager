using System.Collections.Immutable;
using ResourceManager.ReadWrite.Information;

namespace ResourceManager.ReadWrite.Windows.Devices.Printers;

[Flags]
public enum WindowsPrinterInformationFields
{
    None = 0,
    DriverVersionDeclared = 1,
    PortDeclared = 2,
    QueueStateActual = 4,
    All = DriverVersionDeclared | PortDeclared | QueueStateActual
}

public enum WindowsPrinterEnumerationScope
{
    Local,
    Connections,
    LocalAndConnections
}

/// <summary>Caller-selected local/connection directory and hard bounds for one
/// synchronous Winspool capture. Construction performs no native operation.</summary>
public sealed record WindowsPrinterInformationScope
{
    public WindowsPrinterInformationFields Fields { get; }
    public WindowsPrinterEnumerationScope Enumeration { get; }
    public int MaximumPrinters { get; }
    public int MaximumDriverEntriesPerServer { get; }
    public int MaximumNativeBufferBytes { get; }
    public int MaximumStringCharacters { get; }

    public WindowsPrinterInformationScope(
        WindowsPrinterInformationFields fields = WindowsPrinterInformationFields.All,
        WindowsPrinterEnumerationScope enumeration = WindowsPrinterEnumerationScope.LocalAndConnections,
        int maximumPrinters = 256,
        int maximumDriverEntriesPerServer = 2048,
        int maximumNativeBufferBytes = 8 * 1024 * 1024,
        int maximumStringCharacters = 4096)
    {
        if (fields == WindowsPrinterInformationFields.None || (fields & ~WindowsPrinterInformationFields.All) != 0)
            throw new ArgumentOutOfRangeException(nameof(fields));
        if (!Enum.IsDefined(enumeration)) throw new ArgumentOutOfRangeException(nameof(enumeration));
        if (maximumPrinters is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumPrinters));
        if (maximumDriverEntriesPerServer is < 1 or > 16384)
            throw new ArgumentOutOfRangeException(nameof(maximumDriverEntriesPerServer));
        if (maximumNativeBufferBytes is < 4096 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumNativeBufferBytes));
        if (maximumStringCharacters is < 1 or > 32768)
            throw new ArgumentOutOfRangeException(nameof(maximumStringCharacters));
        Fields = fields;
        Enumeration = enumeration;
        MaximumPrinters = maximumPrinters;
        MaximumDriverEntriesPerServer = maximumDriverEntriesPerServer;
        MaximumNativeBufferBytes = maximumNativeBufferBytes;
        MaximumStringCharacters = maximumStringCharacters;
    }

    internal string EnumerationCode => Enumeration switch
    {
        WindowsPrinterEnumerationScope.Local => "local",
        WindowsPrinterEnumerationScope.Connections => "connections",
        WindowsPrinterEnumerationScope.LocalAndConnections => "local-and-connections",
        _ => throw new ArgumentOutOfRangeException(nameof(Enumeration))
    };

    internal uint EnumerationFlags => Enumeration switch
    {
        WindowsPrinterEnumerationScope.Local => 0x00000002,
        WindowsPrinterEnumerationScope.Connections => 0x00000004,
        WindowsPrinterEnumerationScope.LocalAndConnections => 0x00000006,
        _ => throw new ArgumentOutOfRangeException(nameof(Enumeration))
    };

    internal ImmutableArray<DeviceInformationDefinition> Definitions
    {
        get
        {
            var definitions = ImmutableArray.CreateBuilder<DeviceInformationDefinition>();
            foreach (var selected in new[]
                {
                    WindowsPrinterInformationFields.DriverVersionDeclared,
                    WindowsPrinterInformationFields.PortDeclared,
                    WindowsPrinterInformationFields.QueueStateActual
                })
                if ((Fields & selected) != 0) definitions.Add(DefinitionFor(selected));
            return definitions.ToImmutable();
        }
    }

    internal WindowsPrinterInformationFields Select(ImmutableArray<InformationFieldSelection> selection)
    {
        var fields = WindowsPrinterInformationFields.None;
        foreach (var item in selection)
        {
            var field = DefinitionField(item.Definition);
            if (item.Field.FieldName != item.Definition.Name || !ReferenceEquals(item.Definition, DefinitionFor(field)))
                throw new ArgumentException("Printer selection must use the exact static field definition.", nameof(selection));
            if ((Fields & field) == 0)
                throw new ArgumentException("The selected printer field is outside this scope.", nameof(selection));
            fields |= field;
        }
        return fields;
    }

    internal static DeviceInformationDefinition DefinitionFor(WindowsPrinterInformationFields field) => field switch
    {
        WindowsPrinterInformationFields.DriverVersionDeclared => DeviceInformationFieldRegistry.PrinterAllDriverVersionDeclared,
        WindowsPrinterInformationFields.PortDeclared => DeviceInformationFieldRegistry.PrinterAllPortDeclared,
        WindowsPrinterInformationFields.QueueStateActual => DeviceInformationFieldRegistry.PrinterAllQueueStateActual,
        _ => throw new ArgumentOutOfRangeException(nameof(field))
    };

    private static WindowsPrinterInformationFields DefinitionField(DeviceInformationDefinition definition)
    {
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.PrinterAllDriverVersionDeclared))
            return WindowsPrinterInformationFields.DriverVersionDeclared;
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.PrinterAllPortDeclared))
            return WindowsPrinterInformationFields.PortDeclared;
        if (ReferenceEquals(definition, DeviceInformationFieldRegistry.PrinterAllQueueStateActual))
            return WindowsPrinterInformationFields.QueueStateActual;
        throw new ArgumentException("Unknown printer information definition.", nameof(definition));
    }
}

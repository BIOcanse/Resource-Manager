using System.Globalization;
using System.Text.Json;
using ResourceManager.ReadWrite.Windows.Devices.Models;
using ResourceManager.ReadWrite.Windows.Devices.Printers;
using F = ResourceManager.ReadWrite.Information.DeviceInformationFieldRegistry;

namespace ResourceManager.ReadWrite.Information;

public static partial class WindowsInformationFieldProjection
{
    private static readonly JsonSerializerOptions PrinterJsonOptions = InformationJsonContext.Default.Options;

    internal static string PrinterIdentityPrefix(WindowsPrinterInformationScope scope)
        => $"windows-printer|{scope.EnumerationCode}|";

    internal static string PrinterIdentity(WindowsPrinterInformationScope scope,
        string? serverName, string printerName)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(printerName);
        var serverLength = serverName is null ? -1 : serverName.Length;
        return PrinterIdentityPrefix(scope) + serverLength.ToString(CultureInfo.InvariantCulture) + ":"
            + (serverName ?? string.Empty) + "|" + printerName.Length.ToString(CultureInfo.InvariantCulture)
            + ":" + printerName;
    }

    internal static DeviceInformationObject ProjectPrinter(DeviceTopologyPrinterInformation printer,
        WindowsPrinterInformationScope scope, WindowsPrinterInformationFields selected,
        DateTimeOffset observedAt, WindowsPrinterInformationFields failedFields)
    {
        var fields = new List<DeviceInformationField>(3);
        if ((selected & WindowsPrinterInformationFields.DriverVersionDeclared) != 0)
        {
            var value = (failedFields & WindowsPrinterInformationFields.DriverVersionDeclared) == 0
                && printer.DriverVersionRaw is { } version && version != 0
                    ? version.ToString(CultureInfo.InvariantCulture)
                    : null;
            fields.Add(Field(F.PrinterAllDriverVersionDeclared, value, observedAt));
        }
        if ((selected & WindowsPrinterInformationFields.PortDeclared) != 0)
        {
            var value = (failedFields & WindowsPrinterInformationFields.PortDeclared) != 0
                || string.IsNullOrEmpty(printer.PortNameDeclared)
                ? null
                : printer.PortNameDeclared;
            fields.Add(Field(F.PrinterAllPortDeclared, value, observedAt));
        }
        if ((selected & WindowsPrinterInformationFields.QueueStateActual) != 0)
        {
            var value = (failedFields & WindowsPrinterInformationFields.QueueStateActual) != 0
                ? null
                : printer.QueueStateActual is { } status
                    ? ((uint)status).ToString(CultureInfo.InvariantCulture)
                    : null;
            fields.Add(Field(F.PrinterAllQueueStateActual, value, observedAt));
        }
        return new(PrinterIdentity(scope, printer.ServerName, printer.PrinterName), "printer", fields);
    }

    internal static DeviceInformationSnapshot MergePrinters(DeviceInformationSnapshot current,
        WindowsPrinterInformationScope scope, WindowsPrinterInformationFields selected,
        WindowsPrinterInformationReadResult read, DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(read);
        if ((selected & ~scope.Fields) != 0)
            throw new ArgumentOutOfRangeException(nameof(selected));

        var prefix = PrinterIdentityPrefix(scope);
        var objects = current.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        var existing = objects.Values.Where(device => device.DeviceIdentity.StartsWith(prefix, StringComparison.Ordinal))
            .ToArray();
        var definitions = Definitions(scope, selected);

        if (!read.DirectorySucceeded)
        {
            if (read.Printers.Count != 0 || read.Failures.Count > 1
                || read.Failures.Any(failure => failure.PrinterName is not null || failure.ServerName is not null
                    || string.IsNullOrWhiteSpace(failure.Code) || failure.Code.Length > 128
                    || failure.FailedFields == WindowsPrinterInformationFields.None
                    || (failure.FailedFields & ~selected) != 0))
                throw new InvalidOperationException("A failed printer directory returned partial identities.");
            foreach (var device in existing)
                objects[device.DeviceIdentity] = ClearSelected(device, definitions);
            return Snapshot(current, objects);
        }

        if (read.Printers.Count > scope.MaximumPrinters
            || read.Failures.Count > checked(scope.MaximumPrinters * 3))
            throw new InvalidOperationException("The printer reader exceeded the caller's object bound.");

        var previousByIdentity = existing.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        foreach (var device in existing) objects.Remove(device.DeviceIdentity);

        var observedByIdentity = new Dictionary<string, DeviceTopologyPrinterInformation>(StringComparer.Ordinal);
        foreach (var printer in read.Printers)
        {
            if (string.IsNullOrEmpty(printer.PrinterName)
                || printer.PrinterName.Length > scope.MaximumStringCharacters
                || printer.ServerName?.Length > scope.MaximumStringCharacters
                || printer.PortNameDeclared?.Length > scope.MaximumStringCharacters)
                throw new InvalidOperationException("The printer reader returned an invalid identity string.");
            var identity = PrinterIdentity(scope, printer.ServerName, printer.PrinterName);
            if (!identity.StartsWith(prefix, StringComparison.Ordinal) || !observedByIdentity.TryAdd(identity, printer))
                throw new InvalidOperationException("The printer reader returned an out-of-scope or duplicate identity.");
        }

        var failedByIdentity = new Dictionary<string, WindowsPrinterInformationFields>(StringComparer.Ordinal);
        foreach (var failure in read.Failures)
        {
            if (string.IsNullOrWhiteSpace(failure.Code) || failure.Code.Length > 128
                || failure.FailedFields == WindowsPrinterInformationFields.None
                || (failure.FailedFields & ~selected) != 0
                || string.IsNullOrEmpty(failure.PrinterName)
                || failure.PrinterName.Length > scope.MaximumStringCharacters
                || failure.ServerName?.Length > scope.MaximumStringCharacters)
                throw new InvalidOperationException("The printer reader returned an invalid field failure.");
            var identity = PrinterIdentity(scope, failure.ServerName, failure.PrinterName);
            if (!observedByIdentity.ContainsKey(identity))
                throw new InvalidOperationException("The printer reader returned a failure for an unknown identity.");
            failedByIdentity[identity] = failedByIdentity.GetValueOrDefault(identity) | failure.FailedFields;
        }

        foreach (var pair in observedByIdentity)
        {
            var projected = ProjectPrinter(pair.Value, scope, selected, observedAt,
                failedByIdentity.GetValueOrDefault(pair.Key));
            if (previousByIdentity.TryGetValue(pair.Key, out var sameIdentity))
                projected = MergeUnselected(sameIdentity, projected, definitions);
            objects[pair.Key] = projected;
        }

        return Snapshot(current, objects);
    }

    internal static DeviceInformationSnapshot PreservePrinterFields(DeviceInformationSnapshot projected,
        DeviceInformationSnapshot previous)
    {
        var objects = projected.Devices.ToDictionary(static device => device.DeviceIdentity, StringComparer.Ordinal);
        foreach (var device in previous.Devices)
        {
            var fields = device.Fields.Where(static field => IsPrinterDefinition(field.Definition)).ToArray();
            if (fields.Length == 0) continue;
            if (objects.TryGetValue(device.DeviceIdentity, out var current))
            {
                var merged = fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal);
                foreach (var field in current.Fields) merged[field.Definition.Name] = field;
                objects[device.DeviceIdentity] = current with { Fields = merged.Values.ToArray() };
            }
            else objects.Add(device.DeviceIdentity, device with { Fields = fields });
        }
        return new(projected.ObservedAt, objects.Values.OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray());
    }

    internal static DeviceInformationSnapshot PrinterCurrent(DeviceInformationSnapshot current,
        WindowsPrinterInformationScope scope)
    {
        var prefix = PrinterIdentityPrefix(scope);
        var names = scope.Definitions.Select(static definition => definition.Name).ToHashSet(StringComparer.Ordinal);
        var devices = current.Devices.Select(device => device with
        {
            Fields = device.Fields.Where(field => names.Contains(field.Definition.Name)).ToArray()
        }).Where(device => device.DeviceIdentity.StartsWith(prefix, StringComparison.Ordinal)
            && device.Fields.Count > 0).ToArray();
        return new(current.ObservedAt, devices);
    }

    internal static bool IsPrinterDefinition(DeviceInformationDefinition definition)
        => ReferenceEquals(definition, F.PrinterAllDriverVersionDeclared)
            || ReferenceEquals(definition, F.PrinterAllPortDeclared)
            || ReferenceEquals(definition, F.PrinterAllQueueStateActual);

    private static IReadOnlyList<DeviceInformationDefinition> Definitions(
        WindowsPrinterInformationScope scope, WindowsPrinterInformationFields selected)
        => scope.Definitions.Where(definition => (selected & PrinterFieldFor(definition)) != 0).ToArray();

    private static WindowsPrinterInformationFields PrinterFieldFor(DeviceInformationDefinition definition)
    {
        if (ReferenceEquals(definition, F.PrinterAllDriverVersionDeclared))
            return WindowsPrinterInformationFields.DriverVersionDeclared;
        if (ReferenceEquals(definition, F.PrinterAllPortDeclared)) return WindowsPrinterInformationFields.PortDeclared;
        if (ReferenceEquals(definition, F.PrinterAllQueueStateActual)) return WindowsPrinterInformationFields.QueueStateActual;
        throw new ArgumentException("Unknown printer information definition.", nameof(definition));
    }

    private static DeviceInformationObject ClearSelected(DeviceInformationObject device,
        IReadOnlyList<DeviceInformationDefinition> definitions)
    {
        var fields = device.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal);
        foreach (var definition in definitions) fields[definition.Name] = new(definition, null) { ObservedAt = null };
        return device with { Fields = fields.Values.ToArray() };
    }

    private static DeviceInformationObject MergeUnselected(DeviceInformationObject previous,
        DeviceInformationObject projected, IReadOnlyList<DeviceInformationDefinition> selectedDefinitions)
    {
        var fields = previous.Fields.ToDictionary(static field => field.Definition.Name, StringComparer.Ordinal);
        foreach (var field in projected.Fields) fields[field.Definition.Name] = field;
        foreach (var definition in selectedDefinitions)
            if (!fields.ContainsKey(definition.Name)) fields[definition.Name] = new(definition, null) { ObservedAt = null };
        return projected with { Fields = fields.Values.ToArray() };
    }

    private static DeviceInformationSnapshot Snapshot(DeviceInformationSnapshot current,
        Dictionary<string, DeviceInformationObject> objects)
        => new(current.ObservedAt, objects.Values.OrderBy(static device => device.DeviceIdentity, StringComparer.Ordinal).ToArray());

    private static DeviceInformationField Field<T>(DeviceInformationDefinition definition, T? value,
        DateTimeOffset observedAt)
    {
        var field = new DeviceInformationField(definition,
            value is null ? null : JsonSerializer.SerializeToElement(value, PrinterJsonOptions));
        return field with { ObservedAt = value is null ? null : observedAt };
    }
}

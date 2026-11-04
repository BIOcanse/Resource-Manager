using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace ResourceManager.ReadWrite.Windows.Diagnostics;

internal static class WindowsWheaEventParser
{
    internal static WindowsWheaEvent? Parse(string xml, WindowsWheaHistoryRequest request)
    {
        if (xml.Length > request.MaximumRenderedEventBytes / 2) return null;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = request.MaximumRenderedEventBytes / 2 });
            var root = XDocument.Load(reader).Root;
            XNamespace ns = "http://schemas.microsoft.com/win/2004/08/events/event";
            if (root?.Name != ns + "Event") return null;
            var system = root.Element(ns + "System");
            if ((string?)system?.Element(ns + "Provider")?.Attribute("Name") != WindowsWheaHistoryRequest.Provider
                || (string?)system?.Element(ns + "Channel") != request.Channel
                || !ulong.TryParse((string?)system?.Element(ns + "EventRecordID"), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || id == 0 || request.AfterRecordId is { } after && id <= after) return null;
            var timeText = (string?)system?.Element(ns + "TimeCreated")?.Attribute("SystemTime");
            DateTimeOffset? time = timeText is not null && (timeText.EndsWith('Z')
                || timeText.Length >= 6 && timeText[^6] is '+' or '-')
                && DateTimeOffset.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
                ? timestamp.ToUniversalTime() : null;
            if (time is { } t && (t < request.FromUtc || t > request.ThroughUtc)) return null;
            var rawNodes = root.Element(ns + "EventData")?.Elements(ns + "Data")
                .Where(static node => (string?)node.Attribute("Name") == "RawData").Take(2).ToArray();
            ImmutableArray<byte>? raw = null;
            WindowsWheaSeverity? severity = null;
            if (rawNodes is { Length: 1 })
            {
                var hex = rawNodes[0].Value;
                if (hex.Length >= 256 && hex.Length % 2 == 0 && hex.All(static c => char.IsAsciiHexDigit(c)))
                {
                    var bytes = Convert.FromHexString(hex);
                    severity = ReadSeverity(bytes);
                    if (severity is not null) raw = ImmutableArray.CreateRange(bytes);
                }
            }
            return new(request.Channel, id, time, raw, severity);
        }
        catch (XmlException) { return null; }
    }

    internal static WindowsWheaSeverity? ReadSeverity(ReadOnlySpan<byte> bytes)
    {
        // WHEA CPER packed header is 128 bytes; each descriptor is 72 bytes.
        if (bytes.Length < 128 || !bytes[..4].SequenceEqual("CPER"u8)
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]) != 0x0210
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[6..]) != uint.MaxValue
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[20..]) != bytes.Length) return null;
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[10..]);
        var descriptorEnd = 128 + count * 72;
        var severity = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (count == 0 || descriptorEnd > bytes.Length || severity > 3) return null;
        for (var index = 0; index < count; index++)
        {
            var descriptor = bytes[(128 + index * 72)..];
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(descriptor);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(descriptor[4..]);
            if (offset < descriptorEnd || length == 0 || offset > bytes.Length || length > bytes.Length - offset) return null;
        }
        return (WindowsWheaSeverity)severity;
    }
}

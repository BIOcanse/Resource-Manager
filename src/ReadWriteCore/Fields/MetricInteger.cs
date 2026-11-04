using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.App.Domain.Metrics;

[JsonConverter(typeof(MetricIntegerJsonConverter))]
public readonly record struct MetricInteger(ulong LowBits, ulong HighBits = 0, bool Negative = false)
{
    public bool FitsSigned64 => HighBits == 0 && (Negative ? unchecked((long)LowBits) < 0 : LowBits <= long.MaxValue);
    public bool FitsSigned32 => FitsSigned64 && (Negative ? unchecked((long)LowBits) >= int.MinValue : LowBits <= int.MaxValue);
    public long ToSigned64() => FitsSigned64 ? unchecked((long)LowBits) : throw new OverflowException();
    public static MetricInteger FromSigned64(long value) => new(unchecked((ulong)value), 0, value < 0);
    public static MetricInteger FromUnsigned128(UInt128 value) => new((ulong)value, (ulong)(value >> 64));
    public BigInteger ToBigInteger() => Negative ? new BigInteger(unchecked((long)LowBits))
        : ((BigInteger)HighBits << 64) | LowBits;
    public static MetricInteger Parse(ReadOnlySpan<char> text)
    {
        if (text.StartsWith("-", StringComparison.Ordinal)
            && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var signed))
            return FromSigned64(signed);
        if (UInt128.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var unsigned))
            return FromUnsigned128(unsigned);
        throw new FormatException("The integer must be an exact signed64 or unsigned128 value.");
    }
    public bool TryFormat(Span<char> destination, out int written)
        => Negative ? unchecked((long)LowBits).TryFormat(destination, out written, provider: CultureInfo.InvariantCulture)
            : (((UInt128)HighBits << 64) | LowBits).TryFormat(destination, out written, provider: CultureInfo.InvariantCulture);
}

public sealed class MetricIntegerJsonConverter : JsonConverter<MetricInteger>
{
    public override MetricInteger Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String) throw new JsonException("An exact integer requires decimal text.");
        Span<char> text = stackalloc char[40];
        int count;
        try { count = reader.CopyString(text); }
        catch (ArgumentException error) { throw new JsonException("The exact integer exceeds its value contract.", error); }
        try { return MetricInteger.Parse(text[..count]); }
        catch (FormatException error) { throw new JsonException(error.Message, error); }
    }
    public override void Write(Utf8JsonWriter writer, MetricInteger value, JsonSerializerOptions options)
    {
        Span<char> text = stackalloc char[40];
        if (!value.TryFormat(text, out var written)) throw new JsonException("The exact integer exceeds its value contract.");
        writer.WriteStringValue(text[..written]);
    }
}

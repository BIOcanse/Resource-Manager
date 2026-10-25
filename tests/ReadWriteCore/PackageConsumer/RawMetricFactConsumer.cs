using System.Text.Json;
using ResourceManager.App.Domain.Metrics;
using ResourceManager.ReadWrite;

internal static class RawMetricFactConsumer
{
    internal static void Run()
    {
        foreach (var integer in new[] { MetricInteger.FromSigned64(long.MinValue), new MetricInteger(0),
            new MetricInteger(ulong.MaxValue), MetricInteger.FromUnsigned128(UInt128.MaxValue) })
        {
            var value = new FieldValue(Integer: integer);
            var json = JsonSerializer.Serialize(value, ReadWriteJsonContext.Default.FieldValue);
            var restored = JsonSerializer.Deserialize(json, ReadWriteJsonContext.Default.FieldValue);
            if (restored?.Integer != integer || restored.Number is not null)
                throw new InvalidDataException("Exact raw integer failed source-generated JSON round trip.");
        }
        foreach (var json in new[] { "{\"integer\":340282366920938463463374607431768211455}",
            "{\"integer\":\"340282366920938463463374607431768211456\"}",
            "{\"integer\":\"-9223372036854775809\"}" })
        {
            try { _ = JsonSerializer.Deserialize(json, ReadWriteJsonContext.Default.FieldValue); }
            catch (JsonException) { continue; }
            throw new InvalidDataException("Exact raw integer accepted an invalid wire payload.");
        }
        Console.WriteLine("Raw metric fact contract passed: signed64/unsigned128 precision and source-generated wire boundaries.");
    }
}

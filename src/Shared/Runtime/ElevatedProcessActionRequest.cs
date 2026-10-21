using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ResourceManager.Shared.Runtime;

public sealed record ProcessActionTarget(int ProcessId, string ProcessStartKey);
public sealed record ProcessActionBatch(string Action, IReadOnlyList<ProcessActionTarget> Targets)
{
    public void Validate()
    {
        if (Action is not ("terminate" or "dump") || Targets is null || Targets.Count is < 1 or > 64)
            throw new InvalidDataException("Invalid process operation.");
        var pids = new HashSet<int>();
        foreach (var target in Targets)
            if (target is null || target.ProcessId <= 0 || !pids.Add(target.ProcessId)
                || !ulong.TryParse(target.ProcessStartKey, NumberStyles.None, CultureInfo.InvariantCulture, out var key)
                || key == 0 || key > long.MaxValue || key.ToString(CultureInfo.InvariantCulture) != target.ProcessStartKey)
                throw new InvalidDataException("An exact process creation identity is required.");
    }
}

public sealed record ElevatedProcessActionRequest(string OperationId, int ParentProcessId, long ParentCreatedUtcTicks,
    int ServiceProcessId, long ServiceCreatedUtcTicks, ulong ServiceInstanceId, ProcessActionBatch Batch)
{
    public const string Argument = "--elevated-process-action";
    public const int MaximumArgumentBytes = 16 * 1024;
    public const int MaximumResultBytes = 256 * 1024;
    [JsonIgnore] public string PipeName => "ResourceManager.ProcessAction." + OperationId;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public void Validate()
    {
        if (!Guid.TryParseExact(OperationId, "N", out _) || ParentProcessId <= 0 || ParentCreatedUtcTicks <= 0
            || ServiceProcessId <= 0 || ServiceCreatedUtcTicks <= 0 || ServiceInstanceId == 0 || Batch is null)
            throw new InvalidDataException("Invalid process-action binding.");
        Batch.Validate();
    }
    public string Encode()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
        if (bytes.Length > MaximumArgumentBytes) throw new InvalidDataException("Process action exceeds its argument bound.");
        return Convert.ToBase64String(bytes);
    }
    public static ElevatedProcessActionRequest Decode(string encoded)
    {
        if (encoded.Length > (MaximumArgumentBytes + 2) / 3 * 4) throw new InvalidDataException("Process action exceeds its argument bound.");
        var bytes = Convert.FromBase64String(encoded);
        if (bytes.Length > MaximumArgumentBytes) throw new InvalidDataException("Process action exceeds its argument bound.");
        var request = JsonSerializer.Deserialize<ElevatedProcessActionRequest>(bytes, JsonOptions)
            ?? throw new InvalidDataException("Missing process action.");
        request.Validate();
        return request;
    }
    public static async Task WriteResultAsync(Stream stream, string result, CancellationToken cancellationToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(result);
        if (bytes.Length is <= 0 or > MaximumResultBytes) throw new InvalidDataException("Invalid process result size.");
        var length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await stream.WriteAsync(length, cancellationToken);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
    public static async Task<string> ReadResultAsync(Stream stream, CancellationToken cancellationToken)
    {
        var length = new byte[4];
        await stream.ReadExactlyAsync(length, cancellationToken);
        var count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count is <= 0 or > MaximumResultBytes) throw new InvalidDataException("Invalid process result size.");
        var data = new byte[count];
        await stream.ReadExactlyAsync(data, cancellationToken);
        return new System.Text.UTF8Encoding(false, true).GetString(data);
    }
}

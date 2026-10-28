using System.Text.Json;
using ResourceManager.Shared.BusinessBlocks.State;

namespace ResourceManager.Shared.BusinessBlocks;

public sealed partial class NativeControl
{
    public unsafe JsonElement SnapshotJson()
    {
        ThrowIfDisposed();
        var result = Snapshot(handle, null, 0, out var needed);
        if (result != 5) Check(result);
        while (true)
        {
            var buffer = new byte[checked((int)needed)];
            fixed (byte* data = buffer)
            {
                result = Snapshot(handle, data, (nuint)buffer.Length, out needed);
                if (result == 5) continue;
                Check(result);
            }
            using var document = JsonDocument.Parse(buffer.AsMemory(0, checked((int)needed)));
            return document.RootElement.Clone();
        }
    }
    public BusinessBlocksSnapshot Read()
    {
        var json = SnapshotJson();
        var now = DateTimeOffset.UtcNow;
        var program = json.GetProperty("program").GetInt32();
        static string? Name(JsonElement row) => row.TryGetProperty("name", out var name) ? name.GetString() : null;
        static BusinessBlockState State(JsonElement value) => Enum.Parse<BusinessBlockState>(value.GetString()!, true);
        static NoValueReason? Absence(JsonElement row) => row.TryGetProperty("noValueReason", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() switch { "not_published" => NoValueReason.NotPublished, "owner_stopped" => NoValueReason.OwnerStopped, "read_failed" => NoValueReason.ReadFailed, _ => throw new InvalidOperationException("Invalid native absence reason.") } : null;
        var blocks = json.GetProperty("blocks").EnumerateArray().ToArray();
        var blockNames = blocks.ToDictionary(row => row.GetProperty("uid").GetInt32(), Name);
        BusinessBlockItem Item(JsonElement row)
        {
            var blockUid = row.TryGetProperty("blockUid", out var block) ? block.GetInt32() : (int?)null;
            var blockName = blockUid is { } id && blockNames.TryGetValue(id, out var name) ? name : null;
            return new(row.GetProperty("uid").GetInt32(), blockUid,
                row.TryGetProperty("value", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                    ? value.GetBoolean() : null, Absence(row))
            {
                Name = Name(row),
                BlockName = blockName
            };
        }
        BusinessBlockDecision Decision(JsonElement row)
        {
            BusinessBlockReason? why = null;
            var reason = row.GetProperty("reason");
            if (reason.ValueKind != JsonValueKind.Null)
                why = new((BusinessBlockRequestKind)reason.GetProperty("kind").GetInt32(), reason.GetProperty("owner").GetString()!,
                    reason.GetProperty("cause").GetString()!, unchecked((long)reason.GetProperty("planVersion").GetUInt64()));
            return new BusinessBlockDecision(row.GetProperty("sequence").GetInt64(), row.GetProperty("node").GetInt32(),
                row.GetProperty("source").GetProperty("cause").GetString()!, why,
                row.GetProperty("shouldRun").EnumerateArray().Select(value => value.GetInt32()).ToArray(),
                row.GetProperty("items").EnumerateArray().Select(Item).ToArray());
        }
        var decisions = json.GetProperty("decisions").EnumerateArray().Select(Decision).ToArray();
        var currentDecisions = json.GetProperty("currentDecisions").EnumerateArray().Select(Decision).ToArray();
        return new(now, options.HistoryCapacity,
            blocks.Where(row => row.GetProperty("uid").GetInt32() != program).Select(row => new BusinessBlockFacts(row.GetProperty("uid").GetInt32(),
                State(row.GetProperty("state")), row.GetProperty("parent").GetInt32(),
                row.GetProperty("inputs").EnumerateArray().Select(value => value.GetInt32()).ToArray()) { Name = Name(row) }).ToArray(),
            // Native history carries ordering/run numbers, not wall-clock timestamps. MinValue denotes unreported time.
            json.GetProperty("transitions").EnumerateArray().Select(row => new BusinessBlockControlTransition(row.GetProperty("sequence").GetInt64(), DateTimeOffset.MinValue,
                row.GetProperty("uid").GetInt32(), State(row.GetProperty("from")), State(row.GetProperty("to")))).ToArray())
        {
            RegistryVersion = json.GetProperty("registryVersion").GetString()!, Program = program,
            ProgramName = Name(blocks.Single(row => row.GetProperty("uid").GetInt32() == program)), Graph = Graph.Nodes,
            ShuttingDown = json.GetProperty("shuttingDown").GetBoolean(), Items = json.GetProperty("items").EnumerateArray().Select(Item).ToArray(),
            Decisions = decisions, CurrentDecisions = currentDecisions,
            NativeSnapshot = json
        };
    }
}

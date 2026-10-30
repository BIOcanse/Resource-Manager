using System.Buffers;
using System.Text.Json;
using ResourceManager.App.Domain.Settings;
using ResourceManager.App.Infrastructure.Settings;

namespace ResourceManager.App.Endpoints;

public static partial class ResourceManagerEndpointRouteBuilderExtensions
{
    /// <summary>Projects the published tier resolution for the settings page: per option the effective value,
    /// unit, range, five tier cells, current category, source and apply mode; restart options also carry the
    /// service's running value and whether the files hold a different value for the next start.</summary>
    private static MemoryCpuTierPayload MemoryCpuTierProjection(MemoryCpuTierResolution resolution, RuntimeConfigState? runtime)
    {
        var options = new List<MemoryCpuTierOptionPayload>(resolution.Options.Length);
        foreach (var option in resolution.Options)
        {
            var tiers = option.Tiers.Select(static cell => new MemoryCpuTierCellPayload(ToJson(cell.Value), cell.Category)).ToArray();
            var range = option.Range.Kind == MemoryCpuTierRangeKinds.Integer
                ? new MemoryCpuTierRangePayload(option.Range.Kind, option.Range.Minimum, option.Range.Maximum, option.Range.Unset, null)
                : new MemoryCpuTierRangePayload(option.Range.Kind, null, null, null, option.Range.Choices);
            JsonElement? running = null;
            bool? pending = null;
            if (option.Apply == MemoryCpuTierApplyModes.Restart)
            {
                var (now, next) = RunningAndNextStart(option.Name, runtime);
                running = ToJson(now);
                pending = now != next;
            }
            options.Add(new(option.Name, ToJson(option.Value), option.Unit, range, tiers, option.Category, option.Source,
                option.Apply, running, pending));
        }
        var config = runtime is { } state
            ? new MemoryCpuTierRuntimeConfigPayload(state.Writable, state.Reason,
                state.Files.Select(static file => new MemoryCpuTierRuntimeConfigFilePayload(file.Process, file.Path)).ToArray(),
                RuntimeConfigWriter.NativeUiRunningUnavailable, state.LastFailure)
            : new MemoryCpuTierRuntimeConfigPayload(false, RuntimeConfigWriter.NotInstalledReason, [],
                RuntimeConfigWriter.NativeUiRunningUnavailable, null);
        return new(resolution.Tier, options, config,
            resolution.Ignored.Select(static ignored => new MemoryCpuTierIgnoredPayload(ignored.Code, ignored.Path)).ToArray());
    }

    /// <summary>The service's running value (read at startup) and the value its runtimeconfig holds for the
    /// next start. Without a writer the service runs with the runtime's own values and nothing is written.</summary>
    private static (MemoryCpuTierValue Running, MemoryCpuTierValue NextStart) RunningAndNextStart(string name, RuntimeConfigState? runtime)
    {
        var running = runtime?.Running ?? new RuntimeGcSettings(
            RuntimeConfigWriter.ReadInteger(RuntimeConfigWriter.Gen0MaxBudgetKey),
            (int)(RuntimeConfigWriter.ReadInteger(RuntimeConfigWriter.ConserveMemoryKey) ?? 0));
        var next = runtime?.NextStart ?? running;
        return name switch
        {
            MemoryCpuTierOptionNames.GcGen0MaxBudget => (Gen0(running), Gen0(next)),
            MemoryCpuTierOptionNames.GcConserveMemory => (MemoryCpuTierValue.Of(running.ConserveMemory), MemoryCpuTierValue.Of(next.ConserveMemory)),
            _ => throw new InvalidOperationException($"Memory/CPU tier option '{name}' has no runtime value."),
        };
        static MemoryCpuTierValue Gen0(RuntimeGcSettings settings)
            => settings.Gen0MaxBudget is { } bytes ? MemoryCpuTierValue.Of(bytes) : MemoryCpuTierValue.Unset;
    }

    private static JsonElement ToJson(MemoryCpuTierValue value)
    {
        var buffer = new ArrayBufferWriter<byte>(32);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            if (value.Integer is { } integer) writer.WriteNumberValue(integer);
            else if (value.Choice is { } choice) writer.WriteStringValue(choice);
            else writer.WriteNullValue();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MemoryMeasurement;

public sealed record CpuModuleWeight(string Module, string Part, double Exclusive, double Inclusive);
public sealed record CpuPartWeight(string Part, double Exclusive, double Inclusive);

public sealed class CpuReport
{
    public string Unit { get; init; } = "sample-weight";
    public double TotalObservedStackWeight { get; init; }
    public bool IsSchedulerCpuTime => false;
    public int ProfileCount { get; init; }
    public long SampleCount { get; init; }
    public long EventCount { get; init; }
    public IReadOnlyList<CpuModuleWeight> Modules { get; init; } = Array.Empty<CpuModuleWeight>();
    public IReadOnlyList<CpuPartWeight> Parts { get; init; } = Array.Empty<CpuPartWeight>();
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
}

/// <summary>Reads existing speedscope JSON; never attaches to or controls a process.</summary>
public static class CpuSamples
{
    private const int MaxFileBytes = 256 * 1024 * 1024;
    private const int MaxFrames = 1_000_000;
    private const int MaxProfiles = 100_000;
    private const int MaxStack = 4096;
    private const long MaxSamplesAndEvents = 20_000_000;
    private static readonly string[] PartNames = ["own", "external", "runtime-windows", "unresolved"];

    public static CpuReport Read(string path, Func<string, string> classify)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(classify);
        byte[] bytes;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                   64 * 1024, FileOptions.SequentialScan))
        {
            if (stream.Length > MaxFileBytes)
                throw Bad("File exceeds the 256 MiB limit.");
            bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
                throw Bad("File changed while reading or exceeds the 256 MiB limit.");
        }

        try
        {
            var memory = bytes.AsMemory();
            if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf)
                memory = memory[3..];
            using var document = JsonDocument.Parse(memory, new JsonDocumentOptions { MaxDepth = 128 });
            return new Reader(classify).Read(document.RootElement);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("Invalid speedscope JSON: " + error.Message, error);
        }
    }

    private static InvalidDataException Bad(string message) => new("Invalid speedscope input: " + message);

    private static JsonElement Required(JsonElement owner, string property)
    {
        if (owner.ValueKind != JsonValueKind.Object || !owner.TryGetProperty(property, out var result))
            throw Bad("Missing property '" + property + "'.");
        return result;
    }

    private static JsonElement Array(JsonElement owner, string property, int limit)
    {
        var result = Required(owner, property);
        if (result.ValueKind != JsonValueKind.Array)
            throw Bad("'" + property + "' must be an array.");
        if (result.GetArrayLength() > limit)
            throw Bad("'" + property + "' exceeds its limit of " + limit + ".");
        return result;
    }

    private static string Text(JsonElement owner, string property)
    {
        var value = Required(owner, property);
        if (value.ValueKind != JsonValueKind.String)
            throw Bad("'" + property + "' must be a string.");
        return value.GetString()!;
    }

    private static double Number(JsonElement value, string label)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) || !double.IsFinite(result))
            throw Bad(label + " must be a finite number.");
        return result;
    }

    private static int FrameIndex(JsonElement value, int frameCount)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result) || result < 0 || result >= frameCount)
            throw Bad("Frame reference must be an integer within shared.frames.");
        return result;
    }

    private static double Add(double left, double right)
    {
        var result = left + right;
        if (!double.IsFinite(result))
            throw Bad("Accumulated weight exceeds the finite numeric range.");
        return result;
    }

    private static string Module(string frameName)
    {
        var separator = frameName.IndexOf('!');
        if (separator <= 0)
            return "unresolved";
        var name = frameName[..separator].Trim();
        name = Path.GetFileName(name.Replace('\\', '/'));
        if (name.EndsWith(".ni.dll", StringComparison.OrdinalIgnoreCase))
            name = name[..^7];
        else if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];
        return string.IsNullOrWhiteSpace(name) ? "unresolved" : name;
    }

    private sealed class ModuleTotals(string name, int part)
    {
        public string Name { get; } = name;
        public int Part { get; } = part;
        public double Exclusive;
        public double Inclusive;
    }

    private readonly record struct ActiveSpan(int Count, double Start);

    private sealed class Reader(Func<string, string> classify)
    {
        private readonly List<ModuleTotals> modules = [];
        private readonly double[] partExclusive = new double[4];
        private readonly double[] partInclusive = new double[4];
        private int[] frames = [];
        private readonly int[] stack = new int[MaxStack];
        private long samples;
        private long events;
        private double total;
        private bool? timeUnit;
        private int unfinishedProfiles;

        public CpuReport Read(JsonElement root)
        {
            var frameArray = Array(Required(root, "shared"), "frames", MaxFrames);
            frames = new int[frameArray.GetArrayLength()];
            var moduleIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var frameIterator = frameArray.EnumerateArray();
            var frameNumber = 0;
            while (frameIterator.MoveNext())
            {
                var name = Module(Text(frameIterator.Current, "name"));
                if (!moduleIds.TryGetValue(name, out var module))
                {
                    var partName = classify(name);
                    var part = System.Array.IndexOf(PartNames, partName);
                    if (part < 0)
                        throw Bad("Classifier returned an unsupported part for module '" + name + "'.");
                    // Names without a module are unresolved even if a broad classifier matches them.
                    if (name.Equals("unresolved", StringComparison.OrdinalIgnoreCase))
                        part = 3;
                    module = modules.Count;
                    moduleIds.Add(name, module);
                    modules.Add(new ModuleTotals(name, part));
                }
                frames[frameNumber++] = module;
            }

            var profileArray = Array(root, "profiles", MaxProfiles);
            var iterator = profileArray.EnumerateArray();
            var profileNumber = 0;
            while (iterator.MoveNext())
            {
                try
                {
                    ReadProfile(iterator.Current);
                }
                catch (InvalidDataException error)
                {
                    throw new InvalidDataException("Speedscope profile " + profileNumber + ": " + error.Message, error);
                }
                profileNumber++;
            }

            var notes = new List<string>
            {
                "Weights describe observed stack residence, not scheduler-measured CPU execution time; concurrent profiles may overlap.",
                "Exclusive weight belongs to the leaf module. Inclusive weight counts each distinct module, and each distinct part, once per stack.",
                "Empty stacks contribute no observed stack weight. Inclusive totals can exceed the observed total because a stack can contain multiple modules or parts."
            };
            if (timeUnit != true)
                notes.Add("Unit none/samples is retained as sample weight; it is not converted into milliseconds.");
            if (unfinishedProfiles > 0)
                notes.Add(unfinishedProfiles + " evented profile(s) ended with open frames; those frames remain active through endValue.");

            return new CpuReport
            {
                Unit = timeUnit == true ? "milliseconds" : "sample-weight",
                TotalObservedStackWeight = total,
                ProfileCount = profileNumber,
                SampleCount = samples,
                EventCount = events,
                Modules = modules.Where(m => m.Exclusive != 0 || m.Inclusive != 0)
                    .OrderByDescending(m => m.Exclusive).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(m => new CpuModuleWeight(m.Name, PartNames[m.Part], m.Exclusive, m.Inclusive)).ToArray(),
                Parts = Enumerable.Range(0, 4).Select(i => new CpuPartWeight(PartNames[i], partExclusive[i], partInclusive[i])).ToArray(),
                Notes = notes
            };
        }

        private void ReadProfile(JsonElement profile)
        {
            var unit = Text(profile, "unit");
            var scale = unit switch
            {
                "seconds" => 1000d,
                "milliseconds" => 1d,
                "microseconds" => 0.001d,
                "nanoseconds" => 0.000001d,
                "none" or "samples" => 1d,
                _ => throw Bad("Unsupported unit '" + unit + "'.")
            };
            var isTime = unit is not ("none" or "samples");
            if (timeUnit.HasValue && timeUnit.Value != isTime)
                throw Bad("Time profiles and unit none/samples profiles cannot be combined in one report.");
            timeUnit = isTime;
            var start = Number(Required(profile, "startValue"), "startValue");
            var end = Number(Required(profile, "endValue"), "endValue");
            if (end < start || !double.IsFinite(end - start) || !double.IsFinite((end - start) * scale))
                throw Bad("Profile bounds must be ordered and have a finite normalized duration.");
            switch (Text(profile, "type"))
            {
                case "sampled": ReadSampled(profile, start, end, scale, isTime); break;
                case "evented": ReadEvented(profile, start, end, scale); break;
                default: throw Bad("Only sampled and evented profiles are supported.");
            }
        }

        private void CountInput(int count, bool isEvent)
        {
            if (samples + events + count > MaxSamplesAndEvents)
                throw Bad("Combined samples/events exceed the 20 million limit.");
            if (isEvent) events += count;
            else samples += count;
        }

        private void ReadSampled(JsonElement profile, double start, double end, double scale, bool isTime)
        {
            var sampleArray = Array(profile, "samples", (int)MaxSamplesAndEvents);
            var count = sampleArray.GetArrayLength();
            CountInput(count, false);
            var hasWeights = profile.TryGetProperty("weights", out var weights) && weights.ValueKind != JsonValueKind.Null;
            if (hasWeights && (weights.ValueKind != JsonValueKind.Array || weights.GetArrayLength() != count))
                throw Bad("weights must have exactly one entry per sample.");
            var weightIterator = hasWeights ? weights.EnumerateArray() : default;
            var defaultWeight = isTime && count != 0 ? ((end - start) / count) * scale : 1d;
            var seenModules = new HashSet<int>();
            var iterator = sampleArray.EnumerateArray();
            while (iterator.MoveNext())
            {
                var sample = iterator.Current;
                if (sample.ValueKind != JsonValueKind.Array || sample.GetArrayLength() > MaxStack)
                    throw Bad("Each sample must be an array with at most 4096 frames.");
                var weight = defaultWeight;
                if (hasWeights)
                {
                    if (!weightIterator.MoveNext()) throw Bad("Missing sample weight.");
                    weight = Number(weightIterator.Current, "Sample weight") * scale;
                }
                if (weight < 0 || !double.IsFinite(weight))
                    throw Bad("Sample weight must be nonnegative and finite after normalization.");
                seenModules.Clear();
                var seenParts = 0;
                var leaf = -1;
                var stackIterator = sample.EnumerateArray();
                while (stackIterator.MoveNext())
                {
                    leaf = frames[FrameIndex(stackIterator.Current, frames.Length)];
                    var module = modules[leaf];
                    if (seenModules.Add(leaf))
                        module.Inclusive = Add(module.Inclusive, weight);
                    var partBit = 1 << module.Part;
                    if ((seenParts & partBit) == 0)
                        partInclusive[module.Part] = Add(partInclusive[module.Part], weight);
                    seenParts |= partBit;
                }
                if (leaf >= 0)
                    Exclusive(leaf, weight);
            }
        }

        private void Exclusive(int moduleId, double weight)
        {
            var module = modules[moduleId];
            module.Exclusive = Add(module.Exclusive, weight);
            partExclusive[module.Part] = Add(partExclusive[module.Part], weight);
            total = Add(total, weight);
        }

        private void ReadEvented(JsonElement profile, double start, double end, double scale)
        {
            var eventArray = Array(profile, "events", (int)MaxSamplesAndEvents);
            CountInput(eventArray.GetArrayLength(), true);
            var activeModules = new Dictionary<int, ActiveSpan>();
            var partCounts = new int[4];
            var partStarts = new double[4];
            var depth = 0;
            var previous = start;
            var iterator = eventArray.EnumerateArray();
            while (iterator.MoveNext())
            {
                var item = iterator.Current;
                var at = Number(Required(item, "at"), "Event time");
                if (at < previous || at < start || at > end)
                    throw Bad("Event times must be monotonic and within profile bounds.");
                if (depth > 0)
                    Exclusive(frames[stack[depth - 1]], (at - previous) * scale);
                var frame = FrameIndex(Required(item, "frame"), frames.Length);
                var moduleId = frames[frame];
                var part = modules[moduleId].Part;
                switch (Text(item, "type"))
                {
                    case "O":
                        if (depth == MaxStack) throw Bad("Evented stack exceeds 4096 frames.");
                        stack[depth++] = frame;
                        if (activeModules.TryGetValue(moduleId, out var active))
                            activeModules[moduleId] = active with { Count = active.Count + 1 };
                        else
                            activeModules.Add(moduleId, new ActiveSpan(1, at));
                        if (partCounts[part]++ == 0) partStarts[part] = at;
                        break;
                    case "C":
                        if (depth == 0 || stack[depth - 1] != frame)
                            throw Bad("Close event must match the currently open top frame.");
                        depth--;
                        var closing = activeModules[moduleId];
                        if (closing.Count == 1)
                        {
                            modules[moduleId].Inclusive = Add(modules[moduleId].Inclusive, (at - closing.Start) * scale);
                            activeModules.Remove(moduleId);
                        }
                        else
                            activeModules[moduleId] = closing with { Count = closing.Count - 1 };
                        if (--partCounts[part] == 0)
                            partInclusive[part] = Add(partInclusive[part], (at - partStarts[part]) * scale);
                        break;
                    default: throw Bad("Event type must be O (open) or C (close).");
                }
                previous = at;
            }

            if (depth > 0)
            {
                Exclusive(frames[stack[depth - 1]], (end - previous) * scale);
                unfinishedProfiles++;
            }
            using var activeIterator = activeModules.GetEnumerator();
            while (activeIterator.MoveNext())
            {
                var active = activeIterator.Current;
                modules[active.Key].Inclusive = Add(modules[active.Key].Inclusive, (end - active.Value.Start) * scale);
            }
            var partIndex = 0;
            while (partIndex < PartNames.Length)
            {
                if (partCounts[partIndex] > 0)
                    partInclusive[partIndex] = Add(partInclusive[partIndex], (end - partStarts[partIndex]) * scale);
                partIndex++;
            }
        }
    }
}

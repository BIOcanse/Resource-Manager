using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using ResourceManager.App.Domain.RuntimeSpecialization;
using ResourceManager.App.Domain.Settings;

namespace ResourceManager.App.Application.Settings;

// S-tier transition boundary: borrow original declaration/table/JSON fields in
// one synchronous call; materialize only the existing published CLR result.
// S-store / the native parameter-source owner remove this source and result bridge.
internal static unsafe partial class NativeMemoryCpuTierParameters
{
    internal static MemoryCpuTierResolution Resolve(CompiledMemoryCpuTierTable table, MemoryCpuTierSettings? settings)
    {
        ArgumentNullException.ThrowIfNull(table);
        var source = new BorrowedSource(table, settings);
        var effective = Invoke(source, 0, out _);
        return new(source.Tier, source.Options.ToImmutable(), new(
            effective.Gen0Present != 0 ? effective.Gen0 : null, checked((int)effective.Conserve),
            new(effective.ReleaseAfterUse != 0, checked((int)effective.ShrinkAfter)),
            TimeSpan.FromMilliseconds(effective.StopMs), TimeSpan.FromMilliseconds(effective.IdentityMs),
            TimeSpan.FromSeconds(effective.BrowserSeconds), TimeSpan.FromSeconds(effective.PlatformSeconds),
            TimeSpan.FromSeconds(effective.StorageSeconds), checked((int)effective.RendererEntries),
            checked((int)effective.SessionsMax), checked((int)effective.SessionsPrebuilt), effective.CloseIdle != 0),
            source.Ignored.ToImmutable());
    }

    internal static MemoryCpuTierRejection? Validate(MemoryCpuTierSettings? settings)
    {
        if (settings is null) return null;
        var source = new BorrowedSource(null, settings);
        Invoke(source, 1, out _);
        return source.Ignored.Count == 0 ? null : source.Ignored[0];
    }

    internal static void ValidateTable(CompiledMemoryCpuTierTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        Invoke(new BorrowedSource(table, null), 2, out _);
    }

    internal static bool TryParse(MemoryCpuTierOptionDefinition option, JsonElement? element, out MemoryCpuTierValue value)
    {
        ArgumentNullException.ThrowIfNull(option);
        var source = new BorrowedSource(null, null) { StandaloneOption = option, StandaloneValue = element };
        Invoke(source, 3, out var parsed);
        value = source.ParseAccepted ? Decode(option, parsed) : parsed.Kind == 2
            ? MemoryCpuTierValue.Of(element!.Value.GetString()!) : Decode(option, parsed);
        return source.ParseAccepted;
    }

    private static Effective Invoke(BorrowedSource source, uint action, out Value parsed)
    {
        var handle = GCHandle.Alloc(source);
        try
        {
            var input = new Input
            {
                Tier = source.Settings?.Tier ?? 0, HasSettings = source.Settings is null ? 0U : 1U,
                MinimumTier = MemoryCpuTierSettings.MinimumTier, DefaultTier = MemoryCpuTierSettings.DefaultTier,
                MaximumTier = MemoryCpuTierSettings.MaximumTier,
                Options = checked((nuint)MemoryCpuTierResolver.Options.Length), Overrides = checked((nuint)source.Overrides.Length)
            };
            var callbacks = new Callbacks { Range = &ReadRange, Value = &ReadValue, Text = &VisitText, Matches = &Matches, HasOverride = &HasOverride, Emit = &Emit };
            Effective effective = default;
            Value result = default;
            var code = NativeMethods.Resolve(GCHandle.ToIntPtr(handle), in callbacks, in input, action, &effective, &result);
            source.Failure?.Throw();
            if (code == 3) throw new InvalidDataException("Memory/CPU tier table has an invalid cell, category or column length.");
            if (code == 5 && action == 3) source.ParseAccepted = false;
            else if (code != 0) throw new InvalidOperationException($"Native memory/CPU tier parameter resolution failed ({code}).");
            parsed = result;
            return effective;
        }
        finally { handle.Free(); GC.KeepAlive(source); }
    }

    private static MemoryCpuTierValue Decode(MemoryCpuTierOptionDefinition option, Value value) => value.Kind switch
    {
        0 => MemoryCpuTierValue.Unset,
        1 => MemoryCpuTierValue.Of(value.Integer),
        2 => MemoryCpuTierValue.Of(option.Range.Choices[checked((int)value.Choice)]),
        _ => throw new InvalidDataException("Invalid native tier value tag.")
    };
    private static BorrowedSource Borrow(nint context) => (BorrowedSource)GCHandle.FromIntPtr(context).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadRange(nint context, nuint index, Range* output, nuint* cells)
    {
        var source = Borrow(context);
        try
        {
            var option = source.Option(index);
            var range = option.Range;
            *output = new Range { Minimum = range.Minimum, Maximum = range.Maximum,
                Kind = range.Kind == MemoryCpuTierRangeKinds.Integer ? 0U : 1U,
                Unset = range.Unset ? 1U : 0U, Choices = checked((nuint)range.Choices.Length) };
            var column = source.Table is null ? default : option.Column(source.Table);
            *cells = column.IsDefault ? 0 : checked((nuint)column.Length);
            return 0;
        }
        catch (Exception error) { source.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ReadValue(nint context, nuint option, nuint child, uint field, Value* output)
    {
        var source = Borrow(context);
        try
        {
            var element = source.Element(option, child, field);
            *output = element is not { } json || json.ValueKind == JsonValueKind.Null ? default
                : json.ValueKind == JsonValueKind.Number && json.TryGetInt64(out var integer) ? new Value { Integer = integer, Kind = 1 }
                : json.ValueKind == JsonValueKind.String ? new Value { Kind = 2 }
                : new Value { Kind = 3 };
            return 0;
        }
        catch (Exception error) { source.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    [ResourceManager.ReadWrite.RuntimeRead(ResourceManager.ReadWrite.ReadVolatilityKind.Fixed)]
    private static int VisitText(nint context, nuint index, nuint child, uint field, nint sink,
        delegate* unmanaged[Cdecl]<nint, ushort*, nuint, int> visitor)
    {
        var source = Borrow(context);
        try
        {
            var text = field switch
            {
                0 => source.Option(index).Name,
                1 => source.Overrides[checked((int)index)].Key,
                2 => source.Option(index).Range.Choices[checked((int)child)],
                6 => source.Option(index).Column(source.Table!)[checked((int)child)]?.Category,
                _ => throw new InvalidDataException("Invalid tier text field.")
            };
            fixed (char* pointer = text) return visitor(sink, (ushort*)pointer, checked((nuint)(text?.Length ?? 0)));
        }
        catch (Exception error) { source.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Matches(nint context, nuint option, nuint child, uint field, ushort* text, nuint length, uint* equal)
    {
        var source = Borrow(context);
        try
        {
            *equal = source.Element(option, child, field)!.Value.ValueEquals(new ReadOnlySpan<char>(text, checked((int)length))) ? 1U : 0U;
            return 0;
        }
        catch (Exception error) { source.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int HasOverride(nint context, nuint option, uint* present)
    {
        var source = Borrow(context);
        try { *present = source.Settings?.Overrides?.ContainsKey(source.Option(option).Name) == true ? 1U : 0U; return 0; }
        catch (Exception error) { source.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Emit(nint context, nuint index, nuint child, uint field, Value* value, uint flag)
    {
        var source = Borrow(context);
        try { source.Accept(index, child, field, *value, flag); return 0; }
        catch (Exception error) { source.Failure = ExceptionDispatchInfo.Capture(error); return 2; }
    }

    private sealed class BorrowedSource(CompiledMemoryCpuTierTable? table, MemoryCpuTierSettings? settings)
    {
        internal CompiledMemoryCpuTierTable? Table { get; } = table;
        internal MemoryCpuTierSettings? Settings { get; } = settings;
        // Ordering borrows the original entries, just as the previous resolver's
        // OrderBy did. Neither JSON documents nor strings are copied/decoded.
        internal KeyValuePair<string, JsonElement>[] Overrides { get; } = settings?.Overrides?.OrderBy(static item => item.Key, StringComparer.Ordinal).ToArray() ?? [];
        internal MemoryCpuTierOptionDefinition? StandaloneOption;
        internal JsonElement? StandaloneValue;
        internal bool ParseAccepted = true;
        internal ExceptionDispatchInfo? Failure;
        internal int Tier;
        internal readonly ImmutableArray<MemoryCpuTierOptionResolution>.Builder Options = ImmutableArray.CreateBuilder<MemoryCpuTierOptionResolution>();
        internal readonly ImmutableArray<MemoryCpuTierRejection>.Builder Ignored = ImmutableArray.CreateBuilder<MemoryCpuTierRejection>();
        private readonly ImmutableArray<MemoryCpuTierCellResolution>.Builder cells = ImmutableArray.CreateBuilder<MemoryCpuTierCellResolution>();

        internal MemoryCpuTierOptionDefinition Option(nuint index) => StandaloneOption ?? MemoryCpuTierResolver.Options[checked((int)index)];
        internal JsonElement? Element(nuint option, nuint child, uint field) => field switch
        {
            0 => Option(option).Column(Table!)[checked((int)child)].Value,
            1 => Overrides[checked((int)child)].Value,
            2 => StandaloneValue,
            3 => Settings!.Overrides![Option(option).Name],
            _ => throw new InvalidDataException("Invalid tier JSON field.")
        };
        internal void Accept(nuint index, nuint child, uint field, Value value, uint flag)
        {
            switch (field)
            {
                case 0: Tier = checked((int)value.Integer); break;
                case 1:
                    cells.Add(new(Decode(Option(index), value), Option(index).Column(Table!)[checked((int)child)].Category));
                    break;
                case 2:
                    var option = Option(index);
                    Options.Add(new(option.Name, option.Unit, option.Range, cells.ToImmutable(), Decode(option, value),
                        option.Column(Table!)[checked((int)child)].Category, flag != 0 ? MemoryCpuTierSources.User : MemoryCpuTierSources.Tier, option.Apply));
                    cells.Clear();
                    break;
                case 3: Ignored.Add(new(MemoryCpuTierResolver.TierOutOfRange, MemoryCpuTierResolver.TierPath)); break;
                case 4: Ignored.Add(new(MemoryCpuTierResolver.UnknownOption, $"{MemoryCpuTierResolver.OverridesPath}.{Overrides[checked((int)index)].Key}")); break;
                case 5: Ignored.Add(new(MemoryCpuTierResolver.ValueOutOfRange, $"{MemoryCpuTierResolver.OverridesPath}.{Overrides[checked((int)index)].Key}")); break;
                default: throw new InvalidDataException("Invalid tier output event.");
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Value { internal long Integer; internal uint Kind, Choice; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Range { internal long Minimum, Maximum; internal uint Kind, Unset; internal nuint Choices; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Input { internal int Tier, MinimumTier, DefaultTier, MaximumTier; internal uint HasSettings; internal nuint Options, Overrides; }
    [StructLayout(LayoutKind.Sequential)]
    private struct Effective
    {
        internal long Gen0, Conserve, StopMs, IdentityMs, BrowserSeconds, PlatformSeconds, StorageSeconds, RendererEntries, SessionsMax, SessionsPrebuilt;
        internal uint Gen0Present, ReleaseAfterUse, ShrinkAfter, CloseIdle;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Callbacks
    {
        internal delegate* unmanaged[Cdecl]<nint, nuint, Range*, nuint*, int> Range;
        internal delegate* unmanaged[Cdecl]<nint, nuint, nuint, uint, Value*, int> Value;
        internal delegate* unmanaged[Cdecl]<nint, nuint, nuint, uint, nint, delegate* unmanaged[Cdecl]<nint, ushort*, nuint, int>, int> Text;
        internal delegate* unmanaged[Cdecl]<nint, nuint, nuint, uint, ushort*, nuint, uint*, int> Matches;
        internal delegate* unmanaged[Cdecl]<nint, nuint, uint*, int> HasOverride;
        internal delegate* unmanaged[Cdecl]<nint, nuint, nuint, uint, Value*, uint, int> Emit;
    }
    private static partial class NativeMethods
    {
        [LibraryImport("ResourceManager.NativeCore", EntryPoint = "rm_memory_cpu_tier_resolve_parameters")]
        [UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
        internal static partial int Resolve(nint context, in Callbacks callbacks, in Input input, uint action, Effective* output, Value* parsed);
    }
}

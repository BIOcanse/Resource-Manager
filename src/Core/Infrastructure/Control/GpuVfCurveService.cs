using System.Collections.Immutable;
using System.Globalization;
using ResourceManager.App.Application.Control;
using ResourceManager.App.Domain.Control;

namespace ResourceManager.App.Infrastructure.Control;

/// <summary>Library and bounded editing state only. No command here writes hardware.</summary>
public sealed class GpuVfCurveService(
    IGpuVfCurvePresetStore store,
    IGpuVfCurveDescriptorProvider descriptors,
    IGpuVfCurveReferences references,
    GpuVfCurveMutationGate mutationGate,
    TimeProvider timeProvider) : IGpuVfCurvePresets, IGpuVfCurveEditor
{
    private readonly Dictionary<string, DraftState> drafts = new(StringComparer.Ordinal);

    public async Task<GpuVfCurveLibraryView> ReadAsync(string? objectId, CancellationToken cancellationToken)
    {
        var descriptor = objectId is null ? null : await descriptors.GetAsync(objectId, cancellationToken).ConfigureAwait(false);
        var catalog = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var items = catalog.Presets.Select(preset =>
        {
            ControlApplyReason? reason = null;
            if (descriptor is not null)
            {
                try
                {
                    GpuVfCurveValidation.Descriptor(descriptor);
                    if (!preset.Binding.Matches(descriptor.Binding!)) throw GpuVfCurveValidation.Error("gpu-vf-curve-binding-mismatch");
                    GpuVfCurveValidation.Points(preset.Points, descriptor);
                }
                catch (GpuVfCurveRequestException error) { reason = error.Reason; }
            }
            return new GpuVfCurveLibraryItem(preset, reason is null, reason);
        }).ToImmutableArray();
        return new(items, descriptor?.Rules ?? GpuVfCurveSettings.ProductDefaults.Rules);
    }

    public async Task<GpuVfCurvePreset> SaveAsync(string curveId, GpuVfCurveSaveRequest request,
        CancellationToken cancellationToken)
    {
        await mutationGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var descriptor = await descriptors.GetAsync(request.ObjectId, cancellationToken).ConfigureAwait(false);
            GpuVfCurveValidation.Expected(descriptor, request.ExpectedBinding);
            var catalog = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            var existing = catalog.Presets.FirstOrDefault(p => p.Id == curveId);
            if (existing is null && curveId != "new" && request.CloneFromId is null)
                throw GpuVfCurveValidation.Error("gpu-vf-curve-preset-not-found");
            var source = request.CloneFromId is null ? existing
                : catalog.Presets.FirstOrDefault(p => p.Id == request.CloneFromId)
                    ?? throw GpuVfCurveValidation.Error("gpu-vf-curve-preset-not-found");
            if (source is not null && !source.Binding.Matches(descriptor.Binding!))
                throw GpuVfCurveValidation.Error("gpu-vf-curve-binding-mismatch");
            if (request.DraftId is not null && request.Points is not null)
                throw GpuVfCurveValidation.Error("gpu-vf-curve-save-input-ambiguous");
            IReadOnlyList<GpuVfCurvePoint> points;
            if (request.DraftId is not null)
            {
                var draft = FindDraft(request.DraftId, request.ObjectId);
                if (draft.Descriptor.Binding != descriptor.Binding)
                    throw GpuVfCurveValidation.Error("gpu-vf-curve-binding-changed");
                points = draft.Points;
            }
            else points = request.Points ?? source?.Points ?? descriptor.Points.Select(p =>
                new GpuVfCurvePoint(p.PointId, p.VoltageMicrovolts, p.CurrentOffsetKhz)).ToImmutableArray();
            var normalized = GpuVfCurveValidation.Points(points, descriptor);
            var scope = request.BindingScope ?? source?.Binding.Scope ?? descriptor.Rules.DefaultBindingScope;
            if (scope is not (GpuVfCurveBindingScopes.Device or GpuVfCurveBindingScopes.Model)
                || scope == GpuVfCurveBindingScopes.Model && string.IsNullOrWhiteSpace(descriptor.Binding!.Model))
                throw GpuVfCurveValidation.Error("gpu-vf-curve-binding-scope-invalid");
            var binding = descriptor.Binding! with { Scope = scope };
            var createsNew = existing is null || request.CloneFromId is not null;
            var id = createsNew ? Guid.NewGuid().ToString("N") : existing!.Id;
            var name = ValidateName(request.Name, descriptor.Rules);
            if (catalog.Presets.Any(p => p.Id != id && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                throw GpuVfCurveValidation.Error("gpu-vf-curve-name-conflict");
            if (createsNew && catalog.Presets.Count >= descriptor.Rules.MaximumPresetCount)
                throw new GpuVfCurveRequestException(new("gpu-vf-curve-preset-count-limit", new(Maximum: descriptor.Rules.MaximumPresetCount)));
            var now = timeProvider.GetUtcNow();
            var saved = new GpuVfCurvePreset(id, name, binding, normalized,
                createsNew ? now : existing!.CreatedAt, now);
            await store.SaveAsync(new(catalog.Presets.Where(p => p.Id != id).Append(saved).ToImmutableArray()), cancellationToken).ConfigureAwait(false);
            return saved;
        }
        finally { mutationGate.Semaphore.Release(); }
    }

    public async Task DeleteAsync(string curveId, CancellationToken cancellationToken)
    {
        await mutationGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var used = await references.GetAsync(curveId, cancellationToken).ConfigureAwait(false);
            if (used.Count != 0) throw new GpuVfCurveRequestException(new("gpu-vf-curve-in-use"), used);
            var catalog = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            await store.SaveAsync(new(catalog.Presets.Where(p => p.Id != curveId).ToImmutableArray()), cancellationToken).ConfigureAwait(false);
        }
        finally { mutationGate.Semaphore.Release(); }
    }

    public async Task<IReadOnlyList<GpuVfCurveReference>> ReferencesAsync(string curveId, CancellationToken cancellationToken)
    {
        await mutationGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await references.GetAsync(curveId, cancellationToken).ConfigureAwait(false); }
        finally { mutationGate.Semaphore.Release(); }
    }

    public async Task<GpuVfCurveExecution> ResolveAsync(string objectId, string curveId, CancellationToken cancellationToken)
    {
        var descriptor = await descriptors.GetAsync(objectId, cancellationToken).ConfigureAwait(false);
        GpuVfCurveValidation.Descriptor(descriptor);
        var catalog = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var preset = catalog.Presets.FirstOrDefault(p => p.Id == curveId)
            ?? throw GpuVfCurveValidation.Error("gpu-vf-curve-preset-not-found");
        if (!preset.Binding.Matches(descriptor.Binding!)) throw GpuVfCurveValidation.Error("gpu-vf-curve-binding-mismatch");
        return new(preset.Binding, GpuVfCurveValidation.Points(preset.Points, descriptor));
    }

    // Compatibility owner: one-time core-clock-offset migration. Delete only when all supported upgrades start after this release.
    public async Task<string> EnsureUniformAsync(string objectId, double offsetKhz, CancellationToken cancellationToken)
    {
        var descriptor = await descriptors.GetAsync(objectId, cancellationToken).ConfigureAwait(false);
        GpuVfCurveValidation.Descriptor(descriptor);
        var points = GpuVfCurveValidation.Points(descriptor.Points.Select(p =>
            new GpuVfCurvePoint(p.PointId, p.VoltageMicrovolts, offsetKhz)).ToImmutableArray(), descriptor);
        var catalog = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var binding = descriptor.Binding! with { Scope = GpuVfCurveBindingScopes.Device };
        var existing = catalog.Presets.FirstOrDefault(p => p.Binding == binding && p.Points.SequenceEqual(points));
        if (existing is not null) return existing.Id;
        if (catalog.Presets.Count >= descriptor.Rules.MaximumPresetCount)
            throw GpuVfCurveValidation.Error("gpu-vf-curve-preset-count-limit");
        var name = ValidateName("迁移自偏移 " + (offsetKhz > 0 ? "+" : "")
            + (offsetKhz / 1000).ToString("R", CultureInfo.InvariantCulture) + " MHz", descriptor.Rules);
        var id = Guid.NewGuid().ToString("N");
        var now = timeProvider.GetUtcNow();
        var preset = new GpuVfCurvePreset(id, name, binding, points, now, now);
        await store.SaveAsync(new(catalog.Presets.Append(preset).ToImmutableArray()), cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task<GpuVfCurveDraft?> ExecuteAsync(GpuVfCurveEditorCommand command, CancellationToken cancellationToken)
    {
        await mutationGate.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (command.Operation == "close")
            {
                if (command.DraftId is not null)
                {
                    _ = FindDraft(command.DraftId, command.ObjectId);
                    drafts.Remove(command.DraftId);
                }
                return null;
            }
            if (command.Operation is not ("begin" or "move-selection" or "align-left" or "align-right"
                or "step" or "reset-default" or "uniform-offset" or "undo" or "redo"))
                throw GpuVfCurveValidation.Error("gpu-vf-curve-command-invalid");
            var current = await descriptors.GetAsync(command.ObjectId, cancellationToken).ConfigureAwait(false);
            GpuVfCurveValidation.Expected(current, command.ExpectedBinding);
            if (command.Operation == "begin")
            {
                if (drafts.Count >= current.Rules.MaximumDraftCount)
                    throw new GpuVfCurveRequestException(new("gpu-vf-curve-draft-count-limit", new(Maximum: current.Rules.MaximumDraftCount)));
                var points = command.CurveId is { } curveId
                    ? (await ResolveAsync(command.ObjectId, curveId, cancellationToken).ConfigureAwait(false)).Points
                    : current.Points.Select(p => new GpuVfCurvePoint(p.PointId, p.VoltageMicrovolts, p.CurrentOffsetKhz)).ToImmutableArray();
                var id = Guid.NewGuid().ToString("N");
                var state = new DraftState(id, current, GpuVfCurveValidation.Points(points, current));
                drafts.Add(id, state);
                return state.View();
            }
            var draft = FindDraft(command.DraftId, command.ObjectId);
            if (draft.Descriptor.Binding != current.Binding) throw GpuVfCurveValidation.Error("gpu-vf-curve-binding-changed");
            if (command.Operation == "undo")
            {
                if (draft.Undo.Count != 0)
                {
                    var previous = draft.Undo[^1];
                    GpuVfCurveValidation.Points(previous, current);
                    draft.Redo.Add(draft.Points);
                    draft.Points = previous;
                    draft.Undo.RemoveAt(draft.Undo.Count - 1);
                }
            }
            else if (command.Operation == "redo")
            {
                if (draft.Redo.Count != 0)
                {
                    var next = draft.Redo[^1];
                    GpuVfCurveValidation.Points(next, current);
                    draft.Undo.Add(draft.Points);
                    draft.Points = next;
                    draft.Redo.RemoveAt(draft.Redo.Count - 1);
                }
            }
            else
            {
                var next = Edit(draft, command);
                GpuVfCurveValidation.Points(next, current);
                if (!next.SequenceEqual(draft.Points))
                {
                    draft.Undo.Add(draft.Points);
                    if (draft.Undo.Count > draft.Descriptor.Rules.UndoCapacity) draft.Undo.RemoveAt(0);
                    draft.Redo.Clear();
                    draft.Points = next;
                }
            }
            return draft.View();
        }
        finally { mutationGate.Semaphore.Release(); }
    }

    private DraftState FindDraft(string? id, string objectId)
        => id is not null && drafts.TryGetValue(id, out var draft) && draft.Descriptor.ObjectId == objectId
            ? draft : throw GpuVfCurveValidation.Error("gpu-vf-curve-draft-not-found");

    private static string ValidateName(string? name, GpuVfCurveEditorRules rules)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0 || trimmed.Length > rules.MaximumNameLength)
            throw new GpuVfCurveRequestException(new("gpu-vf-curve-name-invalid", new(Maximum: rules.MaximumNameLength)));
        return trimmed;
    }

    private static ImmutableArray<GpuVfCurvePoint> Edit(DraftState draft, GpuVfCurveEditorCommand command)
    {
        var all = command.Operation is "uniform-offset" or "reset-default";
        var selected = all ? draft.Points.Select(p => p.PointId).ToHashSet() : command.PointIds?.ToHashSet() ?? [];
        if (selected.Count == 0 || selected.Any(id => !draft.Points.Any(p => p.PointId == id))
            || !all && command.PointIds!.Count != selected.Count)
            throw GpuVfCurveValidation.Error("gpu-vf-curve-selection-invalid");
        var rules = draft.Descriptor.Rules;
        var next = draft.Points.ToArray();
        switch (command.Operation)
        {
            case "move-selection":
            case "step":
                double delta;
                if (command.Operation == "step")
                {
                    if (command.Direction is not (-1 or 1)) throw GpuVfCurveValidation.Error("gpu-vf-curve-step-direction-invalid");
                    delta = command.Direction.Value * (command.Coarse ? rules.CoarseStepKhz : rules.FineStepKhz);
                }
                else
                {
                    if (command.DeltaKhz is not { } requested || !double.IsFinite(requested))
                        throw GpuVfCurveValidation.Error("gpu-vf-curve-delta-invalid");
                    delta = Quantize(requested, rules.DragQuantizationKhz);
                }
                var minimum = double.NegativeInfinity;
                var maximum = double.PositiveInfinity;
                for (var index = 0; index < next.Length; index++)
                {
                    if (!selected.Contains(next[index].PointId)) continue;
                    var bounds = GpuVfCurveValidation.Bounds(draft.Descriptor.Points[index], rules);
                    minimum = Math.Max(minimum, bounds.Min - next[index].FrequencyOffsetKhz);
                    maximum = Math.Min(maximum, bounds.Max - next[index].FrequencyOffsetKhz);
                }
                if (!double.IsFinite(delta) || delta < minimum || delta > maximum)
                    throw new GpuVfCurveRequestException(new("gpu-vf-curve-common-delta-out-of-range",
                        new(Minimum: minimum, Maximum: maximum, Unit: "kHz", Requested: double.IsFinite(delta) ? delta : null)));
                for (var index = 0; index < next.Length; index++)
                    if (selected.Contains(next[index].PointId)) next[index] = next[index] with { FrequencyOffsetKhz = next[index].FrequencyOffsetKhz + delta };
                break;
            case "uniform-offset":
                if (command.OffsetKhz is not { } offset || !double.IsFinite(offset)) throw GpuVfCurveValidation.Error("gpu-vf-curve-offset-invalid");
                for (var index = 0; index < next.Length; index++) next[index] = next[index] with { FrequencyOffsetKhz = Quantize(offset, rules.DragQuantizationKhz) };
                break;
            case "reset-default":
                if (draft.Descriptor.Points.Any(p => p.DefaultOffsetKhz is null)) throw GpuVfCurveValidation.Error("gpu-vf-curve-default-unavailable");
                for (var index = 0; index < next.Length; index++) next[index] = next[index] with { FrequencyOffsetKhz = draft.Descriptor.Points[index].DefaultOffsetKhz!.Value };
                break;
            case "align-left":
            case "align-right":
                var indices = Enumerable.Range(0, next.Length).Where(i => selected.Contains(next[i].PointId)).ToArray();
                if (indices.Any(i => draft.Descriptor.Points[i].DefaultFrequencyKhz is null || draft.Descriptor.Points[i].DefaultOffsetKhz is null))
                    throw GpuVfCurveValidation.Error("gpu-vf-curve-default-frequency-unavailable");
                var voltageOrder = indices.OrderBy(index => draft.Points[index].VoltageMicrovolts).ToArray();
                var anchor = command.Operation == "align-left" ? voltageOrder[0] : voltageOrder[^1];
                var anchorHardware = draft.Descriptor.Points[anchor];
                var frequency = anchorHardware.DefaultFrequencyKhz!.Value + next[anchor].FrequencyOffsetKhz - anchorHardware.DefaultOffsetKhz!.Value;
                foreach (var index in indices)
                {
                    var hardware = draft.Descriptor.Points[index];
                    next[index] = next[index] with { FrequencyOffsetKhz = frequency - hardware.DefaultFrequencyKhz!.Value + hardware.DefaultOffsetKhz!.Value };
                }
                break;
            default: throw GpuVfCurveValidation.Error("gpu-vf-curve-command-invalid");
        }
        return GpuVfCurveValidation.Points(next, draft.Descriptor);
    }

    private static double Quantize(double value, double quantum)
        => Math.Round(value / quantum, MidpointRounding.AwayFromZero) * quantum;

    private sealed class DraftState(string id, GpuVfCurveDescriptor descriptor, ImmutableArray<GpuVfCurvePoint> points)
    {
        internal GpuVfCurveDescriptor Descriptor { get; } = descriptor with { Points = descriptor.Points.ToImmutableArray() };
        internal ImmutableArray<GpuVfCurvePoint> Points { get; set; } = points;
        internal List<ImmutableArray<GpuVfCurvePoint>> Undo { get; } = [];
        internal List<ImmutableArray<GpuVfCurvePoint>> Redo { get; } = [];
        internal GpuVfCurveDraft View() => new(id, Descriptor.ObjectId, Descriptor.Binding!, Points, Descriptor, Undo.Count != 0, Redo.Count != 0);
    }
}

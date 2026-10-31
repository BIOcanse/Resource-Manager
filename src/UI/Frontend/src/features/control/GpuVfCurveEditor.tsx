import { createEffect, createMemo, createSignal, For, on, onCleanup, Show } from "solid-js";
import { useFrontendRuntime } from "../../frontendRuntime/FrontendRuntimeContext";
import { RequestProblem } from "../../frontendRuntime/request/RequestProblem.ts";
import { onPageLeave } from "../../app/navigation/pageHost.tsx";
import { uiText } from "../../text.ts";
import { StandardSelect } from "../../components/StandardSelect.tsx";
import { controlReasonText } from "../../presentation/controlReasonText.ts";
import { FixedAxisCurveChart } from "./FixedAxisCurveChart.tsx";
import { closeGpuVfDraft, commandGpuVfDraft, deleteGpuVfPreset, getGpuVfDescriptor, getGpuVfLibrary,
  getGpuVfReferences, gpuVfReferencesDecoder, saveGpuVfPreset } from "./gpuVfCurveApi.ts";
import { readControlReason } from "./controlApi.ts";
import type { GpuVfCurveCommand, GpuVfCurveDescriptor, GpuVfCurveDraft, GpuVfCurveLibrary, GpuVfCurveReference } from "./gpuVfCurveTypes.ts";
import type { ControlSetting } from "./controlTypes.ts";

// Hardware units are kHz and µV. These are unit conversions, not policy values.
const kilo = 1000;
export function GpuVfCurveEditor(props: {
  objectId: string; capabilityId: string; setting: ControlSetting | undefined; disabled: boolean;
  onChange: (setting: ControlSetting | null) => void;
}) {
  const client = useFrontendRuntime().requestClient;
  const text = () => uiText.gpuVfCurve;
  const [descriptor, setDescriptor] = createSignal<GpuVfCurveDescriptor | null>(null);
  const [library, setLibrary] = createSignal<GpuVfCurveLibrary | null>(null);
  const [draft, setDraft] = createSignal<GpuVfCurveDraft | null>(null);
  const [opened, setOpened] = createSignal(false), [busy, setBusy] = createSignal(false);
  const [error, setError] = createSignal<string | null>(null);
  const [references, setReferences] = createSignal<readonly GpuVfCurveReference[]>([]);
  const [selected, setSelected] = createSignal<number[]>([]);
  const [preview, setPreview] = createSignal<readonly number[] | null>(null);
  const [name, setName] = createSignal("");
  const [editingId, setEditingId] = createSignal<string | null>(null);
  const [scope, setScope] = createSignal<"device" | "model" | null>(null);
  let generation = 0;
  const facts = () => draft()?.descriptor ?? descriptor();
  const canEdit = () => facts()?.available === true && !props.disabled && !busy();
  const currentEntry = () => library()?.presets.find(entry => entry.preset.id === editingId());
  const frequencies = createMemo(() => {
    const current = draft(); if (!current) return null;
    return current.points.map(point => {
      const hardware = current.descriptor.points.find(item => item.pointId === point.pointId);
      return hardware?.defaultFrequencyKhz == null || hardware.defaultOffsetKhz == null ? null
        : hardware.defaultFrequencyKhz + point.frequencyOffsetKhz - hardware.defaultOffsetKhz;
    });
  });
  const plot = createMemo(() => {
    const current = draft(), values = frequencies();
    if (!current || !values || current.descriptor.scalarOnly || values.some(value => value == null)) return null;
    const hardware = current.descriptor.points;
    const xs = current.points.map(point => point.voltageMicrovolts);
    const minimumX = Math.min(...xs), maximumX = Math.max(...xs);
    // Bounds are the backend's hardware ranges; no substitute envelope in the UI.
    if (minimumX === maximumX || hardware.some(point => point.minimumOffsetKhz == null || point.maximumOffsetKhz == null
      || point.defaultFrequencyKhz == null || point.defaultOffsetKhz == null)) return null;
    const minimum = Math.min(...hardware.map(point => point.defaultFrequencyKhz! + point.minimumOffsetKhz! - point.defaultOffsetKhz!));
    const maximum = Math.max(...hardware.map(point => point.defaultFrequencyKhz! + point.maximumOffsetKhz! - point.defaultOffsetKhz!));
    const padding = (maximum - minimum) * current.descriptor.rules.axisPaddingRatio;
    if (minimum === maximum) return null;
    return { xs, values: (values as number[]).map(value => value / kilo), minimumX, maximumX,
      minimumY: (minimum - padding) / kilo, maximumY: (maximum + padding) / kilo };
  });
  const formatFrequency = (value: number | null | undefined) => value == null ? "—" : (value / kilo).toFixed(facts()!.rules.frequencyDecimals);
  const formatVoltage = (value: number) => (value / kilo).toFixed(facts()!.rules.voltageDecimals);

  function failed(problem: unknown) {
    if (problem instanceof RequestProblem && problem.payload && typeof problem.payload === "object") {
      try {
        const payload = problem.payload as Record<string, unknown>;
        setError(controlReasonText(readControlReason(payload.reason, "$.reason")) ?? problem.message);
        if (payload.references) setReferences(gpuVfReferencesDecoder.decode(payload).references);
        return;
      } catch { /* Transport message still explains an invalid failure payload. */ }
    }
    setError(problem instanceof Error ? problem.message : uiText.control.loadFailed);
  }
  async function closeDraft() {
    const old = draft(); setDraft(null); setPreview(null); setSelected([]);
    if (old) await closeGpuVfDraft(client, { operation: "close", objectId: old.objectId, draftId: old.id, expectedBinding: old.binding });
  }
  function release() {
    generation++; setOpened(false); setBusy(false); setDescriptor(null); setLibrary(null);
    void closeDraft().catch(failed);
  }
  onCleanup(release); onPageLeave(release);
  // Loading another GPU preset replaces the cached editor session as well.
  // A response belonging to the previous selection must never edit the new one.
  createEffect(on(() => props.setting?.gpuVfCurvePresetId, id => {
    if (!opened() || id === editingId()) return;
    if (props.setting === undefined || busy()) { release(); return; }
    void run(() => begin(id ?? undefined));
  }, { defer: true }));
  createEffect(on(() => props.disabled, disabled => { if (disabled && opened()) release(); }));
  async function run(action: () => Promise<void>) {
    if (busy()) return;
    const visit = generation; setBusy(true); setError(null); setReferences([]);
    try { await action(); } catch (problem) { if (generation === visit) failed(problem); }
    finally { if (generation === visit) { setBusy(false); setPreview(null); } }
  }
  async function begin(curveId?: string) {
    await closeDraft();
    const snapshot = descriptor(); if (!snapshot?.binding || !snapshot.available) return;
    const visit = generation;
    const next = await commandGpuVfDraft(client, { operation: "begin", objectId: props.objectId, curveId, expectedBinding: snapshot.binding });
    if (generation !== visit) {
      await closeGpuVfDraft(client, { operation: "close", objectId: next.objectId, draftId: next.id, expectedBinding: next.binding });
      return;
    }
    setDraft(next); setSelected([]);
    setScope(library()?.presets.find(entry => entry.preset.id === curveId)?.preset.binding.scope ?? next.descriptor.rules.defaultBindingScope);
    setEditingId(curveId ?? null);
    setName(library()?.presets.find(entry => entry.preset.id === curveId)?.preset.name ?? "");
  }
  async function open() {
    await run(async () => {
      const visit = generation;
      const [snapshot, catalog] = await Promise.all([getGpuVfDescriptor(client, props.objectId), getGpuVfLibrary(client, props.objectId)]);
      if (visit !== generation) return;
      setDescriptor(snapshot); setLibrary(catalog); setOpened(true); setScope(snapshot.rules.defaultBindingScope);
      const curveId = props.setting?.gpuVfCurvePresetId;
      if (curveId && !catalog.presets.some(entry => entry.applicable && entry.preset.id === curveId)) return;
      await begin(curveId ?? undefined);
    });
  }
  function command(operation: GpuVfCurveCommand["operation"], values: Partial<GpuVfCurveCommand> = {}) {
    void run(async () => {
      const current = draft(); if (!current) return;
      const visit = generation;
      const next = await commandGpuVfDraft(client, { operation, objectId: current.objectId, draftId: current.id, expectedBinding: current.binding,
        pointIds: selected().map(index => current.points[index].pointId), ...values });
      if (visit === generation) setDraft(next);
    });
  }
  async function refreshLibrary() { const visit = generation; const next = await getGpuVfLibrary(client, props.objectId); if (visit === generation) setLibrary(next); }
  function save(duplicate = false, rename = false) {
    void run(async () => {
      const current = draft(); if (!current || !name().trim()) return;
      const visit = generation;
      const next = await saveGpuVfPreset(client, editingId() ?? "new", { objectId: props.objectId, name: name(), expectedBinding: current.binding,
        ...(duplicate && editingId() ? { cloneFromId: editingId()! } : rename ? {} : { draftId: current.id }), bindingScope: scope()! });
      if (visit !== generation) return;
      await refreshLibrary(); if (visit !== generation) return; setEditingId(next.id); setName(next.name);
      props.onChange({ capabilityId: props.capabilityId, gpuVfCurvePresetId: next.id });
    });
  }
  function remove() {
    void run(async () => {
      const id = editingId(); if (!id) return;
      const visit = generation;
      const uses = await getGpuVfReferences(client, id); if (visit !== generation) return; setReferences(uses.references);
      if (uses.references.length) return;
      await deleteGpuVfPreset(client, id); if (visit !== generation) return;
      await closeDraft(); await refreshLibrary(); if (visit !== generation) return; setEditingId(null); setName("");
      if (props.setting?.gpuVfCurvePresetId === id) props.onChange({ capabilityId: props.capabilityId, gpuVfCurvePresetId: null });
    });
  }
  function step(direction: number, coarse: boolean) { if (canEdit() && selected().length) command("step", { direction, coarse }); }
  return <div class="gpu-vf-editor">
    <Show when={!opened()}>
      <button class="secondary" type="button" disabled={props.disabled || busy()} onClick={() => void open()}>{busy() ? text().loading : text().open}</button>
    </Show>
    <Show when={error()}>{message => <p class="control-curve-note" role="alert">{message()}</p>}</Show>
    <Show when={opened()}>
      <div class="gpu-vf-toolbar">
        <label>{text().title}<StandardSelect ariaLabel={text().title} value={editingId() ?? ""} disabled={busy() || props.disabled}
          options={[{ value: "", label: text().noCurve, disabled: true }, ...(library()?.presets ?? []).map(entry => ({
            value: entry.preset.id, label: entry.preset.name, disabled: !entry.applicable }))]}
          onChange={id => {
            if (!id) return;
            props.onChange({ capabilityId: props.capabilityId, gpuVfCurvePresetId: id });
            void run(() => begin(id));
          }} /></label>
        <button class="secondary" type="button" disabled={!canEdit()} onClick={() => void run(() => begin())}>{text().newCurve}</button>
        <button class="secondary" type="button" disabled={busy()} onClick={release}>{text().close}</button>
      </div>
      <For each={library()?.presets.filter(entry => !entry.applicable)}>{entry =>
        <p class="control-curve-note">{entry.preset.name}: {controlReasonText(entry.unavailableReason)}</p>
      }</For>
      <Show when={facts()?.unavailableReason}>{reason => <p class="control-curve-note" role="note">{controlReasonText(reason())}</p>}</Show>
      <Show when={facts()}>{snapshot => <p class="control-curve-note"><time dateTime={snapshot().readAt}>{snapshot().readAt}</time></p>}</Show>
      <Show when={!draft() && facts()?.points.length}>
        <div class="gpu-vf-points"><table><thead><tr><th>{text().voltage}</th><th>{text().defaultFrequency}</th><th>{text().currentFrequency}</th><th>{text().offset}</th></tr></thead>
          <tbody><For each={facts()?.points}>{point => <tr>
            <td>{facts()?.scalarOnly ? text().allVoltages : formatVoltage(point.voltageMicrovolts)}</td>
            <td>{formatFrequency(point.defaultFrequencyKhz)}</td><td>{formatFrequency(point.currentFrequencyKhz)}</td><td>{formatFrequency(point.currentOffsetKhz)}</td>
          </tr>}</For></tbody></table></div>
      </Show>
      <Show when={draft()}>{current => <>
        <div class="gpu-vf-toolbar">
          <label>{text().name}<input value={name()} maxlength={current().descriptor.rules.maximumNameLength} disabled={!canEdit()}
            onInput={event => setName(event.currentTarget.value)} /></label>
          <StandardSelect<"device" | "model"> value={scope()!} disabled={!canEdit()} ariaLabel={text().deviceBinding}
            options={[{ value: "device", label: text().deviceBinding }, { value: "model", label: text().modelBinding }]} onChange={setScope} />
          <button class="secondary" type="button" disabled={!canEdit() || !name().trim()} onClick={() => save()}>{text().save}</button>
          <button class="secondary" type="button" disabled={!canEdit() || !currentEntry() || !name().trim()} onClick={() => save(true)}>{text().duplicate}</button>
          <button class="secondary" type="button" disabled={!canEdit() || !currentEntry() || !name().trim()} onClick={() => save(false, true)}>{text().rename}</button>
          <button class="secondary" type="button" disabled={!canEdit() || !currentEntry()} onClick={remove}>{text().remove}</button>
        </div>
        <p class="control-curve-note">{text().editNote}</p>
        <div class="gpu-vf-toolbar">
          <button class="secondary" type="button" disabled={!canEdit()} onClick={() => setSelected(current().points.map((_, index) => index))}>{text().selectAll}</button>
          <button class="secondary" type="button" disabled={!canEdit()} onClick={() => setSelected([])}>{text().clearSelection}</button>
          <button class="secondary" type="button" disabled={!canEdit() || !selected().length} onClick={() => command("align-left")}>{text().alignLeft}</button>
          <button class="secondary" type="button" disabled={!canEdit() || !selected().length} onClick={() => command("align-right")}>{text().alignRight}</button>
          <button class="secondary" type="button" disabled={!canEdit() || !current().canUndo} onClick={() => command("undo")}>{text().undo}</button>
          <button class="secondary" type="button" disabled={!canEdit() || !current().canRedo} onClick={() => command("redo")}>{text().redo}</button>
          <button class="secondary" type="button" disabled={!canEdit()} onClick={() => command("reset-default")}>{text().reset}</button>
          <label>{text().uniformOffset}<input type="number" step={current().descriptor.rules.fineStepKhz / kilo} disabled={!canEdit()}
            value={current().points.every(point => point.frequencyOffsetKhz === current().points[0]?.frequencyOffsetKhz) ? formatFrequency(current().points[0]?.frequencyOffsetKhz) : ""}
            onChange={event => {
              if (event.currentTarget.value.trim() === "" || !Number.isFinite(event.currentTarget.valueAsNumber)) return;
              const indices = current().points.map((_, index) => index); setSelected(indices);
              command("uniform-offset", { offsetKhz: event.currentTarget.valueAsNumber * kilo, pointIds: current().points.map(point => point.pointId) });
            }} /></label>
        </div>
        <Show when={plot()} fallback={<p class="control-curve-note">{text().noFrequency}</p>}>{geometry =>
          <FixedAxisCurveChart xs={geometry().xs} values={preview() ?? geometry().values}
            minimumX={geometry().minimumX} maximumX={geometry().maximumX} minimumY={geometry().minimumY} maximumY={geometry().maximumY}
            xTicks={[geometry().minimumX, geometry().maximumX]} yTicks={[geometry().minimumY, geometry().maximumY]}
            xUnit="mV" yUnit="MHz" formatX={formatVoltage} formatY={value => value.toFixed(current().descriptor.rules.frequencyDecimals)}
            step={current().descriptor.rules.dragQuantizationKhz / kilo} coarseStep={current().descriptor.rules.coarseStepKhz / kilo}
            hitRadiusPixels={current().descriptor.rules.hitRadiusPixels} disabled={!canEdit()} label={text().title}
            pointLabel={(x, value) => `${formatVoltage(x)} mV · ${value.toFixed(current().descriptor.rules.frequencyDecimals)} MHz`}
            valueLabel={value => `${value.toFixed(current().descriptor.rules.frequencyDecimals)} MHz`}
            selected={selected()} onSelect={setSelected} onChange={setPreview}
            onCommit={(indices, deltaMhz) => command("move-selection", { deltaKhz: deltaMhz * kilo, pointIds: indices.map(index => current().points[index].pointId) })} onStep={step} />
        }</Show>
        <p class="control-curve-note">{text().snapshotNote}</p>
        <div class="gpu-vf-points" tabindex="0" role="group" aria-label={text().selection} onKeyDown={event => {
          if (event.key === "ArrowUp" || event.key === "ArrowDown") { event.preventDefault(); step(event.key === "ArrowUp" ? 1 : -1, event.shiftKey); }
          if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "z") { event.preventDefault(); command(event.shiftKey ? "redo" : "undo"); }
        }}><table><thead><tr><th>{text().selection}</th><th>{text().voltage}</th><th>{text().defaultFrequency}</th><th>{text().currentFrequency}</th><th>{text().editedFrequency}</th><th>{text().offset}</th></tr></thead>
          <tbody><For each={current().points}>{(point, index) => {
            const hardware = () => current().descriptor.points.find(item => item.pointId === point.pointId);
            return <tr classList={{ "gpu-vf-row-selected": selected().includes(index()) }}><td><input type="checkbox" checked={selected().includes(index())}
              disabled={!canEdit()} aria-label={`${text().selection}: ${point.pointId}`} onChange={event => setSelected(indices => event.currentTarget.checked ? [...indices, index()] : indices.filter(at => at !== index()))} /></td>
              <td>{current().descriptor.scalarOnly ? text().allVoltages : formatVoltage(point.voltageMicrovolts)}</td>
              <td>{formatFrequency(hardware()?.defaultFrequencyKhz)}</td><td>{formatFrequency(hardware()?.currentFrequencyKhz)}</td>
              <td>{formatFrequency(frequencies()?.[index()])}</td><td>{formatFrequency(point.frequencyOffsetKhz)}</td></tr>;
          }}</For></tbody></table></div>
      </>}</Show>
      <Show when={references().length}><p role="alert">{text().references}</p><ul><For each={references()}>{use => <li>{use.name ?? use.id} · {use.objectId}</li>}</For></ul></Show>
    </Show>
  </div>;
}

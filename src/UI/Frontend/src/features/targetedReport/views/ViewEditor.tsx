import { createMemo, For, Show, type JSX } from "solid-js";
import { StandardSelect, type StandardSelectOption } from "../../../components/StandardSelect.tsx";
import { uiText } from "../../../text.ts";
import { seriesIdForRole, type SeriesSet } from "./seriesModel.ts";
import {
  aggregationMethods, compatibleSeries, viewDomain, viewKinds, viewLimits, xSeriesId,
  type ReportView, type ViewAxisSide, type ViewPatch, type ViewRange, type ViewX
} from "./viewModel.ts";

/**
 * 视图的全部字段都在这里改：名称、类型、横轴、纵轴序列及其所在的轴、聚合方法和窗口、对齐窗口、数据范围、
 * 显示范围（横轴和两条纵轴）、直方图分箱和累计分布、百分位步长。当前类型或横轴用不到的字段照样显示，
 * 只是不可改，旁边写明它在什么时候起作用。
 *
 * 数字框在提交（失去焦点或回车）时写入；可以为空的框留空 = 自动或不限。超出上下限的值照样写入，
 * 视图上会列出这个问题，不悄悄改成边界值。
 */
export function ViewEditor(props: {
  view: ReportView;
  set: SeriesSet;
  autoBins: number | null;
  onChange: (patch: ViewPatch) => void;
}) {
  const copy = () => uiText.targetedReport.views;
  const fields = () => copy().fields;
  const domain = () => viewDomain(props.view.x);
  const usesXSeries = () => xSeriesId(props.view.x) !== null;

  const kindOptions = createMemo<StandardSelectOption[]>(() =>
    viewKinds.map((kind) => ({ value: kind, label: copy().kinds[kind] })));
  const xOptions = createMemo<StandardSelectOption[]>(() => {
    const options: StandardSelectOption[] = [
      { value: "time", label: copy().x.time },
      { value: "frameIndex", label: copy().x.frameIndex }
    ];
    for (const series of props.set.series) {
      if (series.kind === "time") options.push({ value: `series:${series.id}`, label: copy().x.series(series.label), group: fields().xSeriesGroup });
    }
    // 当前横轴引用的序列这份报告没有时，照样列出来（视图上写着这个问题）。
    if (!options.some((option) => option.value === props.view.x)) options.push({ value: props.view.x, label: props.view.x });
    return options;
  });
  const seriesOptions = (current: string) => {
    const options: StandardSelectOption[] = compatibleSeries(props.set, props.view.x)
      .map((series) => ({ value: series.id, label: `${series.label} (${series.unit || "–"})` }));
    if (!options.some((option) => option.value === current)) options.push({ value: current, label: current });
    return options;
  };
  const axisOptions = createMemo<StandardSelectOption<ViewAxisSide>[]>(() => [
    { value: "left", label: copy().axes.left }, { value: "right", label: copy().axes.right }
  ]);
  const aggregationOptions = createMemo<StandardSelectOption[]>(() =>
    aggregationMethods.map((method) => ({ value: method, label: copy().aggregations[method] })));

  // 换横轴时纵轴序列换成同一指标在新横轴下的那一条（按 role 找）；找不到的保持原样，视图上会标出。
  const changeX = (x: ViewX) => {
    const nextDomain = viewDomain(x);
    props.onChange({ x, y: props.view.y.map((item) => {
      const role = props.set.byId.get(item.seriesId)?.role;
      return { ...item, seriesId: role === undefined ? item.seriesId : seriesIdForRole(props.set, role, nextDomain) ?? item.seriesId };
    }) });
  };
  const setY = (index: number, patch: Partial<ReportView["y"][number]>) =>
    props.onChange({ y: props.view.y.map((item, at) => at === index ? { ...item, ...patch } : item) });
  const addSeries = () => {
    const used = new Set(props.view.y.map((item) => item.seriesId));
    const candidates = compatibleSeries(props.set, props.view.x);
    const next = candidates.find((series) => !used.has(series.id)) ?? candidates[0];
    if (next) props.onChange({ y: [...props.view.y, { seriesId: next.id, axis: "left" }] });
  };
  const windowLimits = () => viewLimits.aggregationWindow[domain()];

  return (
    <div class="targeted-report-view-editor">
      <div class="targeted-report-view-editor-row">
        <label class="targeted-recording-field targeted-report-view-name">
          <span>{fields().name}</span>
          <input type="text" maxLength={viewLimits.nameLength} value={props.view.name}
            onChange={(event) => props.onChange({ name: event.currentTarget.value })} />
        </label>
        <Field label={fields().kind}>
          <StandardSelect value={props.view.kind} options={kindOptions()} ariaLabel={fields().kind}
            onChange={(kind) => props.onChange({ kind: kind as ReportView["kind"] })} />
        </Field>
        <Field label={fields().x}>
          <StandardSelect value={props.view.x} options={xOptions()} ariaLabel={fields().x} searchable
            onChange={(x) => changeX(x as ViewX)} />
        </Field>
      </div>

      <fieldset class="targeted-report-view-group">
        <legend>{fields().y}</legend>
        <For each={props.view.y}>
          {(item, index) => (
            <div class="targeted-report-view-editor-row">
              <Field label={fields().series(index() + 1)} wide>
                <StandardSelect value={item.seriesId} options={seriesOptions(item.seriesId)} searchable
                  ariaLabel={fields().series(index() + 1)} onChange={(seriesId) => setY(index(), { seriesId })} />
              </Field>
              <Field label={fields().axis}>
                <StandardSelect value={item.axis} options={axisOptions()} ariaLabel={`${fields().series(index() + 1)} · ${fields().axis}`}
                  disabled={props.view.kind === "histogram"} onChange={(axis) => setY(index(), { axis })} />
              </Field>
              <button class="secondary" type="button" aria-label={`${fields().removeSeries} · ${fields().series(index() + 1)}`}
                onClick={() => props.onChange({ y: props.view.y.filter((_item, at) => at !== index()) })}>
                {fields().removeSeries}
              </button>
            </div>
          )}
        </For>
        <div class="targeted-report-view-editor-row">
          <button class="secondary" type="button" disabled={props.view.y.length >= viewLimits.ySeries
            || compatibleSeries(props.set, props.view.x).length === 0} onClick={addSeries}>{fields().addSeries}</button>
          <span class="targeted-report-view-hint">{fields().seriesHint(viewLimits.ySeries)}</span>
        </div>
      </fieldset>

      <fieldset class="targeted-report-view-group">
        <legend>{fields().data}</legend>
        <div class="targeted-report-view-editor-row">
          <Field label={fields().aggregation}>
            <StandardSelect value={props.view.aggregation.method} options={aggregationOptions()} ariaLabel={fields().aggregation}
              onChange={(method) => props.onChange({ aggregation: { method: method as ReportView["aggregation"]["method"] } })} />
          </Field>
          <NumberField label={domain() === "frame" ? fields().windowFrames : fields().windowMs}
            value={props.view.aggregation.window} min={windowLimits().min} max={windowLimits().max} step={1}
            disabled={props.view.aggregation.method === "none"}
            onCommit={(value) => value !== null && props.onChange({ aggregation: { window: value } })} />
          <NumberField label={fields().matchWindow} value={props.view.matchWindowMs}
            min={viewLimits.matchWindowMs.min} max={viewLimits.matchWindowMs.max} step={1} disabled={!usesXSeries()}
            onCommit={(value) => value !== null && props.onChange({ matchWindowMs: value })} />
        </div>
        <p class="targeted-report-view-hint">{fields().dataHint}</p>
        <RangeFields label={domain() === "frame" ? fields().sampleWindowFrames : fields().sampleWindowMs}
          range={props.view.sampleWindow} onCommit={(patch) => props.onChange({ sampleWindow: patch })} />
      </fieldset>

      <fieldset class="targeted-report-view-group">
        <legend>{fields().display}</legend>
        <RangeFields label={fields().rangeX} range={props.view.range.x} disabled={props.view.kind === "summaryBars"}
          onCommit={(patch) => props.onChange({ range: { x: patch } })} />
        <RangeFields label={fields().rangeLeft} range={props.view.range.left}
          onCommit={(patch) => props.onChange({ range: { left: patch } })} />
        <RangeFields label={fields().rangeRight} range={props.view.range.right}
          onCommit={(patch) => props.onChange({ range: { right: patch } })} />
        <p class="targeted-report-view-hint">{fields().rangeHint}</p>
        <div class="targeted-report-view-editor-row">
          <NumberField label={fields().bins} value={props.view.histogram.bins} nullable step={1}
            min={viewLimits.bins.min} max={viewLimits.bins.max} disabled={props.view.kind !== "histogram"}
            placeholder={props.autoBins === null ? fields().auto : fields().autoBins(props.autoBins)}
            onCommit={(bins) => props.onChange({ histogram: { bins } })} />
          <label class="targeted-report-view-check">
            <input type="checkbox" checked={props.view.histogram.showCdf} disabled={props.view.kind !== "histogram"}
              onChange={(event) => props.onChange({ histogram: { showCdf: event.currentTarget.checked } })} />
            <span>{fields().showCdf}</span>
          </label>
          <NumberField label={fields().percentileStep} value={props.view.percentileStep}
            min={viewLimits.percentileStep.min} max={viewLimits.percentileStep.max} step={0.01}
            disabled={props.view.kind !== "cdf"}
            onCommit={(value) => value !== null && props.onChange({ percentileStep: value })} />
        </div>
        <p class="targeted-report-view-hint">{fields().binsHint(viewLimits.autoBins.min, viewLimits.autoBins.max)}</p>
      </fieldset>
    </div>
  );
}

function Field(props: { label: string; wide?: boolean; children: JSX.Element }) {
  return (
    <div class="targeted-recording-field targeted-report-view-field" classList={{ wide: props.wide === true }}>
      <span aria-hidden="true">{props.label}</span>
      {props.children}
    </div>
  );
}

function NumberField(props: {
  label: string;
  value: number | null;
  min?: number;
  max?: number;
  step?: number;
  nullable?: boolean;
  placeholder?: string;
  disabled?: boolean;
  onCommit: (value: number | null) => void;
}) {
  return (
    <label class="targeted-recording-field targeted-report-view-number">
      <span>{props.label}</span>
      <input type="number" min={props.min} max={props.max} step={props.step ?? "any"} disabled={props.disabled}
        value={props.value ?? ""} placeholder={props.placeholder ?? (props.nullable ? uiText.targetedReport.views.fields.auto : undefined)}
        onChange={(event) => {
          const text = event.currentTarget.value.trim();
          if (text === "") {
            if (props.nullable) props.onCommit(null);
            else event.currentTarget.value = props.value === null ? "" : String(props.value);
            return;
          }
          const value = Number(text);
          if (Number.isFinite(value)) props.onCommit(value);
          else event.currentTarget.value = props.value === null ? "" : String(props.value);
        }} />
    </label>
  );
}

function RangeFields(props: {
  label: string;
  range: ViewRange;
  disabled?: boolean;
  onCommit: (patch: Partial<ViewRange>) => void;
}) {
  const fields = () => uiText.targetedReport.views.fields;
  return (
    <div class="targeted-report-view-editor-row">
      <NumberField label={`${props.label} · ${fields().min}`} value={props.range.min} nullable disabled={props.disabled}
        onCommit={(min) => props.onCommit({ min })} />
      <NumberField label={`${props.label} · ${fields().max}`} value={props.range.max} nullable disabled={props.disabled}
        onCommit={(max) => props.onCommit({ max })} />
      <Show when={props.range.min !== null || props.range.max !== null}>
        <button class="secondary" type="button" disabled={props.disabled}
          onClick={() => props.onCommit({ min: null, max: null })}>{fields().clearRange}</button>
      </Show>
    </div>
  );
}

import { createMemo, createSignal, For, Show } from "solid-js";
import { fixedAxisCurvePointDefinition } from "../../interactions/components/curvePoint.ts";
import { solidHost, useInteraction, useInteractionComposite } from "../../interactions/solid/lodestone.ts";
import type { InteractionHandle } from "../../interactions/solid/lodestone.ts";
import type { Instance, Presentation } from "../../interactions/protocol/runtime.ts";

// Same plot geometry as the original fan chart. Domain policy comes from its owner.
const width = 100, height = 56;
const padding = { left: 10, right: 5, top: 5, bottom: 8 };
const plotWidth = width - padding.left - padding.right, plotHeight = height - padding.top - padding.bottom;
export interface FixedAxisCurveChartProps {
  xs: readonly number[]; values: readonly number[];
  minimumX: number; maximumX: number; minimumY: number; maximumY: number;
  xTicks: readonly number[]; yTicks: readonly number[]; xUnit: string; yUnit: string;
  step: number; coarseStep: number; disabled?: boolean; label: string;
  pointLabel: (x: number, value: number, index: number) => string;
  valueLabel: (value: number) => string;
  formatX?: (x: number) => string; formatY?: (y: number) => string;
  hitRadiusPixels?: number; nearestPoint?: boolean; selected?: readonly number[];
  onSelect?: (indices: number[]) => void; onChange: (values: number[]) => void;
  onCommit?: (indices: readonly number[], delta: number) => void;
  onStep?: (direction: number, coarse: boolean) => void;
}
export function FixedAxisCurveChart(props: FixedAxisCurveChartProps) {
  let surface!: SVGSVGElement;
  const [drawn, setDrawn] = createSignal<ReadonlyMap<number, number>>(new Map());
  const [box, setBox] = createSignal<{ x: number; y: number; endX: number; endY: number } | null>(null);
  let baseline: readonly number[] | null = null, gestureIndices: readonly number[] = [];
  const xOf = (value: number) => padding.left + (value - props.minimumX) / (props.maximumX - props.minimumX) * plotWidth;
  const yOf = (value: number) => padding.top + (props.maximumY - value) / (props.maximumY - props.minimumY) * plotHeight;
  const drawnValue = (index: number) => drawn().get(index) ?? props.values[index];
  const path = createMemo(() => {
    if (!props.xs.length) return "";
    return [[props.minimumX, drawnValue(0)], ...props.xs.map((x, index) => [x, drawnValue(index)]),
      [props.maximumX, drawnValue(props.xs.length - 1)]]
      .map(([x, y], index) => `${index === 0 ? "M" : "L"}${xOf(x).toFixed(2)},${yOf(y).toFixed(2)}`).join(" ");
  });
  const toScreen = (x: number, y: number) => new DOMPoint(x, y).matrixTransform(surface.getScreenCTM()!);
  const toView = (x: number, y: number) => new DOMPoint(x, y).matrixTransform(surface.getScreenCTM()!.inverse());
  const points = new Map<number, InteractionHandle<number>>();
  function begin(index: number) {
    if (!props.onCommit) return;
    gestureIndices = props.selected?.includes(index) ? [...props.selected] : [index];
    props.onSelect?.([...gestureIndices]); baseline = [...props.values];
  }
  function change(index: number, value: number, commit: boolean) {
    if (!props.onCommit) { const values = [...props.values]; values[index] = value; props.onChange(values); return; }
    if (!baseline) begin(index);
    const original = baseline!, delta = value - original[index];
    if (commit) {
      baseline = null;
      if (delta !== 0) props.onCommit(gestureIndices, delta); else props.onChange([...original]);
    } else props.onChange(original.map((current, at) => gestureIndices.includes(at) ? current + delta : current));
  }
  useInteractionComposite("固定横轴曲线图", () => surface,
    () => [...points.values()].flatMap(point => point.instance ? [point.instance as Instance] : []), position => {
      if (!props.nearestPoint) return null;
      const at = toView(position.x, position.y);
      if (at.x < padding.left || at.x > width - padding.right || at.y < padding.top || at.y > height - padding.bottom) return null;
      let closest: Instance | null = null, distance = Number.POSITIVE_INFINITY;
      props.xs.forEach((x, index) => {
        const child = points.get(index)?.instance, next = Math.abs(xOf(x) - at.x);
        if (child && next < distance) { distance = next; closest = child as Instance; }
      });
      return closest ? { child: closest, why: "横向最近的固定点" } : null;
    });
  return <svg ref={surface} class="fan-curve-chart" classList={{ "fan-curve-chart-off": props.disabled === true }}
    viewBox={`0 0 ${width} ${height}`} role="group" aria-label={props.label}
    onKeyDown={event => { if (event.key === "Escape") setBox(null); }}
    onContextMenu={event => { if (box()) { event.preventDefault(); setBox(null); } }}
    onLostPointerCapture={() => setBox(null)}
    onPointerDown={event => {
      if (!props.onSelect || props.disabled || event.button !== 0 || (event.target as Element).closest(".fan-curve-handle")) return;
      const at = toView(event.clientX, event.clientY);
      if (at.x < padding.left || at.x > width - padding.right || at.y < padding.top || at.y > height - padding.bottom) return;
      event.preventDefault(); surface.setPointerCapture(event.pointerId); setBox({ x: at.x, y: at.y, endX: at.x, endY: at.y });
    }}
    onPointerMove={event => {
      if (!box()) return;
      const at = toView(event.clientX, event.clientY); setBox(current => current && { ...current, endX: at.x, endY: at.y });
    }}
    onPointerUp={event => {
      const bounds = box(); if (!bounds) return;
      const indices = props.xs.flatMap((x, index) => xOf(x) >= Math.min(bounds.x, bounds.endX) && xOf(x) <= Math.max(bounds.x, bounds.endX)
        && yOf(props.values[index]) >= Math.min(bounds.y, bounds.endY) && yOf(props.values[index]) <= Math.max(bounds.y, bounds.endY) ? [index] : []);
      props.onSelect?.(event.shiftKey ? [...new Set([...(props.selected ?? []), ...indices])] : indices);
      setBox(null); if (surface.hasPointerCapture(event.pointerId)) surface.releasePointerCapture(event.pointerId);
    }} onPointerCancel={() => setBox(null)}>
    <text class="fan-curve-axis" x={padding.left - 2} y={3} text-anchor="end">{props.yUnit}</text>
    <text class="fan-curve-axis" x={width - padding.right} y={height - 0.5} text-anchor="end">{props.xUnit}</text>
    <For each={props.yTicks}>{value => <>
      <line class="fan-curve-grid" x1={padding.left} x2={width - padding.right} y1={yOf(value)} y2={yOf(value)} />
      <text class="fan-curve-axis" x={padding.left - 1.5} y={yOf(value) + 1.2} text-anchor="end">{props.formatY?.(value) ?? value}</text>
    </>}</For>
    <For each={props.xTicks}>{value => <>
      <line class="fan-curve-grid" x1={xOf(value)} x2={xOf(value)} y1={padding.top} y2={height - padding.bottom} />
      <text class="fan-curve-axis" x={xOf(value)} y={height - 3.5} text-anchor="middle">{props.formatX?.(value) ?? value}</text>
    </>}</For>
    <path class="fan-curve-line" d={path()} />
    <Show when={box()}>{bounds => <rect class="gpu-vf-selection-box" x={Math.min(bounds().x, bounds().endX)} y={Math.min(bounds().y, bounds().endY)}
      width={Math.abs(bounds().endX - bounds().x)} height={Math.abs(bounds().endY - bounds().y)} />}</Show>
    <For each={props.xs}>{(x, index) => {
      let handle!: SVGGElement;
      const [hitRadius, setHitRadius] = createSignal<number | null>(null);
      const definition = fixedAxisCurvePointDefinition({ minimum: props.minimumY, maximum: props.maximumY,
        step: props.step, coarseStep: props.coarseStep, unit: props.yUnit, commit: !!props.onCommit, backendKeyboard: !!props.onStep });
      const view = (): Presentation => ({ element: handle, bindingLabel: "位移",
        geometry: () => ({ center: "点的圆心", atMin: toScreen(xOf(x), yOf(props.minimumY)), atMax: toScreen(xOf(x), yOf(props.maximumY)) }),
        render: ({ x: value, mode }) => {
          setDrawn(current => new Map(current).set(index(), value as number)); handle.classList.toggle("fan-curve-handle-active", mode !== "静止");
          if (props.hitRadiusPixels != null) {
            const transform = surface.getScreenCTM();
            if (transform) setHitRadius(props.hitRadiusPixels / Math.hypot(transform.a, transform.b));
          }
        }, aria: value => {
          handle.setAttribute("aria-valuenow", String(value)); handle.setAttribute("aria-valuetext", props.valueLabel(value)); return String(value);
        }
      });
      points.set(index(), useInteraction(`固定横轴点 ${x}`, definition, view, solidHost<number>({
        value: () => props.values[index()], enabled: () => !props.disabled,
        call: (port, args) => { if (port === "begin") begin(index());
          else if (port === "step") props.onStep?.(Number(args[0]), args[1] === true);
          else if (port === "select" || port === "commit") change(index(), Number(args[0]), port === "commit"); }
      })));
      return <g ref={handle} class="fan-curve-handle" classList={{ "gpu-vf-point-selected": props.selected?.includes(index()) }}
        tabindex={props.disabled ? -1 : 0} role="slider" aria-disabled={props.disabled === true} aria-orientation="vertical"
        aria-label={props.pointLabel(x, props.values[index()], index())} aria-valuemin={props.minimumY} aria-valuemax={props.maximumY}
        onFocus={() => { if (props.onSelect && !props.selected?.includes(index())) props.onSelect([index()]); }}>
        <title>{props.pointLabel(x, props.values[index()], index())}</title>
        <circle class="fan-curve-hit" cx={xOf(x)} cy={yOf(drawnValue(index()))} r={hitRadius() ?? (props.hitRadiusPixels == null ? 2 : 0)} />
        <circle class="fan-curve-point" cx={xOf(x)} cy={yOf(drawnValue(index()))} r={1} />
      </g>;
    }}</For>
  </svg>;
}

import { For, Show } from "solid-js";
import type { SystemEventRaw } from "../../data/freezeReports/systemEvents.ts";
import { uiText } from "../../text.ts";
import { freezeCurveRange, systemEventRawFacts, type FreezeCurve, type LabelledValue } from "./freezeReportsPresentation.ts";

/** Display event fields and expandable XML unchanged. */
export function FreezeRawEvent(props: { readonly raw: SystemEventRaw; readonly title?: string; readonly facts?: readonly LabelledValue[] }) {
  const copy = () => uiText.freezeReports.systemEvents;
  return (
    <li class="freeze-report-event">
      <Show when={props.title}>{(title) => <strong class="freeze-report-event-title">{title()}</strong>}</Show>
      <dl class="freeze-report-facts">
        <For each={props.facts ?? systemEventRawFacts(props.raw)}>{(entry) => <div><dt>{entry.label}</dt><dd>{entry.value}</dd></div>}</For>
      </dl>
      <Show when={props.raw.xml}>
        <details class="freeze-report-xml">
          <summary>{copy().xml}</summary>
          <pre>{props.raw.xml}</pre>
        </details>
      </Show>
    </li>
  );
}

const curveWidth = 320;
const curveHeight = 64;

/** Break lines at missing samples; scale each static curve to its own value range. */
export function FreezeCurveChart(props: { readonly curve: FreezeCurve }) {
  const detail = () => uiText.freezeReports.recorder.details;
  const range = () => freezeCurveRange(props.curve);
  const segments = () => {
    const points = props.curve.points;
    const span = range();
    if (!span || points.length === 0) return [];
    const first = points[0]!.at;
    const last = points[points.length - 1]!.at;
    const x = (at: number) => last === first ? curveWidth / 2 : (at - first) / (last - first) * curveWidth;
    const y = (value: number) => span.max === span.min ? curveHeight / 2 : curveHeight - (value - span.min) / (span.max - span.min) * curveHeight;
    const result: string[] = [];
    let current: string[] = [];
    for (const point of points) {
      if (point.value === null) {
        if (current.length) result.push(current.join(" "));
        current = [];
        continue;
      }
      current.push(`${x(point.at).toFixed(1)},${y(point.value).toFixed(1)}`);
    }
    if (current.length) result.push(current.join(" "));
    return result;
  };
  return (
    <figure class="freeze-curve" data-freeze-curve={props.curve.id}>
      <figcaption>
        <strong>{props.curve.label}</strong>
        <span>{range() ? detail().curveRange(props.curve.format(range()!.min), props.curve.format(range()!.max)) : detail().noCurveData}</span>
      </figcaption>
      <svg viewBox={`-2 -2 ${curveWidth + 4} ${curveHeight + 4}`} preserveAspectRatio="none" role="img" aria-label={props.curve.label}>
        <For each={segments()}>
          {(points) => points.includes(" ")
            ? <polyline points={points} />
            : <circle cx={points.split(",")[0]} cy={points.split(",")[1]} r="2" />}
        </For>
      </svg>
    </figure>
  );
}

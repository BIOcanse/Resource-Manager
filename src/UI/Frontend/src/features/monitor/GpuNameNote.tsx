import { Show } from "solid-js";
import { gpuNameForMetric } from "./gpuAdapterNames.ts";

/** Display the backend GPU name beside its unchanged index; omit the name when unavailable. */
export function GpuNameNote(props: { metricId: string; names: ReadonlyMap<string, string> }) {
  return <GpuNameText name={gpuNameForMetric(props.metricId, props.names)} />;
}

/** Render nothing for a null GPU name. */
export function GpuNameText(props: { name: string | null }) {
  return (
    <Show when={props.name}>
      {(value) => <small class="metric-device gpu-name-note" data-full-content={value()}>{value()}</small>}
    </Show>
  );
}

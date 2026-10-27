import { Show } from "solid-js";
import { gpuNameForMetric } from "./gpuAdapterNames.ts";

/**
 * 按编号的显卡指标（gpu.N.*）旁边写这块显卡的名字，用监视卡片设备名的那种小字（monitor.css .metric-device）。
 * 编号是后端发的原样，名字来自 gpuAdapterNames；后端没给名字时什么都不画，只留编号。
 */
export function GpuNameNote(props: { metricId: string; names: ReadonlyMap<string, string> }) {
  return <GpuNameText name={gpuNameForMetric(props.metricId, props.names)} />;
}

/** 显卡名字的小字本身；名字为 null 时什么都不画。按编号的显卡在别处（例如控制页的显卡风扇控制器）从各自的后端来源取名字。 */
export function GpuNameText(props: { name: string | null }) {
  return (
    <Show when={props.name}>
      {(value) => <small class="metric-device gpu-name-note" title={value()}>{value()}</small>}
    </Show>
  );
}

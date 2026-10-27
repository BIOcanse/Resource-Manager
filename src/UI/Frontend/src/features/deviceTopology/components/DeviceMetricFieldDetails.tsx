import { Index, Show } from "solid-js";
import { HelpHint } from "../../../ui/primitives/HelpHint.tsx";
import type { MetricFieldDevice } from "../../../data/metricFields/metricFieldSource.ts";
import { deviceMetricSections } from "../deviceMetricFields.ts";
import { ReadingSourceMark } from "../../../components/ReadingSourceMark.tsx";

/**
 * 设备详情弹窗里的实时数值。订阅由 DeviceTopologyView 持有（和设备拓扑同一生命周期），
 * 这里只按传入的 devices 渲染；这台设备没有绑定任何相关字段时什么都不画。
 */
export function DeviceMetricFieldDetails(props: { devices: readonly MetricFieldDevice[] | null; deviceId: string }) {
  return (
    <Index each={deviceMetricSections(props.devices, props.deviceId)}>
      {(section) => (
        <section class="user-details-section">
          <h3>{section().title}</h3>
          <Show when={section().hint}>
            {(hint) => <p class="user-details-note">{hint()}</p>}
          </Show>
          <dl>
            <Index each={section().rows}>
              {(row) => (
                <div class="user-details-row">
                  <dt>
                    {row().label}<ReadingSourceMark mark={row().mark} />
                    <Show when={row().hint}>{(hint) => <HelpHint>{hint()}</HelpHint>}</Show>
                  </dt>
                  <dd>{row().value}</dd>
                </div>
              )}
            </Index>
          </dl>
        </section>
      )}
    </Index>
  );
}

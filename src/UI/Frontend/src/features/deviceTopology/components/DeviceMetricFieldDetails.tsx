import { Index, Show } from "solid-js";
import { HelpHint } from "../../../ui/primitives/HelpHint.tsx";
import type { MetricFieldDevice } from "../../../data/metricFields/metricFieldSource.ts";
import { deviceMetricSections } from "../deviceMetricFields.ts";
import { ReadingSourceMark } from "../../../components/ReadingSourceMark.tsx";

/** Render published device values from the topology view's subscription; omit devices without relevant field bindings. */
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

import { For } from "solid-js";
import type { SpecializedDeviceModel } from "../../deviceTopology/adapters/adapterRegistry";

export function DeviceSpecializedSummary(props: { model: SpecializedDeviceModel }) {
  return (
    <div class="device-detail-summary" aria-label={`${props.model.deviceTypeLabel}关键信息`}>
      <For each={props.model.summaryFields}>
        {(field) => (
          <div class="device-detail-summary-item">
            <span>{field.label}</span>
            <strong>{field.value}</strong>
          </div>
        )}
      </For>
    </div>
  );
}

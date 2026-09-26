import { For } from "solid-js";
import { uiText } from "../../text";
import type { SpecializedDeviceModel } from "../../deviceTopology/adapters/adapterRegistry";

export function DeviceSpecializedSummary(props: { model: SpecializedDeviceModel }) {
  return (
    <div class="device-detail-summary" aria-label={uiText.misc.keyFacts(props.model.deviceTypeLabel)}>
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

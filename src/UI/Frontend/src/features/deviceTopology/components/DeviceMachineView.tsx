import { createUniqueId, For, Index, Show } from "solid-js";
import type { DeviceTopologySystemIdentity, DeviceTopologyMachineIdentity, DeviceTopologyGpuInformation, DeviceTopologyExternalMachine } from "../../../types.ts";
import { uiText } from "../../../text.ts";
import { machineEvidenceLines, machineFormName } from "../machineFormPresentation.ts";
import { machineIdentity } from "../machineIdentityPresentation.ts";
import { exitOnUnmount } from "../../../presentation/nodeAnimation.ts";

/**
 * 整机：按后端判定的形态画一个简洁的外观示意，下面是形态和判定依据，最下面是上面没写过的主板型号和 BIOS。
 * 厂商、系列在整栏里各只出现一次（machineIdentity）。
 * 示意图按形态挂载：形态变了才换一张（入场动效只在这时出现），每次推送只更新上面的文字。
 */
export function DeviceMachineView(props: { system: DeviceTopologySystemIdentity; machineIdentity: DeviceTopologyMachineIdentity; gpuInformation: DeviceTopologyGpuInformation[] | null; externalMachines: DeviceTopologyExternalMachine[] | null }) {
  const copy = () => uiText.deviceTopology.machine;
  const evidence = () => machineEvidenceLines(props.machineIdentity);
  const identity = () => machineIdentity(props.system);
  const evidenceTitleId = createUniqueId();
  return (
    <div class="device-top-view" role="group" aria-label={uiText.deviceTopology.topView}>
      <Show when={props.machineIdentity.formFactor} keyed>
        {(form) => (
          <div ref={(element) => exitOnUnmount(element)} class="device-machine-figure" data-form-factor={form}>
            <div class="device-machine-body">
              <span class="device-brand-logo">{identity().wordmark}</span>
              <Show when={identity().vendor}>
                {(vendor) => <strong class="device-machine-manufacturer">{vendor()}</strong>}
              </Show>
              <small class="device-machine-model">{identity().series}</small>
            </div>
            <Show when={form === "laptop" || form === "mobile_workstation"}>
              <div class="device-machine-hinge" />
              <div class="device-machine-deck" />
            </Show>
            <Show when={form === "workstation" || form === "desktop"}>
              <div class="device-machine-feet" />
            </Show>
          </div>
        )}
      </Show>
      <div class="device-machine-identity" role="group" aria-label={copy().label}>
        <div class="device-machine-form">
          <span>{copy().label}</span>
          <strong data-form-factor={props.machineIdentity.formFactor}>{machineFormName(props.machineIdentity)}</strong>
        </div>
        <span class="device-machine-evidence-title" id={evidenceTitleId}>{copy().evidence}</span>
        <Show
          when={evidence().length > 0}
          fallback={<p class="device-machine-evidence-empty">{copy().noEvidence}</p>}
        >
          <ul class="device-machine-evidence" role="list" aria-labelledby={evidenceTitleId}>
            <Index each={evidence()}>
              {(line) => (
                <li>
                  <span>{line().label}</span>
                  <Show when={line().value}>{(value) => <strong>{value()}</strong>}</Show>
                </li>
              )}
            </Index>
          </ul>
        </Show>
      </div>
      <Show when={props.gpuInformation === null}>
        <p data-graphics-dock-state="unknown">{copy().graphicsDock}: {copy().unknown}</p>
      </Show>
      <For each={props.gpuInformation?.filter((gpu) => gpu.attachmentKind === "graphics_dock") ?? []}>
        {(gpu) => <div data-attachment-kind="graphics_dock">
          <strong>{copy().graphicsDock}</strong>
          <p>{gpu.name ?? copy().unknown}</p>
        </div>}
      </For>
      <Show when={props.externalMachines === null}>
        <p data-external-machine-state="unknown">{copy().externalWorkstation}: {copy().unknown}</p>
      </Show>
      <For each={props.externalMachines ?? []}>
        {(peer) => <div data-external-machine={peer.localLinkDeviceId}>
          <strong>{copy().externalWorkstation}</strong>
          <p>{uiText.deviceTopology.section.deviceIdentity}: {peer.peerIdentity ?? `${copy().unknown} (${peer.evidence.peerPropertiesReason})`}</p>
          <p>{uiText.deviceSpecialized.model}: {peer.model ?? `${copy().unknown} (${peer.evidence.peerPropertiesReason})`}</p>
          <For each={peer.components}>
            {(part) => <p data-component={part.component}>{copy().soc}: {part.model ?? `${copy().unknown} (${peer.evidence.peerPropertiesReason})`}</p>}
          </For>
        </div>}
      </For>
      <div class="device-system-meta">
        <Index each={identity().chips}>
          {(chip) => (
            <span data-full-content={chip().title}>
              <Show when={chip().label}>{(label) => <small>{label()}</small>}</Show>
              {chip().value}
            </span>
          )}
        </Index>
      </div>
    </div>
  );
}

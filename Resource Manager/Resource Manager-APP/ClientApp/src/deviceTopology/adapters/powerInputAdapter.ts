import {
  connectionFacts,
  deviceTitle,
  displayValue,
  hasAnyEvidence,
  summaryFields
} from "./adapterEvidence.ts";
import type { DeviceAdapter, PowerInputDeviceModel } from "./types";
import { renderBackendMessage } from "../../presentation/backendMessage.ts";
import { uiText } from "../../text.ts";

export const powerInputAdapter: DeviceAdapter<PowerInputDeviceModel> = {
  id: "power-input",
  matches: ({ port }) => (port.connectorKind === "power" || port.usb?.portConnectorIsTypeC === true)
    && hasAnyEvidence(port, /power input|charging input|sink role|power sink|ac adapter input|PD input|供电输入|受电端/i),
  createModel: (context) => {
    const title = deviceTitle(context, uiText.deviceAdapters.powerInput);
    const connection = connectionFacts(context);
    const interconnect = context.port.advancedInterconnect;
    const inputRole = renderBackendMessage(interconnect?.role, uiText.deviceAdapters.powerInputRole);
    const relationEvidence = renderBackendMessage(interconnect?.evidence);
    const negotiatedPower = uiText.deviceAdapters.notReported;
    return {
      adapterId: "power-input",
      kind: "power-input",
      deviceTypeLabel: uiText.deviceAdapters.powerInput,
      title,
      subtitle: `${inputRole} · ${negotiatedPower}`,
      badge: uiText.deviceAdapters.powerInputBadge,
      iconKind: "power-input",
      ...connection,
      inputRole,
      negotiatedPower,
      relationEvidence,
      summaryFields: summaryFields(
        [uiText.deviceAdapters.label.deviceType, uiText.deviceAdapters.powerInput],
        [uiText.deviceAdapters.label.inputInterface, connection.upstreamInterface],
        [uiText.deviceAdapters.label.powerRole, inputRole],
        [uiText.deviceAdapters.label.negotiatedPower, negotiatedPower]
      ),
      capabilityLabels: [inputRole],
      searchTerms: ["电脑供电", "充电输入", "power input", inputRole]
    };
  }
};

import {
  connectionFacts,
  deviceTitle,
  displayValue,
  hasAnyEvidence,
  summaryFields
} from "./adapterEvidence.ts";
import type { DeviceAdapter, PowerInputDeviceModel } from "./types";

export const powerInputAdapter: DeviceAdapter<PowerInputDeviceModel> = {
  id: "power-input",
  matches: ({ port }) => (port.connectorKind === "power" || port.usb?.portConnectorIsTypeC === true)
    && hasAnyEvidence(port, /power input|charging input|sink role|power sink|ac adapter input|PD input|供电输入|受电端/i),
  createModel: (context) => {
    const title = deviceTitle(context, "电脑供电输入");
    const connection = connectionFacts(context);
    const interconnect = context.port.advancedInterconnect;
    const inputRole = displayValue(interconnect?.role, "输入供电");
    const relationEvidence = displayValue(interconnect?.evidence);
    const negotiatedPower = "未报告";
    return {
      adapterId: "power-input",
      kind: "power-input",
      deviceTypeLabel: "电脑供电输入",
      title,
      subtitle: `${inputRole} · ${negotiatedPower}`,
      badge: "供电输入",
      iconKind: "power-input",
      ...connection,
      inputRole,
      negotiatedPower,
      relationEvidence,
      summaryFields: summaryFields(
        ["设备类型", "电脑供电输入"],
        ["输入接口", connection.upstreamInterface],
        ["供电角色", inputRole],
        ["协商功率", negotiatedPower]
      ),
      capabilityLabels: [inputRole],
      searchTerms: ["电脑供电", "充电输入", "power input", inputRole]
    };
  }
};

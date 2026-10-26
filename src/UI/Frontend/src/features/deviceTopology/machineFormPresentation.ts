import type { DeviceTopologyMachineIdentity } from "../../types.ts";
import { uiText } from "../../text.ts";

/** 判定依据的一行：label 是这条依据，value 是它的取值（电池这类只有一句话的依据没有取值）。 */
export interface MachineEvidenceLine {
  readonly label: string;
  readonly value: string | null;
}

/** 形态在界面语言里的名字。 */
export function machineFormName(machine: DeviceTopologyMachineIdentity): string {
  return uiText.deviceTopology.machine.formFactor[machine.formFactor];
}

/**
 * 后端给的依据按机箱、电池、命中的部件依次列出；空事实显示未知及原始原因码。
 * 机箱码只翻译常见的（SMBIOS 机箱类型表），表里没有的显示原始码；规则 id 没有说明文字时显示 id 本身。
 */
export function machineEvidenceLines(machine: DeviceTopologyMachineIdentity): MachineEvidenceLine[] {
  const copy = uiText.deviceTopology.machine;
  const lines: MachineEvidenceLine[] = [];
  const { chassisTypes, systemBatteryPresent, workstationComponents } = machine.evidence;
  lines.push({
    label: copy.chassisType,
    value: chassisTypes.value === null ? `${copy.unknown} (${chassisTypes.reason})`
      : chassisTypes.value.map((value) => {
        const code = String(value);
        return Object.hasOwn(copy.chassisTypes, code) ? copy.chassisTypes[code as keyof typeof copy.chassisTypes] : code;
      }).join(", ") || "[]"
  });
  lines.push(systemBatteryPresent.value === null
    ? { label: copy.batteryLabel, value: `${copy.unknown} (${systemBatteryPresent.reason})` }
    : { label: systemBatteryPresent.value ? copy.battery.present : copy.battery.absent, value: null });
  for (const part of workstationComponents) {
    lines.push({
      label: Object.hasOwn(copy.rules, part.ruleId) ? copy.rules[part.ruleId as keyof typeof copy.rules] : part.ruleId,
      value: part.model
    });
  }
  return lines;
}

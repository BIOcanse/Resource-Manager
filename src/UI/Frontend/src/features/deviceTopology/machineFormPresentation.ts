import type { DeviceTopologyMachineIdentity } from "../../types.ts";
import { uiText } from "../../text.ts";

/** One classification fact with a label and optional value. */
export interface MachineEvidenceLine {
  readonly label: string;
  readonly value: string | null;
}

/** Localized form-factor name. */
export function machineFormName(machine: DeviceTopologyMachineIdentity): string {
  return uiText.deviceTopology.machine.formFactor[machine.formFactor];
}

/** List backend chassis, battery, and component evidence in order. Preserve unknown reason, chassis, and rule identifiers when no localized wording exists. */
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

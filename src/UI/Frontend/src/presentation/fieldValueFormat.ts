import { formatFixedDigits, formatInterfaceNumber, formatSignificantDigits, formatTrimmedDigits } from "./interfaceNumbers.ts";
import type { MetricFieldValue } from "../data/metricFields/metricFieldSource.ts";
import { formatBytes, formatBytesPerSecond, type ByteQuantityKind } from "./byteUnits.ts";
import { uiText } from "../text.ts";

/* Format canonical fields by their units. Unavailable is null, never zero; percentages may exceed 100. Byte units use the caller's memory/storage kind. Preserve unknown units and status codes rather than guessing conversions. */
export function formatFieldValue(value: MetricFieldValue, quantityKind: ByteQuantityKind = "memory"): string | null {
  if (value.booleanValue !== null) return value.booleanValue ? uiText.shellBridge.yes : uiText.shellBridge.no;
  const integer = value.integerValue;
  const decoded = integer !== null ? decodedFieldValue(value.name, integer) : null;
  if (decoded !== null) return decoded;
  const number = integer !== null ? Number(integer) : value.numericValue;
  if (number === null) return null;
  switch (value.unit) {
    case "B": return formatBytes(number, quantityKind);
    case "MiB": return formatBytes(number * 1024 * 1024, "memory");
    // One NVMe data unit is 512000 bytes.
    case "512000 B": return formatBytes(number * 512000, "storage");
    case "B/s": return formatBytesPerSecond(number);
    case "%": return `${formatFixedDigits(number, 1)}%`;
    case "°C": return `${formatFixedDigits(number, 1)} °C`;
    case "MHz":
    case "RPM":
    case "mV": return `${Math.round(number)} ${value.unit}`;
    case "W":
    case "V":
    case "J":
    case "s":
    case "ms":
    case "min":
    case "h": return `${formatTrimmedDigits(number, 1)} ${value.unit}`;
    case "A": return `${formatTrimmedDigits(number, 2)} A`;
    case "µs": return `${integer !== null ? integer.toString() : Math.round(number)} µs`;
    case "count":
    case "generation":
    case "lanes":
    case "enum": return integer !== null ? formatInterfaceNumber(integer) : formatTrimmedDigits(number, 2);
    case "bitmask64": return `0x${(integer ?? BigInt(Math.trunc(number))).toString(16).toUpperCase()}`;
    case "1":
    case "ratio": return formatTrimmedDigits(number, 3);
    default: return `${integer !== null ? integer.toString() : plain(number)} ${value.unit}`.trim();
  }
}

/** Decode storage health (0 healthy, 1 warning, 2 unhealthy, 5 unknown) and battery flags (AC 0x1, discharging 0x2, charging 0x4, critical 0x8). Unknown codes, unknown bits, or no battery flags return null for raw-value display. */
function decodedFieldValue(name: string, code: bigint): string | null {
  switch (name) {
    case "storage_all_health_status_reported": {
      const health = uiText.deviceTopology.storageHealth;
      return code === 0n ? health.healthy : code === 1n ? health.warning : code === 2n ? health.unhealthy : code === 5n ? health.unknown : null;
    }
    case "battery_all_charge_state_actual": {
      const flags = uiText.fieldValueLabel.batteryPowerState;
      const known: Array<[bigint, string]> = [[0x1n, flags.onLine], [0x2n, flags.discharging], [0x4n, flags.charging], [0x8n, flags.critical]];
      if (code <= 0n || (code & ~0xFn) !== 0n) return null;
      return known.filter(([bit]) => (code & bit) !== 0n).map(([, label]) => label).join(" · ");
    }
    default:
      return null;
  }
}

/** Use significant digits for tiny values so fixed precision does not turn them into zero. */
function plain(value: number): string {
  return value !== 0 && Math.abs(value) < 0.01
    ? formatSignificantDigits(value, 4)
    : formatTrimmedDigits(value, 3);
}

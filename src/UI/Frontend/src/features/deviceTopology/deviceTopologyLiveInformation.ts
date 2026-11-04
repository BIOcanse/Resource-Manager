import type { SubscriptionFieldReference } from "../../frontendRuntime/push/BackendSubscriptionChannel.ts";
import type { DeviceTopologyInformationSnapshot } from "../../types.ts";

/* Sampled free-space and battery fields arrive through selected-field subscriptions. Resolve volume identities by reported mount path and other devices by informationIdentity. */
export const volumeFreeBytesField = "volume_all_free_bytes_actual";
export const mobileBatteryPercentField = "mobile_all_battery_percent_reported";
export const mobileStorageFreeBytesField = "mobile_all_storage_free_bytes_reported";
const volumeMountPathsField = "volume_all_mount_paths_reported";

/** Match the reported drive mount exactly; labels, order, and disk ordinals are not volume identities. */
export function volumeInformationIdentity(structure: DeviceTopologyInformationSnapshot | null, driveLetter: string | null | undefined): string | null {
  if (!structure || !driveLetter || !/^[a-z]:\\?$/i.test(driveLetter)) return null;
  const mountPath = driveLetter.slice(0, 2).toUpperCase() + "\\";
  const matches = structure.devices.filter(device => device.objectType === "volume" && device.fields.some(field =>
    field.definition.name === volumeMountPathsField && Array.isArray(field.value)
    && field.value.some(path => typeof path === "string" && path.toUpperCase() === mountPath)));
  return matches.length === 1 ? matches[0].deviceIdentity : null;
}

/** Select free-space fields only for volumes with resolved addresses. */
export function volumeFreeSpaceSelection(identities: readonly (string | null)[]): SubscriptionFieldReference[] {
  return identities.filter((identity): identity is string => identity !== null)
    .map(deviceIdentity => ({ deviceIdentity, fieldName: volumeFreeBytesField }));
}

/** Select battery and storage fields only when the port has an information address. */
export function mobileReadingsSelection(identity: string | null | undefined): SubscriptionFieldReference[] {
  return identity
    ? [{ deviceIdentity: identity, fieldName: mobileBatteryPercentField }, { deviceIdentity: identity, fieldName: mobileStorageFreeBytesField }]
    : [];
}

/** Available volume bytes; null for absent frames, missing fields, or invalid decimal integer text. */
export function currentVolumeFreeBytes(information: DeviceTopologyInformationSnapshot | null, identity: string | null): number | null {
  return decimalBytes(fieldValue(information, identity, volumeFreeBytesField));
}

/** Reported battery percentage, or null when absent. */
export function currentMobileBatteryPercent(information: DeviceTopologyInformationSnapshot | null, identity: string | null | undefined): number | null {
  const value = fieldValue(information, identity, mobileBatteryPercentField);
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

/** Remaining bytes for storage identified by its reported name, or null when absent. */
export function currentMobileStorageFreeBytes(information: DeviceTopologyInformationSnapshot | null, identity: string | null | undefined, storageName: string): number | null {
  const rows = fieldValue(information, identity, mobileStorageFreeBytesField);
  if (!Array.isArray(rows)) return null;
  const matches = rows.filter((row): row is { name: unknown; freeBytes: unknown } =>
    typeof row === "object" && row !== null && (row as { name?: unknown }).name === storageName);
  return matches.length === 1 ? decimalBytes(matches[0].freeBytes) : null;
}

function fieldValue(information: DeviceTopologyInformationSnapshot | null, identity: string | null | undefined, fieldName: string): unknown {
  if (!information?.observedAt || !identity) return null;
  const device = information.devices.find(candidate => candidate.deviceIdentity === identity);
  const fields = device?.fields.filter(field => field.definition.name === fieldName) ?? [];
  return fields.length === 1 ? fields[0].value : null;
}

function decimalBytes(value: unknown): number | null {
  if (typeof value !== "string" || !/^\d+$/.test(value)) return null;
  const bytes = Number(value);
  return Number.isSafeInteger(bytes) ? bytes : null;
}

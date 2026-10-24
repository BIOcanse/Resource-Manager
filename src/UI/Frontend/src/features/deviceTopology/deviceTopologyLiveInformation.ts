import type { DeviceTopologyInformationSnapshot } from "../../types.ts";

/** Match the reported drive mount exactly. Labels, list order and disk ordinals are not volume identities. */
export function currentVolumeFreeBytes(information: DeviceTopologyInformationSnapshot, driveLetter: string | null | undefined): number | null {
  if (!information.observedAt || !driveLetter || !/^[a-z]:\\?$/i.test(driveLetter)) return null;
  const mountPath = driveLetter.slice(0, 2).toUpperCase() + "\\";
  const matches = information.devices.filter(device => device.objectType === "volume" && device.fields.some(field =>
    field.definition.name === "volume_all_mount_paths_reported" && Array.isArray(field.value)
    && field.value.some(path => typeof path === "string" && path.toUpperCase() === mountPath)));
  if (matches.length !== 1) return null;
  const fields = matches[0].fields.filter(field => field.definition.name === "volume_all_free_bytes_actual");
  if (fields.length !== 1 || typeof fields[0].value !== "string" || !/^\d+$/.test(fields[0].value)) return null;
  const value = Number(fields[0].value);
  return Number.isSafeInteger(value) && value >= 0 ? value : null;
}

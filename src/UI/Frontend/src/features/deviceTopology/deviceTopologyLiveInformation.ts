import type { SubscriptionFieldReference } from "../../frontendRuntime/push/BackendSubscriptionChannel.ts";
import type { DeviceTopologyInformationSnapshot } from "../../types.ts";

/*
 * 采样值（卷可用空间、手机电量和存储剩余）不在拓扑结构帧里，要显示它们的地方按设备地址和字段名订阅
 * /api/device-topology/fields/subscribe（见 deviceTopologyApi.ts），从那一路的帧里按地址读。
 * 设备地址取自后端：卷按结构帧 informationFields 里的挂载路径找到它的地址，端口上的设备用端口的 informationIdentity。
 */
export const volumeFreeBytesField = "volume_all_free_bytes_actual";
export const mobileBatteryPercentField = "mobile_all_battery_percent_reported";
export const mobileStorageFreeBytesField = "mobile_all_storage_free_bytes_reported";
const volumeMountPathsField = "volume_all_mount_paths_reported";

/**
 * 卷的设备地址：结构帧 informationFields 里挂载路径恰好是这个盘符的那一个卷。
 * Match the reported drive mount exactly. Labels, list order and disk ordinals are not volume identities.
 */
export function volumeInformationIdentity(structure: DeviceTopologyInformationSnapshot | null, driveLetter: string | null | undefined): string | null {
  if (!structure || !driveLetter || !/^[a-z]:\\?$/i.test(driveLetter)) return null;
  const mountPath = driveLetter.slice(0, 2).toUpperCase() + "\\";
  const matches = structure.devices.filter(device => device.objectType === "volume" && device.fields.some(field =>
    field.definition.name === volumeMountPathsField && Array.isArray(field.value)
    && field.value.some(path => typeof path === "string" && path.toUpperCase() === mountPath)));
  return matches.length === 1 ? matches[0].deviceIdentity : null;
}

/** 这些卷的可用空间字段选择；没找到地址的卷不选。 */
export function volumeFreeSpaceSelection(identities: readonly (string | null)[]): SubscriptionFieldReference[] {
  return identities.filter((identity): identity is string => identity !== null)
    .map(deviceIdentity => ({ deviceIdentity, fieldName: volumeFreeBytesField }));
}

/** 一台手机或平板的电量和存储剩余字段选择；端口没有信息地址时不选。 */
export function mobileReadingsSelection(identity: string | null | undefined): SubscriptionFieldReference[] {
  return identity
    ? [{ deviceIdentity: identity, fieldName: mobileBatteryPercentField }, { deviceIdentity: identity, fieldName: mobileStorageFreeBytesField }]
    : [];
}

/** 卷的可用空间（字节）。还没收到帧、没有这个字段或值不是十进制整数文本时为 null。 */
export function currentVolumeFreeBytes(information: DeviceTopologyInformationSnapshot | null, identity: string | null): number | null {
  return decimalBytes(fieldValue(information, identity, volumeFreeBytesField));
}

/** 手机或平板自报的电量百分比；没有时为 null。 */
export function currentMobileBatteryPercent(information: DeviceTopologyInformationSnapshot | null, identity: string | null | undefined): number | null {
  const value = fieldValue(information, identity, mobileBatteryPercentField);
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

/** 手机或平板上一个存储（按自报名称）的剩余字节；没有时为 null。 */
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

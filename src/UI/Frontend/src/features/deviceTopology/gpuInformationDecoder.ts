import {
  requireArray, requireBoolean, requireNonEmptyString, requireNonNegativeSafeInteger,
  requireNullable, requireOneOf, requireRecord, requireString, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { DeviceTopologyGpuInformation } from "../../types.ts";

export function decodeGpuInformation(value: unknown, path: string): DeviceTopologyGpuInformation[] | null {
  return requireNullable(value, path, (value, path) => requireArray(value, path).map((value, index) => {
    const rowPath = `${path}[${index}]`;
    const row = requireRecord(value, rowPath);
    return {
      pnpInstanceId: requireNonEmptyString(row.pnpInstanceId, `${rowPath}.pnpInstanceId`),
      adapterLuid: requireNullable(row.adapterLuid, `${rowPath}.adapterLuid`, requireString),
      name: requireNullable(row.name, `${rowPath}.name`, requireString),
      pciVendorId: requireNullable(row.pciVendorId, `${rowPath}.pciVendorId`, requireString),
      pciDeviceId: requireNullable(row.pciDeviceId, `${rowPath}.pciDeviceId`, requireString),
      driverVersion: requireNullable(row.driverVersion, `${rowPath}.driverVersion`, requireString),
      adapterTypeFlags: requireNullable(row.adapterTypeFlags, `${rowPath}.adapterTypeFlags`, (value, path) => {
        const flags = requireNonNegativeSafeInteger(value, path);
        if (flags > 0xffffffff) throw new ResponseDecodeError(path, "uint32 adapter type flags");
        return flags;
      }),
      isDetachable: requireNullable(row.isDetachable, `${rowPath}.isDetachable`, requireBoolean),
      attachmentKind: requireNullable(row.attachmentKind, `${rowPath}.attachmentKind`, (value, path) =>
        requireOneOf(value, path, ["graphics_dock"] as const)),
      attachmentEvidenceSource: requireNullable(row.attachmentEvidenceSource, `${rowPath}.attachmentEvidenceSource`, (value, path) =>
        requireOneOf(value, path, ["d3dkmt-adapter-type"] as const))
    };
  }));
}

import {
  requireArray, requireNonEmptyString, requireNonNegativeSafeInteger, requireNullable,
  requireOneOf, requireRecord, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { DeviceTopologyTransport } from "../../types.ts";

export function decodeTransport(value: unknown, path: string): DeviceTopologyTransport {
  const row = requireRecord(value, path);
  const code = requireNullable(row.code, `${path}.code`, (value, path) => requireOneOf(value, path,
    ["hd_audio", "bluetooth", "ucsi", "nvme", "ahci", "i2c_hid", "acpi", "pci", "usb", "usb4"] as const));
  const reason = requireNullable(row.reason, `${path}.reason`, requireNonEmptyString);
  const ruleId = requireNullable(row.ruleId, `${path}.ruleId`, requireNonEmptyString);
  const facts = requireArray(row.evidence, `${path}.evidence`);
  if (facts.length > 32 || (code === null ? reason === null || ruleId !== null
    : reason !== null || ruleId === null || facts.length === 0))
    throw new ResponseDecodeError(path, "available transport with rule/evidence, or null transport with reason");
  return { code, reason, ruleId, evidence: facts.map((value, index) => {
    const itemPath = `${path}.evidence[${index}]`;
    const item = requireRecord(value, itemPath);
    const depth = requireNonNegativeSafeInteger(item.depth, `${itemPath}.depth`);
    if (depth !== index) throw new ResponseDecodeError(`${itemPath}.depth`, "ordered parent chain depth");
    return {
      source: requireOneOf(item.source, `${itemPath}.source`, ["setupapi-cfgmgr32", "usb-hub-ioctl"] as const),
      deviceId: requireNonEmptyString(item.deviceId, `${itemPath}.deviceId`), depth,
      enumeratorName: requireNullable(item.enumeratorName, `${itemPath}.enumeratorName`, requireNonEmptyString),
      busTypeGuid: requireNullable(item.busTypeGuid, `${itemPath}.busTypeGuid`, (value, path) => {
        const guid = requireNonEmptyString(value, path);
        if (!/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(guid))
          throw new ResponseDecodeError(path, "bus type GUID");
        return guid;
      }),
      service: requireNullable(item.service, `${itemPath}.service`, requireNonEmptyString),
      parentDeviceId: requireNullable(item.parentDeviceId, `${itemPath}.parentDeviceId`, requireNonEmptyString)
    };
  }) };
}

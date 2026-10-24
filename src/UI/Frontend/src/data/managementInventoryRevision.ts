import {
  defineResponseDecoder,
  requireRecord,
  requireString
} from "../frontendRuntime/request/ResponseDecoder.ts";
import { isCanonicalUInt64Decimal } from "./operations/uint64Decimal.ts";

export interface ManagementInventoryRevisionSnapshot {
  readonly softwareRevision: string;
  readonly componentRevision: string;
  readonly browserRuntimeRevision: string;
}

export const managementInventoryRevisionDecoder = defineResponseDecoder<ManagementInventoryRevisionSnapshot>(
  "management-inventory-revision.v1",
  (value) => {
    const record = requireRecord(value);
    return {
      softwareRevision: revision(record.softwareRevision, "$.softwareRevision"),
      componentRevision: revision(record.componentRevision, "$.componentRevision"),
      browserRuntimeRevision: revision(record.browserRuntimeRevision, "$.browserRuntimeRevision")
    };
  });

export function buildManagementInventoryRevisionSubscriptionUrl(): string {
  return "/api/management/inventory-revisions/subscribe";
}

function revision(value: unknown, path: string): string {
  const text = requireString(value, path);
  if (!isCanonicalUInt64Decimal(text)) {
    throw new TypeError(`Invalid management inventory revision at '${path}'.`);
  }
  return text;
}

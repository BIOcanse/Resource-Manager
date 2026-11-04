import { formatBytes } from "../../presentation/byteUnits.ts";
const maximumUInt64Decimal = "18446744073709551615";

export function isCanonicalUInt64Decimal(value: unknown): value is string {
  return typeof value === "string"
    && /^(0|[1-9][0-9]*)$/.test(value)
    && compareUnsignedDecimalText(value, maximumUInt64Decimal) <= 0;
}

export function compareUInt64Decimal(left: string, right: string): number {
  if (!isCanonicalUInt64Decimal(left) || !isCanonicalUInt64Decimal(right)) {
    throw new TypeError("Expected canonical uint64 decimal strings.");
  }
  return compareUnsignedDecimalText(left, right);
}

export function formatUInt64Bytes(value: string): string {
  if (!isCanonicalUInt64Decimal(value)) {
    throw new TypeError("Expected a canonical uint64 decimal string.");
  }
  // Delegate transfer-size and rate conversion to the storage-unit formatter.
  // Transfer amounts fit below 2^53, so conversion to number remains exact.
  return formatBytes(Number(BigInt(value)), "storage");
}

function compareUnsignedDecimalText(left: string, right: string): number {
  if (left.length !== right.length) {
    return left.length > right.length ? 1 : -1;
  }
  return left === right ? 0 : left > right ? 1 : -1;
}

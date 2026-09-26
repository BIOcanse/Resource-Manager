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
  // 下载体积与速率都是传输量，按存储类交给唯一所有者换算。
  // uint64 在这里先转成 number：传输量远小于 2^53，不会丢精度。
  return formatBytes(Number(BigInt(value)), "storage");
}

function compareUnsignedDecimalText(left: string, right: string): number {
  if (left.length !== right.length) {
    return left.length > right.length ? 1 : -1;
  }
  return left === right ? 0 : left > right ? 1 : -1;
}

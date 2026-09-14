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
  const units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"] as const;
  const bytes = BigInt(value);
  let unit = 0;
  let divisor = 1n;
  while (unit < units.length - 1 && bytes >= divisor * 1024n) {
    divisor *= 1024n;
    unit += 1;
  }
  if (unit === 0) {
    return `${bytes} B`;
  }

  const whole = bytes / divisor;
  const digits = whole >= 10n ? 1 : 2;
  const scale = 10n ** BigInt(digits);
  const rounded = (bytes * scale + divisor / 2n) / divisor;
  const integer = rounded / scale;
  const fraction = String(rounded % scale).padStart(digits, "0");
  return `${integer}.${fraction} ${units[unit]}`;
}

function compareUnsignedDecimalText(left: string, right: string): number {
  if (left.length !== right.length) {
    return left.length > right.length ? 1 : -1;
  }
  return left === right ? 0 : left > right ? 1 : -1;
}

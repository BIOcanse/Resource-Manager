import type { BackendMessage } from "../../types.ts";

export interface ResponseDecoder<T> {
  readonly id: string;
  decode(value: unknown): T;
}

export function defineResponseDecoder<T>(
  id: string,
  decode: (value: unknown) => T
): ResponseDecoder<T> {
  const normalizedId = id.trim();
  if (!normalizedId) {
    throw new Error("A response decoder id is required.");
  }
  return { id: normalizedId, decode };
}

export class ResponseDecodeError extends Error {
  readonly path: string;
  readonly expected: string;

  constructor(path: string, expected: string) {
    super(`Invalid response field '${path}'; expected ${expected}.`);
    this.name = "ResponseDecodeError";
    this.path = path;
    this.expected = expected;
  }
}

export function requireRecord(
  value: unknown,
  path = "$"
): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new ResponseDecodeError(path, "object");
  }
  return value as Record<string, unknown>;
}

export function requireString(value: unknown, path: string): string {
  if (typeof value !== "string") {
    throw new ResponseDecodeError(path, "string");
  }
  return value;
}

export function requireNonEmptyString(value: unknown, path: string): string {
  const text = requireString(value, path).trim();
  if (!text) {
    throw new ResponseDecodeError(path, "non-empty string");
  }
  return text;
}

export function requireBoolean(value: unknown, path: string): boolean {
  if (typeof value !== "boolean") {
    throw new ResponseDecodeError(path, "boolean");
  }
  return value;
}

export function requireSafeInteger(value: unknown, path: string): number {
  if (!Number.isSafeInteger(value)) {
    throw new ResponseDecodeError(path, "safe integer");
  }
  return value as number;
}

export function requireNonNegativeSafeInteger(
  value: unknown,
  path: string
): number {
  const integer = requireSafeInteger(value, path);
  if (integer < 0) {
    throw new ResponseDecodeError(path, "non-negative safe integer");
  }
  return integer;
}

export function requireFiniteNumber(value: unknown, path: string): number {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    throw new ResponseDecodeError(path, "finite number");
  }
  return value;
}

export function requireNonNegativeFiniteNumber(
  value: unknown,
  path: string
): number {
  const number = requireFiniteNumber(value, path);
  if (number < 0) {
    throw new ResponseDecodeError(path, "non-negative finite number");
  }
  return number;
}

export function requireNullable<T>(
  value: unknown,
  path: string,
  decode: (value: unknown, path: string) => T
): T | null {
  return value === null ? null : decode(value, path);
}

export function requireStringArray(value: unknown, path: string): string[] {
  return requireArray(value, path)
    .map((item, index) => requireString(item, `${path}[${index}]`));
}

export function requireOneOf<const T extends string>(
  value: unknown,
  path: string,
  allowed: readonly T[]
): T {
  if (typeof value !== "string" || !allowed.includes(value as T)) {
    throw new ResponseDecodeError(
      path,
      allowed.map((item) => `'${item}'`).join(" or "));
  }
  return value as T;
}

/**
 * 后端消息码的线上形状：`{ domain, code, args }`，域和码都是 byte。
 * 措辞不在线上，前端按当前语言渲染，见 `presentation/backendMessage.ts`。
 */
export function requireBackendMessage(value: unknown, path: string): BackendMessage {
  const record = requireRecord(value, path);
  return {
    domain: requireByte(record.domain, `${path}.domain`),
    code: requireByte(record.code, `${path}.code`),
    args: requireStringArray(record.args ?? [], `${path}.args`)
  };
}

function requireByte(value: unknown, path: string): number {
  const number = requireNonNegativeSafeInteger(value, path);
  if (number > 255) {
    throw new ResponseDecodeError(path, "byte (0-255)");
  }
  return number;
}

export function requireArray(value: unknown, path: string): unknown[] {
  if (!Array.isArray(value)) {
    throw new ResponseDecodeError(path, "array");
  }
  return value;
}

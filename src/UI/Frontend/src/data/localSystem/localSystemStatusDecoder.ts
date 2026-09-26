import {
  defineResponseDecoder,
  requireNonEmptyString,
  requireRecord,
  requireSafeInteger,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

export interface LocalSystemStatus {
  readonly capturedAt: string;
  readonly bootedAt: string;
  readonly uptimeSeconds: number;
}

export const localSystemStatusDecoder = defineResponseDecoder<LocalSystemStatus>(
  "local-system.status.v1",
  (value) => {
    const record = requireRecord(value);
    const capturedAt = requireTimestamp(record.capturedAt, "$.capturedAt");
    const bootedAt = requireTimestamp(record.bootedAt, "$.bootedAt");
    const uptimeSeconds = requireSafeInteger(
      record.uptimeSeconds,
      "$.uptimeSeconds");
    if (uptimeSeconds < 0) {
      throw new ResponseDecodeError("$.uptimeSeconds", "non-negative safe integer");
    }
    if (Date.parse(bootedAt) > Date.parse(capturedAt)) {
      throw new ResponseDecodeError(
        "$.bootedAt",
        "timestamp not later than $.capturedAt");
    }

    return { capturedAt, bootedAt, uptimeSeconds };
  });

function requireTimestamp(value: unknown, path: string): string {
  const timestamp = requireNonEmptyString(value, path);
  if (!Number.isFinite(Date.parse(timestamp))) {
    throw new ResponseDecodeError(path, "valid timestamp string");
  }
  return timestamp;
}

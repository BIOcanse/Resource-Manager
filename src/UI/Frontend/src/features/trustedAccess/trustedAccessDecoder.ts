import {
  defineResponseDecoder,
  requireArray,
  requireBoolean,
  requireNonEmptyString,
  requireNonNegativeSafeInteger,
  requireOneOf,
  requireRecord,
  requireString,
  ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type {
  AccessKeyCreated,
  AccessKeyPurpose,
  AccessKeySnapshot,
  AccessKeySummary,
  AccessRequestDecisionResult,
  AccessRequestPending,
  AccessRequestPendingSnapshot,
  DefaultAccessKeyState
} from "./trustedAccessTypes.ts";

const purposes = ["control", "openai", "anthropic"] as const;
const denialReasons = ["not-approved", "unsupported", "duplicate", "not-needed"] as const;
const decisionStatuses = ["approved", "denied", "expired"] as const;

export const accessKeySummaryDecoder = defineResponseDecoder<AccessKeySummary>(
  "trusted-access.key-summary.v1",
  decodeAccessKeySummary);

export const accessKeySnapshotDecoder = defineResponseDecoder<AccessKeySnapshot>(
  "trusted-access.key-snapshot.v1",
  (value) => {
    const row = requireRecord(value);
    requireNoSecretFields(row, "$");
    const keys = requireArray(row.keys, "$.keys").map((key, index) =>
      decodeAccessKeySummary(key, `$.keys[${index}]`));
    if (keys.some((key) => key.purpose !== "control")) {
      throw new ResponseDecodeError("$.keys", "control-purpose keys only");
    }
    const defaultEnabled = requireBoolean(row.defaultEnabled, "$.defaultEnabled");
    const defaultKeyId = row.defaultKeyId === null
      ? null
      : requireNonEmptyString(row.defaultKeyId, "$.defaultKeyId");
    const availableScopes = uniqueStrings(row.availableScopes, "$.availableScopes");
    const defaultRows = keys.filter((key) => key.isDefault);
    if (defaultEnabled !== (defaultKeyId !== null)
      || (defaultEnabled
        ? defaultRows.length !== 1 || defaultRows[0].id !== defaultKeyId
        : defaultRows.length !== 0)) {
      throw new ResponseDecodeError("$.defaultKeyId", "consistent enabled default key metadata");
    }
    return { keys, defaultEnabled, defaultKeyId, availableScopes };
  });

export const accessKeyCreatedDecoder = defineResponseDecoder<AccessKeyCreated>(
  "trusted-access.key-created.v1",
  (value) => {
    const row = requireRecord(value);
    const key = decodeAccessKeySummary(row.key, "$.key");
    if (key.purpose !== "control" || key.isDefault) {
      throw new ResponseDecodeError("$.key", "an independent control key");
    }
    return { key, apiKey: requireNonEmptyString(row.apiKey, "$.apiKey") };
  });

export const defaultAccessKeyStateDecoder = defineResponseDecoder<DefaultAccessKeyState>(
  "trusted-access.default-key-state.v1",
  (value) => {
    const row = requireRecord(value);
    requireNoSecretFields(row, "$");
    const defaultEnabled = requireBoolean(row.defaultEnabled, "$.defaultEnabled");
    const defaultKeyId = row.defaultKeyId === null
      ? null
      : requireNonEmptyString(row.defaultKeyId, "$.defaultKeyId");
    if (defaultEnabled !== (defaultKeyId !== null)) {
      throw new ResponseDecodeError("$.defaultKeyId", "consistent enabled default key metadata");
    }
    return { defaultEnabled, defaultKeyId };
  });

export const accessRequestPendingSnapshotDecoder = defineResponseDecoder<AccessRequestPendingSnapshot>(
  "trusted-access.pending-requests.v1",
  (value) => {
    const requests = requireArray(value, "$").map(decodeAccessRequestPending);
    const ids = new Set<string>();
    for (const request of requests) {
      if (ids.has(request.requestId)) {
        throw new ResponseDecodeError("$.requestId", "unique pending request IDs");
      }
      ids.add(request.requestId);
    }
    return { requests };
  });

export const accessRequestDecisionDecoder = defineResponseDecoder<AccessRequestDecisionResult>(
  "trusted-access.request-decision.v1",
  decodeAccessRequestDecision);

export function decodeAccessRequestDecision(value: unknown, expectedRequestId?: string): AccessRequestDecisionResult {
  const row = requireRecord(value);
  requireNoSecretFields(row, "$");
  const status = requireOneOf(row.status, "$.status", decisionStatuses);
  const requestId = row.requestId === undefined
    ? expectedRequestId ?? requireNonEmptyString(row.requestId, "$.requestId")
    : requireNonEmptyString(row.requestId, "$.requestId");
  if (expectedRequestId && requestId !== expectedRequestId) {
    throw new ResponseDecodeError("$.requestId", "the requested access-request ID");
  }
  const key = row.key == null ? undefined : decodeAccessKeySummary(row.key, "$.key");
  if (key && (key.purpose !== "control" || key.isDefault)) {
    throw new ResponseDecodeError("$.key", "an independent control key");
  }
  const denialReason = row.denialReason == null
    ? undefined
    : requireOneOf(row.denialReason, "$.denialReason", denialReasons);
  if (status === "approved" && denialReason !== undefined) {
    throw new ResponseDecodeError("$.denialReason", "absent for approved decisions");
  }
  if (status !== "approved" && key !== undefined) {
    throw new ResponseDecodeError("$.key", "absent unless approved");
  }
  if (status !== "denied" && denialReason !== undefined) {
    throw new ResponseDecodeError("$.denialReason", "absent unless denied");
  }
  return {
    requestId,
    status,
    ...(key ? { key } : {}),
    ...(denialReason ? { denialReason } : {})
  };
}

function decodeAccessKeySummary(value: unknown, path = "$"): AccessKeySummary {
  const row = requireRecord(value, path);
  requireNoSecretFields(row, path);
  return {
    id: requireNonEmptyString(row.id, `${path}.id`),
    name: requireNonEmptyString(row.name, `${path}.name`),
    purpose: requireOneOf(row.purpose, `${path}.purpose`, purposes) as AccessKeyPurpose,
    scopes: uniqueStrings(row.scopes, `${path}.scopes`),
    createdAt: utcDateTime(row.createdAt, `${path}.createdAt`),
    isDefault: requireBoolean(row.isDefault, `${path}.isDefault`)
  };
}

function requireNoSecretFields(row: Record<string, unknown>, path: string): void {
  for (const field of ["apiKey", "secret", "defaultSecret", "deliverySecret", "credential"]) {
    if (Object.prototype.hasOwnProperty.call(row, field)) {
      throw new ResponseDecodeError(`${path}.${field}`, "absent from metadata responses");
    }
  }
}

function decodeAccessRequestPending(value: unknown, index: number): AccessRequestPending {
  const path = `$[${index}]`;
  const row = requireRecord(value, path);
  requireNoSecretFields(row, path);
  return {
    requestId: requireNonEmptyString(row.requestId, `${path}.requestId`),
    name: requireNonEmptyString(row.name, `${path}.name`),
    requestedScopes: uniqueStrings(row.requestedScopes, `${path}.requestedScopes`),
    observedExecutablePath: requireNonEmptyString(row.observedExecutablePath, `${path}.observedExecutablePath`),
    observedProcessId: requireNonNegativeSafeInteger(row.observedProcessId, `${path}.observedProcessId`),
    submittedAt: utcDateTime(row.submittedAt, `${path}.submittedAt`),
    expiresAt: utcDateTime(row.expiresAt, `${path}.expiresAt`)
  };
}

function uniqueStrings(value: unknown, path: string): string[] {
  const items = requireArray(value, path).map((item, index) =>
    requireNonEmptyString(item, `${path}[${index}]`));
  if (new Set(items).size !== items.length) {
    throw new ResponseDecodeError(path, "unique non-empty strings");
  }
  return items;
}

function utcDateTime(value: unknown, path: string): string {
  const text = requireString(value, path);
  if (!/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)$/.test(text)
    || !Number.isFinite(Date.parse(text))) {
    throw new ResponseDecodeError(path, "UTC date-time with an explicit offset");
  }
  return text;
}

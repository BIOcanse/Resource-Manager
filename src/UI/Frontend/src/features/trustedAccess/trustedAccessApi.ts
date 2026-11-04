import { ApiRequestError, requestJson, requestNoContent } from "../../api/httpTransport.ts";
import { RequestProblem } from "../../frontendRuntime/request/RequestProblem.ts";
import type { ResponseDecoder } from "../../frontendRuntime/request/ResponseDecoder.ts";
import {
  accessKeyCreatedDecoder,
  accessKeySnapshotDecoder,
  defaultAccessKeyStateDecoder,
  decodeAccessRequestDecision
} from "./trustedAccessDecoder.ts";
import type {
  AccessKeyCreated,
  AccessKeySnapshot,
  AccessRequestDecisionResult,
  AccessRequestDenialReason,
  DefaultAccessKeyState,
  TrustedAccessFailureReason
} from "./trustedAccessTypes.ts";

/** The API boundary classifies localized failure reasons; generic failures retain the original cause and caller explanation. */
export class TrustedAccessRequestError extends Error {
  readonly reason: TrustedAccessFailureReason;

  constructor(reason: TrustedAccessFailureReason, cause: unknown) {
    super(cause instanceof Error ? cause.message : String(cause), { cause });
    this.name = "TrustedAccessRequestError";
    this.reason = reason;
  }
}

/** Classify backend status and error codes; preserve other errors as failed causes. */
export function classifyTrustedAccessFailure(error: unknown): TrustedAccessFailureReason {
  if (!(error instanceof ApiRequestError || error instanceof RequestProblem) || error.kind !== "http") return "failed";
  const code = typeof error.payload === "object" && error.payload !== null && "error" in error.payload
    ? (error.payload as { error: unknown }).error
    : null;
  if (error.status === 401 || (error.status === 403 && code === "access-key-required")) return "unauthorized";
  if (error.status === 403 && (code === "access-scope-required" || code === "access-key-management-scope-exceeded")) {
    return "scope-required";
  }
  if (error.status === 403 && code === "default-key-scope-escalation-denied") return "default-scope-denied";
  return "failed";
}

async function classified<T>(operation: () => Promise<T>): Promise<T> {
  try {
    return await operation();
  } catch (error) {
    throw new TrustedAccessRequestError(classifyTrustedAccessFailure(error), error);
  }
}

export function buildAccessRequestPendingSubscriptionUrl(): string {
  return "/api/access-requests/subscribe";
}

export async function getAccessKeySnapshot(
  fallbackError: string,
  signal?: AbortSignal
): Promise<AccessKeySnapshot> {
  return classified(async () => decode(accessKeySnapshotDecoder, await requestJson<unknown>("/api/access-keys", {
    cache: "no-store",
    fallbackError,
    signal
  })));
}

export async function createAccessKey(
  name: string,
  scopes: readonly string[],
  fallbackError: string
): Promise<AccessKeyCreated> {
  return classified(() => post("/api/access-keys", { name, purpose: "control", scopes }, accessKeyCreatedDecoder, fallbackError));
}

export async function rotateAccessKey(
  id: string,
  fallbackError: string
): Promise<AccessKeyCreated> {
  return classified(() => post(`/api/access-keys/${encodeURIComponent(id)}/rotate`, {}, accessKeyCreatedDecoder, fallbackError));
}

export async function revokeAccessKey(id: string, fallbackError: string): Promise<void> {
  await classified(() => requestNoContent(`/api/access-keys/${encodeURIComponent(id)}`, {
    method: "DELETE",
    cache: "no-store",
    fallbackError
  }));
}

/** Elevated administrator authorization can manage the default key without a key, including re-enabling it. */
export async function setDefaultAccessKey(
  change: "enable" | "disable" | "rotate",
  fallbackError: string
): Promise<DefaultAccessKeyState> {
  const enabled = change !== "disable";
  return classified(async () => requireDefaultState(await post(
    "/api/access-keys/default",
    { enabled, rotate: change === "rotate" },
    defaultAccessKeyStateDecoder,
    fallbackError), enabled));
}

function requireDefaultState(result: DefaultAccessKeyState, enabled: boolean): DefaultAccessKeyState {
  if (result.defaultEnabled !== enabled) {
    throw new TypeError("The backend returned a different default access-key state.");
  }
  return result;
}

export async function approveAccessRequest(
  requestId: string,
  scopes: readonly string[],
  fallbackError: string
): Promise<AccessRequestDecisionResult> {
  return classified(() => decide(`/api/access-requests/${encodeURIComponent(requestId)}/approve`,
    requestId, { scopes }, fallbackError));
}

export async function denyAccessRequest(
  requestId: string,
  reasonCode: AccessRequestDenialReason,
  fallbackError: string
): Promise<AccessRequestDecisionResult> {
  return classified(() => decide(`/api/access-requests/${encodeURIComponent(requestId)}/deny`,
    requestId, { reasonCode }, fallbackError));
}

async function decide(
  url: string,
  requestId: string,
  body: unknown,
  fallbackError: string
): Promise<AccessRequestDecisionResult> {
  try {
    return decodeAccessRequestDecision(await postValue(url, body, fallbackError), requestId);
  } catch (error) {
    if (error instanceof ApiRequestError && error.status === 409) {
      try {
        return decodeAccessRequestDecision(asRecord(error.payload), requestId);
      } catch {
        // Preserve ordinary API errors unless the conflict is a typed terminal state.
      }
    }
    throw error;
  }
}

async function post<T>(
  url: string,
  body: unknown,
  decoder: ResponseDecoder<T>,
  fallbackError: string
): Promise<T> {
  return decode(decoder, await postValue(url, body, fallbackError));
}

async function postValue(url: string, body: unknown, fallbackError: string): Promise<unknown> {
  return requestJson<unknown>(url, {
    method: "POST",
    cache: "no-store",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
    fallbackError
  });
}

function decode<T>(decoder: ResponseDecoder<T>, value: unknown): T {
  return decoder.decode(value);
}

function asRecord(value: unknown): Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new TypeError("The access-request decision response is invalid.");
  }
  return value as Record<string, unknown>;
}

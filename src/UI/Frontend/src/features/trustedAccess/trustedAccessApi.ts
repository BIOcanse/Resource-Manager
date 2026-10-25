import { ApiRequestError, requestJson, requestNoContent } from "../../api/httpTransport.ts";
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
  DefaultAccessKeyState
} from "./trustedAccessTypes.ts";

export function buildAccessRequestPendingSubscriptionUrl(): string {
  return "/api/access-requests/subscribe";
}

export async function getAccessKeySnapshot(
  fallbackError: string,
  signal?: AbortSignal
): Promise<AccessKeySnapshot> {
  return decode(accessKeySnapshotDecoder, await requestJson<unknown>("/api/access-keys", {
    cache: "no-store",
    fallbackError,
    signal
  }));
}

export async function createAccessKey(
  name: string,
  scopes: readonly string[],
  fallbackError: string
): Promise<AccessKeyCreated> {
  return post("/api/access-keys", { name, purpose: "control", scopes }, accessKeyCreatedDecoder, fallbackError);
}

export async function rotateAccessKey(
  id: string,
  fallbackError: string
): Promise<AccessKeyCreated> {
  return post(`/api/access-keys/${encodeURIComponent(id)}/rotate`, {}, accessKeyCreatedDecoder, fallbackError);
}

export async function revokeAccessKey(id: string, fallbackError: string): Promise<void> {
  await requestNoContent(`/api/access-keys/${encodeURIComponent(id)}`, {
    method: "DELETE",
    cache: "no-store",
    fallbackError
  });
}

export async function setDefaultAccessKey(
  enabled: boolean,
  rotate: boolean,
  fallbackError: string
): Promise<DefaultAccessKeyState> {
  const result = await post(
    "/api/access-keys/default",
    { enabled, rotate },
    defaultAccessKeyStateDecoder,
    fallbackError);
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
  return decide(`/api/access-requests/${encodeURIComponent(requestId)}/approve`,
    requestId, { scopes }, fallbackError);
}

export async function denyAccessRequest(
  requestId: string,
  reasonCode: AccessRequestDenialReason,
  fallbackError: string
): Promise<AccessRequestDecisionResult> {
  return decide(`/api/access-requests/${encodeURIComponent(requestId)}/deny`,
    requestId, { reasonCode }, fallbackError);
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
        // The ordinary API error remains visible when the conflict is not a typed terminal state.
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

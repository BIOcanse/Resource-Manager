import { ApiRequestError, requestJson, requestNoContent } from "../../api/httpTransport.ts";
import { RequestProblem } from "../../frontendRuntime/request/RequestProblem.ts";
import { sendElevatedBackendJson } from "../../api/elevatedBackendRequest.ts";
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

/**
 * 可信访问请求在 API 边界归类后的失败。`reason` 决定界面显示哪条本地化原因；
 * `failed` 时界面按 `cause`（原始错误）和调用方的通用文案显示。
 */
export class TrustedAccessRequestError extends Error {
  readonly reason: TrustedAccessFailureReason;

  constructor(reason: TrustedAccessFailureReason, cause: unknown) {
    super(cause instanceof Error ? cause.message : String(cause), { cause });
    this.name = "TrustedAccessRequestError";
    this.reason = reason;
  }
}

/** 按后端的状态码和错误码归类失败；其余错误原样作为 failed 的原因。 */
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

/** 关闭默认密钥，或轮换已启用的默认密钥。走界面自己的密钥。 */
export async function setDefaultAccessKey(
  change: "disable" | "rotate",
  fallbackError: string
): Promise<DefaultAccessKeyState> {
  const enabled = change === "rotate";
  return classified(async () => requireDefaultState(await post(
    "/api/access-keys/default",
    { enabled, rotate: enabled },
    defaultAccessKeyStateDecoder,
    fallbackError), enabled));
}

/**
 * 重新启用默认密钥。默认密钥关闭后界面已没有可用的密钥，所以只走原生壳的显式提升通道
 * （管理员身份的后端客户端），由 Windows 请求用户授权。
 */
export async function enableDefaultAccessKeyElevated(fallbackError: string): Promise<DefaultAccessKeyState> {
  return classified(async () => requireDefaultState(decode(defaultAccessKeyStateDecoder,
    await sendElevatedBackendJson<unknown>("POST", "/api/access-keys/default", { enabled: true, rotate: false }, fallbackError)),
  true));
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

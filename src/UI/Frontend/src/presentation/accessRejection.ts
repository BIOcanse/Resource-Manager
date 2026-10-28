import { uiText } from "../text.ts";

/*
 * 本地服务的访问控制（后端 TrustedAccessHttpPolicy）拒绝请求时回 { error: <码> }，状态码 401、403 或 503。
 * 任何受保护的端点都可能这样拒绝，所以按码在这一处写成当前语言的原因，所有操作的失败提示都用它，
 * 不让某条操作路径只剩一句“操作失败”。码的集合与后端逐个相等（测试读后端源码）。
 */
export const accessRejectionCodes = [
  "keyless-request-required",
  "access-key-invalid",
  "access-service-unavailable",
  "access-scope-required",
  "elevated-administrator-required",
  "local-windows-caller-required",
  "access-key-required"
] as const;

export type AccessRejectionCode = typeof accessRejectionCodes[number];

/** 请求被访问控制拒绝时返回拒绝的码，其他失败返回 null。 */
export function accessRejectionCode(error: unknown): AccessRejectionCode | null {
  if (!error || typeof error !== "object") return null;
  const record = error as Record<string, unknown>;
  if ((record.name !== "ApiRequestError" && record.name !== "RequestProblem") || record.kind !== "http") return null;
  if (record.status !== 401 && record.status !== 403 && record.status !== 503) return null;
  const payload = record.payload;
  const code = payload && typeof payload === "object" ? (payload as Record<string, unknown>).error : null;
  return accessRejectionCodes.find((known) => known === code) ?? null;
}

/** 被访问控制拒绝时的原因（当前语言），其他失败返回 null。 */
export function accessRejectionMessage(error: unknown): string | null {
  const code = accessRejectionCode(error);
  return code === null ? null : uiText.accessRejection[code];
}

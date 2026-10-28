import { currentSettingsText, uiText } from "../text.ts";
import { isKnownAccessScope } from "../features/trustedAccess/trustedAccessTypes.ts";

/*
 * 本地服务的访问控制（后端 TrustedAccessHttpPolicy）拒绝请求时回 { error: <码> }，状态码 401、403 或 503。
 * 任何受保护的端点都可能这样拒绝，所以按码在这一处写成当前语言的原因，所有操作的失败提示都用它，
 * 不让某条操作路径只剩一句“操作失败”。码的集合与后端逐个相等（测试读后端源码）。
 * access-scope-required 还带 missingScopes（后端范围 ID，按路由要求的顺序）：本版本都认识时写出缺少哪些范围，
 * 有不认识的就只写缺少范围，不显示原始 ID，也不只列认识的那一部分。
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

export interface AccessRejection {
  readonly code: AccessRejectionCode;
  /** 只在 access-scope-required 时可能非空；其余码和不带该字段的回复是空列表。 */
  readonly missingScopes: readonly string[];
}

/** 请求被访问控制拒绝时返回拒绝的码和缺少的范围，其他失败返回 null。 */
export function accessRejection(error: unknown): AccessRejection | null {
  if (!error || typeof error !== "object") return null;
  const record = error as Record<string, unknown>;
  if ((record.name !== "ApiRequestError" && record.name !== "RequestProblem") || record.kind !== "http") return null;
  if (record.status !== 401 && record.status !== 403 && record.status !== 503) return null;
  const payload = record.payload && typeof record.payload === "object" ? record.payload as Record<string, unknown> : null;
  const code = accessRejectionCodes.find((known) => known === payload?.error);
  if (code === undefined) return null;
  const scopes = code === "access-scope-required" && Array.isArray(payload?.missingScopes) ? payload.missingScopes : [];
  return { code, missingScopes: scopes.filter((scope): scope is string => typeof scope === "string" && scope.length > 0) };
}

/** 请求被访问控制拒绝时返回拒绝的码，其他失败返回 null。 */
export function accessRejectionCode(error: unknown): AccessRejectionCode | null {
  return accessRejection(error)?.code ?? null;
}

/** 被访问控制拒绝时的原因（当前语言），其他失败返回 null。 */
export function accessRejectionMessage(error: unknown): string | null {
  const rejection = accessRejection(error);
  if (rejection === null) return null;
  const scopes = rejection.missingScopes;
  if (scopes.length > 0 && scopes.every(isKnownAccessScope)) {
    const labels = currentSettingsText().systemIntegration.trustedAccess.scopeLabels;
    return uiText.accessRejection.missingScopes(scopes.map((scope) => labels[scope]));
  }
  return uiText.accessRejection[rejection.code];
}

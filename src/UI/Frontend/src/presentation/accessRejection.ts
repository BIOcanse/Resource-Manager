import { currentSettingsText, uiText } from "../text.ts";
import { isKnownAccessScope } from "../features/trustedAccess/trustedAccessTypes.ts";

/* Access-control refusals return error codes with HTTP 401, 403, or 503. Localize them consistently across operations. For missing scopes, list names only when every ID is known; otherwise show a generic scope message without a partial list or raw IDs. */
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
  /** Non-empty only for access-scope-required replies carrying missingScopes. */
  readonly missingScopes: readonly string[];
}

/** Access refusal code and missing scopes; null for other failures. */
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

/** Access refusal code; null for other failures. */
export function accessRejectionCode(error: unknown): AccessRejectionCode | null {
  return accessRejection(error)?.code ?? null;
}

/** Localized access refusal reason; null for other failures. */
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

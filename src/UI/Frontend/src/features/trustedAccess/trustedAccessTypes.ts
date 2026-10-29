export type AccessKeyPurpose = "control" | "openai" | "anthropic";

export interface AccessKeySummary {
  readonly id: string;
  readonly name: string;
  readonly purpose: AccessKeyPurpose;
  readonly scopes: readonly string[];
  readonly createdAt: string;
  readonly isDefault: boolean;
}

export interface AccessKeySnapshot {
  readonly keys: readonly AccessKeySummary[];
  readonly defaultEnabled: boolean;
  readonly defaultKeyId: string | null;
  readonly availableScopes: readonly string[];
  /** 持有者必须是提权管理员的范围：带着其中任何一个的密钥，未提权的进程拿去用，每个请求都会被拒绝。对所有调用方相同。 */
  readonly administratorHolderScopes: readonly string[];
}

export interface AccessKeyCreated {
  readonly key: AccessKeySummary;
  readonly apiKey: string;
}

export interface DefaultAccessKeyState {
  readonly defaultEnabled: boolean;
  readonly defaultKeyId: string | null;
}

export type AccessRequestDenialReason =
  | "not-approved"
  | "unsupported"
  | "duplicate"
  | "not-needed";

export interface AccessRequestPending {
  readonly requestId: string;
  readonly name: string;
  readonly requestedScopes: readonly string[];
  readonly observedExecutablePath: string;
  readonly observedProcessId: number;
  readonly submittedAt: string;
  readonly expiresAt: string;
}

export interface AccessRequestPendingSnapshot {
  readonly requests: readonly AccessRequestPending[];
}

export type AccessRequestDecisionStatus = "approved" | "denied" | "expired";

export interface AccessRequestDecisionResult {
  readonly requestId: string;
  readonly status: AccessRequestDecisionStatus;
  readonly key?: AccessKeySummary;
  readonly denialReason?: AccessRequestDenialReason;
}

/** 本前端版本能说明含义的访问范围（与后端 AccessKeyScopes 一致）。不在其中的范围显示为“未知范围”，不能从这里授予。 */
export const knownAccessScopes = [
  "read",
  "metrics.read",
  "processes.read",
  "session",
  "adapters.register.self",
  "desktop.publish",
  "gpu.resolve",
  "gpu.report",
  "keys.manage",
  "machine.write",
  "injection.configure"
] as const;

export type KnownAccessScope = typeof knownAccessScopes[number];

const knownAccessScopeSet: ReadonlySet<string> = new Set(knownAccessScopes);

export function isKnownAccessScope(scope: string): scope is KnownAccessScope {
  return knownAccessScopeSet.has(scope);
}

/**
 * 可信访问请求失败的原因，由 API 边界从后端状态码和错误码归类：
 * unauthorized —— 后端不接受此界面的密钥（默认密钥已关闭、刚轮换或失效）；
 * scope-required —— 当前密钥缺少管理密钥的范围；
 * default-scope-denied —— 修改默认密钥需要全部默认范围或管理员权限；
 * failed —— 其他失败，显示调用方给的通用文案。
 */
export type TrustedAccessFailureReason = "unauthorized" | "scope-required" | "default-scope-denied" | "failed";

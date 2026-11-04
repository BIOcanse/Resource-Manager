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
  /** Any elevated-holder scope requires the calling process to be an elevated administrator. */
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

/** Offer only recognized backend scopes; unknown scopes cannot be granted here. */
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

/** Classify authorization, missing management scope, default-scope denial, and generic failures at the API boundary. */
export type TrustedAccessFailureReason = "unauthorized" | "scope-required" | "default-scope-denied" | "failed";

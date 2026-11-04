import { sourceCanRender, type SourceSnapshot, type SourceStatus } from "../../frontendRuntime/source/SourceSnapshot.ts";
import type { TrustedAccessCopy } from "../../i18n/settingsTypes.ts";
import {
  isKnownAccessScope,
  type AccessRequestPending,
  type AccessRequestPendingSnapshot
} from "./trustedAccessTypes.ts";

/** Represent loading, unavailable, error, and stale subscription states. */
export type AccessRequestListNotice = "loading" | "unavailable" | "error" | "stale";

export interface AccessRequestListView {
  /** Return null when the list is current and needs no notice. */
  readonly notice: AccessRequestListNotice | null;
  /** Show only subscription-provided displayable requests; do not retain stale or invented entries. */
  readonly requests: readonly AccessRequestPending[];
  /** Show the empty message only for a ready, empty subscription. */
  readonly empty: boolean;
}

/** Share subscription-state presentation between the request section and banner. */
export function accessRequestListView(
  snapshot: SourceSnapshot<AccessRequestPendingSnapshot> | null
): AccessRequestListView {
  const renderable = snapshot !== null && sourceCanRender(snapshot);
  const requests = renderable ? snapshot!.data!.requests : [];
  const notice = accessRequestNotice(snapshot?.status ?? "loading", renderable);
  return {
    notice,
    requests,
    empty: notice === null && requests.length === 0
  };
}

function accessRequestNotice(status: SourceStatus, renderable: boolean): AccessRequestListNotice | null {
  switch (status) {
    case "ready": return null;
    case "refreshing": return renderable ? null : "loading";
    case "stale": return "stale";
    case "unavailable":
    case "disposed": return "unavailable";
    case "error": return "error";
    case "idle":
    case "loading": return "loading";
  }
}

export function accessRequestNoticeText(copy: TrustedAccessCopy, notice: AccessRequestListNotice): string {
  switch (notice) {
    case "loading": return copy.requestsLoading;
    case "unavailable": return copy.requestsUnavailable;
    case "error": return copy.requestsLoadFailed;
    case "stale": return copy.requestsStale;
  }
}

/** Use an unknown-scope label without exposing unrecognized IDs. */
export function accessScopeLabel(copy: TrustedAccessCopy, scope: string): string {
  return isKnownAccessScope(scope) ? copy.scopeLabels[scope] : copy.unknownScope;
}

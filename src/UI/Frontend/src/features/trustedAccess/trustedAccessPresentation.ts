import { sourceCanRender, type SourceSnapshot, type SourceStatus } from "../../frontendRuntime/source/SourceSnapshot.ts";
import type { TrustedAccessCopy } from "../../i18n/settingsTypes.ts";
import {
  isKnownAccessScope,
  type AccessRequestPending,
  type AccessRequestPendingSnapshot
} from "./trustedAccessTypes.ts";

/** 待处理请求区的提示：loading 读取中、unavailable 订阅不可用、error 读取失败、stale 列表可能过时。 */
export type AccessRequestListNotice = "loading" | "unavailable" | "error" | "stale";

export interface AccessRequestListView {
  /** 需要显示的状态提示；null 表示列表是当前的，不需要提示。 */
  readonly notice: AccessRequestListNotice | null;
  /** 可以显示的请求。订阅没有可显示的数据时为空，不沿用旧数据也不编造。 */
  readonly requests: readonly AccessRequestPending[];
  /** 订阅已就绪且确实没有请求：显示“目前没有待处理请求”。 */
  readonly empty: boolean;
}

/** 把待处理请求订阅的每个状态映射成请求区和横幅共同使用的显示。 */
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

/** 范围的显示名。本版本不认识的范围只显示“未知范围”，不显示原始 ID。 */
export function accessScopeLabel(copy: TrustedAccessCopy, scope: string): string {
  return isKnownAccessScope(scope) ? copy.scopeLabels[scope] : copy.unknownScope;
}

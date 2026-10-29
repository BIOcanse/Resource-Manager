import {
  defineResponseDecoder, requireArray, requireNonEmptyString, requireNonNegativeSafeInteger, requireOneOf, requireRecord,
  requireString, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

// docs/notifications_2026-10-29.md：后端只发种类和事实，界面自己解释。种类的登记表在合同“通知种类”一节。

/** 订阅 /api/notifications/subscribe 的一条通知：信封已校验，事实按种类另行解码。 */
export interface BackendNotification {
  readonly sequence: number;
  readonly kind: string;
  readonly occurredAt: string;
  readonly facts: Readonly<Record<string, unknown>>;
}

/** 一帧：当前通知列表，从旧到新。 */
export interface BackendNotificationList {
  readonly notifications: readonly BackendNotification[];
}

export const backendNotificationsSubscriptionPath = "/api/notifications/subscribe";

/**
 * 只校验信封（序号、种类、时间、事实是对象）。事实不在这里解码：不认识的种类、或认识的种类事实写错，
 * 都只让那一条被跳过，不让整帧作废（合同“消费端自己解释”）。
 */
export const backendNotificationListDecoder = defineResponseDecoder<BackendNotificationList>(
  "notifications.list.v1",
  (value) => {
    const row = requireRecord(value);
    let previous = 0;
    const notifications = requireArray(row.notifications, "$.notifications").map((item, index) => {
      const path = `$.notifications[${index}]`;
      const notification = requireRecord(item, path);
      const sequence = requireNonNegativeSafeInteger(notification.sequence, `${path}.sequence`);
      if (sequence <= previous) throw new ResponseDecodeError(`${path}.sequence`, "increasing sequence from 1");
      previous = sequence;
      return Object.freeze({
        sequence,
        kind: requireNonEmptyString(notification.kind, `${path}.kind`),
        occurredAt: requireString(notification.occurredAt, `${path}.occurredAt`),
        facts: Object.freeze({ ...requireRecord(notification.facts, `${path}.facts`) })
      });
    });
    return Object.freeze({ notifications: Object.freeze(notifications) });
  });

/** 主机内存保护一次处理（一个 actionId）的全部记录写完以后（合同“通知种类”）。 */
export interface MemoryProtectionActionNotification {
  readonly kind: "memory-protection.action";
  readonly sequence: number;
  readonly targetKind: "process" | "processTree";
  readonly name: string;
  readonly processId: number;
  readonly recordCount: number;
  readonly bytesWritten: number;
}

/** 产品更新第一次看到一个可用的新版本。 */
export interface UpdateAvailableNotification {
  readonly kind: "update-available";
  readonly sequence: number;
  readonly version: string;
}

/** 一个可信访问请求进入待批准。name 是请求方自报的名称，observedExecutablePath 是后端观察到的可执行文件路径。 */
export interface AccessRequestNotification {
  readonly kind: "access-request";
  readonly sequence: number;
  readonly requestId: string;
  readonly name: string;
  readonly observedExecutablePath: string;
}

/** 界面认识的通知。新种类先登记在合同里，再加在这里。 */
export type KnownBackendNotification = MemoryProtectionActionNotification | UpdateAvailableNotification | AccessRequestNotification;

/** 认识的种类且事实完整时给出它；不认识的种类、事实写错时给 null，调用方跳过这一条。只读界面要显示的事实。 */
export function knownBackendNotification(notification: BackendNotification): KnownBackendNotification | null {
  const facts = notification.facts;
  const sequence = notification.sequence;
  try {
    switch (notification.kind) {
      case "memory-protection.action": return Object.freeze({
        kind: "memory-protection.action", sequence,
        targetKind: requireOneOf(facts.targetKind, "$.facts.targetKind", ["process", "processTree"] as const),
        name: requireString(facts.name, "$.facts.name"),
        processId: requireNonNegativeSafeInteger(facts.processId, "$.facts.processId"),
        recordCount: requireNonNegativeSafeInteger(facts.recordCount, "$.facts.recordCount"),
        bytesWritten: requireNonNegativeSafeInteger(facts.bytesWritten, "$.facts.bytesWritten")
      });
      case "update-available": return Object.freeze({
        kind: "update-available", sequence, version: requireNonEmptyString(facts.version, "$.facts.version")
      });
      case "access-request": return Object.freeze({
        kind: "access-request", sequence,
        requestId: requireNonEmptyString(facts.requestId, "$.facts.requestId"),
        name: requireNonEmptyString(facts.name, "$.facts.name"),
        observedExecutablePath: requireNonEmptyString(facts.observedExecutablePath, "$.facts.observedExecutablePath")
      });
      default: return null;
    }
  } catch (error) {
    if (error instanceof ResponseDecodeError) return null;
    throw error;
  }
}

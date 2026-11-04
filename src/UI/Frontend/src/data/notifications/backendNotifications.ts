import {
  defineResponseDecoder, requireArray, requireNonEmptyString, requireNonNegativeSafeInteger, requireOneOf, requireRecord,
  requireString, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";

// The backend publishes kinds and facts; the UI supplies their meaning and wording.

/** Validated notification envelope; decode facts separately by kind. */
export interface BackendNotification {
  readonly sequence: number;
  readonly kind: string;
  readonly occurredAt: string;
  readonly facts: Readonly<Record<string, unknown>>;
}

/** Complete notification list, oldest first. */
export interface BackendNotificationList {
  readonly notifications: readonly BackendNotification[];
}

export const backendNotificationsSubscriptionPath = "/api/notifications/subscribe";

/** Validate the envelope only; unknown kinds or malformed facts skip one item rather than the frame. */
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

/** All records for one memory-protection action have been written. */
export interface MemoryProtectionActionNotification {
  readonly kind: "memory-protection.action";
  readonly sequence: number;
  readonly targetKind: "process" | "processTree";
  readonly name: string;
  readonly processId: number;
  readonly recordCount: number;
  readonly bytesWritten: number;
}

/** A newly available product version was first observed. */
export interface UpdateAvailableNotification {
  readonly kind: "update-available";
  readonly sequence: number;
  readonly version: string;
}

/** Pending trusted-access request: self-reported name and backend-observed executable path. */
export interface AccessRequestNotification {
  readonly kind: "access-request";
  readonly sequence: number;
  readonly requestId: string;
  readonly name: string;
  readonly observedExecutablePath: string;
}

/** Newly registered startup crash records; latestReportId, program and role describe the newest. */
export interface CrashReportNotification {
  readonly kind: "crash-report";
  readonly sequence: number;
  readonly count: number;
  readonly latestReportId: string;
  readonly program: string;
  readonly role: "service" | "window" | "resident";
}

/** One CPU-monopoly action attempt, even at record capacity; success is normalized with a null reason. */
export interface CpuMonopolyProtectionActionNotification {
  readonly kind: "cpu-monopoly-protection.action";
  readonly sequence: number;
  readonly actionId: string;
  readonly name: string;
  readonly processId: number;
  readonly outcome: "normalized" | "failed";
  readonly reason: string | null;
}

/** Notification kinds recognized by the UI. */
export type KnownBackendNotification = MemoryProtectionActionNotification | UpdateAvailableNotification | AccessRequestNotification
  | CrashReportNotification | CpuMonopolyProtectionActionNotification;

/** Decode known, valid facts used by the UI; return null to skip unknown or malformed items. */
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
      case "crash-report": return Object.freeze({
        kind: "crash-report", sequence,
        count: requireNonNegativeSafeInteger(facts.count, "$.facts.count"),
        latestReportId: requireNonEmptyString(facts.latestReportId, "$.facts.latestReportId"),
        program: requireNonEmptyString(facts.program, "$.facts.program"),
        role: requireOneOf(facts.role, "$.facts.role", ["service", "window", "resident"] as const)
      });
      case "cpu-monopoly-protection.action": return Object.freeze({
        kind: "cpu-monopoly-protection.action", sequence,
        actionId: requireNonEmptyString(facts.actionId, "$.facts.actionId"),
        name: requireString(facts.name, "$.facts.name"),
        processId: requireNonNegativeSafeInteger(facts.processId, "$.facts.processId"),
        outcome: requireOneOf(facts.outcome, "$.facts.outcome", ["normalized", "failed"] as const),
        reason: facts.reason === undefined || facts.reason === null ? null : requireString(facts.reason, "$.facts.reason")
      });
      default: return null;
    }
  } catch (error) {
    if (error instanceof ResponseDecodeError) return null;
    throw error;
  }
}

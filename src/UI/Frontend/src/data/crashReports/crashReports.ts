import type { RequestClient } from "../../frontendRuntime/request/RequestClient.ts";
import {
  defineResponseDecoder, requireArray, requireBoolean, requireNonEmptyString, requireNonNegativeSafeInteger,
  requireNullable, requireOneOf, requireRecord, requireString
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import { uiText } from "../../text.ts";

// The backend owns crash records, read/report state, archives and issue URLs.
// The UI displays these facts and asks the window process to open or reveal them on user action.

export interface CrashReportDump {
  readonly path: string;
  readonly sizeBytes: number;
}

export interface CrashReportFault {
  readonly module: string | null;
  readonly offset: string | null;
  readonly exceptionCode: string | null;
  readonly managedStack: string | null;
  readonly description: string | null;
}

export interface CrashReportPackage {
  readonly path: string;
  readonly sizeBytes: number;
  readonly createdAt: string;
  readonly exceedsIssueLimit: boolean;
}

export type CrashReportRole = "service" | "window" | "resident";
export type CrashReportDumpStatus = "saved" | "notEnabled" | "missing";

export interface CrashReport {
  readonly id: string;
  readonly occurredAt: string;
  readonly program: string;
  readonly role: CrashReportRole;
  readonly version: string | null;
  /** notEnabled has no dump; missing retains the path and size of a dump no longer on disk. */
  readonly dumpStatus: CrashReportDumpStatus;
  readonly dump: CrashReportDump | null;
  readonly fault: CrashReportFault | null;
  readonly package: CrashReportPackage | null;
  readonly acknowledged: boolean;
  readonly reported: boolean;
}

/** All records, newest first, with dump directory and issue attachment limit. */
export interface CrashReportList {
  readonly reports: readonly CrashReport[];
  readonly dumpFolder: string;
  readonly issueAttachmentLimitBytes: number;
}

export interface CrashReportPackageReceipt {
  readonly report: CrashReport;
  readonly package: CrashReportPackage;
  /** Oversized archives report exceedsIssueAttachmentLimit; dumps are never truncated. */
  readonly reason: "exceedsIssueAttachmentLimit" | null;
}

export interface CrashReportIssueLink {
  readonly url: string;
  readonly report: CrashReport;
}

export const crashReportsSubscriptionPath = "/api/crash-reports/subscribe";

function decodeDump(value: unknown, path: string): CrashReportDump {
  const dump = requireRecord(value, path);
  return Object.freeze({
    path: requireNonEmptyString(dump.path, path + ".path"),
    sizeBytes: requireNonNegativeSafeInteger(dump.sizeBytes, path + ".sizeBytes")
  });
}

function decodePackage(value: unknown, path: string): CrashReportPackage {
  const item = requireRecord(value, path);
  return Object.freeze({
    path: requireNonEmptyString(item.path, path + ".path"),
    sizeBytes: requireNonNegativeSafeInteger(item.sizeBytes, path + ".sizeBytes"),
    createdAt: requireString(item.createdAt, path + ".createdAt"),
    exceedsIssueLimit: requireBoolean(item.exceedsIssueLimit, path + ".exceedsIssueLimit")
  });
}

function decodeFault(value: unknown, path: string): CrashReportFault {
  const fault = requireRecord(value, path);
  const text = (name: keyof CrashReportFault) => requireNullable(fault[name], `${path}.${name}`, requireString);
  return Object.freeze({
    module: text("module"),
    offset: text("offset"),
    exceptionCode: text("exceptionCode"),
    managedStack: text("managedStack"),
    description: text("description")
  });
}

export function decodeCrashReport(value: unknown, path = "$.report"): CrashReport {
  const report = requireRecord(value, path);
  return Object.freeze({
    id: requireNonEmptyString(report.id, path + ".id"),
    occurredAt: requireString(report.occurredAt, path + ".occurredAt"),
    program: requireNonEmptyString(report.program, path + ".program"),
    role: requireOneOf(report.role, path + ".role", ["service", "window", "resident"] as const),
    version: requireNullable(report.version, path + ".version", requireString),
    dumpStatus: requireOneOf(report.dumpStatus, path + ".dumpStatus", ["saved", "notEnabled", "missing"] as const),
    dump: requireNullable(report.dump, path + ".dump", decodeDump),
    fault: requireNullable(report.fault, path + ".fault", decodeFault),
    package: requireNullable(report.package, path + ".package", decodePackage),
    acknowledged: requireBoolean(report.acknowledged, path + ".acknowledged"),
    reported: requireBoolean(report.reported, path + ".reported")
  });
}

export const crashReportListDecoder = defineResponseDecoder<CrashReportList>("crash-reports.list.v1", (value) => {
  const list = requireRecord(value);
  return Object.freeze({
    reports: Object.freeze(requireArray(list.reports, "$.reports").map((item, index) => decodeCrashReport(item, `$.reports[${index}]`))),
    dumpFolder: requireString(list.dumpFolder, "$.dumpFolder"),
    issueAttachmentLimitBytes: requireNonNegativeSafeInteger(list.issueAttachmentLimitBytes, "$.issueAttachmentLimitBytes")
  });
});

export const crashReportPackageDecoder = defineResponseDecoder<CrashReportPackageReceipt>("crash-reports.package.v1", (value) => {
  const receipt = requireRecord(value);
  return Object.freeze({
    report: decodeCrashReport(receipt.report, "$.report"),
    package: decodePackage(receipt.package, "$.package"),
    reason: requireNullable(receipt.reason, "$.reason", (item, path) =>
      requireOneOf(item, path, ["exceedsIssueAttachmentLimit"] as const))
  });
});

export const crashReportIssueLinkDecoder = defineResponseDecoder<CrashReportIssueLink>("crash-reports.issue-link.v1", (value) => {
  const link = requireRecord(value);
  return Object.freeze({
    url: requireNonEmptyString(link.url, "$.url"),
    report: decodeCrashReport(link.report, "$.report")
  });
});

export const crashReportAcknowledgeDecoder = defineResponseDecoder<CrashReport>("crash-reports.acknowledge.v1", (value) =>
  decodeCrashReport(requireRecord(value).report, "$.report"));

type Client = Pick<RequestClient, "request">;
const reportUrl = (id: string, action?: string) =>
  `/api/crash-reports/${encodeURIComponent(id)}${action ? "/" + action : ""}`;

/** Build a report archive; rebuilding replaces the previous archive. */
export function packageCrashReport(client: Client, id: string): Promise<CrashReportPackageReceipt> {
  return client.request({ key: "crash-reports.package", url: reportUrl(id, "package"), decoder: crashReportPackageDecoder,
    fallbackError: uiText.stores.actionFailed, request: { method: "POST" } });
}

/** Return a prefilled issue URL and mark the record as reported. */
export function requestCrashReportIssueLink(client: Client, id: string): Promise<CrashReportIssueLink> {
  return client.request({ key: "crash-reports.issue-link", url: reportUrl(id, "issue-link"), decoder: crashReportIssueLinkDecoder,
    fallbackError: uiText.stores.actionFailed, request: { method: "POST" } });
}

/** Mark read to suppress the crash prompt while retaining the record. */
export function acknowledgeCrashReport(client: Client, id: string): Promise<CrashReport> {
  return client.request({ key: "crash-reports.acknowledge", url: reportUrl(id, "acknowledge"), decoder: crashReportAcknowledgeDecoder,
    fallbackError: uiText.stores.actionFailed, request: { method: "POST" } });
}

/** Delete the record, dump and report archive; success returns 204. */
export function deleteCrashReport(client: Client, id: string): Promise<null> {
  return client.request({ key: "crash-reports.delete", url: reportUrl(id),
    decoder: defineResponseDecoder<null>("crash-reports.empty.v1", () => null),
    fallbackError: uiText.stores.actionFailed, request: { method: "DELETE", allowEmptyResponse: true } });
}

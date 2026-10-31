import assert from "node:assert/strict";
import { createRoot } from "solid-js";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";
import { decodeCrashDumpDiagnostics } from "../src/data/appSettings/crashDumpDiagnosticsDecoder.ts";
import { appSettingsResultDecoder } from "../src/data/appSettings/appSettingsResultDecoder.ts";
import {
  crashReportAcknowledgeDecoder, crashReportIssueLinkDecoder, crashReportListDecoder, crashReportPackageDecoder,
  type CrashReportList
} from "../src/data/crashReports/crashReports.ts";
import {
  crashDumpModeView, crashDumpStatusView, crashFaultSummary, crashReportDumpText, crashReportLocalFile
} from "../src/features/crashReports/crashReportPresentation.ts";
import { createCrashReportStore } from "../src/features/crashReports/crashReportStore.ts";
import { requestShellOpen, ShellOpenError, type ShellOpenRequest } from "../src/host/shellOpen.ts";
import type { HostMessageTransport } from "../src/host/hostMessageTransport.ts";
import { defaultAppSettings, normalizeAppSettings, normalizeCrashDumpSettings } from "../src/stores/settingsStore.ts";
import type { CurrentValueSource } from "../src/frontendRuntime/push/PushValueSourceFamily.ts";
import type { SourceSnapshot } from "../src/frontendRuntime/source/SourceSnapshot.ts";

// docs/crash_dumps_2026-10-31.md; the shapes are those of crash-reports wire-contract.md.

// Settings: the stored default follows the release channel; the bounds are the backend's.
assert.equal(normalizeCrashDumpSettings(undefined), undefined, "missing backend settings stay missing");
// The values and the upper bound belong to the backend; the frontend only projects received settings.
assert.deepEqual(normalizeCrashDumpSettings({ mode: "off", dumpType: "full", keepCount: 99 }), { mode: "off", dumpType: "full", keepCount: 99 });
assert.equal(defaultAppSettings().systemIntegration!.crashDumps, undefined, "the frontend has no crash dump default copy");
assert.deepEqual(normalizeAppSettings({ systemIntegration: { autoStartEnabled: false, taskManagerShortcutReplacementEnabled: false, hotkeys: [],
  crashDumps: { mode: "ask", dumpType: "mini", keepCount: 7 } } }).systemIntegration!.crashDumps, { mode: "ask", dumpType: "mini", keepCount: 7 });

const diagnosticsWire = {
  state: "mismatch", effectiveMode: "on", modeSource: "channelDefault", defaultMode: "on", channel: "beta",
  dumpFolder: "C:\\Program Files\\ResourceManager Beta\\UserData\\crash-dumps", keepCountMaximum: 64,
  images: ["ResourceManager.exe", "ResourceManager.NativeUi.exe", "ResourceManager.Desktop.exe"],
  actual: [
    { image: "ResourceManager.exe", present: true, ownedByResourceManager: true,
      dumpFolder: "C:\\Program Files\\ResourceManager Beta\\UserData\\crash-dumps", dumpType: 0, customDumpFlags: 4453, dumpCount: 5 },
    { image: "ResourceManager.NativeUi.exe", present: false, ownedByResourceManager: false,
      dumpFolder: null, dumpType: null, customDumpFlags: null, dumpCount: null },
    { image: "ResourceManager.Desktop.exe", present: true, ownedByResourceManager: false,
      dumpFolder: "D:\\Other", dumpType: 2, customDumpFlags: null, dumpCount: 10 }
  ],
  skipReason: null, writeError: null, readError: null
};
const diagnostics = decodeCrashDumpDiagnostics(diagnosticsWire);
assert.equal(diagnostics.actual!.length, 3);
assert.ok(Object.isFrozen(diagnostics.actual));
assert.equal(diagnostics.keepCountMaximum, 64);
assert.equal(decodeCrashDumpDiagnostics({ ...diagnosticsWire, defaultMode: "off" }).defaultMode, "off", "a channel default can be off");
for (const invalid of [{ state: "pretend" }, { effectiveMode: "default" }, { modeSource: "backend" }, { channel: "nightly" },
  { defaultMode: "default" }, { keepCountMaximum: 0 }, { keepCountMaximum: undefined }, { keepCountMaximum: 2.5 },
  { actual: [{ ...diagnosticsWire.actual[0], present: "yes" }] }, { images: "ResourceManager.exe" }]) {
  assert.throws(() => decodeCrashDumpDiagnostics({ ...diagnosticsWire, ...invalid }), JSON.stringify(invalid));
}
assert.equal(decodeCrashDumpDiagnostics({ ...diagnosticsWire, state: "skipped", actual: null,
  skipReason: "not-running-as-installed-service" }).actual, null);

// The settings result carries the diagnostics beside serviceRecovery and checks the stored crashDumps section.
const settingsResult = (crashDumps: unknown, stored: unknown) => ({
  settings: { version: "1.0.37", systemIntegration: { autoStartEnabled: false, taskManagerShortcutReplacementEnabled: false, hotkeys: [],
    ...(stored === undefined ? {} : { crashDumps: stored }) } },
  revision: "r1", serviceRecovery: null, ...(crashDumps === undefined ? {} : { crashDumps })
});
assert.equal(appSettingsResultDecoder.decode(settingsResult(diagnosticsWire, { mode: "default", dumpType: "mini", keepCount: 5 }))
  .crashDumps!.effectiveMode, "on");
assert.equal(appSettingsResultDecoder.decode(settingsResult(diagnosticsWire, { mode: "on", dumpType: "mini", keepCount: 64 }))
  .revision, "r1", "no upper literal: the backend clamps keepCount to its maximum");
assert.equal(appSettingsResultDecoder.decode(settingsResult(null, undefined)).crashDumps, null);
assert.equal(appSettingsResultDecoder.decode(settingsResult(undefined, undefined)).crashDumps, undefined, "older backends send none");
for (const stored of [{ mode: "always", dumpType: "mini", keepCount: 5 }, { mode: "on", dumpType: "tiny", keepCount: 5 },
  { mode: "on", dumpType: "mini", keepCount: 0 }, { mode: "on", dumpType: "mini", keepCount: 1.5 }]) {
  assert.throws(() => appSettingsResultDecoder.decode(settingsResult(null, stored)), JSON.stringify(stored));
}

// The crash list: newest first, three dump states, optional fault and package.
const saved = {
  id: "20261030T152506Z-ResourceManager.exe-12345", occurredAt: "2026-10-30T15:25:06Z", program: "ResourceManager.exe",
  role: "service", version: "1.4.0-beta.20261030.8", dumpStatus: "saved",
  dump: { path: "C:\\RM\\crash-dumps\\ResourceManager.exe.12345.dmp", sizeBytes: 4194304 },
  fault: { module: "ntdll.dll", offset: "0x00000000000a1b2c", exceptionCode: "0xc0000005",
    managedStack: "\n   at NativeKernelEtwSession.Process()\n   at Worker.Run()", description: null },
  package: null, acknowledged: false, reported: false
};
const notEnabled = { ...saved, id: "20261029T080000Z-ResourceManager.NativeUi.exe-77", program: "ResourceManager.NativeUi.exe",
  role: "window", version: null, dumpStatus: "notEnabled", dump: null, fault: null, acknowledged: true };
const missing = { ...saved, id: "20261028T080000Z-ResourceManager.Desktop.exe-9", program: "ResourceManager.Desktop.exe", role: "resident",
  dumpStatus: "missing", package: { path: "C:\\RM\\UserData\\CrashReports\\packages\\x.zip", sizeBytes: 30000000,
    createdAt: "2026-10-28T09:00:00Z", exceedsIssueLimit: true }, reported: true };
const listWire = { reports: [saved, notEnabled, missing], dumpFolder: "C:\\RM\\crash-dumps", issueAttachmentLimitBytes: 26214400 };
const list = crashReportListDecoder.decode(listWire) as CrashReportList;
assert.deepEqual(list.reports.map(report => report.dumpStatus), ["saved", "notEnabled", "missing"]);
assert.ok(Object.isFrozen(list.reports) && Object.isFrozen(list.reports[0]));
for (const invalid of [{ role: "helper" }, { dumpStatus: "lost" }, { acknowledged: "no" }, { dump: { path: "", sizeBytes: 1 } },
  { fault: { module: 1 } }, { package: { path: "x.zip", sizeBytes: -1, createdAt: "", exceedsIssueLimit: false } }]) {
  assert.throws(() => crashReportListDecoder.decode({ ...listWire, reports: [{ ...saved, ...invalid }] }), JSON.stringify(invalid));
}
assert.throws(() => crashReportListDecoder.decode({ reports: [] }), "dumpFolder and the attachment limit are required");
const receipt = crashReportPackageDecoder.decode({ report: missing, package: missing.package, reason: "exceedsIssueAttachmentLimit" });
assert.equal(receipt.reason, "exceedsIssueAttachmentLimit");
assert.throws(() => crashReportPackageDecoder.decode({ report: missing, package: missing.package, reason: "tooBig" }));
assert.equal(crashReportIssueLinkDecoder.decode({ url: "https://github.com/BIOcanse/Resource-Manager/issues/new?title=x",
  report: { ...saved, reported: true } }).report.reported, true);
assert.equal(crashReportAcknowledgeDecoder.decode({ report: { ...saved, acknowledged: true } }).acknowledged, true);

// The local file the dialog and "open containing folder" select: the package, else a dump that is still there.
assert.equal(crashReportLocalFile(list.reports[0]!), saved.dump.path);
assert.equal(crashReportLocalFile(list.reports[1]!), null);
assert.equal(crashReportLocalFile(list.reports[2]!), missing.package.path);

// Every selectable language presents the same facts in its own words.
const selected = languageOptions.filter(option => option.id !== "system");
assert.equal(selected.length, 9);
const titles = new Set<string>();
for (const { id } of selected) {
  const bundle = await loadSettingsText(id);
  const copy = bundle.crashDumps;
  titles.add(copy.title);
  assert.ok(bundle.notifications.kinds.crashReport.title.length > 0, id);
  const mode = crashDumpModeView(diagnostics, copy)!;
  assert.equal(mode.offerTurnOff, true, `${id}: a beta default can be turned off in one click`);
  assert.ok(mode.effective.includes(copy.effectiveModes.on) && mode.effective.includes(copy.channelNames.beta), id);
  const chosen = crashDumpModeView({ ...diagnostics, modeSource: "user" }, copy)!;
  assert.equal(chosen.offerTurnOff, false);
  assert.ok(chosen.effective.includes(copy.sourceUser), id);
  assert.equal(crashDumpModeView(undefined, copy), null);
  const offDefault = crashDumpModeView({ ...diagnostics, effectiveMode: "off", defaultMode: "off", channel: "stable" }, copy)!;
  assert.ok(offDefault.effective.includes(copy.defaultModeNames.off) && copy.defaultModeNames.off.length > 0, id);
  assert.doesNotMatch(copy.dumpTypeDescription, /\d/, `${id}: the attachment limit is not stated as a literal`);
  const status = crashDumpStatusView(diagnostics, copy);
  assert.equal(status.status, copy.status.mismatch);
  assert.deepEqual(status.images.map(image => image.state), [copy.imageStates.written, copy.imageStates.absent, copy.imageStates.foreign]);
  assert.ok(status.images[0]!.detail!.includes(copy.dumpTypeOptions.mini.label) && status.images[0]!.detail!.includes("5"), id);
  const skipped = crashDumpStatusView({ ...diagnostics, state: "skipped", actual: null, skipReason: "not-running-as-installed-service",
    writeError: "access denied" }, copy);
  assert.deepEqual(skipped.reasons, [copy.skipReasons["not-running-as-installed-service"], copy.writeFailed("access denied")]);
  assert.ok(crashDumpStatusView({ ...diagnostics, skipReason: "registry-locked" }, copy).reasons[0]!.includes("registry-locked"));
  assert.equal(crashFaultSummary(list.reports[0]!.fault, copy),
    "ntdll.dll 0x00000000000a1b2c · 0xc0000005 · at NativeKernelEtwSession.Process()");
  assert.equal(crashFaultSummary(null, copy), copy.faultUnknown);
  assert.equal(crashReportDumpText(list.reports[1]!, copy).note, copy.dumpNotEnabled);
  assert.equal(crashReportDumpText(list.reports[2]!, copy).note, copy.dumpMissing);
  for (const text of [copy.requestWhere("C:\\RM\\crash-dumps"), copy.packageExceeds("28.6 MB", "26.2 MB"),
    copy.dialogCrashed("ResourceManager.exe", "2026-10-30 23:25"), copy.deleteConfirmMessage("ResourceManager.exe", "2026-10-30 23:25")]) {
    assert.doesNotMatch(text, /\$\{|\{\w+\}/, `${id}: ${text}`);
  }
  assert.ok(copy.requestWhere("C:\\RM\\crash-dumps").includes("C:\\RM\\crash-dumps"), id);
  const packageWarning = copy.packageExceeds("28.6 MB", "26.2 MB");
  assert.ok(packageWarning.includes("28.6 MB") && packageWarning.includes("26.2 MB"),
    `${id}: package and attachment limits remain formatted storage sizes`);
}
assert.equal(titles.size, 9, "every selectable language needs its own title");

// The window process opens addresses and selects files; without a native host only an address can open.
{
  const opened: string[] = [];
  await requestShellOpen({ kind: "url", url: "https://github.com/x" }, null, url => opened.push(url));
  assert.deepEqual(opened, ["https://github.com/x"]);
  await assert.rejects(requestShellOpen({ kind: "file", path: "C:\\x.dmp" }, null, () => assert.fail()),
    (error: unknown) => error instanceof ShellOpenError && error.reason === "unavailable");
  const posted: Array<Record<string, unknown>> = [];
  const listeners = new Set<(message: unknown) => void>();
  const transport: HostMessageTransport = {
    canSubscribeMessages: true,
    postMessage: (message) => {
      const request = message as Record<string, unknown>;
      posted.push(request);
      const reply = { type: "shell.open.result", requestId: request.requestId,
        opened: request.type === "shell.openUrl", error: request.type === "shell.openUrl" ? null : "not-found" };
      for (const listener of [...listeners]) listener({ type: "shell.open.result", requestId: "someone-else", opened: false });
      for (const listener of [...listeners]) listener(reply);
    },
    subscribeMessages: (listener) => { listeners.add(listener); return () => listeners.delete(listener); }
  };
  await requestShellOpen({ kind: "url", url: "https://github.com/BIOcanse/Resource-Manager/issues/new" }, transport);
  await assert.rejects(requestShellOpen({ kind: "file", path: "C:\\gone.dmp" }, transport),
    (error: unknown) => error instanceof ShellOpenError && error.reason === "not-found");
  assert.deepEqual(posted.map(message => [message.type, message.url ?? message.path]), [
    ["shell.openUrl", "https://github.com/BIOcanse/Resource-Manager/issues/new"], ["shell.revealFile", "C:\\gone.dmp"]]);
  assert.equal(listeners.size, 0, "each request stops listening after its reply");
}

// The store only calls the backend: "Report on GitHub" packages, asks for the link, opens it, selects the package and
// marks the record read; "Later" marks every listed record read and the dialog stops listing them at once.
{
  let current = list;
  const listeners = new Set<(snapshot: SourceSnapshot<CrashReportList>) => void>();
  const snapshot = (data: CrashReportList): SourceSnapshot<CrashReportList> =>
    ({ key: "crash-reports.list", status: "ready", data, error: null, backendEpoch: "e", revision: 1, acceptedAttempt: 1,
      domainRevision: null, capturedAt: null });
  const source = {
    readSnapshot: () => snapshot(current),
    subscribeSnapshot(listener: (value: SourceSnapshot<CrashReportList>) => void) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    }
  } as unknown as CurrentValueSource<CrashReportList>;
  const calls: string[] = [];
  const reportWire = (id: string, change: Record<string, unknown>) => ({ ...listWire.reports.find(item => item.id === id)!, ...change });
  const requestClient = {
    request: async (options: { key: string; url: string; decoder: { decode(value: unknown): unknown } }) => {
      calls.push(`${options.key} ${options.url}`);
      const id = decodeURIComponent(options.url.split("/")[3]!);
      const packaged = { path: `C:\\RM\\UserData\\CrashReports\\packages\\${id}.zip`, sizeBytes: 5000000, createdAt: "2026-10-31T00:00:00Z", exceedsIssueLimit: false };
      switch (options.key) {
        case "crash-reports.package": return options.decoder.decode({ report: reportWire(id, { package: packaged }), package: packaged, reason: null });
        case "crash-reports.issue-link": return options.decoder.decode({ url: "https://github.com/BIOcanse/Resource-Manager/issues/new?title=t",
          report: reportWire(id, { package: packaged, reported: true }) });
        case "crash-reports.acknowledge": return options.decoder.decode({ report: reportWire(id, { acknowledged: true }) });
        default: return options.decoder.decode(null);
      }
    }
  } as never;
  const opens: ShellOpenRequest[] = [];
  const errors: string[] = [];
  let confirmAnswer = false;
  const { store, dispose } = createRoot(dispose => ({ dispose, store: createCrashReportStore({
    source, active: () => true, requestClient, confirm: async () => confirmAnswer, notifyError: message => errors.push(message),
    open: async request => { opens.push(request); } }) }));
  assert.deepEqual(store.unacknowledged().map(report => report.id), [saved.id, missing.id]);
  await store.reportOnGitHub(store.unacknowledged()[0]!);
  assert.deepEqual(calls, [
    `crash-reports.package /api/crash-reports/${encodeURIComponent(saved.id)}/package`,
    `crash-reports.issue-link /api/crash-reports/${encodeURIComponent(saved.id)}/issue-link`,
    `crash-reports.acknowledge /api/crash-reports/${encodeURIComponent(saved.id)}/acknowledge`]);
  assert.deepEqual(opens, [{ kind: "url", url: "https://github.com/BIOcanse/Resource-Manager/issues/new?title=t" },
    { kind: "file", path: `C:\\RM\\UserData\\CrashReports\\packages\\${saved.id}.zip` }]);
  assert.deepEqual(store.unacknowledged().map(report => report.id), [missing.id], "the reported record leaves the dialog at once");
  calls.length = 0;
  await store.acknowledge(store.unacknowledged());
  assert.deepEqual(calls, [`crash-reports.acknowledge /api/crash-reports/${encodeURIComponent(missing.id)}/acknowledge`]);
  assert.deepEqual(store.unacknowledged(), []);
  // A new unread record pushed by the backend appears; one already closed in this window does not come back.
  current = crashReportListDecoder.decode({ ...listWire, reports: [{ ...saved, id: "new-crash" }, saved, missing] }) as CrashReportList;
  for (const listener of [...listeners]) listener(snapshot(current));
  assert.deepEqual(store.unacknowledged().map(report => report.id), ["new-crash"]);
  calls.length = 0;
  await store.remove(current.reports[0]!);
  assert.deepEqual(calls, [], "deleting asks first");
  confirmAnswer = true;
  await store.remove(current.reports[0]!);
  assert.deepEqual(calls, ["crash-reports.delete /api/crash-reports/new-crash"]);
  assert.deepEqual(errors, []);
  dispose();
  assert.equal(listeners.size, 0, "disposal unsubscribes");
}

console.log("crash reports: settings, diagnostics, list decoders, nine-language presentation, window-process open and store actions passed.");

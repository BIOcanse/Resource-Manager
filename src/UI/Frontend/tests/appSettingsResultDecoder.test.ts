import assert from "node:assert/strict";
import {
  appSettingsResultDecoder
} from "../src/data/appSettings/appSettingsResultDecoder.ts";
import {
  ResponseDecodeError
} from "../src/frontendRuntime/request/ResponseDecoder.ts";

const source = {
  settings: {
    version: "1.0.23",
    debug: {
      debugLogEnabled: false,
      futureNestedValue: { enabled: true }
    },
    futureSection: { retained: true }
  },
  revision: "revision-a",
  runtimeApplicationDisposition: "committedAndApplied",
  runtimePlanVersion: 3,
  runtimePublicationSequence: 4,
  runtimeDeliveryFailureCount: 0,
  runtimeCapabilityConstrainedPaths: ["performance.example"],
  runtimeFailureCode: null,
  futureEnvelopeField: "ignored"
};

const decoded = appSettingsResultDecoder.decode(source);
for (const disposition of ["notRequested", "rejectedBeforeCommit", "committedAndApplied",
  "committedWithCapabilityConstraints", "committedWithDeliveryFailures", "savedNotApplied", "revisionConflict"]) {
  assert.equal(appSettingsResultDecoder.decode({ ...source, runtimeApplicationDisposition: disposition }).runtimeApplicationDisposition, disposition);
}
assert.equal(decoded.revision, "revision-a");
assert.equal(appSettingsResultDecoder.decode({ ...source, serviceRecovery: null }).serviceRecovery, null,
  "Memory-protection PATCH returns the coordinator envelope without service diagnostics");
assert.equal(decoded.settings.version, "1.0.23");
assert.deepEqual(
  (decoded.settings as unknown as Record<string, unknown>).futureSection,
  { retained: true });
assert.equal(Object.isFrozen(decoded), true);
assert.equal(Object.isFrozen(decoded.settings), true);
assert.equal(Object.isFrozen(decoded.settings.debug), true);
assert.equal(Object.isFrozen(decoded.runtimeCapabilityConstrainedPaths), true);

// A single response must retain the new crash diagnostics and the normalized backend-owned overlay section together.
const crashDiagnostics = {
  state: "applied", effectiveMode: "on", modeSource: "user", defaultMode: "ask", channel: "beta",
  dumpFolder: "C:\\fixture\\dumps", keepCountMaximum: 7, images: ["ResourceManager.exe"], actual: null,
  skipReason: null, writeError: null, readError: null
};
const overlayWire = {
  enabled: true, mode: "external", sizeMode: "windowRatio", fontSizePx: 23,
  regionWidthRatio: 0.4, regionHeightRatio: 0.3, anchor: "bottomRight", marginPx: 19,
  items: [{ kind: "software", id: " cpu.usage ", source: " auto ", intervalMs: null, syncPrecision: "high", ignored: true }],
  ignored: true
};
const combined = appSettingsResultDecoder.decode({ ...source, crashDumps: crashDiagnostics,
  settings: { ...source.settings, performanceOverlay: { software: { game: overlayWire } } } });
assert.deepEqual(combined.crashDumps, crashDiagnostics);
assert.equal(Object.isFrozen(combined.crashDumps), true);
const overlay = combined.settings.performanceOverlay!.software.game!;
assert.deepEqual(overlay.items, [{ kind: "software", id: "cpu.usage", source: "auto", intervalMs: null, syncPrecision: "high" }]);
assert.equal("ignored" in overlay, false);
assert.equal(Object.isFrozen(overlay.items[0]), true);
assert.equal(overlayWire.items[0].id, " cpu.usage ");
assert.equal(appSettingsResultDecoder.decode({ ...source, crashDumps: null }).crashDumps, null);
assert.throws(() => appSettingsResultDecoder.decode({ ...source, crashDumps: { ...crashDiagnostics, state: "unknown" } }), ResponseDecodeError);

for (const [value, expectedPath] of [
  [{ ...source, settings: null }, "$.settings"],
  [{ ...source, settings: { ...source.settings, version: "" } }, "$.settings.version"],
  [{ ...source, revision: "" }, "$.revision"],
  [{ ...source, settings: { ...source.settings, appearance: [] } }, "$.settings"],
  [{ ...source, runtimeApplicationDisposition: "committedAndPublished" }, "$.runtimeApplicationDisposition"],
  [{ ...source, runtimeApplicationDisposition: "maybe" }, "$.runtimeApplicationDisposition"],
  [{ ...source, settings: { ...source.settings, control: { keepAlive: false, reassertionIntervalSeconds: 0 } } }, "$.settings.control.reassertionIntervalSeconds"],
  [{ ...source, settings: { ...source.settings, control: { keepAlive: false, reassertionIntervalSeconds: 86401 } } }, "$.settings.control.reassertionIntervalSeconds"],
  // 保活维持设置是必有的布尔（后端读旧设置文件时补成 false 再发出来）。
  [{ ...source, settings: { ...source.settings, control: { reassertionIntervalSeconds: 120 } } }, "$.settings.control.keepAlive"],
  [{ ...source, settings: { ...source.settings, control: { keepAlive: "on", reassertionIntervalSeconds: 120 } } }, "$.settings.control.keepAlive"],
  [{ ...source, runtimeDeliveryFailureCount: -1 }, "$.runtimeDeliveryFailureCount"]
] as const) {
  assert.throws(
    () => appSettingsResultDecoder.decode(value),
    (error: unknown) => error instanceof ResponseDecodeError
      && error.path === expectedPath);
}

assert.deepEqual(appSettingsResultDecoder.decode({ ...source, settings: { ...source.settings,
  control: { keepAlive: true, reassertionIntervalSeconds: 337 } } }).settings.control,
  { keepAlive: true, reassertionIntervalSeconds: 337 });

assert.equal("frontendRefreshIntervals" in decoded, false);
for (const value of [
  { ...source, frontendRefreshIntervals: {} },
  { ...source, settings: { ...source.settings, appearance: { theme: "dark" } } },
  { ...source, settings: { ...source.settings, performance: { smartMonitoringMode: "enabled" } } }
]) assert.throws(() => appSettingsResultDecoder.decode(value), ResponseDecodeError);

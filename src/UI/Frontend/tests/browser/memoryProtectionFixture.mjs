// A stateful backend fixture: all arithmetic here models backend responses, never frontend UI logic.
export const memoryProtectionScenarios = ["no-point", "no-reserve", "disabled-ready", "active", "terminate-only",
  "reserve-building", "reserve-failed", "reserve-partial-cluster", "reserve-incomplete", "reserve-consumed",
  "commit-unavailable", "all-outcomes", "exclusions"];
export const defaultMemoryProtectionSettings = () => ({ enabled: false, thresholdPercent: 95,
  checkIntervalSeconds: 1, reserves: [], actionMode: "dumpThenTerminate" });
const commit = () => ({ currentBytes: 200, limitBytes: 1000, percent: 20 });
const configured = () => [{ location: "F:\\MockReserve", sizeBytes: 67108864 }, { location: "G:\\MockReserve", sizeBytes: 131072 }];
const cluster = location => location.toUpperCase().startsWith("G:") ? 65536 : 4096;
const same = (a, b) => a.length === b.length && a.every((entry, index) => entry.location.toLowerCase() === b[index].location.toLowerCase()
  && entry.sizeBytes === b[index].configuredSizeBytes);

export function invalidMemoryProtectionPatch(changes) {
  if (!Object.hasOwn(changes, "memoryProtection")) return null;
  const patch = changes.memoryProtection;
  if (patch === null) return "Application settings field 'memoryProtection' cannot be removed.";
  for (const [key, value] of Object.entries(patch)) {
    if (!["enabled", "thresholdPercent", "checkIntervalSeconds", "reserves", "actionMode"].includes(key)) {
      return `Unknown application settings field 'memoryProtection.${key}'.`;
    }
    if (value === null) return `Application settings field 'memoryProtection.${key}' cannot be removed.`;
  }
  return null;
}

export function createMemoryProtectionFixture(getSettings) {
  let point = null;
  let entries = [];
  let currentCommit = commit();
  let actions = [];
  let excludedCandidates = [];
  let buildFailure = null;
  let sequence = 0;
  const at = () => new Date(Date.UTC(2026, 9, 27, 5, 0, sequence++)).toISOString();
  const reserve = () => ({ entries: structuredClone(entries),
    configuredSizeBytes: entries.reduce((sum, entry) => sum + entry.configuredSizeBytes, 0),
    built: entries.length > 0 && entries.every(entry => entry.built),
    reason: entries.length === 0 ? "reserve-not-configured" : entries.find(entry => !entry.built)?.reason ?? null,
    remainingBytes: entries.reduce((sum, entry) => sum + entry.remainingBytes, 0) });
  function unavailable(settings) {
    const value = reserve();
    return !same(settings.reserves ?? [], entries) ? "disk-reserve-not-built" : !value.built ? value.reason ?? "disk-reserve-not-built" : null;
  }
  function prepare(rebuild = false) {
    const requested = getSettings().reserves ?? [];
    if (!rebuild && same(requested, entries)) return;
    entries = requested.map(entry => {
      const clusterBytes = cluster(entry.location);
      const reason = entry.sizeBytes % clusterBytes !== 0
        ? `reserve-size-must-be-a-whole-allocation-unit; cluster-bytes=${clusterBytes}` : buildFailure;
      return { location: entry.location, configuredSizeBytes: entry.sizeBytes, built: reason === null,
        reason, remainingBytes: reason ? 0 : entry.sizeBytes, clusterBytes };
    });
  }
  function status() {
    const settings = getSettings();
    const triggerBytes = currentCommit === null ? null : currentCommit.limitBytes * settings.thresholdPercent / 100;
    return { enabled: settings.enabled, thresholdPercent: settings.thresholdPercent, checkIntervalSeconds: settings.checkIntervalSeconds,
      actionMode: settings.actionMode, reserve: reserve(), triggerBytes,
      remainingBytes: triggerBytes === null ? null : triggerBytes - currentCommit.currentBytes,
      currentCommit: structuredClone(currentCommit), normalRunningPoint: point ? { at: point.at, processCount: point.processCount } : null,
      inactiveReason: !point ? "no-normal-running-point" : !settings.enabled ? "disabled" : unavailable(settings),
      excludedCandidates: structuredClone(excludedCandidates), actionRecordLimit: 64, actions: structuredClone(actions) };
  }
  function createPoint() {
    point = { at: at(), commit: commit(), processes: [{ processId: 20, processStartKey: "18446744073709551615",
      name: "example", privateCommitBytes: 100, softwareId: "frontend-harness.software" },
    { processId: 21, processStartKey: "123", name: "unavailable", privateCommitBytes: null, softwareId: null }], processCount: 2 };
    excludedCandidates = [];
    return structuredClone(point);
  }
  function validate(settings) {
    if (!["dumpThenTerminate", "terminateOnly"].includes(settings.actionMode)) return "invalid-action-mode";
    if (!Number.isFinite(settings.thresholdPercent) || settings.thresholdPercent <= 0 || settings.thresholdPercent > 100) return "invalid-threshold-percent";
    if (!Number.isFinite(settings.checkIntervalSeconds) || settings.checkIntervalSeconds < 1 || settings.checkIntervalSeconds > 86400) return "invalid-check-interval";
    const locations = new Set(); let total = 0;
    for (const entry of settings.reserves ?? []) {
      if (!entry || typeof entry.location !== "string" || !entry.location.trim()) return "invalid-reserve-location";
      if (!Number.isInteger(entry.sizeBytes) || entry.sizeBytes <= 0 || entry.sizeBytes > 9223372036854775807) return "invalid-reserve-size";
      const location = entry.location.replace(/[\\/]+$/, "").toLowerCase();
      if (locations.has(location)) return "duplicate-reserve-location";
      locations.add(location); total += entry.sizeBytes;
      if (total > 9223372036854775807) return "invalid-reserve-total-size";
      const size = cluster(entry.location);
      if (entry.sizeBytes % size !== 0) return `reserve-size-must-be-whole-clusters:cluster-bytes=${size}`;
    }
    if (settings.enabled && !point) return "no-normal-running-point";
    return settings.enabled ? unavailable(settings) : null;
  }
  function scenario(name) {
    if (!memoryProtectionScenarios.includes(name)) throw new Error(`Unknown memoryProtection scenario: ${name}`);
    point = null; entries = []; currentCommit = commit(); actions = []; excludedCandidates = []; buildFailure = null;
    Object.assign(getSettings(), defaultMemoryProtectionSettings());
    if (name === "no-point") return;
    createPoint();
    if (name === "no-reserve") return;
    Object.assign(getSettings(), { reserves: configured(), enabled: name !== "disabled-ready" });
    if (name === "terminate-only") getSettings().actionMode = "terminateOnly";
    if (name === "reserve-failed") buildFailure = "insufficient-disk-space-for-reserve";
    if (name === "reserve-building") buildFailure = "reserve-building";
    if (name === "reserve-incomplete") buildFailure = "reserve-build-incomplete; request an explicit rebuild";
    prepare();
    if (name === "reserve-partial-cluster") {
      entries[1].configuredSizeBytes += 1;
      getSettings().reserves[1].sizeBytes += 1;
      entries[1].built = false; entries[1].remainingBytes = 0;
      entries[1].reason = "reserve-size-must-be-a-whole-allocation-unit; cluster-bytes=65536";
    }
    if (name === "reserve-consumed") for (const entry of entries) entry.remainingBytes = 0;
    if (name === "commit-unavailable") currentCommit = null;
    if (name === "exclusions" || name === "all-outcomes") {
      excludedCandidates = ["resource-manager-process", "critical-process", "process-identity-unavailable", "private-commit-unavailable",
        "baseline-private-commit-unavailable", "cannot-open", "identity-changed", "criticality-unavailable"]
        .map((reason, index) => ({ processId: index + 1, processStartKey: `${index}`, name: `excluded-${index}`, reason }));
    }
    if (name === "all-outcomes") {
      const results = [["completed", null], ["completed-with-release-limit", "private-memory-decommit-limited chunks=1 bytes=4096; address=0x7ffe0000 bytes=0x1000 protect=0x2 native-error=87 shared-user-data"],
        ["preflight-failed", "ReadProcessMemory failed"], ["suspend-failed", "ntstatus-c0000022"],
        ["slice-incomplete", "fixture IO failure"], ["termination-failed", "terminate-failed-5"],
        ...["terminate-only", "software-no-dump", "private-commit-exceeds-reserve", "minidump-exceeds-reserve"].map(reason => ["terminated-without-dump", reason])];
      actions = results.map(([outcome, reason], index) => ({ at: at(), processId: 20 + index, processStartKey: `${123 + index}`,
        name: `example-${index}`, baselineBytes: 100, currentBytes: 800, growthBytes: 700,
        dumpPaths: ["completed", "completed-with-release-limit", "slice-incomplete"].includes(outcome)
          ? ["F:\\MockReserve\\example.dmp", "G:\\MockReserve\\example.part0002.dmp"] : [],
        bytesWritten: ["completed", "completed-with-release-limit", "slice-incomplete"].includes(outcome) ? 16384 : 0, outcome, reason })).reverse();
    }
  }
  return { status, createPoint, readPoint: () => structuredClone(point), validate, prepare, scenario,
    rebuild: () => { if (["reserve-building", "reserve-build-incomplete; request an explicit rebuild"].includes(buildFailure)) buildFailure = null;
      prepare(true); return status(); } };
}

export function defaultSoftwarePolicy(softwareId, softwareName) {
  return { softwareId, softwareName, allowedProviders: ["windows-graphics-preference", "vendor-profile", "dxgi-launch-shim", "d3d-device-create-shim"],
    startupTargetGpu: "system_default_gpu", runtimeHotSwitchEnabled: true, preferredRuntimeSwitchMethod: "future_frame_takeover",
    baseScoreOverride: null, processOverrideAllowed: true, updatedAt: "2026-10-27T05:00:00Z", cpuSchedulingExcluded: false,
    memorySchedulingExcluded: false, gpuSchedulingExcluded: false, cpuMaximumOccupancyMode: "all_cores",
    cpuManualExclusivePositionIds: [], cpuManualLockedPositionIds: [], hostMemoryProtectionNoDump: false };
}

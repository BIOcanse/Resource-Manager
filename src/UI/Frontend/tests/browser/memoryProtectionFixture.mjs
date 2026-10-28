// A stateful backend fixture: all arithmetic here models backend responses, never frontend UI logic.
// GET /api/memory-protection serves the full status (HostMemoryProtectionStatus) for scripts; the subscription
// sends the frames of HostMemoryProtectionFrames: liveStatus(), records(), and the full point in effect (readPoint()).
// Saved points mirror HostMemoryProtectionService.PointStore: oldest first, one in effect or none, creation past the
// limit refused with normal-running-point-limit, a new point takes effect, deleting the one in effect leaves none.
export const memoryProtectionScenarios = ["no-point", "no-reserve", "disabled-ready", "active", "terminate-only",
  "reserve-building", "reserve-failed", "reserve-partial-cluster", "reserve-incomplete", "reserve-consumed",
  "commit-unavailable", "all-outcomes", "exclusions", "tree-actions", "action-in-progress"];
export const memoryProtectionDecisionReasons = ["commit-unavailable", "below-threshold", "process-handled",
  "process-tree-handled", "no-positive-child-memory-growth", "no-eligible-tree"];
export const defaultMemoryProtectionSettings = () => ({ enabled: false, thresholdPercent: 95,
  checkIntervalSeconds: 1, reserves: [], actionMode: "dumpThenTerminate",
  minimumProcessGrowthBytes: 1073741824, protectedProcesses: ["explorer.exe"] });
const memoryProtectionSettingKeys = ["enabled", "thresholdPercent", "checkIntervalSeconds", "reserves", "actionMode",
  "minimumProcessGrowthBytes", "protectedProcesses"];
const commit = () => ({ currentBytes: 200, limitBytes: 1000, percent: 20 });
const configured = () => [{ location: "F:\\MockReserve", sizeBytes: 67108864 }, { location: "G:\\MockReserve", sizeBytes: 131072 }];
const cluster = location => location.toUpperCase().startsWith("G:") ? 65536 : 4096;
const same = (a, b) => a.length === b.length && a.every((entry, index) => entry.location.toLowerCase() === b[index].location.toLowerCase()
  && entry.sizeBytes === b[index].configuredSizeBytes);

// Mirrors AppSettingsPatch for this section: removal, unknown keys and values the typed fields cannot hold (a
// fractional Int64, a non-string whitelist entry) are the owner's invalid-settings-value-type rejection; this
// returns the rejected value's JSON path. Other JSON type mismatches are checked by the mock's settingsValueTypeProblem.
export function invalidMemoryProtectionPatch(changes) {
  if (!Object.hasOwn(changes, "memoryProtection")) return null;
  const patch = changes.memoryProtection;
  if (patch === null || typeof patch !== "object" || Array.isArray(patch)) return "$.memoryProtection";
  for (const [key, value] of Object.entries(patch)) {
    if (!memoryProtectionSettingKeys.includes(key) || value === null) return `$.memoryProtection.${key}`;
  }
  if (typeof patch.minimumProcessGrowthBytes === "number" && !Number.isInteger(patch.minimumProcessGrowthBytes)) {
    return "$.memoryProtection.minimumProcessGrowthBytes";
  }
  if (Array.isArray(patch.protectedProcesses)) {
    const index = patch.protectedProcesses.findIndex(item => typeof item !== "string");
    if (index >= 0) return `$.memoryProtection.protectedProcesses[${index}]`;
  }
  return null;
}

// HostMemoryProtectionService.ValidateSettings for protectedProcesses: no blank, padded or repeated
// (case-insensitive) entries; a file name, or a fully qualified path with a file name and no wildcards.
function invalidProtectedProcesses(entries) {
  if (!Array.isArray(entries)) return true;
  const seen = new Set();
  for (const entry of entries) {
    if (typeof entry !== "string" || !entry.trim() || entry !== entry.trim() || seen.has(entry.toLowerCase())) return true;
    seen.add(entry.toLowerCase());
    const fullPath = /^[A-Za-z]:[\\/]/.test(entry) || /^\\\\[^\\]+\\[^\\]+/.test(entry);
    const fileName = entry.split(/[\\/]/).at(-1);
    if (fullPath ? !fileName || /[*?]/.test(entry) : fileName !== entry || /[<>:"|?*\u0000-\u001f]/.test(entry)) return true;
  }
  return false;
}

export const normalRunningPointLimit = 32;

export function createMemoryProtectionFixture(getSettings) {
  let points = [];
  let activeId = null;
  let pointLimit = normalRunningPointLimit;
  let entries = [];
  let currentCommit = commit();
  let actions = [];
  let excludedCandidates = [];
  let buildFailure = null;
  let reserveRuntimeError = null;
  let currentActionId = null;
  let decisionReason = null;
  let recordLimit = 64;
  let sequence = 0;
  const at = () => new Date(Date.UTC(2026, 9, 27, 5, 0, sequence++)).toISOString();
  const active = () => points.find(item => item.id === activeId) ?? null;
  const summary = item => ({ id: item.id, at: item.at, processCount: item.processCount, commit: structuredClone(item.commit) });
  const reserve = () => {
    const value = { entries: structuredClone(entries),
      configuredSizeBytes: entries.reduce((sum, entry) => sum + entry.configuredSizeBytes, 0),
      built: entries.length > 0 && entries.every(entry => entry.built),
      reason: entries.length === 0 ? "reserve-not-configured" : entries.find(entry => !entry.built)?.reason ?? null,
      remainingBytes: entries.reduce((sum, entry) => sum + entry.remainingBytes, 0) };
    return value;
  };
  function unavailable(settings) {
    // A reserve that was accepted at enable and failed while running keeps protection active (reserveAccepted).
    if (reserveRuntimeError !== null) return null;
    const value = reserve();
    return !same(settings.reserves ?? [], entries) ? "disk-reserve-not-built" : !value.built ? value.reason ?? "disk-reserve-not-built" : null;
  }
  function prepare(rebuild = false) {
    const requested = getSettings().reserves ?? [];
    if (!rebuild && same(requested, entries)) return;
    reserveRuntimeError = null;
    entries = requested.map(entry => {
      const clusterBytes = cluster(entry.location);
      const reason = entry.sizeBytes % clusterBytes !== 0
        ? `reserve-size-must-be-a-whole-allocation-unit; cluster-bytes=${clusterBytes}` : buildFailure;
      return { location: entry.location, configuredSizeBytes: entry.sizeBytes, built: reason === null,
        reason, remainingBytes: reason ? 0 : entry.sizeBytes, clusterBytes };
    });
  }
  // HostMemoryProtectionLiveStatus: the sampled status frame, without records or the point.
  function liveStatus() {
    const settings = getSettings();
    const triggerBytes = currentCommit === null ? null : currentCommit.limitBytes * settings.thresholdPercent / 100;
    const inactiveReason = !active() ? "no-normal-running-point" : !settings.enabled ? "disabled" : unavailable(settings);
    return { enabled: settings.enabled, thresholdPercent: settings.thresholdPercent, checkIntervalSeconds: settings.checkIntervalSeconds,
      reserve: reserve(), triggerBytes,
      remainingBytes: triggerBytes === null ? null : triggerBytes - currentCommit.currentBytes,
      currentCommit: structuredClone(currentCommit),
      inactiveReason, excludedCandidates: structuredClone(excludedCandidates),
      actionMode: settings.actionMode, active: inactiveReason === null,
      atOrAboveThreshold: currentCommit === null ? null : currentCommit.percent >= settings.thresholdPercent,
      currentActionId, minimumProcessGrowthBytes: settings.minimumProcessGrowthBytes,
      protectedProcesses: structuredClone(settings.protectedProcesses), decisionReason };
  }
  function status() {
    return { ...liveStatus(), normalRunningPoint: active() ? summary(active()) : null,
      actionRecordLimit: recordLimit, actions: structuredClone(actions) };
  }
  const records = () => ({ records: structuredClone(actions), limit: recordLimit });
  // Null when the saved points are at the limit (the endpoint answers 409 normal-running-point-limit).
  function createPoint() {
    if (points.length >= pointLimit) return null;
    const point = { id: (sequence + 1).toString(16).padStart(32, "0"), at: at(), commit: commit(), processes: [{ processId: 20, processStartKey: "18446744073709551615",
      name: "example", privateCommitBytes: 100, softwareId: "frontend-harness.software",
      executablePath: "C:\\Fixture\\example.exe", parentProcessId: 17 },
    { processId: 21, processStartKey: "123", name: "unavailable", privateCommitBytes: null, softwareId: null,
      executablePath: null, parentProcessId: null }], processCount: 2 };
    points = [...points, point];
    activeId = point.id;
    excludedCandidates = [];
    return structuredClone(point);
  }
  const listPoints = () => ({ activeId, points: points.map(summary), limit: pointLimit });
  // Null when no saved point has this id (404 normal-running-point-not-found).
  function selectPoint(id) {
    const selected = points.find(item => item.id === id);
    if (!selected) return null;
    if (activeId !== id) excludedCandidates = [];
    activeId = id;
    return structuredClone(selected);
  }
  function deletePoint(id) {
    if (!points.some(item => item.id === id)) return false;
    points = points.filter(item => item.id !== id);
    if (activeId === id) { activeId = null; excludedCandidates = []; }
    return true;
  }
  function validate(settings) {
    if (!["dumpThenTerminate", "terminateOnly"].includes(settings.actionMode)) return "invalid-action-mode";
    if (!Number.isInteger(settings.minimumProcessGrowthBytes) || settings.minimumProcessGrowthBytes <= 0) return "invalid-minimum-process-growth-bytes";
    if (invalidProtectedProcesses(settings.protectedProcesses)) return "invalid-protected-processes";
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
    if (settings.enabled && !active()) return "no-normal-running-point";
    return settings.enabled ? unavailable(settings) : null;
  }
  // HostMemoryProtectionService: a single-process action ranks by the process's growth, a tree action by its root's
  // direct-children memory growth; both carry the minimum the decision compared with.
  const processBasis = growth => ({ rule: "process-growth-at-least-minimum", processGrowthBytes: growth,
    directChildrenMemoryGrowthBytes: null, minimumProcessGrowthBytes: getSettings().minimumProcessGrowthBytes });
  const treeBasis = growth => ({ rule: "largest-direct-children-memory-growth", processGrowthBytes: null,
    directChildrenMemoryGrowthBytes: growth, minimumProcessGrowthBytes: getSettings().minimumProcessGrowthBytes });
  const record = (fields) => ({ at: at(), processStartKey: `${fields.processId}0`, baselineBytes: 100, currentBytes: 800, growthBytes: 700,
    dumpPaths: [], bytesWritten: 0, reason: null, detail: null, targetKind: "process", treeRoot: null,
    selectionBasis: processBasis(fields.growthBytes ?? 700), ...fields });
  function treeActions() {
    // One tree action, newest first like the backend list: the protected root (handled last) comes first, then the
    // children in reverse leaves-first order; one child is skipped by its software policy and has no readable memory.
    const treeRoot = { processId: 300, processStartKey: "3000", name: "launcher" };
    const tree = { actionId: "a1b2c3d4e5f60718293a4b5c6d7e8f90", targetKind: "processTree", treeRoot, selectionBasis: treeBasis(30) };
    const single = record({ actionId: "0f1e2d3c4b5a69788796a5b4c3d2e1f0", processId: 40, name: "worker",
      outcome: "terminated-without-dump", reason: "reserve-error", detail: "reserve-release-failed" });
    return [
      record({ ...tree, processId: 300, name: "launcher", baselineBytes: 100, currentBytes: 100, growthBytes: 0,
        outcome: "excluded", reason: "protected-process" }),
      record({ ...tree, processId: 303, name: "game-client", baselineBytes: null, currentBytes: null, growthBytes: null,
        outcome: "excluded", reason: "software-never-terminate" }),
      record({ ...tree, processId: 302, name: "cat", baselineBytes: 0, currentBytes: 20, growthBytes: 20,
        outcome: "terminated-without-dump", reason: "process-tree" }),
      record({ ...tree, processId: 301, name: "cat", baselineBytes: 0, currentBytes: 10, growthBytes: 10,
        outcome: "terminated-without-dump", reason: "process-tree" }),
      single
    ];
  }
  function scenario(name) {
    if (!memoryProtectionScenarios.includes(name)) throw new Error(`Unknown memoryProtection scenario: ${name}`);
    points = []; activeId = null; pointLimit = normalRunningPointLimit; entries = []; currentCommit = commit(); actions = []; excludedCandidates = []; buildFailure = null;
    reserveRuntimeError = null; currentActionId = null; decisionReason = null; recordLimit = 64;
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
    if (name === "commit-unavailable") { currentCommit = null; decisionReason = "commit-unavailable"; }
    if (name === "exclusions" || name === "all-outcomes") {
      excludedCandidates = ["resource-manager-process", "critical-process", "process-identity-unavailable", "private-commit-unavailable",
        "baseline-private-commit-unavailable", "cannot-open", "identity-changed", "criticality-unavailable", "protected-process",
        "software-never-terminate"]
        .map((reason, index) => ({ processId: index + 1, processStartKey: `${index}`, name: `excluded-${index}`, reason }));
    }
    if (name === "all-outcomes") {
      // WindowsHostMemoryProtectionExecutor: a stable reason code per outcome, diagnostic text in detail.
      const results = [["completed", null, null],
        ["completed-with-release-limit", "release-limit", "private-memory-decommit-limited chunks=1 bytes=4096; address=0x7ffe0000 bytes=0x1000 protect=0x2 native-error=87 shared-user-data"],
        ["preflight-failed", "preflight-failed", "ReadProcessMemory failed"], ["suspend-failed", "suspend-failed", "ntstatus-c0000022"],
        ["slice-incomplete", "slice-incomplete", "fixture IO failure"], ["termination-failed", "termination-failed", "terminate-failed-5"],
        ...["terminate-only", "software-no-dump", "private-commit-exceeds-reserve", "minidump-exceeds-reserve"].map(reason => ["terminated-without-dump", reason, null]),
        ["terminated-without-dump", "reserve-error", "Access to the reserve file was denied."]];
      actions = results.map(([outcome, reason, detail], index) => ({ at: at(), processId: 20 + index, processStartKey: `${123 + index}`,
        name: `example-${index}`, baselineBytes: 100, currentBytes: 800, growthBytes: 700,
        dumpPaths: ["completed", "completed-with-release-limit", "slice-incomplete"].includes(outcome)
          ? ["F:\\MockReserve\\example.dmp", "G:\\MockReserve\\example.part0002.dmp"] : [],
        bytesWritten: ["completed", "completed-with-release-limit", "slice-incomplete"].includes(outcome) ? 16384 : 0, outcome, reason, detail,
        actionId: `${index}`.padStart(32, "a"), targetKind: "process", treeRoot: null, selectionBasis: processBasis(700) })).reverse();
      decisionReason = "process-handled";
    }
    if (name === "tree-actions") {
      // The reserve failed after protection was enabled: still active, reserve not built; commit charge unreadable.
      reserveRuntimeError = "reserve-release-failed";
      for (const entry of entries) { entry.built = false; entry.reason = reserveRuntimeError; }
      currentCommit = null;
      actions = treeActions();
      decisionReason = "commit-unavailable";
    }
    if (name === "action-in-progress") {
      currentCommit = { currentBytes: 960, limitBytes: 1000, percent: 96 };
      currentActionId = "5fb774b0d85b49259ad57b3280d2fa51";
    }
  }
  // Test-only publication of backend decision fields, as the service's Finish/Begin would publish them.
  function publishDecision(fields) {
    if (Object.hasOwn(fields, "decisionReason")) {
      if (fields.decisionReason !== null && !memoryProtectionDecisionReasons.includes(fields.decisionReason)) {
        throw new Error(`Unknown memory protection decision reason: ${fields.decisionReason}`);
      }
      decisionReason = fields.decisionReason;
    }
    if (Object.hasOwn(fields, "currentActionId")) currentActionId = fields.currentActionId;
  }
  // Test-only: the record limit the next records replay carries.
  function setRecordLimit(limit) {
    if (!Number.isSafeInteger(limit) || limit < 0) throw new Error(`Invalid memory protection record limit: ${limit}`);
    recordLimit = limit;
    actions = actions.slice(0, recordLimit);
  }
  // Test-only: one new or updated record, as the action event publishes it. A record with the same actionId and
  // process identity replaces the earlier one in place; a new one is the newest. The list keeps the newest limit.
  function upsertRecord(fields) {
    const value = record({ actionId: "f".repeat(32), processId: 50, name: "growing", outcome: "terminated-without-dump",
      reason: "terminate-only", ...fields });
    const index = actions.findIndex(item => item.actionId === value.actionId && item.processId === value.processId
      && item.processStartKey === value.processStartKey);
    if (index >= 0) actions[index] = value;
    else actions.unshift(value);
    actions = actions.slice(0, recordLimit);
    return structuredClone(value);
  }
  // Test-only: the limit the list reports and creation checks (the backend's is the constant 32).
  function setPointLimit(limit) {
    if (!Number.isSafeInteger(limit) || limit < 0) throw new Error(`Invalid normal running point limit: ${limit}`);
    pointLimit = limit;
  }
  return { status, liveStatus, records, createPoint, listPoints, selectPoint, deletePoint, setPointLimit,
    readPoint: () => structuredClone(active()), validate, prepare, scenario, publishDecision, setRecordLimit, upsertRecord,
    rebuild: () => { if (["reserve-building", "reserve-build-incomplete; request an explicit rebuild"].includes(buildFailure)) buildFailure = null;
      prepare(true); return status(); } };
}

export function defaultSoftwarePolicy(softwareId, softwareName) {
  return { softwareId, softwareName, allowedProviders: ["windows-graphics-preference", "vendor-profile", "dxgi-launch-shim", "d3d-device-create-shim"],
    startupTargetGpu: "system_default_gpu", runtimeHotSwitchEnabled: true, preferredRuntimeSwitchMethod: "future_frame_takeover",
    baseScoreOverride: null, processOverrideAllowed: true, updatedAt: "2026-10-27T05:00:00Z", cpuSchedulingExcluded: false,
    memorySchedulingExcluded: false, gpuSchedulingExcluded: false, cpuMaximumOccupancyMode: "all_cores",
    cpuManualExclusivePositionIds: [], cpuManualLockedPositionIds: [], hostMemoryProtectionNoDump: false,
    hostMemoryProtectionNeverTerminate: false };
}

import { componentFixture, componentVersionFixture } from "./componentFixtures.js";
import { canonicalFieldNames } from "../../src/features/monitor/canonicalFields.ts";
import { reportRules } from "../fixtures/reportRuleSettings.ts";
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, resolve, sep, isAbsolute } from "node:path";
import { fileURLToPath } from "node:url";

const port = Number(process.env.RM_FRONTEND_HARNESS_PORT || 4177);
const staticRoot = process.env.RM_FRONTEND_STATIC_ROOT
  ? resolve(process.env.RM_FRONTEND_STATIC_ROOT)
  : fileURLToPath(new URL("../../../../../src/Core/wwwroot/", import.meta.url));
const now = () => new Date().toISOString();
const recordedEdidState = JSON.parse(await readFile(process.env.RM_EDID_UI_STATE_JSON || new URL("../fixtures/displayEdid.json", import.meta.url), "utf8"));
const recordedType3State = JSON.parse(await readFile(process.env.RM_EDID_UI_TYPE3_STATE_JSON || new URL("../fixtures/displayEdidType3.json", import.meta.url), "utf8"));
const typeXWirePath = process.env.RM_EDID_UI_TYPEX_STATE_JSON;
if (typeXWirePath && !isAbsolute(typeXWirePath)) throw new Error("TypeX wire path must be absolute");
const recordedTypeXState = typeXWirePath ? JSON.parse(await readFile(typeXWirePath, "utf8")) : null;
// DisplayID Type V/VI、Adaptive-Sync 与动态时序范围：真实 parser／生产 DTO 生成的仓库夹具，按场景名选用。
const recordedDeclarationStates = Object.fromEntries(await Promise.all(Object.entries({
  typeVVi: "displayEdidTypeVVi.json", typeVViClock: "displayEdidTypeVViClock.json", adaptiveSync: "displayEdidAdaptiveSync.json",
  adaptiveSyncZero: "displayEdidAdaptiveSyncZero.json", dynamicRange: "displayEdidDynamicRange.json"
}).map(async ([name, file]) => [name, JSON.parse(await readFile(new URL(`../fixtures/${file}`, import.meta.url), "utf8"))])));
const defaultScenario = {
  capabilities: "ready",
  settings: "ready",
  settingsConflict: false,
  settingsRuntimeDisposition: "committedAndApplied",
  settingsGetDelayMs: 0,
  settingsPatchDelayMs: 0,
  settingsRevision: "frontend-harness-r1",
  overlaySaveDelayMs: 0,
  overlaySaveFailure: false,
  overlayElevationOutcome: null,
  diskUsageVolumesFailure: false,
  diskUsageVolumesEmpty: false,
  diskUsageResultFailure: false,
  diskUsageScanFailure: false,
  diskUsageScanLifecycle: false,
  diskUsageScanCompletionDelayMs: 900,
  diskUsageMultiRoot: false,
  diskUsagePaged: false,
  diskUsageRootIndexOneDelayMs: 0,
  deviceTopologyDelayMs: 0,
  deviceTopologyInvalid: false,
  invalidMetricFields: false,
  deviceTopologyNullProtocol: false,
  deviceTopologyPortCount: null,
  deviceTopologyPorts: null,
  displayEdid: "off",
  gpuGrade: "normal",
  cpuGrade: "normal",
  resourceBreakdownGeneration: 0,
  resourceSegmentCount: 1,
  resourceDetailsDelayMs: 0,
  resourceCpuValue: null,
  resourceTableVariant: "normal",
  resourceTableRowCount: null,
  debugMode: false,
  runtimeEffectOwners: false,
  optimizationRuntime: false,
  optimizationReports: "ready",
  smartPlacements: [],
  // 后端调度状态（GET /api/optimization/smart/status 与它的推送订阅同源）；只写与默认值不同的字段。
  smartStatus: {},
  softwareIssues: true,
  operations: "empty",
  operationCanceled: false,
  // 进行中的那个任务还在排队（同一 ID，状态修订更早）。
  operationQueued: false,
  componentPayloadFailure: false,
  managementInventoryRevisions: {
    softwareRevision: "0",
    componentRevision: "0",
    browserRuntimeRevision: "0"
  },
  cpuTopologyEmpty: false,
  // 规范字段的设备值（/api/metrics/subscribe?fields=）。默认没有任何设备绑定。
  // 字段目录（/api/metrics/fields/catalog/subscribe）由同一份设备值生成。
  metricFieldDevices: [],
  // SamplingObservationStatus overrides for current-value/placeholder contract tests.
  metricStatuses: {},
  // 指标目录里额外的部件地址："none" 没有；"reading-source" 加一个带候选来源的核显温度地址
  // （gpu.5.temperature，候选是 metricFieldsProbe 的 readingSourceDevices 字段，后端当前选中 ADLX 次选）。
  metricCatalogAddresses: "none",
  // 资源条按进程字段订阅（measures=）时，这些字段的整机总量读不到：后端不发这一条（其余条照常）。
  resourceBarMeasureTotalsMissing: [],
  // 监视控制台卡片设置：保存（PUT）后读回保存的内容；null 时是夹具默认值。
  dashboardSettings: null,
  gpuPlacementEnabled: false,
  unknownGpuScore: false,
  gpuPressureFixture: false,
  gpuOverflowThresholdsMissing: false,
  accessRequests: [],
  // 可信访问密钥的后端状态：ready 正常；read-failure 读取密钥列表返回 503；
  // default-key-off 默认密钥已关闭，界面的每个 /api 请求都返回 401（后端不再接受界面的密钥）。
  trustedAccess: "ready",
  pausedSubscriptionPaths: [],
  // 产品更新状态（GET /api/updates/product/status 与它的推送订阅同源）：只写与默认状态不同的字段；
  // "invalid" 让推送发出不合合同的帧、GET 返回 503，模拟状态不可用。未知字段和不合合同的值直接拒绝。
  productUpdateStatus: {}
};
const trustedAccessScenarios = new Set(["ready", "read-failure", "default-key-off"]);
const trustedAccessDefaultScopes = ["read", "metrics.read", "processes.read", "session", "adapters.register.self",
  "desktop.publish", "gpu.resolve", "gpu.report", "keys.manage"];
const trustedAccessAvailableScopes = [...trustedAccessDefaultScopes, "machine.write", "injection.configure"];
let trustedAccessDefaultSerial = 1;
const scenario = structuredClone(defaultScenario);
const emptyDiskUsageLayout = Buffer.alloc(64);
emptyDiskUsageLayout.writeUInt32LE(0x55444d52, 0);
emptyDiskUsageLayout.writeUInt32LE(3, 4);
emptyDiskUsageLayout.writeUInt32LE(0, 8);
emptyDiskUsageLayout.writeInt32LE(-1, 12);
emptyDiskUsageLayout.writeInt32LE(-1, 16);
const diskUsageSummary = {
  scope: "allVolumes",
  mode: "fast",
  target: "",
  roots: ["C:\\", "D:\\"],
  scanKind: "masterFileTable",
  skipped: [],
  completedAt: "2026-10-09T12:00:00.000Z",
  durationSeconds: 2.5,
  totalBytes: 91_000_000,
  scannedBytes: 91_000_000,
  fileCount: 17,
  directoryCount: 5,
  skippedCount: 0
};
const diskUsageLayouts = new Map([
  ["root-c", createDiskUsageLayout("C:\\", 0, 18_000_000, [
    diskUsageTile(0, -1, 0, 18_000_000, "", true, 17),
    diskUsageTile(1, 0, 1, 8_000_000, "Projects", true, 4),
    diskUsageTile(5, 1, 2, 3_000_000, "Archive", true, 2)
  ])],
  ["root-d", createDiskUsageLayout("D:\\", 2, 73_000_000, [
    diskUsageTile(2, -1, 0, 73_000_000, "", true, 17),
    diskUsageTile(3, 2, 1, 22_000_000, "Projects", true, 4),
    diskUsageTile(4, 3, 2, 9_000_000, "Archive", true, 2)
  ])]
]);
const requestTelemetry = Object.create(null);
const ndjsonPublishers = new Map();
const operationSubscribers = new Set();
const managementInventoryRevisionSubscribers = new Set();
const subscriptionChannelResponses = new Set();
const productUpdateStatusSubscribers = new Set();
// 后端帧的真实形状（codex/update-notice，与 GET /api/updates/product/status 相同）。
const defaultProductUpdateStatus = Object.freeze({
  stage: "idle", targetVersion: null, workspace: null, error: null, updatedAt: "2026-10-25T19:00:00+00:00",
  errorCode: null, errorParameters: null, lastCheckedAt: null, checkResult: null, availableVersion: null,
  nextCheckAt: null, onlineTimeSinceLastCheckSeconds: 0, automaticCheckStatus: "idle", failedVersion: null,
  checkedInstalledVersion: null, incompatibleVersions: []
});
let deviceTopologyRevision = 0;
let operationsPublicationRevision = 0;
let operationsCurrentValue = null;
let diskScanOperation = null;
let diskScanSequence = 0;
let diskScanCompletionTimer = null;
let targetedRecordingCompletionTimer = null;
let targetedRecordingSequence = 0;
let cpuBaselineOverrideRatio = null;
const targetedRecordings = new Map();
const defaultAppSettings = {
  version: "1.0.33",
  performance: { gpuOverflowUsagePercent: 95, gpuOverflowDedicatedMemoryPercent: 80, realtimeActionBudgetMode: "fixed", realtimeActionBudgetCount: 32, realtimeActionBudgetPercent: 5 },
  reports: { ruleOverrides: [] },
  debug: {
    debugModeEnabled: scenario.debugMode
  },
  update: { mode: "automatic", periodicCheckEnabled: false, checkIntervalHours: 24 }
};
const appSettings = structuredClone(defaultAppSettings);
let appSettingsRuntimeDisposition = "committedAndApplied";
let metricSlot = {
  observedAt: now()
};
let resourceSlotCapturedAt = now();

const startupModuleSelections = new Map();
const metricCatalog = [
  metric("cpu.usage", "处理器", "CPU", "%"),
  metric("cpu.frequency", "处理器频率", "CPU", "MHz"),
  metric("cpu.frequencyPercent", "处理器频率比例", "CPU", "%"),
  metric("memory.usage", "内存", "Memory", "B"),
  metric("memory.percent", "内存占用", "Memory", "%")
];
const appSettingsResult = (runtimeApplicationDisposition = appSettingsRuntimeDisposition) => {
  const settings = structuredClone(appSettings);
  if (scenario.gpuOverflowThresholdsMissing) {
    delete settings.performance?.gpuOverflowUsagePercent;
    delete settings.performance?.gpuOverflowDedicatedMemoryPercent;
  }
  return {
    settings,
    revision: scenario.settingsRevision,
    runtimeApplicationDisposition,
    runtimePlanVersion: runtimeApplicationDisposition === "savedNotApplied" ? null : 1,
    runtimePublicationSequence: runtimeApplicationDisposition === "savedNotApplied" ? null : 1,
    runtimeDeliveryFailureCount: runtimeApplicationDisposition === "committedWithDeliveryFailures" ? 1 : 0,
    runtimeCapabilityConstrainedPaths: runtimeApplicationDisposition === "committedWithCapabilityConstraints" ? ["performance.preciseGpuPlacementEnabled"] : [],
    runtimeFailureCode: runtimeApplicationDisposition === "savedNotApplied" ? "runtime-plan-publication-failed"
      : runtimeApplicationDisposition === "revisionConflict" ? "settings-revision-conflict" : null
  };
};
const settingsResultStatus = disposition => ["savedNotApplied", "committedWithDeliveryFailures"].includes(disposition) ? 202 : 200;
const mockSoftware = {
  id: "frontend-harness.software",
  name: "示例软件",
  kind: "other",
  state: "installed",
  sourceFacts: [{ kind: "windows_registry" }, { kind: "adapter" }],
  registrationFacts: { kind: "adapter", processCount: 2, serviceCount: 3, endpointState: "online" },
  requiresRootPathConfirmation: false,
  identityConfirmed: true,
  executablePaths: ["C:\\FrontendHarness\\example.exe"],
  rootPaths: ["C:\\FrontendHarness"],
  processIds: [4242],
  processNames: ["example.exe"],
  operations: { canUninstall: false, uninstallKind: "None", uninstallConstraint: "missing_uninstall_entry" },
  issues: [
    {
      id: "app-notebook-fan-control-winring0x64-known-vulnerabilities",
      kind: "known_security_vulnerability",
      severity: "warning",
      source: "static_catalog",
      label: "存在已公开安全漏洞",
      message: "WinRing0x64.sys 与已公开漏洞记录匹配。",
      dynamic: false,
      references: [
        { label: "CVE-2020-14979", url: "https://nvd.nist.gov/vuln/detail/CVE-2020-14979" }
      ],
      reportIds: []
    },
    {
      id: "dynamic:abnormal_memory_usage:frontend-harness.software",
      kind: "abnormal_memory_usage",
      severity: "info",
      source: "dynamic_report",
      label: "内存占用异常",
      message: "当前报告判定该非游戏软件的内存占用异常。",
      dynamic: true,
      references: [],
      evidence: [{ metricKind: "memory", value: 4294967296, valueUnit: "B" }],
        reportIds: ["hm-report-memory"]
    },
    {
      id: "dynamic:long_system_interrupts:frontend-harness.software",
      kind: "long_system_interrupts",
      severity: "critical",
      source: "dynamic_report",
      label: "造成长系统中断",
      message: "example.sys 是同一采样窗口中的主要归因驱动。",
      dynamic: true,
      references: [],
      evidence: [{ metricKind: "interrupt_maximum_single_duration_milliseconds", value: 12, valueUnit: "ms", moduleName: "example.sys" }],
        reportIds: ["hm-report-interrupt"]
    }
  ]
};

const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url ?? "/", `http://127.0.0.1:${port}`);
    if (url.pathname === "/__test/scenario" && request.method === "POST") {
      const patch = JSON.parse(await readBody(request));
      applyScenarioPatch(patch);
      return json(response, 200, scenario);
    }
    if (url.pathname === "/__test/scenario/reset" && request.method === "POST") {
      resetScenario();
      return json(response, 200, scenario);
    }
    if (url.pathname === "/__test/scenario") {
      return json(response, 200, scenario);
    }
    if (url.pathname === "/__test/requests/reset" && request.method === "POST") {
      resetRequestTelemetry();
      return json(response, 200, requestTelemetry);
    }
    if (url.pathname === "/__test/requests") {
      return json(response, 200, requestTelemetry);
    }
    if (url.pathname === "/__test/elevated-backend-request" && request.method === "POST") {
      const command = JSON.parse(await readBody(request));
      const result = { type: "backend.elevatedRequest.result", requestId: command.requestId, statusCode: null, body: null, error: null };
      const match = typeof command.path === "string" && /^\/api\/startup-modules\/software\/([^/?#\\]+)$/.exec(command.path);
      const defaultKey = command.method === "POST" && command.path === "/api/access-keys/default";
      if (command.type !== "backend.elevatedRequest" || typeof command.requestId !== "string" || !command.requestId
        || !((command.method === "PUT" && match) || defaultKey)
        || typeof command.body !== "string" || Buffer.byteLength(command.body) > 128 * 1024) {
        return json(response, 200, { ...result, error: "unavailable" });
      }
      if (scenario.overlayElevationOutcome) return json(response, 200, { ...result, error: scenario.overlayElevationOutcome });
      let settings;
      try { settings = JSON.parse(command.body); } catch { return json(response, 200, { ...result, error: "unavailable" }); }
      if (defaultKey) {
        // 管理员身份的后端客户端没有密钥，后端按管理员授权它修改默认密钥。
        const saved = setTrustedAccessDefault(settings);
        return json(response, 200, { ...result, statusCode: saved.statusCode, body: JSON.stringify(saved.body) });
      }
      const saved = await saveStartupModuleSelection(decodeURIComponent(match[1]), settings);
      return json(response, 200, { ...result, statusCode: saved.statusCode, body: saved.body === null ? "" : JSON.stringify(saved.body) });
    }
    if (url.pathname === "/__test/management-inventory-revisions/publish"
      && request.method === "POST") {
      const patch = JSON.parse(await readBody(request));
      const next = { ...scenario.managementInventoryRevisions };
      for (const key of ["softwareRevision", "componentRevision", "browserRuntimeRevision"]) {
        if (Object.hasOwn(patch, key)) {
          if (typeof patch[key] !== "string" || !/^(0|[1-9][0-9]*)$/.test(patch[key])) {
            return json(response, 400, { error: `invalid ${key}` });
          }
          next[key] = patch[key];
        }
      }
      scenario.managementInventoryRevisions = next;
      publishManagementInventoryRevisions();
      return json(response, 200, next);
    }
    if (url.pathname.startsWith("/api/")) {
      trackRequest(response, url.pathname, url.search);
      return await handleApi(request, response, url);
    }
    return await serveStatic(response, url.pathname);
  } catch (error) {
    return json(response, 500, { error: String(error) });
  }
});

server.listen(port, "127.0.0.1", () => {
  console.log(`frontend harness listening on http://127.0.0.1:${port}`);
});

async function handleApi(request, response, url) {
  const path = url.pathname;
  if (scenario.trustedAccess === "default-key-off") {
    return json(response, 401, { error: "access-key-invalid" });
  }
  if (path === "/api/access-keys" && request.method === "GET") {
    if (scenario.trustedAccess === "read-failure") return json(response, 503, { error: "fixture-access-key-read-failure" });
    return json(response, 200, trustedAccessSnapshot());
  }
  if (path === "/api/access-keys/default" && request.method === "POST") {
    const saved = setTrustedAccessDefault(JSON.parse(await readBody(request)));
    return json(response, saved.statusCode, saved.body);
  }
  if (path === "/api/control/objects") {
    return json(response, 200, { readAt: now(), objects: [{
      id: "gpu:test", kind: "gpu", reportedName: "Fixture GPU", adapterIndex: 0,
      platform: { operatingSystem: "windows", vendor: "nvidia" }, isControllable: true,
      capabilities: [
        { id: "gpu.temperature-limit", valueKind: "number", supported: false, unavailableReason: { code: "nvidia.M63" }, unavailableKind: "platform", requiredAccessLevel: "normal", range: { minimum: 89, maximum: 89, step: 1, unit: "°C", defaultValue: 89 } },
        { id: "gpu.dynamic-boost-enabled", valueKind: "toggle", supported: true, requiredAccessLevel: "normal" },
        { id: "gpu.core-voltage-offset", valueKind: "number", supported: false, unavailableReason: { code: "control-access-level-required", arguments: { requiredAccessLevel: "root" } }, unavailableKind: "access-level", requiredAccessLevel: "root", range: { minimum: -100, maximum: 100, step: 1, unit: "mV", defaultValue: 0 } }
      ]
    }] });
  }
  if (path === "/api/control/state") return json(response, 200, { desired: { objects: [] }, lastApply: { outcomes: [], appliedAt: now() } });
  if (path === "/api/control/instances") return json(response, 200, { instances: [], readAt: now() });
  if (path === "/api/control/presets") return json(response, 200, { presets: [], readAt: now() });
  if (path === "/api/control/notice") return json(response, 200, { acknowledged: true });
  if (path === "/api/control/overclock-consent") return json(response, 200, { accepted: false });
  if (path === "/api/control/access-level") return json(response, 200, { level: "normal" });
  if (path === "/api/subscriptions/stream" && request.method === "POST") {
    return subscriptionChannelStream(request, response);
  }
  if (path === "/api/runtime/capabilities") {
    if (scenario.capabilities === "hang") {
      return;
    }
    if (scenario.capabilities === "error") {
      return text(response, 503, "capability unavailable");
    }
    return json(response, 200, {
      profileId: "frontend-harness",
      readOnly: false,
      mutablePersistence: true,
      legacyPersistenceImport: false,
      gpuLaunchInterceptionReconciliation: scenario.gpuPlacementEnabled,
      runtimeEffectOwners: scenario.runtimeEffectOwners,
      publicServiceCoordination: false,
      optimizationRuntime: scenario.optimizationRuntime === true,
      sharedResourceOwnership: false
    });
  }
  const readsOptimizationReports = path === "/api/optimization/reports"
    && request.method === "GET";
  const refreshesOptimizationReports = path === "/api/optimization/reports/refresh"
    && request.method === "POST";
  if (readsOptimizationReports || refreshesOptimizationReports) {
    if (scenario.optimizationReports === "error") {
      return text(response, 503, "optimization reports unavailable");
    }
    const capturedAt = new Date().toISOString();
    if (scenario.optimizationReports === "invalid") {
      return json(response, 200, {
        capturedAt,
        reports: [],
        trustedTargets: [],
        protectedTargets: [],
        status: { recorderRunning: true }
      });
    }
    return json(response, 200, {
      capturedAt,
      reports: [],
      trustedTargets: [],
      protectedTargets: [],
      status: {
        lastEvaluationAt: capturedAt,
        lastObservedAt: capturedAt,
        evaluationState: scenario.optimizationReports === "evaluation-failed" ? "failed" : "completed",
        evaluationFailureReason: scenario.optimizationReports === "evaluation-failed" ? "IOException" : null,
        unavailableRules: scenario.optimizationReports === "partial" ? ["cpu_throttle_temperature"] : [],
        configuredRuleCount: 7,
        availableRuleCount: scenario.optimizationReports === "partial" ? 6 : 7,
        activeReportCount: 0,
        trustedCount: 0,
        protectedCount: 0,
        sampleIntervalSeconds: 10
      }
    });
  }
  if (path === "/api/optimization/smart/status" && request.method === "GET") {
    return json(response, 200, smartCoordinatorStatus());
  }
  if (path === "/api/performance-overlay/metrics/catalog" && request.method === "GET") {
    // 浮窗目录和后端一样也带部件地址的候选来源（MetricCatalog.ForPerformanceOverlay 继承同一份）。
    return json(response, 200, [...["target.fps", "target.frameTime", "target.onePercentLow", "target.pointOnePercentLow", "target.cpu", "target.gpu", "target.memory", "target.vram"]
      .map(id => metric(id, id, "Target", id === "target.frameTime" ? "ms" : id.endsWith("cpu") || id.endsWith("gpu") ? "%" : "FPS")),
      ...(scenario.metricCatalogAddresses === "reading-source" ? [readingSourceAddress()] : [])]);
  }
  if (path === "/api/startup-modules/settings" && request.method === "GET") {
    return json(response, 200, { version: 1, software: [...startupModuleSelections.values()] });
  }
  if (path.startsWith("/api/startup-modules/software/")) {
    const id = decodeURIComponent(path.slice("/api/startup-modules/software/".length)).trim();
    if (!id) return json(response, 400, { error: "Software ID is required." });
    if (request.method === "GET") return json(response, 200, startupModuleSelections.get(id.toLowerCase()) ?? { softwareId: id, moduleIds: [] });
    if (request.method === "PUT") {
      return json(response, 403, { error: "access-scope-required" });
    }
  }
  if (path === "/api/disk-usage/volumes" && request.method === "GET") {
    if (scenario.diskUsageVolumesFailure) return json(response, 503, { error: "fixture volume read failure" });
    if (scenario.diskUsageVolumesEmpty) return json(response, 200, []);
    if (scenario.diskUsageMultiRoot === true) {
      return json(response, 200, [
        {
          volumeId: "C:",
          label: "Recorded system volume",
          fileSystem: "NTFS",
          volumeKind: "physical",
          totalBytes: 1_000_000_000,
          freeBytes: 250_000_000,
          isReady: true,
          supportsMasterFileTable: true
        },
        {
          volumeId: "D:",
          label: "Recorded data volume",
          fileSystem: "NTFS",
          volumeKind: "physical",
          totalBytes: 2_000_000_000,
          freeBytes: 500_000_000,
          isReady: true,
          supportsMasterFileTable: true
        }
      ]);
    }
    return json(response, 200, [{
      volumeId: "C:",
      label: "Harness volume",
      fileSystem: "NTFS",
      volumeKind: "physical",
      totalBytes: 1_000_000_000,
      freeBytes: 250_000_000,
      isReady: true,
      supportsMasterFileTable: true
    }]);
  }
  if (path === "/api/disk-usage/summary" && request.method === "GET") {
    if (scenario.diskUsageResultFailure) return json(response, 503, { error: "fixture summary read failure" });
    return json(response, 200,
      scenario.diskUsageMultiRoot === true ? diskUsageSummary : null);
  }
  if (path === "/api/disk-usage/tree" && request.method === "GET") {
    const rootIndex = url.searchParams.get("rootIndex");
    const allowed = ["rootIndex", "node", "cursor", "maxNodes", "maxBytes"];
    if (!["0", "1"].includes(rootIndex)
        || url.searchParams.get("maxNodes") !== "400000"
        || url.searchParams.get("maxBytes") !== "33554432"
        || [...url.searchParams.keys()].some(key => !allowed.includes(key))) {
      return json(response, 400, { error: "tree requires rootIndex and explicit node/byte budgets, without view parameters" });
    }
    if (scenario.diskUsageResultFailure) return json(response, 503, { error: "fixture tree read failure" });
    if (scenario.diskUsageMultiRoot === true) {
      if (rootIndex === "1") {
        await delay(normalizeDelay(scenario.diskUsageRootIndexOneDelayMs));
      }
      const layout = diskUsageQueryBuffer(rootIndex, url.searchParams.get("node"), url.searchParams.get("cursor"));
      if (!layout) {
        return json(response, 404, { error: "recorded disk layout was not found" });
      }
      response.writeHead(200, {
        "Content-Type": "application/octet-stream",
        "Cache-Control": "no-store"
      });
      if (!response.destroyed && !response.writableEnded) {
        response.end(layout);
      }
      return;
    }
    response.writeHead(200, {
      "Content-Type": "application/octet-stream",
      "Cache-Control": "no-store"
    });
    response.end(emptyDiskUsageLayout);
    return;
  }
  if (path === "/api/disk-usage/scan" && request.method === "POST") {
    await readBody(request);
    if (scenario.diskUsageScanFailure) {
      return json(response, 503, { error: "fixture scan submission failure" });
    }
    if (!scenario.diskUsageScanLifecycle) {
      return json(response, 409, { error: "scan lifecycle scenario is not enabled" });
    }
    const createdAt = now();
    diskScanOperation = {
      id: `d5${(++diskScanSequence).toString(16).padStart(30, "0")}`,
      kind: "disk-usage.scan",
      domainKey: null,
      subject: { targetId: null, scope: "global", mode: "fast", targetPath: null },
      state: "running",
      configurationGeneration: "1",
      stateRevision: "1",
      attemptNumber: 1,
      maximumAttempts: 1,
      createdAt,
      updatedAt: createdAt,
      completedAt: null,
      cancelRequested: false,
      progress: null
    };
    commitOperationsValue();
    diskScanCompletionTimer = setTimeout(() => {
      diskScanCompletionTimer = null;
      const completedAt = now();
      diskScanOperation = {
        ...diskScanOperation,
        state: "succeeded",
        stateRevision: "2",
        updatedAt: completedAt,
        completedAt
      };
      diskUsageSummary.completedAt = completedAt;
      diskUsageSummary.totalBytes = 92_000_000;
      diskUsageSummary.scannedBytes = 92_000_000;
      diskUsageLayouts.set("root-c", createDiskUsageLayout("C:\\", 0, 19_000_000, [
        diskUsageTile(0, -1, 0, 19_000_000, "", true, 17),
        diskUsageTile(1, 0, 1, 9_000_000, "Projects", true, 4),
        diskUsageTile(5, 1, 2, 3_000_000, "Archive", true, 2)
      ]));
      commitOperationsValue();
    }, Math.max(0, Math.min(5000, Math.floor(Number(scenario.diskUsageScanCompletionDelayMs) || 900))));
    return json(response, 200, diskScanOperation);
  }
  if (path === "/api/settings/report-rules" && request.method === "GET") {
    return json(response, 200, { rules: reportRules });
  }
  if (path === "/api/settings/app") {
    if (scenario.settings === "error" && request.method === "GET") {
      return text(response, 503, "settings unavailable");
    }
    if (request.method === "GET") {
      const result = appSettingsResult();
      await delay(normalizeDelay(scenario.settingsGetDelayMs));
      return json(response, 200, result);
    }
    if (request.method === "PATCH") {
      const patch = JSON.parse(await readBody(request));
      if (patch.changes?.appearance || patch.changes?.notifications || ["smartMonitoringMode", "monitorRefreshIntervalMs", "resourceTableRefreshIntervalMs", "localSystemRefreshIntervalMs"]
        .some(key => key in (patch.changes?.performance ?? {}))) {
        return json(response, 400, { error: "Presentation does not belong to the backend." });
      }
      const updateProblem = invalidUpdateSettingsPatch(patch.changes?.update);
      if (updateProblem) return json(response, 400, { error: updateProblem });
      await delay(normalizeDelay(scenario.settingsPatchDelayMs));
      if (scenario.settingsConflict === true
        || patch.expectedRevision !== scenario.settingsRevision) {
        return json(response, 409, appSettingsResult("revisionConflict"));
      }
      mergeRecord(appSettings, patch.changes);
      scenario.settingsRevision = nextSettingsRevision(scenario.settingsRevision);
      scenario.debugMode = appSettings.debug?.debugModeEnabled === true;
      appSettingsRuntimeDisposition = scenario.settingsRuntimeDisposition;
      return json(response, settingsResultStatus(appSettingsRuntimeDisposition), appSettingsResult());
    }
    if (request.method === "PUT") {
      const settings = JSON.parse(await readBody(request));
      const updateProblem = invalidUpdateSettingsPatch(settings.update);
      if (updateProblem) return json(response, 400, { error: updateProblem });
      for (const key of Object.keys(appSettings)) delete appSettings[key];
      Object.assign(appSettings, settings);
      scenario.settingsRevision = nextSettingsRevision(scenario.settingsRevision);
      scenario.debugMode = appSettings.debug?.debugModeEnabled === true;
      appSettingsRuntimeDisposition = scenario.settingsRuntimeDisposition;
      return json(response, settingsResultStatus(appSettingsRuntimeDisposition), appSettingsResult());
    }
    return json(response, 405, { error: "settings method not supported" });
  }
  if (path === "/api/updates/product/status" && request.method === "GET") {
    return scenario.productUpdateStatus === "invalid"
      ? json(response, 503, { errorCode: "update.statusUnavailable" })
      : json(response, 200, currentProductUpdateStatus());
  }
  if (path === "/api/settings/app/reapply" && request.method === "POST") {
    if (scenario.settingsRuntimeDisposition === "savedNotApplied") return json(response, 500, { error: "runtime-plan-publication-failed" });
    appSettingsRuntimeDisposition = scenario.settingsRuntimeDisposition;
    return json(response, settingsResultStatus(appSettingsRuntimeDisposition), appSettingsResult());
  }
  if (path === "/api/targeted-recordings" && request.method === "GET") {
    return json(response, 200, [...targetedRecordings.values()].map((entry) => entry.header));
  }
  if (path === "/api/targeted-recordings" && request.method === "POST") {
    const body = JSON.parse(await readBody(request));
    const softwareId = typeof body.softwareId === "string" ? body.softwareId : "frontend-harness.software";
    const softwareName = softwareId === "frontend-harness.software" ? "示例软件" : softwareId;
    const startedAt = now();
    const id = `recording-fixture-${++targetedRecordingSequence}`;
    const entry = {
      header: {
        id,
        softwareId,
        softwareName,
        startedAt,
        endedAt: null,
        maximumDurationSeconds: body.maximumDurationSeconds ?? 600,
        status: "recording",
        stopReason: null,
        incomplete: false
      },
      frameIntervals: [],
      fpsDistribution: [],
      resourceSamples: []
    };
    targetedRecordings.set(id, entry);
    if (targetedRecordingCompletionTimer) clearTimeout(targetedRecordingCompletionTimer);
    targetedRecordingCompletionTimer = setTimeout(() => {
      targetedRecordingCompletionTimer = null;
      completeTargetedRecording(id, "max-duration");
    }, 15_000);
    return json(response, 200, entry.header);
  }
  const targetedRecordingMatch = path.match(/^\/api\/targeted-recordings\/([^/]+)$/);
  const targetedRecordingStopMatch = path.match(/^\/api\/targeted-recordings\/([^/]+)\/stop$/);
  if (targetedRecordingStopMatch && request.method === "POST") {
    const entry = targetedRecordings.get(decodeURIComponent(targetedRecordingStopMatch[1]));
    if (!entry) return json(response, 404, { error: "recording not found" });
    if (targetedRecordingCompletionTimer) clearTimeout(targetedRecordingCompletionTimer);
    targetedRecordingCompletionTimer = null;
    completeTargetedRecording(entry.header.id, "user");
    return json(response, 200, entry.header);
  }
  if (targetedRecordingMatch && request.method === "GET") {
    const entry = targetedRecordings.get(decodeURIComponent(targetedRecordingMatch[1]));
    return entry ? json(response, 200, {
      recording: entry.header,
      summary: null,
      frameIntervals: entry.frameIntervals,
      fpsDistribution: entry.fpsDistribution,
      resourceSamples: entry.resourceSamples
    }) : json(response, 404, { error: "recording not found" });
  }
  if (targetedRecordingMatch && request.method === "DELETE") {
    targetedRecordings.delete(decodeURIComponent(targetedRecordingMatch[1]));
    response.writeHead(204);
    response.end();
    return;
  }
  if (path.startsWith("/api/targeted-recordings/") && request.method === "GET") {
    return json(response, 200, { recording: {}, summary: null, frameIntervals: [], fpsDistribution: [], resourceSamples: [] });
  }
  if (path === "/api/metrics/catalog") {
    return json(response, 200, [...metricCatalog,
      ...(scenario.gpuPressureFixture ? [
        metric("gpu.0.usage", "GPU 占用", "GPU", "%"),
        metric("gpu.0.vram", "GPU 显存", "GPU", "B"),
        metric("gpu.0.vramPercent", "GPU 显存占用", "GPU", "%")] : []),
      ...(scenario.metricCatalogAddresses === "reading-source" ? [readingSourceAddress()] : [])]);
  }
  if (path === "/api/resource-breakdown/fields/catalog" && request.method === "GET") {
    return json(response, 200, processFieldCatalog);
  }
  if (path === "/api/metrics/fields/snapshot") {
    let selections;
    try { selections = parseMetricFieldSelections(url); }
    catch { return json(response, 400, { error: "One or more subscription queries are invalid." }); }
    return json(response, 200, metricFieldsSnapshot(selections));
  }
  if (path === "/api/metrics/fields/catalog") {
    return json(response, 200, metricFieldCatalog());
  }
  if (path === "/api/metrics/snapshot") {
    return json(response, 200, metricSnapshot(url.searchParams.getAll("ids")));
  }
  if (path === "/api/metrics/gpu-specialized") {
    return json(response, 200, {
      version: 2,
      capturedAt: now(),
      adapters: []
    });
  }
  if (path === "/api/gpu/performance-overrides") {
    return json(response, 200, {
      scoresByGpuId: {},
      updatedAt: now(),
      storagePath: "frontend-harness"
    });
  }
  if (path === "/api/gpu/performance-scores") {
    return json(response, 200, {
      capturedAt: now(),
      gpus: scenario.unknownGpuScore ? [{
        gpuId: "gpu:0",
        index: 0,
        name: "Unlisted GPU",
        defaultPerformanceScore: 0,
        performanceScore: 0,
        rasterPerformanceScore: 0,
        hasPerformanceOverride: false,
        isIntegrated: false,
        source: "unavailable:steel-nomad-dx12",
        matchedPreset: null,
        isPresetMatch: false
      }] : [],
      storagePath: "frontend-harness"
    });
  }
  if (path === "/api/resource-monitor/snapshot") {
    const capturedAt = resourceSlotCapturedAt;
    if (!resourceMeasuresKnown(url)) return json(response, 400, { error: "One or more subscription queries are invalid." });
    if (url.searchParams.get("scope") === "details") return json(response, 200, resourceProcessDetails(url, capturedAt));
    const breakdown = resourceBreakdown(
      capturedAt,
      new Set(normalizeMetricIds(url.searchParams.getAll("processDetails")).map(id => id.toLowerCase())),
      url.searchParams.getAll("measures"),
      url.searchParams.getAll("modes"), { ids: url.searchParams.getAll("ids") });
    return json(response, 200, {
      version: 10,
      capturedAt: breakdown.capturedAt,
      breakdown
    });
  }
  if (path === "/api/resource-breakdown/snapshot") {
    return json(
      response,
      200,
      resourceBreakdown(
        resourceSlotCapturedAt,
        new Set(normalizeMetricIds(url.searchParams.getAll("processDetails")).map(id => id.toLowerCase())), [], url.searchParams.getAll("modes"),
        { ids: url.searchParams.getAll("ids") }));
  }
  if (path === "/api/resource-facts/snapshot") {
    if (!resourceFactItemsKnown(url)) return json(response, 400, { error: "unknown resource fact item" });
    return json(response, 200, resourceFacts(url, resourceSlotCapturedAt));
  }
  if (path === "/api/local-system/status") {
    return json(response, 200, { capturedAt: now(), bootedAt: now(), uptimeSeconds: 3600 });
  }
  if (path === "/api/cpu/topology") {
    return json(response, 200, scenario.cpuTopologyEmpty ? null : fixtureCpuTopology());
  }
  if (path === "/api/cpu/residency") {
    return json(response, 200, fixtureCpuResidency());
  }
  if (path === "/api/cpu/baseline-ratio") {
    if (request.method === "GET") {
      return json(response, 200, scenario.cpuTopologyEmpty ? null : cpuBaselineSettings());
    }
    if (request.method === "PUT" || request.method === "DELETE") {
      if (request.method === "PUT") {
        const ratio = JSON.parse(await readBody(request)).ratio;
        if (!(Number.isFinite(ratio) && ratio > 0 && ratio <= 1)) {
          return json(response, 400, { error: "CPU baseline ratio must be greater than zero and at most one, with a finite reciprocal." });
        }
        cpuBaselineOverrideRatio = ratio;
      } else {
        cpuBaselineOverrideRatio = null;
      }
      return json(response, 200, {
        settings: cpuBaselineSettings(),
        savedOverrideRatio: cpuBaselineOverrideRatio,
        runtimeApplicationDisposition: "applied"
      });
    }
  }
  if (path === "/api/cpu/topology/exclusive-bindings") {
    return json(response, 200, {
      capturedAt: now(),
      cpuName: "Frontend Harness CPU",
      bindings: []
    });
  }
  if (path === "/api/device-topology/state") {
    await delay(normalizeDelay(scenario.deviceTopologyDelayMs));
    return json(response, 200, fixtureDeviceTopologyState(now()));
  }
  if (path === "/api/adapters/resource-manager/scheduling") {
    return json(response, 200, {
      cpuGrade: scenario.cpuGrade,
      gpuGrade: scenario.gpuGrade,
      cpuUpdatedAt: now(),
      gpuUpdatedAt: now(),
      cpuPolicyId: "frontend-harness",
      gpuPolicyId: "frontend-harness",
      cpuReason: "frontend harness",
      gpuReason: "frontend harness",
      sources: []
    });
  }
  if (path === "/api/gpu-placement/process-history/observe" && request.method === "POST") {
    return json(response, 200, { softwareId: mockSoftware.id, softwareName: mockSoftware.name, processes: [] });
  }
  if (path === "/api/gpu-placement/software/frontend-harness.software") {
    return json(response, 200, {
      softwareId: mockSoftware.id,
      softwareName: mockSoftware.name,
      softwarePolicy: {
        softwareId: mockSoftware.id,
        softwareName: mockSoftware.name,
        allowedProviders: [],
        startupTargetGpu: "system_default_gpu",
        runtimeHotSwitchEnabled: false,
        preferredRuntimeSwitchMethod: "future_frame_takeover",
        baseScoreOverride: null,
        processOverrideAllowed: false,
        cpuSchedulingExcluded: false,
        memorySchedulingExcluded: false,
        gpuSchedulingExcluded: false,
        cpuMaximumOccupancyMode: "all_cores",
        cpuManualExclusivePositionIds: [],
        cpuManualLockedPositionIds: []
      },
      processPolicies: [],
      processHistory: { softwareId: mockSoftware.id, softwareName: mockSoftware.name, processes: [] },
      targetInventory: { state: "current", exactTargets: [] },
      processCapabilities: []
    });
  }
  if (path === "/api/operations") {
    return json(response, 200, currentOperationsValue());
  }
  const cancelMatch = path.match(/^\/api\/operations\/([0-9a-f]{32})\/cancel$/);
  if (cancelMatch && request.method === "POST") {
    const operation = fixtureOperations().find((candidate) => candidate.id === cancelMatch[1]);
    if (!operation) {
      return json(response, 404, { error: "operation not found" });
    }
    scenario.operationCanceled = true;
    commitOperationsValue();
    return json(response, 200, fixtureOperations()[0]);
  }
  if (path === "/api/components") {
    return json(response, 200, [componentFixture(), componentFixture("shared-webview2-runtime")]);
  }
  if (path === "/api/components/hardware-bridge/versions") {
    return json(response, 200, componentVersionFixture());
  }
  if (path === "/api/controlled") {
    return json(response, 200, []);
  }
  if (path === "/api/browser-runtimes") {
    const runtime = {
      id: "webview2:fixture",
      name: "Fixture WebView2 Runtime",
      kind: "WebView2Runtime",
      version: "1.0.0",
      executablePath: "C:/Fixture/WebView2/msedgewebview2.exe",
      runtimeDirectory: "C:/Fixture/WebView2",
      source: "Managed",
      nativeWebView2Compatible: true,
      selected: true
    };
    return json(response, 200, {
      sharedRuntime: runtime,
      browserFallback: null,
      candidates: [runtime],
      installComponentId: "shared-webview2-runtime",
      capturedAt: now()
    });
  }
  if (path === "/api/software") {
    return json(response, 200, [{
      ...mockSoftware,
      issues: scenario.softwareIssues ? mockSoftware.issues : []
    }]);
  }
  return json(response, 404, { error: "frontend harness route not configured" });
}

async function saveStartupModuleSelection(id, settings) {
  // Minimal API JSON binding rejects undeclared DTO fields before the handler.
  if (settings && typeof settings === "object"
    && Object.keys(settings).some(key => !["softwareId", "moduleIds"].includes(key))) {
    return { statusCode: 400, body: null };
  }
  if (!id || id !== mockSoftware.id || !settings || typeof settings !== "object"
    || settings.softwareId !== id || !Array.isArray(settings.moduleIds)
    || settings.moduleIds.some(id => !["performance-overlay-preload", "performance-overlay-vulkan"].includes(id))) {
    return { statusCode: 400, body: { code: "startup-modules.invalid-selection" } };
  }
  await delay(normalizeDelay(scenario.overlaySaveDelayMs));
  if (scenario.overlaySaveFailure) return { statusCode: 503, body: { code: "fixture-startup-module-save-failed" } };
  const saved = { softwareId: id, moduleIds: [...new Set(settings.moduleIds)].sort() };
  startupModuleSelections.set(id.toLowerCase(), saved);
  return { statusCode: 200, body: saved };
}

function fixtureOperations() {
  const active = {
    id: "000000000000000000000000000000a1",
    kind: "component.install",
    domainKey: "component.frontend-harness",
    subject: { targetId: "frontend-harness", scope: null, mode: null, targetPath: null },
    state: scenario.operationCanceled ? "canceled" : scenario.operationQueued ? "queued" : "running",
    configurationGeneration: "1",
    stateRevision: scenario.operationCanceled ? "2" : scenario.operationQueued ? "0" : "1",
    attemptNumber: 1,
    maximumAttempts: 3,
    createdAt: "2026-08-22T12:00:00.000Z",
    updatedAt: scenario.operationCanceled
      ? "2026-08-22T12:00:03.000Z"
      : "2026-08-22T12:00:01.000Z",
    completedAt: scenario.operationCanceled ? "2026-08-22T12:00:03.000Z" : null,
    cancelRequested: scenario.operationCanceled,
    progress: scenario.operationCanceled || scenario.operationQueued ? null : {
      sequence: "1",
      percent: 40,
      bytesDone: "4096",
      bytesTotal: "10240",
      speedBytesPerSecond: "1024",
      stage: "Download"
    }
  };
  return [
    active,
    {
      id: "000000000000000000000000000000a2",
      kind: "software.uninstall",
      domainKey: "software.frontend-harness",
      subject: { targetId: "frontend-harness", scope: null, mode: null, targetPath: null },
      state: "succeeded",
      configurationGeneration: "1",
      stateRevision: "3",
      attemptNumber: 1,
      maximumAttempts: 3,
      createdAt: "2026-08-22T11:50:00.000Z",
      updatedAt: "2026-08-22T11:50:03.000Z",
      completedAt: "2026-08-22T11:50:03.000Z",
      cancelRequested: false,
      progress: null,
      resultCode: "RemovedManagedRoot",
      resultMessage: { domain: 4, code: 13, args: [] },
      errorMessage: null
    },
    {
      ...active, id: "000000000000000000000000000000a3", domainKey: "component:failed-runtime",
      state: "failed", completedAt: active.updatedAt, cancelRequested: false, progress: null,
      errorMessage: scenario.componentPayloadFailure
        ? { domain: 1, code: 24, args: [] }
        : { domain: 6, code: 1, args: [] }
    }
  ];
}

function fixtureManyOperations() {
  return Array.from({ length: 24 }, (_, index) => ({
    id: `0000000000000000000000000000${(index + 16).toString(16).padStart(4, "0")}`,
    kind: "component.install",
    domainKey: `component.frontend-harness.${index + 1}`,
    subject: { targetId: `frontend-harness-${index + 1}`, scope: null, mode: null, targetPath: null },
    state: "succeeded",
    configurationGeneration: "1",
    stateRevision: "3",
    attemptNumber: 1,
    maximumAttempts: 3,
    createdAt: "2026-08-22T11:40:00.000Z",
    updatedAt: "2026-08-22T11:40:03.000Z",
    completedAt: "2026-08-22T11:40:03.000Z",
    cancelRequested: false,
    progress: null
  }));
}

// 一长串接口，让设备清单需要滚动。
function fixtureManyHdmiPorts(count) {
  return Array.from({ length: count }, (_, index) => ({
    ...fixtureHdmiPort(),
    id: `fixture-hdmi-many-${index + 1}`,
    deviceId: `DISPLAY\\FIXTURE_HDMI_MANY_${index + 1}`,
    topologyPath: { domain: 5, code: 64, args: [`root/fixture-hdmi-many-${index + 1}`] }
  }));
}

function fixtureHdmiPort() {
  return {
    id: "fixture-hdmi-1",
    isPhysicalConnector: true,
    displayName: "Fixture HDMI",
    connectorKind: "hdmi",
    busKind: "display",
    transport: { code: null, reason: "not-queried", ruleId: null, evidence: [] },
    hardwareKind: "display-output",
    protocol: "HDMI 2.1",
    speed: "48 Gbps FRL，支持 7680 × 4320、可变刷新率与高动态范围传输",
    physicalMaximumSpeed: "48 Gbps",
    deviceId: "DISPLAY\\FIXTURE_HDMI_1",
    pnpClass: null,
    manufacturer: "Frontend Harness",
    service: null,
    status: "OK",
    confidence: { domain: 5, code: 24, args: [] },
    source: { domain: 5, code: 35, args: [] },
    upstreamDeviceId: null,
    upstreamDisplayName: null,
    topologyPath: { domain: 5, code: 64, args: ["root/fixture-hdmi-1"] },
    nativeParentDeviceId: null,
    nativeParentDisplayName: null,
    locationInfo: null,
    locationPaths: [],
    classGuid: null,
    display: scenario.displayEdid === "off" ? null : fixtureDisplayEdid(),
    network: null,
    idResolution: null,
    advancedInterconnect: null,
    usb: null,
    hardwareIds: [],
    compatibleIds: [],
    devNodeStatus: null,
    problemCode: null,
    hid: null,
    camera: null,
    smartDevice: null,
    storage: null
  };
}

function diskUsageTile(nodeId, parentNodeId, depth, sizeBytes, name, isDirectory, fileCount) {
  return {
    nodeId,
    parentNodeId,
    depth,
    sizeBytes,
    name,
    isDirectory,
    fileCount,
    x: 0,
    y: 0,
    width: 1,
    height: 1
  };
}

function createDiskUsageLayout(rootPath, rootNodeId, rootSizeBytes, tiles, nextCursor = -1) {
  const children = new Map();
  for (const tile of tiles) {
    if (tile.nodeId === rootNodeId) continue;
    const siblings = children.get(tile.parentNodeId) ?? [];
    siblings.push(tile);
    children.set(tile.parentNodeId, siblings);
  }
  const nextSiblings = new Map();
  for (const siblings of children.values()) {
    for (let index = 0; index < siblings.length; index++) nextSiblings.set(siblings[index].nodeId, siblings[index + 1]?.nodeId ?? -1);
  }
  tiles = tiles.map(tile => ({
    ...tile,
    childSizeTotal: tile.childSizeTotal ?? (children.get(tile.nodeId) ?? []).reduce((sum, child) => sum + child.sizeBytes, 0),
    firstChildId: tile.firstChildId ?? children.get(tile.nodeId)?.[0]?.nodeId ?? -1,
    nextSiblingId: tile.nextSiblingId ?? nextSiblings.get(tile.nodeId) ?? -1
  }));
  const encodedNames = tiles.map((tile) => Buffer.from(tile.name, "utf8"));
  const rootPathBytes = Buffer.from(rootPath, "utf8");
  const count = tiles.length;
  const columnsBytes = (8 * count * 2) + (4 * count * 5) + count;
  const padding = (4 - (columnsBytes % 4)) % 4;
  const namesBytes = encodedNames.reduce((total, name) => total + name.length, 0);
  const buffer = Buffer.alloc(64 + columnsBytes + padding + (4 * count) + namesBytes + rootPathBytes.length);
  buffer.writeUInt32LE(0x55444d52, 0);
  buffer.writeUInt32LE(3, 4);
  buffer.writeUInt32LE(count, 8);
  buffer.writeInt32LE(rootNodeId, 12);
  buffer.writeInt32LE(nextCursor, 16);
  buffer.writeDoubleLE(rootSizeBytes, 24);
  buffer.writeUInt32LE(rootPathBytes.length, 60);

  let offset = 64;
  for (const field of ["sizeBytes", "childSizeTotal"]) {
    for (let index = 0; index < count; index++) {
      buffer.writeDoubleLE(tiles[index][field], offset + (index * 8));
    }
    offset += 8 * count;
  }
  for (const field of ["nodeId", "parentNodeId", "fileCount", "firstChildId", "nextSiblingId"]) {
    for (let index = 0; index < count; index++) {
      buffer.writeInt32LE(tiles[index][field], offset + (index * 4));
    }
    offset += 4 * count;
  }
  for (let index = 0; index < count; index++) {
    buffer[offset + index] = tiles[index].isDirectory ? 1 : 0;
  }
  offset += count + padding;
  for (let index = 0; index < count; index++) {
    buffer.writeUInt32LE(encodedNames[index].length, offset + (index * 4));
  }
  offset += 4 * count;
  for (const name of encodedNames) {
    name.copy(buffer, offset);
    offset += name.length;
  }
  rootPathBytes.copy(buffer, offset);
  return buffer;
}

function diskUsageQueryBuffer(rootIndex, node, cursor) {
  const cRoot = rootIndex === "0";
  const rootNodeId = cRoot ? 0 : 2;
  const projectId = cRoot ? 1 : 3;
  const archiveId = cRoot ? 5 : 4;
  const rootPath = cRoot ? "C:\\" : "D:\\";
  const full = diskUsageLayouts.get(cRoot ? "root-c" : "root-d");
  const projectSize = cRoot ? full.readDoubleLE(24) - 10_000_000 : 22_000_000;
  const archiveSize = cRoot ? 3_000_000 : 9_000_000;
  if (node === String(projectId)) return createDiskUsageLayout(`${rootPath}Projects`, projectId, projectSize, [
    diskUsageTile(projectId, rootNodeId, 0, projectSize, "Projects", true, 4),
    diskUsageTile(archiveId, projectId, 1, archiveSize, "Archive", true, 2)
  ]);
  if (node === String(archiveId)) return createDiskUsageLayout(`${rootPath}Projects\\Archive`, archiveId, archiveSize, [
    diskUsageTile(archiveId, projectId, 0, archiveSize, "Archive", true, 2)
  ]);
  if (node !== null && node !== String(rootNodeId)) return undefined;
  if (scenario.diskUsagePaged && cRoot && cursor === null) return createDiskUsageLayout(rootPath, rootNodeId, full.readDoubleLE(24), [
    diskUsageTile(rootNodeId, -1, 0, full.readDoubleLE(24), "", true, 17),
    { ...diskUsageTile(projectId, rootNodeId, 1, projectSize, "Projects", true, 4), firstChildId: archiveId, childSizeTotal: archiveSize }
  ], archiveId);
  if (cursor !== null && cursor !== String(archiveId)) return undefined;
  return full;
}

function trackRequest(response, path, query) {
  const telemetry = requestTelemetryFor(path);
  telemetry.physicalStarted += 1;
  telemetry.physicalInFlight += 1;
  telemetry.physicalMaxInFlight = Math.max(
    telemetry.physicalMaxInFlight,
    telemetry.physicalInFlight);
  telemetry.started += 1;
  telemetry.inFlight += 1;
  telemetry.maxInFlight = Math.max(telemetry.maxInFlight, telemetry.inFlight);
  telemetry.startTimes.push(Date.now());
  telemetry.queries.push(query);
  telemetry.inFlightByQuery[query] = (telemetry.inFlightByQuery[query] ?? 0) + 1;
  telemetry.maxInFlightByQuery[query] = Math.max(
    telemetry.maxInFlightByQuery[query] ?? 0,
    telemetry.inFlightByQuery[query]);

  let settled = false;
  const settle = (completed) => {
    if (settled) {
      return;
    }
    settled = true;
    telemetry.physicalInFlight = Math.max(
      0,
      telemetry.physicalInFlight - 1);
    if (completed) {
      telemetry.physicalCompleted += 1;
    } else {
      telemetry.physicalAborted += 1;
    }
    telemetry.inFlight = Math.max(0, telemetry.inFlight - 1);
    telemetry.inFlightByQuery[query] = Math.max(
      0,
      (telemetry.inFlightByQuery[query] ?? 1) - 1);
    if (completed) {
      telemetry.completed += 1;
      telemetry.statuses.push(response.statusCode);
    } else {
      telemetry.aborted += 1;
    }
  };
  response.once("finish", () => settle(true));
  response.once("close", () => settle(response.writableFinished));
}

function requestTelemetryFor(path) {
  return requestTelemetry[path] ??= {
    physicalStarted: 0,
    physicalCompleted: 0,
    physicalAborted: 0,
    physicalInFlight: 0,
    physicalMaxInFlight: 0,
    logicalStarted: 0,
    logicalRetired: 0,
    logicalInFlight: 0,
    logicalMaxInFlight: 0,
    started: 0,
    completed: 0,
    aborted: 0,
    inFlight: 0,
    maxInFlight: 0,
    startTimes: [],
    queries: [],
    statuses: [],
    inFlightByQuery: {},
    maxInFlightByQuery: {},
    publications: 0,
    publicationTimes: [],
    publicationsByQuery: {},
    publicationTimesByQuery: {},
    subscriptionSets: []
  };
}

function resetRequestTelemetry() {
  for (const [path, telemetry] of Object.entries(requestTelemetry)) {
    if (telemetry.inFlight === 0) {
      delete requestTelemetry[path];
      continue;
    }

    telemetry.started = telemetry.inFlight;
    telemetry.completed = 0;
    telemetry.aborted = 0;
    telemetry.maxInFlight = telemetry.inFlight;
    telemetry.startTimes = [];
    telemetry.queries = Object.entries(telemetry.inFlightByQuery)
      .flatMap(([query, count]) => Array.from({ length: count }, () => query));
    telemetry.statuses = [];
    telemetry.maxInFlightByQuery = { ...telemetry.inFlightByQuery };
    telemetry.publications = 0;
    telemetry.publicationTimes = [];
    telemetry.publicationsByQuery = {};
    telemetry.publicationTimesByQuery = {};
    telemetry.physicalStarted = telemetry.physicalInFlight;
    telemetry.physicalCompleted = 0;
    telemetry.physicalAborted = 0;
    telemetry.physicalMaxInFlight = telemetry.physicalInFlight;
    telemetry.logicalStarted = telemetry.logicalInFlight;
    telemetry.logicalRetired = 0;
    telemetry.logicalMaxInFlight = telemetry.logicalInFlight;
  }
}

function normalizeDelay(value) {
  return Number.isFinite(value) && value > 0
    ? Math.min(2000, Math.floor(value))
    : 0;
}

function trustedAccessSnapshot() {
  const defaultKeyId = `default-${trustedAccessDefaultSerial}`;
  return {
    keys: [{
      id: defaultKeyId, name: "Native default", purpose: "control", scopes: trustedAccessDefaultScopes,
      createdAt: "2026-10-25T08:00:00.000Z", isDefault: true
    }],
    defaultEnabled: true,
    defaultKeyId,
    availableScopes: trustedAccessAvailableScopes
  };
}

// 和后端 POST /api/access-keys/default 一样：关闭时撤销默认密钥，之后界面的请求被拒绝，
// 已打开的订阅流随之结束；启用或轮换时生成新的默认密钥。
function setTrustedAccessDefault(body) {
  if (!body || typeof body.enabled !== "boolean" || typeof body.rotate !== "boolean"
    || Object.keys(body).some(key => key !== "enabled" && key !== "rotate") || (!body.enabled && body.rotate)) {
    return { statusCode: 400, body: { error: "default-key-request-invalid" } };
  }
  if (!body.enabled) {
    scenario.trustedAccess = "default-key-off";
    for (const stream of [...subscriptionChannelResponses]) stream.end();
    return { statusCode: 200, body: { defaultEnabled: false, defaultKeyId: null } };
  }
  if (scenario.trustedAccess === "default-key-off" || body.rotate) trustedAccessDefaultSerial += 1;
  scenario.trustedAccess = "ready";
  return { statusCode: 200, body: { defaultEnabled: true, defaultKeyId: `default-${trustedAccessDefaultSerial}` } };
}

function delay(milliseconds) {
  return new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));
}

// 外观、动效和刷新选择是前端偏好（docs/frontend_preferences_2026-10-24.md），后端设置不携带，
// mock 也就没有这些开关；旅程改用 frontendPreferencesFixture.js 写前端缓存。传入旧开关直接报错，免得静默无效。
const frontendPreferenceScenarioKeys = ["settingsTheme", "settingsUiStyle", "animations", "localSystemIntervalMs", "performanceMonitorRefreshIntervalMs"];
const scenarioChoices = {
  metricCatalogAddresses: ["none", "reading-source"],
  settingsRuntimeDisposition: ["committedAndApplied", "committedWithCapabilityConstraints", "committedWithDeliveryFailures", "savedNotApplied"]
};

function applyScenarioPatch(patch) {
  const presentation = frontendPreferenceScenarioKeys.filter(key => Object.hasOwn(patch, key));
  if (presentation.length > 0) throw new Error(`Frontend preferences are not backend scenario values: ${presentation.join(", ")}`);
  if (Object.hasOwn(patch, "trustedAccess") && !trustedAccessScenarios.has(patch.trustedAccess)) {
    throw new Error(`Unknown trustedAccess scenario: ${JSON.stringify(patch.trustedAccess)}`);
  }
  if (Object.hasOwn(patch, "productUpdateStatus")) validateProductUpdateStatusScenario(patch.productUpdateStatus);
  for (const [key, allowed] of Object.entries(scenarioChoices)) {
    if (Object.hasOwn(patch, key) && !allowed.includes(patch[key])) throw new Error(`Unknown ${key} scenario: ${JSON.stringify(patch[key])}`);
  }
  if (patch.displayEdid === "typex" && !recordedTypeXState) throw new Error("TypeX requires real RM_EDID_UI_TYPEX_STATE_JSON output; no fallback");
  Object.assign(scenario, patch);
  settleMockValueSlots(patch);
  if (Object.hasOwn(patch, "operations")
    || Object.hasOwn(patch, "operationCanceled")
    || Object.hasOwn(patch, "operationQueued")
    || Object.hasOwn(patch, "componentPayloadFailure")) {
    commitOperationsValue();
  }
  if (Number.isSafeInteger(patch.gpuOverflowUsagePercent)
    && patch.gpuOverflowUsagePercent >= 1
    && patch.gpuOverflowUsagePercent <= 100) {
    appSettings.performance.gpuOverflowUsagePercent = patch.gpuOverflowUsagePercent;
  }
  if (Number.isSafeInteger(patch.gpuOverflowDedicatedMemoryPercent)
    && patch.gpuOverflowDedicatedMemoryPercent >= 1
    && patch.gpuOverflowDedicatedMemoryPercent <= 100) {
    appSettings.performance.gpuOverflowDedicatedMemoryPercent = patch.gpuOverflowDedicatedMemoryPercent;
  }
  if (typeof patch.debugMode === "boolean") {
    appSettings.debug.debugModeEnabled = patch.debugMode;
  }
  if (Object.hasOwn(patch, "productUpdateStatus")) publishProductUpdateStatus();
}

// 后端合同：update.mode 三档，间隔检查开关，间隔 24–168 小时；旧的 autoUpdateEnabled 与已取消的联网等待开关不存在。
function invalidUpdateSettingsPatch(update) {
  if (update === undefined) return null;
  if (!isRecord(update)) return "update must be an object";
  for (const key of Object.keys(update)) {
    if (!["mode", "periodicCheckEnabled", "checkIntervalHours"].includes(key)) return `unknown update setting ${key}`;
  }
  if (Object.hasOwn(update, "mode") && !["off", "notify", "automatic"].includes(update.mode)) return "invalid update.mode";
  if (Object.hasOwn(update, "periodicCheckEnabled") && typeof update.periodicCheckEnabled !== "boolean") return "invalid update.periodicCheckEnabled";
  if (Object.hasOwn(update, "checkIntervalHours") && !(Number.isSafeInteger(update.checkIntervalHours)
    && update.checkIntervalHours >= 24 && update.checkIntervalHours <= 168)) return "update.checkIntervalHours must be 24-168";
  return null;
}

const utcTimePattern = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d{1,7})?)?(?:Z|\+00:00)$/;
function validateProductUpdateStatusScenario(value) {
  if (value === "invalid") return;
  if (!isRecord(value)) throw new Error("productUpdateStatus must be \"invalid\" or an object of status fields");
  for (const [key, field] of Object.entries(value)) {
    if (!Object.hasOwn(defaultProductUpdateStatus, key)) throw new Error(`Unknown productUpdateStatus field: ${key}`);
    const valid = key === "stage" ? typeof field === "string" && field.length > 0
      : key === "errorParameters" ? field === null || (isRecord(field) && Object.values(field).every(item => typeof item === "string"))
        : key === "onlineTimeSinceLastCheckSeconds" ? field === null || (typeof field === "number" && Number.isFinite(field) && field >= 0)
          : key === "automaticCheckStatus" ? ["disabled", "waitingForInternet", "checking", "scheduled", "idle"].includes(field)
          : key === "incompatibleVersions" ? Array.isArray(field) && field.every(item => typeof item === "string")
          : key === "lastCheckedAt" || key === "nextCheckAt" || key === "updatedAt" ? field === null || (typeof field === "string" && utcTimePattern.test(field))
            : field === null || (typeof field === "string" && field.length > 0);
    if (!valid) throw new Error(`Invalid productUpdateStatus.${key}: ${JSON.stringify(field)}`);
  }
}

function currentProductUpdateStatus() {
  return scenario.productUpdateStatus === "invalid"
    ? { invalid: true }
    : { ...defaultProductUpdateStatus, ...structuredClone(scenario.productUpdateStatus) };
}

function publishProductUpdateStatus() {
  for (const subscriber of [...productUpdateStatusSubscribers]) publishProductUpdateStatusValue(subscriber);
}

function publishProductUpdateStatusValue(subscriber) {
  const { response, requestUrl, write } = subscriber;
  if (response.destroyed || response.writableEnded) return;
  write(currentProductUpdateStatus());
  recordLogicalPublication(requestUrl);
}

function resetScenario() {
  startupModuleSelections.clear();
  trustedAccessDefaultSerial = 1;
  if (diskScanCompletionTimer) clearTimeout(diskScanCompletionTimer);
  if (targetedRecordingCompletionTimer) clearTimeout(targetedRecordingCompletionTimer);
  diskScanCompletionTimer = null;
  targetedRecordingCompletionTimer = null;
  diskScanOperation = null;
  diskScanSequence = 0;
  targetedRecordingSequence = 0;
  targetedRecordings.clear();
  cpuBaselineOverrideRatio = null;
  diskUsageSummary.totalBytes = 91_000_000;
  diskUsageSummary.scannedBytes = 91_000_000;
  diskUsageSummary.completedAt = "2026-10-09T12:00:00.000Z";
  diskUsageLayouts.set("root-c", createDiskUsageLayout("C:\\", 0, 18_000_000, [
    diskUsageTile(0, -1, 0, 18_000_000, "", true, 17),
    diskUsageTile(1, 0, 1, 8_000_000, "Projects", true, 4),
    diskUsageTile(5, 1, 2, 3_000_000, "Archive", true, 2)
  ]));
  for (const key of Object.keys(scenario)) {
    delete scenario[key];
  }
  Object.assign(scenario, structuredClone(defaultScenario));
  for (const key of Object.keys(appSettings)) {
    delete appSettings[key];
  }
  Object.assign(appSettings, structuredClone(defaultAppSettings));
  appSettingsRuntimeDisposition = "committedAndApplied";
  for (const key of Object.keys(requestTelemetry)) {
    delete requestTelemetry[key];
  }
  deviceTopologyRevision = 0;
  operationsPublicationRevision = 0;
  operationsCurrentValue = null;
  commitOperationsValue();
  metricSlot = {
    observedAt: now()
  };
  resourceSlotCapturedAt = now();
}

function settleMockValueSlots(patch) {
  if (Object.hasOwn(patch, "resourceBreakdownGeneration")
    || Object.hasOwn(patch, "resourceSegmentCount")
    || Object.hasOwn(patch, "resourceTableVariant")) {
    resourceSlotCapturedAt = now();
  }
}

function mergeRecord(target, patch) {
  if (!isRecord(patch)) {
    return;
  }
  for (const [key, value] of Object.entries(patch)) {
    if (isRecord(value) && isRecord(target[key])) {
      mergeRecord(target[key], value);
    } else {
      target[key] = structuredClone(value);
    }
  }
}

function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function nextSettingsRevision(revision) {
  const match = /^(.*-r)(\d+)$/.exec(String(revision));
  return match
    ? `${match[1]}${Number(match[2]) + 1}`
    : `frontend-harness-r${Date.now()}`;
}

async function serveStatic(response, pathname) {
  const requestedPath = pathname === "/" ? "/index.html" : pathname;
  const absolutePath = resolve(staticRoot, `.${decodeURIComponent(requestedPath)}`);
  const rootedPrefix = staticRoot.endsWith(sep) ? staticRoot : `${staticRoot}${sep}`;
  if (absolutePath !== resolve(staticRoot, "index.html") && !absolutePath.startsWith(rootedPrefix)) {
    return text(response, 403, "forbidden");
  }
  try {
    const content = await readFile(absolutePath);
    response.writeHead(200, { "Content-Type": mimeType(absolutePath), "Cache-Control": "no-store" });
    response.end(content);
  } catch {
    const content = await readFile(resolve(staticRoot, "index.html"));
    response.writeHead(200, { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" });
    response.end(content);
  }
}

function metric(id, label, group, unit, candidateSources = []) {
  return { id, unit, selectable: true, candidateSources };
}

// 部件地址的候选来源，形状同后端 MetricSourceCandidate；按后端排序（rank，再按请求地址）。
function readingSourceAddress() {
  const reading = (id, extra) => ({ id, quantity: "q", sourceRank: 10, sourceAvailability: "available", isPreferredSource: false,
    preferredDeviceIdentity: "gpu|reading", preferredFieldName: "gpu_amd_apu_temperature_reported_celsius", ...extra });
  const candidate = (fieldName, deviceIdentity, readingValue, isSelected) => ({ fieldName, deviceIdentity, sourceId: "fixture",
    reading: readingValue, sourceAvailability: readingValue.sourceAvailability, isSelected });
  return { ...metric("gpu.5.temperature", "", "", "°C", [
    candidate("gpu_amd_apu_temperature_reported_celsius", "gpu|reading", reading("apu-edge", { isPreferredSource: true }), false),
    candidate("cpu_amd_apu_graphics_temperature_celsius", "cpu-package|reading", reading("apu-edge"), false),
    candidate("gpu_amd_edge_temperature_celsius", "gpu|reading", reading("apu-edge", { sourceRank: 20 }), true)
  ]), fieldName: "gpu_amd_edge_temperature_celsius", deviceIdentity: "gpu|reading" };
}

// 进程字段目录（后端 ProcessMetricFieldRegistry 的形状）：内存四种口径带 reading，其余字段没有。
const processFieldCatalog = [
  ["process_all_cpu_usage_actual", "float64", "%", "windows.process.cpu-interval", null],
  ["process_all_working_set_actual", "unsigned64", "B", "windows.process.query", "working_set_including_shared"],
  ["process_all_private_working_set_actual", "unsigned64", "B", "windows.process.query", "private_working_set"],
  ["process_all_private_commit_actual", "unsigned64", "B", "windows.process.query", "private_commit"],
  ["process_all_peak_private_commit_actual", "unsigned64", "B", "windows.process.query", "peak_private_commit"]
].map(([name, valueKind, unit, sourceId, quantity]) => ({ name, valueKind, unit, sourceId, resource: "process", vendor: "all",
  reading: quantity === null ? null : { resource: "memory", quantity },
  // 后端：工作集、私有工作集、私有提交量能作资源事实，峰值提交量和非内存字段不能；
  // resourceFactTotalMetricId 是作资源事实时比较的整机指标（不能作资源事实的为 null）。
  supportsResourceFacts: quantity !== null && quantity !== "peak_private_commit",
  resourceFactTotalMetricId: quantity === "working_set_including_shared" || quantity === "private_working_set" ? "memory.usage"
    : quantity === "private_commit" ? "virtualMemory.usage" : null }));

// 自动的整机内存指标实际读的进程字段（后端在资源事实和资源条上写的 processFieldName）。
const automaticProcessFields = { "memory.usage": "process_all_private_working_set_actual", "virtualMemory.usage": "process_all_private_commit_actual" };

// 资源事实接受的进程内存字段：整机比较事实、整机量、容量和全部进程之和（按软件平分）。
const GiB = 1024 ** 3;
const processMemoryFacts = {
  process_all_working_set_actual: { totalMetricId: "memory.usage", totalValue: 8 * GiB, capacityValue: 16 * GiB, processSum: 0.625 * GiB },
  process_all_private_working_set_actual: { totalMetricId: "memory.usage", totalValue: 8 * GiB, capacityValue: 16 * GiB, processSum: 0.5 * GiB },
  process_all_private_commit_actual: { totalMetricId: "virtualMemory.usage", totalValue: 6 * GiB, capacityValue: 24 * GiB, processSum: GiB }
};

// 后端只接受能作资源事实的进程字段作 measures；峰值提交量、未知字段整条请求 400。
function resourceMeasuresKnown(url) {
  return normalizeMetricIds(url.searchParams.getAll("measures")).every(field => Object.hasOwn(processMemoryFacts, field.toLowerCase()));
}

function resourceFactItemsKnown(url) {
  return url.searchParams.getAll("items").every(id => ["process.inventory", "process.attribution", "cpu.usage", "memory.usage", "disk.io", "network.traffic"].includes(id)
    || Object.hasOwn(processMemoryFacts, id) || /^gpu\.\d+\.(usage|vram)$/.test(id));
}

function metricSnapshot(requestedIds) {
  const allItems = {
    "cpu.usage": { id: "cpu.usage", unit: "%", status: 1, numericValue: 12, percent: 12 },
    "cpu.frequency": { id: "cpu.frequency", unit: "MHz", status: 1, numericValue: 4200 },
    "cpu.frequencyPercent": { id: "cpu.frequencyPercent", unit: "%", status: 1, numericValue: 88, percent: 88 },
    "memory.usage": { id: "memory.usage", unit: "B", status: 1, numericValue: 8 * 1024 ** 3, percent: 25 },
    "memory.percent": { id: "memory.percent", unit: "%", status: 1, numericValue: 25, percent: 25 }
  };
  if (scenario.gpuPressureFixture) {
    Object.assign(allItems, {
      "gpu.0.usage": { id: "gpu.0.usage", unit: "%", status: 1, numericValue: 96, percent: 96 },
      "gpu.0.vram": { id: "gpu.0.vram", unit: "B", status: 1, numericValue: 4 * 1024 ** 3, percent: 50 },
        "gpu.0.vramPercent": { id: "gpu.0.vramPercent", unit: "%", status: 1, numericValue: 50, percent: 50 },
        "gpu.0.graphicsClock": { id: "gpu.0.graphicsClock", unit: "MHz", status: 1, numericValue: 1500 },
        "gpu.0.memoryClock": { id: "gpu.0.memoryClock", unit: "MHz", status: 1, numericValue: 7000 }
    });
  }
  const definitions = [...metricCatalog, ...(scenario.metricCatalogAddresses === "reading-source" ? [readingSourceAddress()] : [])];
  const knownMetricIds = new Map([...definitions.map(item => item.id), ...Object.keys(allItems)]
    .map(metricId => [metricId.toLowerCase(), metricId]));
  const selectedIds = normalizeMetricIds(requestedIds).map(id => knownMetricIds.get(id.toLowerCase()) ?? id);
  const statuses = new Map(Object.entries(scenario.metricStatuses).map(([id, status]) => [id.toLowerCase(), status]));
  const items = Object.fromEntries(selectedIds.map(id => {
    const current = allItems[id];
    const status = statuses.get(id.toLowerCase()) ?? (current ? 1 : 4);
    if (current && status === 1) return [id, { ...current, observedAt: metricSlot.observedAt }];
    return [id, {
      ...(current ?? { id, unit: definitions.find(item => item.id === id)?.unit ?? "" }),
      numericValue: null, integerValue: null, bytesValue: null, sampleDurationMilliseconds: null,
      percent: null, booleanValue: null, total: null, observedAt: null, status
    }];
  }));
  return {
    version: 5,
    capturedAt: Object.values(items).some(item => item.status === 1) ? metricSlot.observedAt : null,
    catalogRevision: "mock-catalog-1",
    items
  };
}

// 与后端 CpuBaselineRatioSettings 同形：ratio/multiplier 由默认值与用户值推出。
function cpuBaselineSettings() {
  const defaultRatio = 0.5;
  const ratio = cpuBaselineOverrideRatio ?? defaultRatio;
  return {
    cpuName: "Frontend Harness CPU",
    defaultRatio,
    overrideRatio: cpuBaselineOverrideRatio,
    defaultSource: "amd:symmetric-dual-ccd",
    matchedModel: null,
    datasetVersion: "2026-09-02",
    ratio,
    multiplier: 1 / ratio,
    defaultMultiplier: 1 / defaultRatio
  };
}

function fixtureCpuTopology(capturedAt = now()) {
  return {
    capturedAt,
    cpuName: "Frontend Harness CPU",
    specification: {
      name: "Frontend Harness CPU",
      vendor: "Fixture",
      family: "Fixture",
      physicalCoreCount: 2,
      logicalProcessorCount: 4,
      maxClockSpeedMhz: 4800,
      currentClockSpeedMhz: 4200,
      l2CacheSizeKb: 2048,
      l3CacheSizeKb: 16384,
      source: "frontend-harness"
    },
    topologySource: "frontend-harness",
    usageSource: "frontend-harness",
    affinityTargetKind: "logical_processor_mask",
    visualLayoutKind: "grid",
    visualLayoutSource: "frontend-harness",
    physicalCoreCount: 2,
    logicalProcessorCount: 4,
    ccdCount: 1,
    simultaneousMultithreading: true,
    ccds: [{
      id: "ccd:0",
      index: 0,
      usagePercent: 31,
      physicalCoreIndexes: [0, 1],
      logicalProcessorIds: [0, 1, 2, 3],
      source: "frontend-harness"
    }],
    physicalCores: [0, 1].map((index) => ({
      id: `core:${index}`,
      index,
      ccdId: "ccd:0",
      efficiencyClass: 0,
      performanceScore: 100 - index * 5,
      usagePercent: index === 0 ? 42 : 20,
      logicalProcessorIds: [index * 2, index * 2 + 1],
      cacheLevels: [
        { level: 1, sizeKb: 64, logicalProcessorIds: [index * 2, index * 2 + 1], scope: "private" },
        { level: 2, sizeKb: 1024, logicalProcessorIds: [index * 2, index * 2 + 1], scope: "private" },
        { level: 3, sizeKb: 16384, logicalProcessorIds: [0, 1, 2, 3], scope: "shared" }
      ]
    })),
    logicalProcessors: [0, 1, 2, 3].map((id) => ({
      id,
      processorGroup: 0,
      groupRelativeIndex: id,
      physicalCoreId: `core:${Math.floor(id / 2)}`,
      ccdId: "ccd:0",
      performanceScore: id < 2 ? 100 : 95,
      usagePercent: 10 + id * 7,
      affinitySelectable: id !== 3
    })),
  };
}

function fixtureCpuResidency(capturedAt = now()) {
  const measuredThrough = capturedAt;
  const measuredFrom = new Date(Date.parse(capturedAt) - 5_000).toISOString();
  return {
    capturedAt,
    window: "00:00:05",
    sessionGeneration: 7,
    measuredFrom,
    measuredThrough,
    processes: [{
      processInstanceId: "7:1",
      processId: 4242,
      processStartKey: "134000000000000000",
      processName: "example.exe",
      executionTimeMilliseconds: 900,
      switchCount: 10,
      threadCount: 1,
      primaryCcdId: "ccd:0",
      primaryPhysicalCoreId: "core:0",
      primaryLogicalProcessorId: 0,
      ccds: [{
        ccdId: "ccd:0",
        executionTimeMilliseconds: 900,
        switchCount: 10,
        sharePercent: 100
      }],
      physicalCores: [
        {
          physicalCoreId: "core:0",
          ccdId: "ccd:0",
          executionTimeMilliseconds: 800,
          switchCount: 5,
          sharePercent: 88.8888888889
        },
        {
          physicalCoreId: "core:1",
          ccdId: "ccd:0",
          executionTimeMilliseconds: 100,
          switchCount: 5,
          sharePercent: 11.1111111111
        }
      ],
      logicalProcessors: [
        {
          logicalProcessorId: 0,
          physicalCoreId: "core:0",
          ccdId: "ccd:0",
          executionTimeMilliseconds: 600,
          switchCount: 4,
          sharePercent: 66.6666666667
        },
        {
          logicalProcessorId: 1,
          physicalCoreId: "core:0",
          ccdId: "ccd:0",
          executionTimeMilliseconds: 200,
          switchCount: 1,
          sharePercent: 22.2222222222
        },
        {
          logicalProcessorId: 2,
          physicalCoreId: "core:1",
          ccdId: "ccd:0",
          executionTimeMilliseconds: 100,
          switchCount: 5,
          sharePercent: 11.1111111111
        }
      ],
      threads: [{
        threadInstanceId: "7:1",
        threadId: 5001,
        executionTimeMilliseconds: 900,
        switchCount: 10,
        primaryCcdId: "ccd:0",
        primaryPhysicalCoreId: "core:0",
        primaryLogicalProcessorId: 0,
        physicalCoreIds: ["core:0", "core:1"]
      }]
    }, {
      processInstanceId: "7:2",
      processId: 4343,
      processStartKey: "134000000000000001",
      processName: "slower-same-switches.exe",
      executionTimeMilliseconds: 100,
      switchCount: 10,
      threadCount: 1,
      primaryCcdId: "ccd:0",
      primaryPhysicalCoreId: "core:0",
      primaryLogicalProcessorId: 0,
      ccds: [{
        ccdId: "ccd:0",
        executionTimeMilliseconds: 100,
        switchCount: 10,
        sharePercent: 100
      }],
      physicalCores: [{
        physicalCoreId: "core:0",
        ccdId: "ccd:0",
        executionTimeMilliseconds: 100,
        switchCount: 10,
        sharePercent: 100
      }],
      logicalProcessors: [{
        logicalProcessorId: 0,
        physicalCoreId: "core:0",
        ccdId: "ccd:0",
        executionTimeMilliseconds: 100,
        switchCount: 10,
        sharePercent: 100
      }],
      threads: [{
        threadInstanceId: "7:2",
        threadId: 5002,
        executionTimeMilliseconds: 100,
        switchCount: 10,
        primaryCcdId: "ccd:0",
        primaryPhysicalCoreId: "core:0",
        primaryLogicalProcessorId: 0,
        physicalCoreIds: ["core:0"]
      }]
    }]
  };
}

function resourceProcessDetails(url, capturedAt) {
  const requested = new Set(normalizeMetricIds(url.searchParams.getAll("processDetails")).map(id => id.toLowerCase()));
  const measures = url.searchParams.getAll("measures");
  // 明细按 measures 给出这个字段的进程，整机总量读不到也照样给。
  const breakdown = resourceBreakdown(capturedAt, requested, measures, url.searchParams.getAll("modes"), { keepMissingTotals: true, ids: url.searchParams.getAll("ids") });
  const metrics = breakdown.bars
    .map(bar => ({ metricId: bar.metricId, software: bar.software
      .filter(row => requested.has(breakdown.softwareCatalog[row[0]][0].toLowerCase()))
      .map(row => ({ softwareId: breakdown.softwareCatalog[row[0]][0], processes: row[5] })) }))
    .filter(metric => metric.software.length > 0);
  return { version: 1, capturedAt: metrics.length ? capturedAt : null, metrics };
}

// 后端只在物理内存条（memory.usage 和 process_all_private_working_set_actual 字段条）上写 residualSource。
function physicalResidualSource(metricId) {
  return metricId === "memory.usage" || metricId === "process_all_private_working_set_actual" ? "counters" : null;
}

function normalizeMetricIds(values) {
  const ids = new Map();
  for (const raw of values) for (const part of raw.split(",")) {
    const id = part.trim();
    if (id && !ids.has(id.toLowerCase())) ids.set(id.toLowerCase(), id);
  }
  return [...ids.values()];
}

function resourceBreakdown(capturedAt = now(), processDetailSoftwareIds = new Set(), measures = [], modes = [], { keepMissingTotals = false, ids = [] } = {}) {
  const generation = Math.max(
    0,
    Math.floor(Number(scenario.resourceBreakdownGeneration) || 0));
  const cpuValue = scenario.resourceCpuValue ?? [12, 38, 21, 55][generation % 4];
  const segmentCount = normalizeFixtureCount(scenario.resourceSegmentCount, 1, 1000);
  const softwareCatalog = Array.from({ length: segmentCount }, (_, index) => [
    index === 0 ? "frontend-harness.software" : `frontend-harness.software.${index}`,
    index === 0 ? "示例软件" : `示例软件 ${index}`,
    "other",
    "other"
  ]);
  const visibleIds = new Set(normalizeMetricIds([...ids, ...measures]).map(id => id.toLowerCase()));
  const processFields = [...visibleIds].filter(id => Object.hasOwn(processMemoryFacts, id));
  const bars = [
      {
        metricId: "cpu.usage",
        unit: "%",
        scaleMode: "capacity",
        totalValue: cpuValue,
        capacityValue: 100,
        totalSystemPercent: cpuValue,
        totalDisplay: `${cpuValue}%`,
        software: createResourceSoftwareRows(
          segmentCount,
          cpuValue,
          cpuValue,
          (value) => `${value.toFixed(3)}%`,
          processDetailSoftwareIds)
      },
      {
        metricId: "memory.usage",
        // 后端在自动的整机内存条上写出实际读的进程字段。
        processFieldName: automaticProcessFields["memory.usage"],
        // 后端物理内存条的残差来自计数器（residualSource "counters"，见 runtime_process_attribution_and_io_network_telemetry.md 的 Wire 节）。
        residualSource: physicalResidualSource("memory.usage"),
        unit: scenario.resourceSharedMemory ? "B" : "GB",
        scaleMode: "capacity",
        totalValue: scenario.resourceSharedMemory ? 8 * 1024 ** 3 : 8,
        capacityValue: scenario.resourceSharedMemory ? 32 * 1024 ** 3 : 32,
        totalSystemPercent: 25,
        totalDisplay: "8.0 GB",
        software: createResourceSoftwareRows(
          segmentCount,
          scenario.resourceSharedMemory ? 8 * 1024 ** 3 : 8,
          25,
          (value) => `${(value * 1024).toFixed(2)} MB`,
          processDetailSoftwareIds).map(row => scenario.resourceSharedMemory ? [...row, row[1] / 2] : row)
      },
      ...(scenario.gpuPressureFixture ? [
        { metricId: "gpu.0.usage", unit: "%", scaleMode: "capacity", totalValue: 96, capacityValue: 100, totalSystemPercent: 96,
          software: createResourceSoftwareRows(segmentCount, 96, 96, value => String(value) + "%", processDetailSoftwareIds) },
        { metricId: "gpu.0.vram", unit: "B", scaleMode: "capacity", totalValue: 4 * GiB, capacityValue: 8 * GiB, totalSystemPercent: 50,
          software: createResourceSoftwareRows(segmentCount, 4 * GiB, 50, value => String(value) + " B", processDetailSoftwareIds) }
      ] : []),
      ...resourceMeasureBars(softwareCatalog, processFields, modes, processDetailSoftwareIds, keepMissingTotals)
    ].filter(bar => visibleIds.has(bar.metricId.toLowerCase()));
  // Only selected bars contribute software identities, as in the C# wire projection.
  const selectedCatalog = [];
  const indexes = new Map();
  for (const bar of bars) bar.software = bar.software.map(row => {
    if (!indexes.has(row[0])) {
      indexes.set(row[0], selectedCatalog.length);
      selectedCatalog.push(softwareCatalog[row[0]]);
    }
    return [indexes.get(row[0]), ...row.slice(1)];
  });
  return { version: 8, capturedAt: bars.length ? capturedAt : null, softwareCatalog: selectedCatalog, bars };
}

// 按进程字段订阅的资源条（后端 ResourceBreakdownWireSnapshot.ProjectProcessMemoryBars 的形状）：
// metricId 和 processFieldName 是字段名，totalMetricId 是它比较的整机事实；第一个软件拿全部进程之和，
// 差额进 resource-residual:<totalMetricId>。缩放看 modes=，键可以是字段名或它的 totalMetricId，同时出现时字段名优先，
// active 按整机已用量比较。后端只认三种能作资源事实的字段（别的在请求时已经 400）；
// 总量读不到（resourceBarMeasureTotalsMissing）时条上不出这一条，明细照样给。
function resourceMeasureBars(softwareCatalog, measures, modes = [], processDetailSoftwareIds = new Set(), keepMissingTotals = false) {
  const modeByKey = new Map(modes.map(value => value.split(":")).filter(parts => parts.length === 2));
  const known = [...new Set(measures)].filter(field => Object.hasOwn(processMemoryFacts, field)
    && (keepMissingTotals || !(scenario.resourceBarMeasureTotalsMissing ?? []).includes(field)));
  return known.map(field => {
    const fact = processMemoryFacts[field];
    const scaleMode = modeByKey.get(field) ?? modeByKey.get(fact.totalMetricId) ?? "capacity";
    const base = scaleMode === "active" ? fact.totalValue : fact.capacityValue;
    const percent = value => value * 100 / base;
    const detailed = processDetailSoftwareIds.has(softwareCatalog[0][0].toLowerCase());
    const residualId = `resource-residual:${fact.totalMetricId}`;
    let residualIndex = softwareCatalog.findIndex(row => row[0] === residualId);
    if (residualIndex < 0) residualIndex = softwareCatalog.push([residualId, "", "windows_system", "windows_system"]) - 1;
    const residual = fact.totalValue - fact.processSum;
    return {
      metricId: field,
      totalMetricId: fact.totalMetricId,
      processFieldName: field,
      ...(physicalResidualSource(field) ? { residualSource: physicalResidualSource(field) } : {}),
      unit: "B",
      scaleMode,
      totalValue: fact.totalValue,
      capacityValue: fact.capacityValue,
      totalSystemPercent: percent(fact.totalValue),
      sharedValue: null,
      software: [[0, fact.processSum, percent(fact.processSum), 1, 5, detailed ? [[4242, "example.exe", "C:\\FrontendHarness\\example.exe",
        fact.processSum, percent(fact.processSum), 100, "测试用户", "x64", "direct", 0, String(132000000000000000n + 4242n)]] : []],
        [residualIndex, residual, percent(residual), 0, 0, []]]
    };
  });
}

function resourceFacts(url, capturedAt = now()) {
  const count = scenario.resourceTableVariant === "empty" ? 0 : normalizeFixtureCount(scenario.resourceTableRowCount, scenario.resourceTableVariant === "many" ? 80 : 1, 1000);
  const software = Array.from({ length: count }, (_, index) => ({ softwareId: `frontend-harness.software${index ? `.${index}` : ""}`,
    name: index ? `示例软件 ${index}` : "示例软件", kind: "other", attributionKind: "running" }));
  const processes = software.map((_, index) => ({ processId: 4242 + index, processStartKey: String(132000000000000000n + BigInt(4242 + index)),
    name: `example-${index}.exe`, executablePath: "C:\\FrontendHarness\\example.exe", userName: "测试用户", architecture: "x64", attributionKind: "process" }));
  const items = new Set(url.searchParams.getAll("items"));
  const residualAttributions = [];
    const metrics = [...items].filter(id => id !== "process.inventory" && id !== "process.attribution").map(metricId => {
    const fact = processMemoryFacts[metricId];
    if (fact) {
      // 进程字段：总计是 totalMetricId 指向的整机事实，不是进程之和；差额进现有的系统/驱动残差行。
      const share = fact.processSum / Math.max(1, count);
      const percent = value => value * 100 / fact.capacityValue;
      const residualId = `resource-residual:${fact.totalMetricId}`;
      const residual = fact.totalValue - (count > 0 ? fact.processSum : 0);
      const rows = software.slice(0, count).map((identity, index) => ({ softwareId: identity.softwareId, value: share, systemPercent: percent(share), processCount: 1, baseScore: 0, sharedValue: null,
        processes: [{ processIndex: index, value: share, systemPercent: percent(share), softwarePercent: 100, baseScore: 0, sharedValue: null }] }));
      if (residual > 0) {
        if (!software.some(identity => identity.softwareId === residualId)) software.push({ softwareId: residualId, name: "", kind: "windows_system", attributionKind: "system-residual" });
        const processIndex = processes.push({ processId: -1, processStartKey: null, name: "", executablePath: null, userName: null, architecture: null, attributionKind: "system-residual" }) - 1;
        residualAttributions.push({ softwareId: residualId, processIndex, baseScore: 0 });
        rows.push({ softwareId: residualId, value: residual, systemPercent: percent(residual), processCount: 0, baseScore: 0, sharedValue: null,
          processes: [{ processIndex, value: residual, systemPercent: percent(residual), softwarePercent: 100, baseScore: 0, sharedValue: null }] });
      }
      return { metricId, unit: "B", totalMetricId: fact.totalMetricId, processFieldName: metricId, residualSource: physicalResidualSource(metricId), totalValue: fact.totalValue, capacityValue: fact.capacityValue,
        totalSystemPercent: percent(fact.totalValue), processSumValue: count > 0 ? fact.processSum : 0, sharedValue: null, software: rows };
    }
    const memory = metricId === "memory.usage" || metricId.endsWith(".vram");
    const value = memory ? 536870912 : 12;
    const sharedValue = memory ? 134217728 : null;
    return { metricId, unit: memory ? "B" : metricId.startsWith("disk.") ? "B/s" : metricId.startsWith("network.") ? "bps" : "%",
      totalMetricId: null, processFieldName: automaticProcessFields[metricId] ?? null, residualSource: physicalResidualSource(metricId), totalValue: value, capacityValue: memory ? 1073741824 : 100, totalSystemPercent: 12, processSumValue: null, sharedValue,
      software: software.slice(0, count).map((identity, index) => ({ softwareId: identity.softwareId, value: value / Math.max(1, count), systemPercent: 12 / Math.max(1, count), processCount: 1,
        baseScore: 0, sharedValue: sharedValue === null ? null : sharedValue / Math.max(1, count),
        processes: [{ processIndex: index, value: value / Math.max(1, count), systemPercent: 12 / Math.max(1, count), softwarePercent: 100, baseScore: 0, sharedValue: sharedValue === null ? null : sharedValue / Math.max(1, count) }] })) };
  });
    const attributions = [...(items.has("process.attribution") ? software.slice(0, count).map((identity, index) => ({ softwareId: identity.softwareId, processIndex: index, baseScore: 0 })) : []),
      ...residualAttributions];
    return { version: 2, capturedAt, software, processes, metrics, attributions };
}

function fixtureDisplayEdid() {
  const type3 = scenario.displayEdid.startsWith("type3");
  const typex = scenario.displayEdid === "typex";
  if (typex && !recordedTypeXState) throw new Error("TypeX real wire input missing");
  const declarations = Object.hasOwn(recordedDeclarationStates, scenario.displayEdid) ? recordedDeclarationStates[scenario.displayEdid] : null;
  const display = structuredClone((typex ? recordedTypeXState : type3 ? recordedType3State : declarations ?? recordedEdidState).snapshot.ports[0].display);
  if (scenario.displayEdid === "type3Inactive") {
    display.active = false; display.targetAvailable = false; display.resolution = null; display.refreshRate = null;
    for (const key of ["currentWidth", "currentHeight", "currentRefreshRateNumerator", "currentRefreshRateDenominator", "currentBitsPerColorChannel"]) display[key] = null;
  }
  if (scenario.displayEdid === "type3Unknown") {
    const bytes = Buffer.from(display.edidRawDeclaredBase64, "base64"); bytes[139] = 9;
    bytes[142] = 0; bytes[142] = (-bytes.subarray(129, 142).reduce((sum, value) => sum + value, 0)) & 255;
    bytes[255] = 0; bytes[255] = (-bytes.subarray(128, 255).reduce((sum, value) => sum + value, 0)) & 255;
    display.modeCapabilityDeclared = null; display.edidRawDeclaredBase64 = bytes.toString("base64");
  }
  // inactive: the monitor is gone and only its cached declaration remains; inactiveConnected: it is attached but its output is off.
  if (scenario.displayEdid === "inactive" || scenario.displayEdid === "inactiveConnected") {
    display.active = false; display.targetAvailable = scenario.displayEdid === "inactiveConnected";
    display.resolution = null; display.refreshRate = null;
    for (const key of ["currentWidth", "currentHeight", "currentRefreshRateNumerator", "currentRefreshRateDenominator", "currentBitsPerColorChannel"]) display[key] = null;
  }
  if (scenario.displayEdid === "unknown") {
    display.modeCapabilityDeclared = null; const bytes = Buffer.from(display.edidRawDeclaredBase64, "base64");
    bytes[126] = 4; bytes[127] = 0; bytes[127] = (-bytes.subarray(0, 127).reduce((sum, value) => sum + value, 0)) & 255;
    display.edidRawDeclaredBase64 = bytes.toString("base64");
  }
  if (scenario.displayEdid === "empty") {
    const bytes = Buffer.from(display.edidRawDeclaredBase64, "base64").subarray(0, 128); bytes.fill(0, 54, 126); for (const offset of [54, 72, 90, 108]) bytes[offset + 3] = 0x10; bytes[126] = 0; bytes[127] = 0;
    bytes[127] = (-bytes.subarray(0, 127).reduce((sum, value) => sum + value, 0)) & 255;
    display.edidRawDeclaredBase64 = bytes.toString("base64");
    display.modeCapabilityDeclared = { modes: [], ranges: [], selectionDeclarations: [], dynamicTimingRanges: [], adaptiveSyncRanges: [], edidRevision: 4, baseFeatures: 2, ctaFlags: null };
  }
  if (scenario.displayEdid === "emptyRaw") { display.edidRawDeclaredBase64 = ""; display.modeCapabilityDeclared = null; }
  if (scenario.displayEdid === "legacy") for (const key of ["modeCapabilityDeclared", "edidRawDeclaredBase64", "currentWidth", "currentHeight", "currentRefreshRateNumerator", "currentRefreshRateDenominator", "currentBitsPerColorChannel"]) delete display[key];
  // 端口上什么都没接（2026-10-22 实机 HDMI / Mini DP 的原样形状）：没有显示器名称、设备路径和 EDID，当前模式为空。
  if (scenario.displayEdid === "detached") {
    Object.assign(display, { monitorName: "", resolution: null, refreshRate: null, active: false, targetAvailable: false, monitorDevicePath: null,
      edidRawDeclaredBase64: null, modeCapabilityDeclared: null, currentWidth: null, currentHeight: null,
      currentRefreshRateNumerator: 0, currentRefreshRateDenominator: 0, currentBitsPerColorChannel: null });
  }
  return display;
}
function fixtureDeviceTopologyState(capturedAt = now()) {
  if (scenario.deviceTopologyInvalid) return { schemaVersion: "invalid" };
  return {
    schemaVersion: "4.0.0",
    state: "ready",
    snapshot: {
      capturedAt,
      system: {
        manufacturer: "Frontend Harness",
        model: "Fixture PC",
        brandDisplayName: "Frontend Harness",
        brandLogoText: "FH",
        biosVersion: null,
        baseBoardManufacturer: null,
        baseBoardProduct: null
      },
      gpuInformation: [],
      externalMachines: null,
      machineIdentity: { formFactor: "desktop", evidence: {
        chassisTypes: { value: [3], reason: null }, systemBatteryPresent: { value: false, reason: null },
        cpuModels: { value: [], reason: null }, gpuModels: { value: [], reason: null },
        gpuAttachments: { value: [], reason: null }, workstationComponents: []
      } },
      ports: Array.isArray(scenario.deviceTopologyPorts) ? scenario.deviceTopologyPorts
        : Number.isSafeInteger(scenario.deviceTopologyPortCount) ? fixtureManyHdmiPorts(scenario.deviceTopologyPortCount)
        : scenario.deviceTopologyNullProtocol ? [fixtureHdmiPort(), {
        ...fixtureHdmiPort(), id: "fixture-null-protocol", deviceId: "DISPLAY\\FIXTURE_NULL_PROTOCOL", protocol: null
      }] : scenario.displayEdid === "identity" ? [fixtureHdmiPort(), { ...fixtureHdmiPort(), id: "fixture-hdmi-2", deviceId: "DISPLAY\\FIXTURE_HDMI_2",
        display: { ...fixtureDisplayEdid(), monitorDevicePath: "different-monitor-path", modeCapabilityDeclared: null } }] : [fixtureHdmiPort()],
      notes: []
    },
    contentGeneration: 1,
    stateRevision: ++deviceTopologyRevision,
    source: "live",
    lastSuccessAt: capturedAt,
    lastAttemptAt: capturedAt,
    failureCode: null,
    informationFields: { observedAt: null, devices: [] }, attemptDiagnostics: []
  };
}

function createResourceSoftwareRows(
  segmentCount,
  totalValue,
  totalSystemPercent,
  formatDisplay,
  processDetailSoftwareIds
) {
  const value = totalValue / segmentCount;
  const systemPercent = totalSystemPercent / segmentCount;
  return Array.from({ length: segmentCount }, (_, index) => {
    const processId = 4242 + index;
    const name = index === 0 ? "example.exe" : `example-${index}.exe`;
    const softwareId = index === 0
      ? "frontend-harness.software"
      : `frontend-harness.software.${index}`;
    return [
      index,
      value,
      systemPercent,
      1,
      index % 3,
      processDetailSoftwareIds.has(softwareId.toLowerCase()) ? [[
        processId,
        name,
        `C:\\FrontendHarness\\${name}`,
        value,
        systemPercent,
        100,
        "测试用户",
        "x64",
        "direct",
        index % 3,
        String(132000000000000000n + BigInt(processId))
      ]] : []
    ];
  });
}

function normalizeFixtureCount(value, fallback, maximum) {
  const parsed = Number(value);
  return Number.isSafeInteger(parsed) && parsed > 0
    ? Math.min(maximum, parsed)
    : fallback;
}

function json(response, status, payload) {
  response.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
  response.end(JSON.stringify(payload));
}

function currentOperationsValue() {
  operationsCurrentValue ??= createOperationsValue();
  return operationsCurrentValue;
}

function commitOperationsValue() {
  operationsCurrentValue = createOperationsValue();
  for (const subscriber of [...operationSubscribers]) {
    publishOperationValue(subscriber, operationsCurrentValue);
  }
}

function createOperationsValue() {
  const operations = scenario.operations === "mixed"
    ? fixtureOperations()
    : scenario.operations === "many"
      ? fixtureManyOperations()
      : [];
  if (diskScanOperation) operations.push(diskScanOperation);
  return {
    schema: "host-manager.operations.state.v2",
    capturedAt: now(),
    publicationRevision: String(++operationsPublicationRevision),
    configurationGeneration: "1",
    ready: true,
    persistenceFaulted: false,
    faultStage: null,
    operations
  };
}

function completeTargetedRecording(id, stopReason) {
  const entry = targetedRecordings.get(id);
  if (!entry) return;
  const endedAt = now();
  entry.header = {
    ...entry.header,
    status: "completed",
    endedAt,
    stopReason,
    incomplete: false
  };
}

function publishOperationValue(subscriber, value) {
  const { response, requestUrl, write } = subscriber;
  if (response.destroyed || response.writableEnded) {
    return;
  }
  write(value);
  recordLogicalPublication(requestUrl);
}

function publishManagementInventoryRevisions() {
  for (const subscriber of [...managementInventoryRevisionSubscribers]) {
    publishManagementInventoryRevisionValue(subscriber);
  }
}

function publishManagementInventoryRevisionValue(subscriber) {
  const { response, requestUrl, write } = subscriber;
  if (response.destroyed || response.writableEnded) return;
  write(structuredClone(scenario.managementInventoryRevisions));
  recordLogicalPublication(requestUrl);
}

async function subscriptionChannelStream(request, response) {
  const body = JSON.parse(await readBody(request));
  if (body?.version !== 1
    || !Array.isArray(body.subscriptions)
    || body.subscriptions.length < 1
    || body.subscriptions.length > 64) {
    return json(response, 400, { error: "invalid subscription channel request" });
  }
  requestTelemetryFor("/api/subscriptions/stream")
    .subscriptionSets.push(body.subscriptions.map((item) => item?.path));

  const ids = new Set();
  const subscriptions = [];
  const rejections = [];
  for (const item of body.subscriptions) {
    if (typeof item?.id !== "string"
      || !/^[A-Za-z0-9._:-]{1,128}$/.test(item.id)
      || ids.has(item.id)) {
      return json(response, 400, { error: "invalid subscription channel item" });
    }
    ids.add(item.id);
    const requestUrl = parseSubscriptionChannelSelector(item.path);
    if (requestUrl === null) {
      rejections.push({ id: item.id, value: { statusCode: 400, error: "unsupported-subscription-selector" } });
      continue;
    }
    const operation = requestUrl.pathname === "/api/operations/subscribe";
    const managementInventoryRevision = requestUrl.pathname === "/api/management/inventory-revisions/subscribe";
    const productUpdateStatus = requestUrl.pathname === "/api/updates/product/status/subscribe";
    const eventOnly = operation || managementInventoryRevision || productUpdateStatus;
    let createPayload;
    try { createPayload = eventOnly ? null : subscriptionPayloadFactory(requestUrl); }
    catch { return json(response, 400, { error: "One or more subscription queries are invalid." }); }
    if (!eventOnly && createPayload === null) {
      return json(response, 400, { error: "unsupported subscription path" });
    }
    subscriptions.push({
      id: item.id,
      requestUrl,
      operation,
      managementInventoryRevision,
      productUpdateStatus,
      createPayload
    });
  }

  response.writeHead(200, {
    "Content-Type": "application/x-ndjson; charset=utf-8",
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff"
  });
  for (const rejection of rejections) writeSubscriptionChannelFrame(response, rejection.id, rejection.value);
  if (subscriptions.length === 0) return response.end();
  subscriptionChannelResponses.add(response);
  response.once("close", () => subscriptionChannelResponses.delete(response));
  const releases = [];
  for (const subscription of subscriptions) {
    const releaseTelemetry = trackLogicalSubscription(subscription.requestUrl);
    if (subscription.operation) {
      const subscriber = {
        response,
        requestUrl: subscription.requestUrl,
        write: (value) => writeSubscriptionChannelFrame(
          response,
          subscription.id,
          value)
      };
      operationSubscribers.add(subscriber);
      publishOperationValue(subscriber, currentOperationsValue());
      releases.push(() => {
        operationSubscribers.delete(subscriber);
        releaseTelemetry();
      });
      continue;
    }

    if (subscription.productUpdateStatus) {
      const subscriber = {
        response,
        requestUrl: subscription.requestUrl,
        write: (value) => writeSubscriptionChannelFrame(response, subscription.id, value)
      };
      productUpdateStatusSubscribers.add(subscriber);
      publishProductUpdateStatusValue(subscriber);
      releases.push(() => {
        productUpdateStatusSubscribers.delete(subscriber);
        releaseTelemetry();
      });
      continue;
    }

    if (subscription.managementInventoryRevision) {
      const subscriber = {
        response,
        requestUrl: subscription.requestUrl,
        write: (value) => writeSubscriptionChannelFrame(
          response,
          subscription.id,
          value)
      };
      managementInventoryRevisionSubscribers.add(subscriber);
      publishManagementInventoryRevisionValue(subscriber);
      releases.push(() => {
        managementInventoryRevisionSubscribers.delete(subscriber);
        releaseTelemetry();
      });
      continue;
    }

    const subscriber = {
      response,
      requestUrl: subscription.requestUrl,
      intervalMs: pushInterval(subscription.requestUrl),
      createPayload: subscription.createPayload,
      detailsReadyAt: Date.now() + (subscription.requestUrl.pathname === "/api/resource-monitor/subscribe"
        && subscription.requestUrl.searchParams.get("scope") === "details" ? scenario.resourceDetailsDelayMs : 0),
      publish(publishedAt) {
        if (response.destroyed || response.writableEnded
          || Date.now() < this.detailsReadyAt
          || scenario.pausedSubscriptionPaths.includes(subscription.requestUrl.pathname)) {
          return;
        }
        writeSubscriptionChannelFrame(
          response,
          subscription.id,
          subscription.createPayload(publishedAt));
        recordLogicalPublication(subscription.requestUrl);
      }
    };
    const publisherKey = sharedPublisherKey(subscription.requestUrl);
    const publisher = acquireNdjsonPublisher(publisherKey, subscriber);
    releases.push(() => {
      releaseNdjsonPublisher(publisherKey, publisher, subscriber);
      releaseTelemetry();
    });
  }

  let stopped = false;
  const stop = () => {
    if (stopped) {
      return;
    }
    stopped = true;
    for (const release of releases) {
      release();
    }
  };
  request.once("aborted", stop);
  response.once("close", stop);
}

const subscriptionChannelSelectors = new Set([
  "/api/control/actual/subscribe",
  "/api/metrics/subscribe",
  "/api/metrics/fields/catalog/subscribe",
  "/api/metrics/gpu-specialized/subscribe",
  "/api/resource-monitor/subscribe",
  "/api/resource-facts/subscribe",
  "/api/adapters/resource-manager/scheduling/subscribe",
  "/api/local-system/status/subscribe",
  "/api/device-topology/state/subscribe",
  "/api/cpu/topology/subscribe",
  "/api/cpu/residency/subscribe",
  "/api/optimization/smart/state/subscribe",
  "/api/optimization/smart/status/subscribe",
  "/api/operations/subscribe",
  "/api/management/inventory-revisions/subscribe",
  "/api/access-requests/subscribe",
  "/api/updates/product/status/subscribe"
]);

function smartCoordinatorStatus() {
  return {
    mode: "normal",
    schedulerRunning: false,
    lastRunAt: null,
    lastRestoreAt: null,
    pendingChangeCount: 0,
    appliedTargetCount: 0,
    faultClosedReason: null,
    coordinator: null,
    ...scenario.smartStatus
  };
}

function parseSubscriptionChannelSelector(value) {
  if (typeof value !== "string" || !value
    || value.length > 8192
    || /[\\#\s\u0000-\u001f\u007f]/u.test(value)) {
    return null;
  }
  const separator = value.indexOf("?");
  const selector = separator < 0 ? value : value.slice(0, separator);
  if (!subscriptionChannelSelectors.has(selector)) {
    return null;
  }
  return new URL(value, `http://127.0.0.1:${port}`);
}

function writeSubscriptionChannelFrame(response, subscriptionId, value) {
  response.write(`${JSON.stringify({
    subscriptionId,
    value
  })}\n`);
}

const publicFieldAliases = new Map([
  ["fan_all_rotational_speed", "fan_all_speed_rpm"],
  ["fan_all_pwm_reported", "fan_all_reported_pwm_duty"]
]);
const knownFieldNames = new Set(canonicalFieldNames);
const normalizeFieldName = name => publicFieldAliases.get(name) ?? name;

function parseMetricFieldSelections(url) {
  const devices = url.searchParams.getAll("device");
  const groups = url.searchParams.getAll("fields");
  if (devices.some(device => !device.trim()) || (devices.length > 1 && groups.length !== devices.length))
    throw new Error("metric-field-device-invalid");
  const names = values => new Set(values.flatMap(value => value.split(",")).map(value => normalizeFieldName(value.trim())).filter(Boolean));
  const selections = devices.length > 1
    ? devices.map((device, index) => ({ device, fields: names([groups[index]]) }))
    : [{ device: devices[0] ?? null, fields: names(groups) }];
  for (const selection of selections) {
    const available = selection.device === null ? null : scenario.metricFieldDevices.find(device => device.deviceIdentity === selection.device);
    if (selection.device !== null && !available) throw new Error("metric-field-device-unknown");
    if (selection.fields.size === 0 || [...selection.fields].some(name =>
      available ? !available.fields.some(field => normalizeFieldName(field.name) === name) : !knownFieldNames.has(name)))
      throw new Error("metric-field-name-unknown");
  }
  return selections;
}

function metricFieldsSnapshot(selections) {
  if (scenario.invalidMetricFields) return [{}];
  const projected = new Map();
  for (const selection of selections) for (const device of scenario.metricFieldDevices) {
    if (selection.device !== null && selection.device !== device.deviceIdentity) continue;
    const fields = device.fields.filter(field => selection.fields.has(normalizeFieldName(field.name)))
      .map(field => ({
        ...field, name: normalizeFieldName(field.name),
        ...(field.status === 1 ? {} : { numericValue: null, integerValue: null, bytesValue: null,
          booleanValue: null, sampleDurationMilliseconds: null, observedAt: null })
      })).sort((left, right) => left.name.localeCompare(right.name));
    if (fields.length === 0) continue;
    if (!projected.has(device.deviceIdentity)) projected.set(device.deviceIdentity, { ...device, fields: [] });
    const row = projected.get(device.deviceIdentity);
    for (const field of fields) if (!row.fields.some(current => current.name === field.name)) row.fields.push(field);
  }
  return [...projected.values()];
}

function metricFieldCatalog() {
  return scenario.metricFieldDevices.map(({ deviceIdentity, device, fields }) => ({
    deviceIdentity, device,
    fields: fields.map(({ name, unit, valueKind, reading }) => ({ name: normalizeFieldName(name), unit, valueKind, meaning: "", reading: reading ?? null }))
  }));
}

function subscriptionPayloadFactory(url) {
  switch (url.pathname) {
    case "/api/access-requests/subscribe":
      return () => scenario.accessRequests;
    case "/api/control/actual/subscribe":
      return publishedAt => ({ readAt: publishedAt, values: scenario.controlActualValues ?? [
        { objectId: "gpu:test", capabilityId: "gpu.temperature-limit", number: 89, unit: "°C" },
        { objectId: "gpu:test", capabilityId: "gpu.dynamic-boost-enabled", toggle: false },
        { objectId: "gpu:test", capabilityId: "gpu.core-voltage-offset", number: 0, unit: "mV" }
      ] });
    case "/api/metrics/subscribe": {
      if (url.searchParams.has("device") || url.searchParams.has("fields")) {
        const selections = parseMetricFieldSelections(url);
        return () => metricFieldsSnapshot(selections);
      }
      const ids = url.searchParams.getAll("ids");
      return (publishedAt) => {
        metricSlot.observedAt = publishedAt;
        return metricSnapshot(ids);
      };
    }
    case "/api/metrics/fields/catalog/subscribe":
      return () => metricFieldCatalog();
    case "/api/metrics/gpu-specialized/subscribe":
      return (publishedAt) => ({
        version: 2,
        capturedAt: publishedAt,
        adapters: []
      });
    case "/api/resource-monitor/subscribe": {
      // 和后端一样，不支持的 measures（例如峰值提交量）在条和明细上都拒绝。
      if (!resourceMeasuresKnown(url)) return null;
      if (url.searchParams.get("scope") === "details") return publishedAt => resourceProcessDetails(url, publishedAt);
      const processDetails = new Set(normalizeMetricIds(url.searchParams.getAll("processDetails")).map(id => id.toLowerCase()));
      return (publishedAt) => {
        resourceSlotCapturedAt = publishedAt;
        const capturedAt = resourceSlotCapturedAt;
        const breakdown = resourceBreakdown(capturedAt, processDetails, url.searchParams.getAll("measures"), url.searchParams.getAll("modes"), { ids: url.searchParams.getAll("ids") });
        return {
          version: 10,
          capturedAt: breakdown.capturedAt,
          breakdown
        };
      };
    }
    case "/api/resource-facts/subscribe":
      // 和后端一样不接受不认识的 items。
      return resourceFactItemsKnown(url) ? publishedAt => resourceFacts(url, publishedAt) : null;
    case "/api/device-topology/state/subscribe":
      return (publishedAt) => fixtureDeviceTopologyState(publishedAt);
    case "/api/cpu/topology/subscribe":
      return (publishedAt) => scenario.cpuTopologyEmpty ? null : fixtureCpuTopology(publishedAt);
    case "/api/cpu/residency/subscribe":
      return (publishedAt) => fixtureCpuResidency(publishedAt);
    case "/api/local-system/status/subscribe":
      return (publishedAt) => ({
        capturedAt: publishedAt,
        bootedAt: new Date(Date.parse(publishedAt) - 3_600_000).toISOString(),
        uptimeSeconds: 3_600
      });
    case "/api/adapters/resource-manager/scheduling/subscribe":
      return (publishedAt) => ({
        cpuGrade: scenario.cpuGrade,
        gpuGrade: scenario.gpuGrade,
        cpuUpdatedAt: publishedAt,
        gpuUpdatedAt: publishedAt,
        cpuPolicyId: "frontend-harness",
        gpuPolicyId: "frontend-harness",
        cpuReason: "frontend harness",
        gpuReason: "frontend harness",
        sources: []
      });
    case "/api/optimization/smart/state/subscribe":
      return () => ({
        version: 3,
        lastRunAt: null,
        lastRestoreAt: null,
        coordinator: null,
        appliedPlacements: scenario.smartPlacements
      });
    case "/api/optimization/smart/status/subscribe":
      return () => smartCoordinatorStatus();
    default:
      return null;
  }
}

function trackLogicalSubscription(url) {
  const telemetry = requestTelemetryFor(url.pathname);
  const query = url.search;
  telemetry.logicalStarted += 1;
  telemetry.logicalInFlight += 1;
  telemetry.logicalMaxInFlight = Math.max(
    telemetry.logicalMaxInFlight,
    telemetry.logicalInFlight);
  telemetry.started += 1;
  telemetry.inFlight += 1;
  telemetry.maxInFlight = Math.max(telemetry.maxInFlight, telemetry.inFlight);
  telemetry.startTimes.push(Date.now());
  telemetry.queries.push(query);
  telemetry.inFlightByQuery[query] =
    (telemetry.inFlightByQuery[query] ?? 0) + 1;
  telemetry.maxInFlightByQuery[query] = Math.max(
    telemetry.maxInFlightByQuery[query] ?? 0,
    telemetry.inFlightByQuery[query]);
  let released = false;
  return () => {
    if (released) {
      return;
    }
    released = true;
    telemetry.logicalInFlight = Math.max(0, telemetry.logicalInFlight - 1);
    telemetry.logicalRetired += 1;
    telemetry.inFlight = Math.max(0, telemetry.inFlight - 1);
    telemetry.inFlightByQuery[query] = Math.max(
      0,
      (telemetry.inFlightByQuery[query] ?? 1) - 1);
    telemetry.aborted += 1;
  };
}

function recordLogicalPublication(url) {
  const telemetry = requestTelemetryFor(url.pathname);
  const query = url.search;
  const publishedAt = Date.now();
  telemetry.publications += 1;
  telemetry.publicationTimes.push(publishedAt);
  telemetry.publicationsByQuery[query] =
    (telemetry.publicationsByQuery[query] ?? 0) + 1;
  (telemetry.publicationTimesByQuery[query] ??= []).push(publishedAt);
}

function sharedPublisherKey(url) {
  const parameters = [...url.searchParams.entries()]
    .filter(([key]) => key !== "intervalMs")
    .sort(([leftKey, leftValue], [rightKey, rightValue]) =>
      leftKey.localeCompare(rightKey) || leftValue.localeCompare(rightValue));
  return `${url.pathname}?${new URLSearchParams(parameters)}`;
}

function acquireNdjsonPublisher(key, subscriber) {
  const publisher = ndjsonPublishers.get(key) ?? {
    subscribers: new Set(),
    intervalMs: 0,
    timer: null
  };
  publisher.subscribers.add(subscriber);
  ndjsonPublishers.set(key, publisher);
  rescheduleNdjsonPublisher(publisher);
  return publisher;
}

function releaseNdjsonPublisher(key, publisher, subscriber) {
  publisher.subscribers.delete(subscriber);
  if (publisher.subscribers.size === 0) {
    if (publisher.timer !== null) {
      clearTimeout(publisher.timer);
    }
    ndjsonPublishers.delete(key);
    return;
  }
  rescheduleNdjsonPublisher(publisher);
}

function rescheduleNdjsonPublisher(publisher) {
  const intervalMs = Math.min(
    ...[...publisher.subscribers].map((subscriber) => subscriber.intervalMs));
  if (publisher.timer !== null) {
    clearTimeout(publisher.timer);
  }
  publisher.intervalMs = intervalMs;
  const current = Date.now();
  const nextBoundary = (Math.floor(current / intervalMs) + 1) * intervalMs;
  publisher.timer = setTimeout(
    () => publishNdjsonBoundary(publisher),
    Math.max(0, nextBoundary - current));
}

function publishNdjsonBoundary(publisher) {
  publisher.timer = null;
  const publishedAt = now();
  for (const subscriber of [...publisher.subscribers]) {
    subscriber.publish(publishedAt);
  }
  if (publisher.subscribers.size > 0) {
    rescheduleNdjsonPublisher(publisher);
  }
}

function pushInterval(url) {
  if (url.pathname === "/api/access-requests/subscribe") return 100;
  const value = Number(url.searchParams.get("intervalMs"));
  return Number.isFinite(value)
    ? Math.min(60_000, Math.max(100, Math.round(value)))
    : 1_000;
}

function text(response, status, payload) {
  response.writeHead(status, { "Content-Type": "text/plain; charset=utf-8", "Cache-Control": "no-store" });
  response.end(payload);
}

function readBody(request) {
  return new Promise((resolveBody, rejectBody) => {
    let body = "";
    request.setEncoding("utf8");
    request.on("data", (chunk) => { body += chunk; });
    request.on("end", () => resolveBody(body));
    request.on("error", rejectBody);
  });
}

function mimeType(pathname) {
  switch (extname(pathname).toLowerCase()) {
    case ".html": return "text/html; charset=utf-8";
    case ".js": return "text/javascript; charset=utf-8";
    case ".css": return "text/css; charset=utf-8";
    case ".json": return "application/json; charset=utf-8";
    case ".svg": return "image/svg+xml";
    case ".png": return "image/png";
    default: return "application/octet-stream";
  }
}

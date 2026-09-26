import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const port = Number(process.env.RM_FRONTEND_HARNESS_PORT || 4177);
const staticRoot = process.env.RM_FRONTEND_STATIC_ROOT
  ? resolve(process.env.RM_FRONTEND_STATIC_ROOT)
  : fileURLToPath(new URL("../../../../../Resource Manager/Resource Manager-APP/wwwroot/", import.meta.url));
const now = () => new Date().toISOString();
const defaultScenario = {
  capabilities: "ready",
  settings: "ready",
  settingsConflict: false,
  settingsGetDelayMs: 0,
  settingsPatchDelayMs: 0,
  settingsRevision: "frontend-harness-r1",
  settingsTheme: "system",
  localSystemIntervalMs: null,
  deviceTopologyDelayMs: 0,
  animations: "normal",
  gpuGrade: "normal",
  resourceBreakdownGeneration: 0,
  resourceSegmentCount: 1,
  resourceCpuValue: null,
  resourceTableVariant: "normal",
  resourceTableRowCount: null,
  debugMode: false,
  runtimeEffectOwners: false,
  optimizationRuntime: false,
  optimizationReports: "ready",
  softwareIssues: true,
  operations: "empty",
  operationCanceled: false,
  cpuTopologyEmpty: false,
  gpuPlacementEnabled: false,
  unknownGpuScore: false,
  pausedSubscriptionPaths: []
};
const scenario = structuredClone(defaultScenario);
const requestTelemetry = Object.create(null);
const ndjsonPublishers = new Map();
const operationSubscribers = new Set();
let deviceTopologyRevision = 0;
let operationsPublicationRevision = 0;
let operationsCurrentValue = null;
const defaultAppSettings = {
  version: "1.0.23",
  appearance: {
    theme: scenario.settingsTheme,
    animations: scenario.animations,
    language: "zh-CN"
  },
  debug: {
    debugModeEnabled: scenario.debugMode
  }
};
const appSettings = structuredClone(defaultAppSettings);
let metricSlot = {
  observedAt: now()
};
let resourceSlotCapturedAt = now();

const metricCatalog = [
  metric("cpu.usage", "处理器", "CPU", "%"),
  metric("cpu.frequency", "处理器频率", "CPU", "GHz"),
  metric("cpu.frequencyPercent", "处理器频率比例", "CPU", "%"),
  metric("memory.usage", "内存", "Memory", "GB"),
  metric("memory.percent", "内存占用", "Memory", "%")
];
const appSettingsResult = (runtimeApplicationDisposition = "committedAndApplied") => ({
  settings: structuredClone(appSettings),
  revision: scenario.settingsRevision,
  runtimeApplicationDisposition
});
const dashboardSettingsResult = {
  settings: {
    version: 14,
    cards: [
      { id: "cpu", main: "cpu.usage", small: ["cpu.frequency", "cpu.frequencyPercent"] },
      { id: "memory", main: "memory.usage", small: ["memory.percent"] }
    ],
    resourceBars: [
      { id: "resource-cpu-usage", metricId: "cpu.usage", scaleMode: "capacity" },
      { id: "resource-memory-usage", metricId: "memory.usage", scaleMode: "capacity" }
    ],
    resourceTableColumns: tableColumnSettings(),
    resourceTableProcessColumns: tableColumnSettings(true)
  },
  updatedAt: now(),
  source: { kind: "Persisted", sourceVersion: 14, recoveryDisposition: "none" }
};
const mockSoftware = {
  id: "frontend-harness.software",
  name: "示例软件",
  kind: "Other",
  displayKind: "普通软件",
  state: "installed",
  message: "已安装",
  executablePaths: ["C:\\FrontendHarness\\example.exe"],
  rootPaths: ["C:\\FrontendHarness"],
  processIds: [4242],
  processNames: ["example.exe"],
  operations: { canUninstall: false },
  issues: [
    {
      id: "fixture-known-vulnerability",
      kind: "KnownSecurityVulnerability",
      severity: "Warning",
      source: "StaticCatalog",
      label: "存在已公开安全漏洞",
      message: "WinRing0x64.sys 与已公开漏洞记录匹配。",
      dynamic: false,
      references: [
        { label: "CVE-2020-14979", url: "https://nvd.nist.gov/vuln/detail/CVE-2020-14979" }
      ],
      reportIds: []
    },
    {
      id: "dynamic:AbnormalMemoryUsage:frontend-harness.software",
      kind: "AbnormalMemoryUsage",
      severity: "Info",
      source: "DynamicReport",
      label: "内存占用异常",
      message: "当前报告判定该非游戏软件的内存占用异常。",
      dynamic: true,
      references: [],
      reportIds: ["hm-report-memory"]
    },
    {
      id: "dynamic:LongSystemInterrupts:frontend-harness.software",
      kind: "LongSystemInterrupts",
      severity: "Critical",
      source: "DynamicReport",
      label: "造成长系统中断",
      message: "example.sys 是同一采样窗口中的主要归因驱动。",
      dynamic: true,
      references: [],
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
  if (path === "/api/control/objects") {
    return json(response, 200, { readAt: now(), objects: [{
      id: "gpu:test", kind: "gpu", displayName: "Fixture GPU",
      platform: { operatingSystem: "windows", vendor: "nvidia" }, isControllable: true,
      capabilities: [
        { id: "gpu.temperature-limit", label: "Temperature limit", valueKind: "number", supported: false, unavailableReason: "Read-only hardware reading", unavailableKind: "platform", requiredAccessLevel: "normal" },
        { id: "gpu.dynamic-boost-enabled", label: "Dynamic Boost", valueKind: "toggle", supported: true, requiredAccessLevel: "normal" },
        { id: "gpu.voltage-offset", label: "Voltage", valueKind: "number", supported: false, unavailableReason: "Requires Root", unavailableKind: "access-level", requiredAccessLevel: "root", range: { minimum: -100, maximum: 100, step: 1, unit: "mV", defaultValue: 0 } }
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
        configuredRuleCount: 7,
        availableRuleCount: 7,
        activeReportCount: 0,
        trustedCount: 0,
        protectedCount: 0,
        sampleIntervalSeconds: 10
      }
    });
  }
  if (path === "/api/optimization/smart/status" && request.method === "GET") {
    return json(response, 200, {
      mode: "normal",
      schedulerRunning: true,
      lastRunAt: null,
      lastRestoreAt: null,
      pendingChangeCount: 0,
      appliedTargetCount: 0,
      message: "frontend performance harness"
    });
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
      await delay(normalizeDelay(scenario.settingsPatchDelayMs));
      if (scenario.settingsConflict === true
        || patch.expectedRevision !== scenario.settingsRevision) {
        return json(response, 409, appSettingsResult("revisionConflict"));
      }
      mergeRecord(appSettings, patch.changes);
      scenario.settingsRevision = nextSettingsRevision(scenario.settingsRevision);
      scenario.settingsTheme = appSettings.appearance?.theme ?? "system";
      scenario.animations = appSettings.appearance?.animations ?? "normal";
      scenario.debugMode = appSettings.debug?.debugModeEnabled === true;
      return json(response, 200, appSettingsResult());
    }
    return json(response, 405, { error: "settings method not supported" });
  }
  if (path === "/api/settings/app/reapply") {
    return json(response, 200, appSettingsResult());
  }
  if (path === "/api/settings/dashboard") {
    return json(response, 200, dashboardSettingsResult);
  }
  if (path === "/api/metrics/catalog") {
    return json(response, 200, metricCatalog);
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
    const breakdown = resourceBreakdown(
      capturedAt,
      new Set(url.searchParams.getAll("processDetails")));
    return json(response, 200, {
      version: 9,
      capturedAt,
      breakdown,
      table: resourceTable(
        url.searchParams.get("mode") === "process",
        capturedAt)
    });
  }
  if (path === "/api/resource-breakdown/snapshot") {
    return json(
      response,
      200,
      resourceBreakdown(
        resourceSlotCapturedAt,
        new Set(url.searchParams.getAll("processDetails"))));
  }
  if (path === "/api/resource-table/snapshot") {
    return json(response, 200, resourceTable(url.searchParams.get("mode") === "process"));
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
      cpuGrade: "normal",
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
  if (path === "/api/gpu-placement/software/frontend-harness.software") {
    return json(response, 200, {
      softwareId: mockSoftware.id,
      softwareName: mockSoftware.name,
      softwarePolicy: {
        softwareId: mockSoftware.id,
        softwareName: mockSoftware.name,
        enabledMode: "Manual",
        maxRisk: "Low",
        allowedProviders: [],
        schedulingMode: "Ordinary",
        startupTargetGpu: "SystemDefaultGpu",
        targetGpu: "SystemDefaultGpu",
        runtimeSchedulingMode: "Ordinary",
        explicitSelectionMode: "DefaultSkip",
        runtimeHotSwitchEnabled: false,
        preferredRuntimeSwitchMethod: "FutureFrameTakeover",
        baseScoreOverride: null,
        processOverrideAllowed: false,
        gpuExclusive: false,
        absolutePerformanceModeEnabled: false,
        cpuMaximumOccupancyMode: "AllCores",
        cpuExclusiveLocksAffinity: false,
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
  if (path === "/api/components" || path === "/api/controlled") {
    return json(response, 200, []);
  }
  if (path === "/api/software") {
    return json(response, 200, [{
      ...mockSoftware,
      issues: scenario.softwareIssues ? mockSoftware.issues : []
    }]);
  }
  return json(response, 404, { error: "frontend harness route not configured" });
}

function fixtureOperations() {
  const active = {
    id: "000000000000000000000000000000a1",
    kind: "component.install",
    domainKey: "component.frontend-harness",
    title: "安装前端测试组件",
    state: scenario.operationCanceled ? "canceled" : "running",
    configurationGeneration: "1",
    stateRevision: scenario.operationCanceled ? "2" : "1",
    attemptNumber: 1,
    maximumAttempts: 3,
    createdAt: "2026-08-22T12:00:00.000Z",
    updatedAt: scenario.operationCanceled
      ? "2026-08-22T12:00:03.000Z"
      : "2026-08-22T12:00:01.000Z",
    completedAt: scenario.operationCanceled ? "2026-08-22T12:00:03.000Z" : null,
    cancelRequested: scenario.operationCanceled,
    progress: scenario.operationCanceled ? null : {
      sequence: "1",
      percent: 40,
      bytesDone: "4096",
      bytesTotal: "10240",
      speedBytesPerSecond: "1024",
      stage: "install",
      message: "正在安装"
    },
    result: null,
    error: null
  };
  return [
    active,
    {
      id: "000000000000000000000000000000a2",
      kind: "migration.restore",
      domainKey: "migration.frontend-harness",
      title: "恢复示例软件数据",
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
      result: "恢复完成",
      error: null
    }
  ];
}

function fixtureManyOperations() {
  return Array.from({ length: 24 }, (_, index) => ({
    id: `0000000000000000000000000000${(index + 16).toString(16).padStart(4, "0")}`,
    kind: "component.install",
    domainKey: `component.frontend-harness.${index + 1}`,
    title: `已完成前端测试组件 ${index + 1}`,
    state: "succeeded",
    configurationGeneration: "1",
    stateRevision: "3",
    attemptNumber: 1,
    maximumAttempts: 3,
    createdAt: "2026-08-22T11:40:00.000Z",
    updatedAt: "2026-08-22T11:40:03.000Z",
    completedAt: "2026-08-22T11:40:03.000Z",
    cancelRequested: false,
    progress: null,
    result: "安装完成",
    error: null
  }));
}

function fixtureHdmiPort() {
  return {
    id: "fixture-hdmi-1",
    isPhysicalConnector: true,
    displayName: "Fixture HDMI",
    connectorKind: "hdmi",
    busKind: "display",
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
    display: null,
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

function delay(milliseconds) {
  return new Promise((resolveDelay) => setTimeout(resolveDelay, milliseconds));
}

function applyScenarioPatch(patch) {
  Object.assign(scenario, patch);
  settleMockValueSlots(patch);
  if (Object.hasOwn(patch, "operations")
    || Object.hasOwn(patch, "operationCanceled")) {
    commitOperationsValue();
  }
  if (Number.isSafeInteger(patch.localSystemIntervalMs)
    && patch.localSystemIntervalMs > 0) {
    appSettings.performance ??= {};
    appSettings.performance.localSystemRefreshIntervalMs = {
      mode: "custom",
      preset: "responsive",
      customValue: patch.localSystemIntervalMs
    };
  }
  if (typeof patch.settingsTheme === "string") {
    appSettings.appearance.theme = patch.settingsTheme;
  }
  if (typeof patch.animations === "string") {
    appSettings.appearance.animations = patch.animations;
  }
  if (typeof patch.debugMode === "boolean") {
    appSettings.debug.debugModeEnabled = patch.debugMode;
  }
}

function resetScenario() {
  for (const key of Object.keys(scenario)) {
    delete scenario[key];
  }
  Object.assign(scenario, structuredClone(defaultScenario));
  for (const key of Object.keys(appSettings)) {
    delete appSettings[key];
  }
  Object.assign(appSettings, structuredClone(defaultAppSettings));
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

function metric(id, label, group, unit) {
  return { id, label, group, unit, preferredSlot: "main", selectable: true };
}

function tableColumnSettings(process = false) {
  return [
    { id: "name", visible: true, width: 260 },
    ...(process ? [{ id: "pid", visible: true, width: 76 }] : []),
    { id: "status", visible: true, width: 92 },
    { id: "cpu", visible: true, width: 86 },
    { id: "memory", visible: true, width: 110 }
  ];
}

function metricSnapshot(requestedIds) {
  const allItems = {
    "cpu.usage": { id: "cpu.usage", displayValue: "12%", numericValue: 12, percent: 12 },
    "cpu.frequency": { id: "cpu.frequency", displayValue: "4.2 GHz", numericValue: 4.2 },
    "cpu.frequencyPercent": { id: "cpu.frequencyPercent", displayValue: "88%", numericValue: 88, percent: 88 },
    "memory.usage": { id: "memory.usage", displayValue: "8.0 GB", numericValue: 8, percent: 25 },
    "memory.percent": { id: "memory.percent", displayValue: "25%", numericValue: 25, percent: 25 }
  };
  const knownMetricIds = new Map(Object.keys(allItems).map(metricId =>
    [metricId.toLowerCase(), metricId]));
  const selectedIds = requestedIds.length > 0
    ? [...new Set(requestedIds.map(metricId =>
      knownMetricIds.get(metricId.toLowerCase()) ?? metricId))]
    : Object.keys(allItems);
  const items = Object.fromEntries(selectedIds
    .filter(id => Object.hasOwn(allItems, id))
    .map(id => [id, allItems[id]]));
  return {
    version: 4,
    capturedAt: metricSlot.observedAt,
    items
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
    affinityTargetKind: "logical",
    visualLayoutKind: "Grid",
    visualLayoutSource: "frontend-harness",
    physicalCoreCount: 2,
    logicalProcessorCount: 4,
    ccdCount: 1,
    simultaneousMultithreading: true,
    ccds: [{
      id: "ccd:0",
      index: 0,
      label: "CCD 0",
      usagePercent: 31,
      physicalCoreIndexes: [0, 1],
      logicalProcessorIds: [0, 1, 2, 3],
      source: "frontend-harness"
    }],
    physicalCores: [0, 1].map((index) => ({
      id: `core:${index}`,
      index,
      label: `核心 ${index}`,
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
    notes: []
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

function resourceBreakdown(capturedAt = now(), processDetailSoftwareIds = new Set()) {
  const generation = Math.max(
    0,
    Math.floor(Number(scenario.resourceBreakdownGeneration) || 0));
  const cpuValue = scenario.resourceCpuValue ?? [12, 38, 21, 55][generation % 4];
  const segmentCount = normalizeFixtureCount(scenario.resourceSegmentCount, 1, 1000);
  const softwareCatalog = Array.from({ length: segmentCount }, (_, index) => [
    index === 0 ? "frontend-harness.software" : `frontend-harness.software.${index}`,
    index === 0 ? "示例软件" : `示例软件 ${index}`,
    "Other",
    "普通软件"
  ]);
  return {
    version: 8,
    capturedAt,
    softwareCatalog,
    bars: [
      {
        metricId: "cpu.usage",
        label: "处理器",
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
        label: "内存",
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
      }
    ]
  };
}

function resourceTable(processMode, capturedAt = now()) {
  const generation = Math.max(
    0,
    Math.floor(Number(scenario.resourceBreakdownGeneration) || 0));
  const columns = [
    { id: "name", label: "名称", unit: "", visible: true, sortable: true, width: 260 },
    ...(processMode ? [{ id: "pid", label: "PID", unit: "", visible: true, sortable: true, width: 76 }] : []),
    { id: "status", label: "状态", unit: "", visible: true, sortable: true, width: 92 },
    { id: "cpu", label: "CPU", unit: "%", visible: true, sortable: true, width: 86 },
    { id: "memory", label: "内存", unit: "MB", visible: true, sortable: true, width: 110 }
  ];
  const common = {
    softwareName: "示例软件",
    softwareId: "frontend-harness.software",
    processCount: 1,
    processIds: [4242],
    processNames: ["example.exe"],
    executablePaths: ["C:\\FrontendHarness\\example.exe"],
    impactScore: 12,
    status: "running",
    values: {
      cpu: { displayValue: "", unit: "%", value: 12, percent: 12, heatPercent: 12 },
      memory: { displayValue: "", unit: "B", value: 536870912, sharedValue: 134217728, heatPercent: 10 }
    }
  };
  const rows = resourceTableRows(processMode, common);
  return {
    capturedAt,
    columns,
    rows,
    sort: { columnId: "cpu", direction: "desc" },
    viewMode: processMode ? "process" : "software"
  };
}

function fixtureDeviceTopologyState(capturedAt = now()) {
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
      ports: [fixtureHdmiPort()],
      notes: []
    },
    contentGeneration: 1,
    stateRevision: ++deviceTopologyRevision,
    source: "live",
    lastSuccessAt: capturedAt,
    lastAttemptAt: capturedAt,
    failureCode: null,
    attemptDiagnostics: []
  };
}

function resourceTableRows(processMode, common) {
  if (scenario.resourceTableVariant === "empty") {
    return [];
  }
  if (scenario.resourceTableVariant === "unavailable-actions") {
    return [{
      ...common,
      id: "software:unavailable-actions",
      depth: 0,
      kind: "software",
      name: "无可用操作条目",
      softwareId: null,
      softwareName: null,
      processIds: [],
      processNames: [],
      executablePaths: [],
      processCount: 0,
      values: common.values
    }];
  }

  const rowCount = normalizeFixtureCount(
    scenario.resourceTableRowCount,
    scenario.resourceTableVariant === "many" ? 80 : 1,
    1000);
  return Array.from({ length: rowCount }, (_, index) => {
    const suffix = index === 0 ? "" : `.${index}`;
    const processId = 4242 + index;
    const softwareId = `frontend-harness.software${suffix}`;
    return processMode
      ? {
          ...common,
          id: `process:${processId}`,
          depth: 0,
          kind: "process",
          name: `example-${index}.exe`,
          softwareId,
          softwareName: `示例软件 ${index}`,
          processId,
          processStartKey: String(132000000000000000n + BigInt(processId)),
          processIds: [processId],
          values: { ...common.values, pid: { displayValue: String(processId) } }
        }
      : {
          ...common,
          id: `software:${softwareId}`,
          depth: 0,
          kind: "software",
          name: index === 0 ? "示例软件" : `示例软件 ${index}`,
          softwareId,
          softwareName: index === 0 ? "示例软件" : `示例软件 ${index}`,
          processIds: [processId]
        };
  });
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
      processDetailSoftwareIds.has(softwareId) ? [[
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
  return {
    schema: "host-manager.operations.state.v1",
    capturedAt: now(),
    publicationRevision: String(++operationsPublicationRevision),
    configurationGeneration: "1",
    ready: true,
    persistenceFaulted: false,
    faultStage: null,
    faultMessage: null,
    operations: scenario.operations === "mixed"
      ? fixtureOperations()
      : scenario.operations === "many"
        ? fixtureManyOperations()
        : []
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
  for (const item of body.subscriptions) {
    if (typeof item?.id !== "string"
      || !/^[A-Za-z0-9._:-]{1,128}$/.test(item.id)
      || ids.has(item.id)
      || typeof item.path !== "string") {
      return json(response, 400, { error: "invalid subscription channel item" });
    }
    ids.add(item.id);
    const requestUrl = parseSubscriptionChannelSelector(item.path);
    if (requestUrl === null) {
      return json(response, 400, { error: "unsupported subscription path" });
    }
    const operation = requestUrl.pathname === "/api/operations/subscribe";
    const createPayload = operation ? null : subscriptionPayloadFactory(requestUrl);
    if (!operation && createPayload === null) {
      return json(response, 400, { error: "unsupported subscription path" });
    }
    subscriptions.push({
      id: item.id,
      requestUrl,
      operation,
      createPayload
    });
  }

  response.writeHead(200, {
    "Content-Type": "application/x-ndjson; charset=utf-8",
    "Cache-Control": "no-store",
    "X-Content-Type-Options": "nosniff"
  });
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

    const subscriber = {
      response,
      requestUrl: subscription.requestUrl,
      intervalMs: pushInterval(subscription.requestUrl),
      createPayload: subscription.createPayload,
      publish(publishedAt) {
        if (response.destroyed || response.writableEnded
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
  "/api/metrics/gpu-specialized/subscribe",
  "/api/resource-monitor/subscribe",
  "/api/adapters/resource-manager/scheduling/subscribe",
  "/api/local-system/status/subscribe",
  "/api/device-topology/state/subscribe",
  "/api/cpu/topology/subscribe",
  "/api/cpu/residency/subscribe",
  "/api/optimization/smart/state/subscribe",
  "/api/operations/subscribe"
]);

function parseSubscriptionChannelSelector(value) {
  if (!value
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

function subscriptionPayloadFactory(url) {
  switch (url.pathname) {
    case "/api/control/actual/subscribe":
      return publishedAt => ({ readAt: publishedAt, values: [
        { objectId: "gpu:test", capabilityId: "gpu.temperature-limit", number: 89, unit: "°C" },
        { objectId: "gpu:test", capabilityId: "gpu.dynamic-boost-enabled", toggle: false },
        { objectId: "gpu:test", capabilityId: "gpu.voltage-offset", number: 0, unit: "mV" }
      ] });
    case "/api/metrics/subscribe": {
      const ids = url.searchParams.getAll("ids");
      return (publishedAt) => {
        metricSlot.observedAt = publishedAt;
        return metricSnapshot(ids);
      };
    }
    case "/api/metrics/gpu-specialized/subscribe":
      return (publishedAt) => ({
        version: 2,
        capturedAt: publishedAt,
        adapters: []
      });
    case "/api/resource-monitor/subscribe": {
      const processDetails = new Set(url.searchParams.getAll("processDetails"));
      const processMode = url.searchParams.get("mode") === "process";
      return (publishedAt) => {
        resourceSlotCapturedAt = publishedAt;
        const capturedAt = resourceSlotCapturedAt;
        return {
          version: 9,
          capturedAt,
          breakdown: resourceBreakdown(capturedAt, processDetails),
          table: resourceTable(processMode, capturedAt)
        };
      };
    }
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
        cpuGrade: "normal",
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
        version: 2,
        lastRunAt: null,
        lastRestoreAt: null,
        message: "frontend harness",
        appliedPlacements: []
      });
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

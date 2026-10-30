import { componentFixture, componentVersionFixture } from "./componentFixtures.js";
import { canonicalFieldNames } from "../../src/features/monitor/canonicalFields.ts";
import { reportRules } from "../fixtures/reportRuleSettings.ts";
import { kernelLeakReportFrames } from "../fixtures/kernelLeakReports.js";
import * as showcase from "./readmeShowcaseFixture.js";
import { createMemoryProtectionFixture, defaultMemoryProtectionSettings, memoryProtectionScenarios, defaultSoftwarePolicy, invalidMemoryProtectionPatch } from "./memoryProtectionFixture.mjs";
import { mockBaseScoreResolution } from "./baseScoreFixture.mjs";
import { defaultMemoryCpuTierSettings, invalidMemoryCpuTierSettings, memoryCpuTierReading, memoryCpuTierScenarios } from "./memoryCpuTierFixture.mjs";
import { loadSliceReplay } from "../realData/replaySlice.mjs";
import { generatedMetricDefinitions, generatedMetricItems, generatedGpuScores, generatedFieldDevices } from "../realData/generatedMockData.mjs";
import { generateScenario } from "../realData/generateScenario.mjs";
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, resolve, sep, isAbsolute } from "node:path";
import { fileURLToPath } from "node:url";

let port = Number(process.env.RM_FRONTEND_HARNESS_PORT || 4177);
const staticRoot = process.env.RM_FRONTEND_STATIC_ROOT
  ? resolve(process.env.RM_FRONTEND_STATIC_ROOT)
  : fileURLToPath(new URL("../../../../../src/Core/wwwroot/", import.meta.url));
const now = () => new Date().toISOString();
const realDataProfile = JSON.parse(await readFile(new URL("../fixtures/realDataProfile.json", import.meta.url), "utf8"));
let sliceReplay = await loadSliceReplay(process.env.RM_REAL_SLICE);
const journeyControlledPaths = new Set();
const canReplay = path => !scenario.readmeShowcase && !journeyControlledPaths.has(new URL(path, "http://mock").pathname);
const recordedEdidState = JSON.parse(await readFile(process.env.RM_EDID_UI_STATE_JSON || new URL("../fixtures/displayEdid.json", import.meta.url), "utf8"));
const recordedType3State = JSON.parse(await readFile(process.env.RM_EDID_UI_TYPE3_STATE_JSON || new URL("../fixtures/displayEdidType3.json", import.meta.url), "utf8"));
const typeXWirePath = process.env.RM_EDID_UI_TYPEX_STATE_JSON;
if (typeXWirePath && !isAbsolute(typeXWirePath)) throw new Error("TypeX wire path must be absolute");
const recordedTypeXState = typeXWirePath ? JSON.parse(await readFile(typeXWirePath, "utf8")) : null;
// Real device/user data stays in its original file, outside the repository.
const recordedTopologyPath = process.env.RM_DEVICE_TOPOLOGY_STATE_JSON;
if (recordedTopologyPath && !isAbsolute(recordedTopologyPath)) throw new Error("Device topology wire path must be absolute");
const recordedTopologyFile = recordedTopologyPath ? JSON.parse(await readFile(recordedTopologyPath, "utf8")) : null;
const recordedTopologyState = recordedTopologyFile?.responses ? recordedTopologyFile.responses[0].body : recordedTopologyFile;
// DisplayID Type V/VI、Adaptive-Sync 与动态时序范围：真实 parser／生产 DTO 生成的仓库夹具，按场景名选用。
const recordedDeclarationStates = Object.fromEntries(await Promise.all(Object.entries({
  typeVVi: "displayEdidTypeVVi.json", typeVViClock: "displayEdidTypeVViClock.json", adaptiveSync: "displayEdidAdaptiveSync.json",
  adaptiveSyncZero: "displayEdidAdaptiveSyncZero.json", dynamicRange: "displayEdidDynamicRange.json"
}).map(async ([name, file]) => [name, JSON.parse(await readFile(new URL(`../fixtures/${file}`, import.meta.url), "utf8"))])));
const defaultScenario = {
  memoryProtection: "no-point",
  // 内存与 CPU 取舍的 GET：ready 正常表，unknown-option 多一项前端不认识的名称，malformed 回复不合合同，error 读取失败。
  memoryCpuTier: "ready",
  // 新建、切换、删除正常运行点的响应延迟，用来看按钮的进行中状态。
  memoryProtectionPointChangeDelayMs: 0,
  slice: null,
  generatedData: null,
  capabilities: "ready",
  settings: "ready",
  settingsConflict: false,
  settingsRuntimeDisposition: "committedAndApplied",
  settingsGetDelayMs: 0,
  settingsPatchDelayMs: 0,
  settingsRevision: "frontend-harness-r1",
  overlaySaveDelayMs: 0,
  overlaySaveFailure: false,
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
  deviceTopologyMachineIdentity: null,
  deviceTopologyGraphicsDock: false,
  deviceTopologyExternalMachine: false,
  deviceTopologyNumericUsb: false,
  deviceTopologyWireless: null,
  deviceTopologyTransport: false,
  // 接一块带分区和卷（F:）的 USB 移动硬盘；卷的可用空间（Sampled）只经 /api/device-topology/fields/subscribe 发。
  deviceTopologyStorage: false,
  // 卷 F: 的可用空间，后端写无损十进制字符串；null 表示后端这一帧没有值。
  deviceTopologyVolumeFreeBytes: "250092956672",
  // 接一部手机（端口带 smartDevice 和 informationIdentity）；电量和存储剩余是 Sampled，只经字段订阅发。
  deviceTopologyMobile: false,
  // 手机自报的电量百分比；null 表示没有值。
  deviceTopologyMobileBatteryPercent: 76,
  // 手机内部存储的剩余字节，后端写无损十进制字符串；null 表示没有值。
  deviceTopologyMobileStorageFreeBytes: "48318382080",
  // 采集失败（state failed、failureCode 和一条诊断）；快照沿用最后一次成功提交的那份。
  deviceTopologyFailure: false,
  // 整机 BIOS 版本（snapshot.system.biosVersion）：只在整机图里显示，改它是一次不动清单和详情的结构变化。
  deviceTopologyBiosVersion: null,
  displayEdid: "off",
  resourceBreakdownGeneration: 0,
  resourceSegmentCount: 1,
  // "audit"：资源条换成一组混合数据（各展示分组、很小的段、系统/驱动保留残差），值随 resourceBreakdownGeneration 变；
  // 默认 "uniform" 是 resourceSegmentCount 个等值的“其它”软件。
  resourceSoftwareMix: "uniform",
  resourceDetailsDelayMs: 0,
  resourceCpuValue: null,
  resourceTableVariant: "normal",
  resourceTableRowCount: null,
  // 整机内存的系统/驱动残差按后端计数器叶子拆开（PhysicalMemoryResidualKinds）：PID -1，名字为空，池标签例外。
  resourceMemoryResidualLeaves: false,
  resourceTableAccountingFixture: false,
  resourceTableTotalsMissing: [],
  // 读不到总量的指标换成后端的另一个原因代码，例如没有独立显存读数的 GPU 发 gpu-dedicated-memory-metrics-unsupported。
  resourceTableTotalsReasons: {},
  debugMode: false,
  runtimeEffectOwners: false,
  optimizationRuntime: false,
  optimizationReports: "ready",
  smartPlacements: [],
  // 智能调度历史（GET /api/optimization/smart/records/history）：后端历史表里的条目，序号从新到旧。
  smartRecordHistory: [],
  // 软件列表里示例软件之外的软件（{ id, name }），设置 → 性能浮层的多软件情形用。
  extraSoftware: [],
  // 后端调度状态（GET /api/optimization/smart/status 与它的推送订阅同源）；只写与默认值不同的字段。
  smartStatus: {},
  // 调度段结果：smartStatusSegments 里的一种（后端 tests/App 的 smart-status 夹具三例），smartStatus 再覆盖在上面。
  smartStatusSegment: "success",
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
  // CPU residency publishes a typed unavailable value with the owner's reason on GET and on its subscription alike.
  cpuResidencyUnavailableReason: null,
  runtimePolicyDisposition: "committedAndPublished",
  runtimePolicyStartupDisposition: "applied",
  cpuTopologyUsageOffset: 0,
  cpuTopologyLayoutKind: "grid",
  // 规范字段的设备值（/api/metrics/subscribe?fields=）。默认没有任何设备绑定。
  // 字段目录（/api/metrics/fields/catalog/subscribe）由同一份设备值生成。
  metricFieldDevices: [],
  // SamplingObservationStatus overrides for current-value/placeholder contract tests.
  metricStatuses: {},
  metricCpuUsage: 12,
  // 指标目录里额外的部件地址："none" 没有；"reading-source" 加一个带候选来源的核显温度地址
  // （gpu.5.temperature，候选是 metricFieldsProbe 的 readingSourceDevices 字段，后端当前选中 ADLX 次选）。
  metricCatalogAddresses: "none",
  // 额外指标与默认目录一同进入 GET 和订阅信封，不能只改写 GET 响应。
  metricCatalogExtra: [],
  // 后端认定的 GPU 专用显存指标能力（hasDedicatedMemoryMetrics）：true／false／null（不知道）。
  // 写在指标目录的每条 gpu.N.* 定义和 /api/gpu/performance-scores 的条目上，后端总是写这个键。
  gpuDedicatedMemoryMetrics: true,
  // cpu.frequency 的组件安装信息："none" 不需要组件；"missing" 需要传感器组件且未安装；"installed" 已安装但还没有有效读数。
  // 安装信息也是目录内容：变化时后端换目录修订号并推一帧（MetricCatalogPushTests）。
  metricCatalogComponent: "none",
  // 整机总量读不到时资源条保留为显式不可用条，明细保留该指标自己的事实。
  resourceBarMeasureTotalsMissing: [],
  // 物理内存“系统/驱动保留”的分项与页帧明细（docs/runtime_process_attribution_and_io_network_telemetry.md，
  // Physical memory residual）。"off" 保持原来的夹具；其余三种让物理内存条按字节给出、带 residualSource:"counters"
  // 和 resource-residual:memory.usage 这一行（带上这个 id 时给出计数器分项），进程明细带上这个 id 时：
  // "ready" 给就绪的页帧帧，"unavailable" 给不可用帧（原因代码见 physicalMemoryResidualReason），"absent" 不给帧。
  physicalMemoryResidual: "off",
  physicalMemoryResidualReason: "capture-time-limit",
  // 页帧帧自己的采集时间：计数器采集不会改写它，所以它和外层明细帧的 capturedAt 不同（合成值）。
  physicalMemoryResidualCapturedAt: "2026-10-26T05:59:50.000Z",
  // 物理内存条的软件行和保留量的计数器分项（physicalMemoryResidual 不是 "off" 时）。null 用下面 physicalMemoryFixture 的示例软件和分项；
  // 软件给 [软件 id, 名字, kind, displayKind, 字节]，分项给 [kind, 字节, 池标记]，用来摆出后端那样上百个小服务、一个大保留量的条
  // （real-slices/20261027T043049Z 的形状）。保留量 = 已用 − 软件之和；页帧帧不随它变。
  physicalMemorySoftware: null,
  physicalMemoryCounterLeaves: null,
  // CPU 条的软件行（[软件 id, 名字, kind, displayKind, 百分比]）和最后的系统/驱动保留（百分比，它下面是一项 PID -1 的 system-residual，
  // residualDetails 选了 cpu.usage 时才写出）。null 用示例软件、没有保留量；用来摆出真实 CPU 条那样一串细段后跟保留量的形状。
  cpuSoftware: null,
  cpuResidualPercent: 0,
  // 监视控制台卡片设置：保存（PUT）后读回保存的内容；null 时是夹具默认值。
  dashboardSettings: null,
  gpuPlacementEnabled: false,
  unknownGpuScore: false,
  gpuPressureFixture: false,
  gpuOverflowThresholdsMissing: false,
  accessRequests: [],
  // 可信访问密钥的后端状态：ready 正常；read-failure 读取密钥列表返回 503；
  // default-key-off 默认密钥已关闭（界面以管理员身份运行、不带密钥，照常访问，只是列表里没有默认密钥）；
  // key-rejected 后端不接受界面带的密钥，每个 /api 请求都返回 401（用默认密钥连接的未提权界面在默认密钥关闭后的样子）。
  trustedAccess: "ready",
  pausedSubscriptionPaths: [],
  // 这些订阅路径不按自己的间隔发帧，只在 POST /__test/subscriptions/publish 时发一帧（按真机抓到的间隔回放用）。
  manualPublishPaths: [],
  // 这些资源条按后端的“无读数”形状发：保留 id、单位和缩放，总量、容量、百分比为 null，没有软件，带 unavailableReason。
  resourceUnavailableBarIds: [],
  // 这些资源条原样重发上一次发出的值（真机 10-26 的半帧：一帧里只有一半的条变了）。
  resourceFrozenBarIds: [],
  // 产品更新状态（GET /api/updates/product/status 与它的推送订阅同源）：只写与默认状态不同的字段；
  // "invalid" 让推送发出不合合同的帧、GET 返回 503，模拟状态不可用。未知字段和不合合同的值直接拒绝。
  productUpdateStatus: {},
  // 内核泄漏报告（/api/kernel-leaks/subscribe）：取 tests/fixtures/kernelLeakReports.js 的一个场景名。
  // 订阅时先发当前帧，改场景时向现有订阅再发一帧（后端在报告变化时推送）。
  kernelLeakReport: "empty",
  // README 截图的虚构整机（readmeShowcaseFixture.js）：打开后各页改用那里的值；只给 captureReadmeScreenshots.mjs 用。
  readmeShowcase: false
};
const trustedAccessScenarios = new Set(["ready", "read-failure", "default-key-off", "key-rejected"]);
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
// /api/notifications/subscribe：订阅时一次当前列表，之后每次变化一次整份列表。
const backendNotificationSubscribers = new Set();
let backendNotificationList = [];
function publishBackendNotifications(subscriber) {
  subscriber.write({ notifications: structuredClone(backendNotificationList) });
}
const kernelLeakReportSubscribers = new Set();
// POST /api/kernel-leaks/history/clear 之后，当前场景的帧不再带历史条目（后端删除后发布新帧）；换场景或重置时恢复。
let kernelLeakHistoryCleared = false;
// /api/memory-protection/subscribe：和后端一样按 type 分帧。订阅时依次重放 status、records、normalRunningPoint；
// 之后状态每次变化发 status，每条新增或更新的记录发一个 record，生效的正常运行点换了（新建、切换、删除生效的点）时发 normalRunningPoint，
// 已保存的点或生效的点一变就发 normalRunningPoints。
const memoryProtectionSubscribers = new Set();
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
// 计时器要结束的那条录制；/__test/targeted-recordings/complete-pending 用它立即触发同一个结束。
let targetedRecordingCompletionId = null;
let targetedRecordingSequence = 0;
let cpuBaselineOverrideRatio = null;
const targetedRecordings = new Map();
// 录制配置（只在内存里）、录制项目录和“目标是否在运行”。形状和错误代码照后端
// （TargetedRecordingModels.cs、TargetedRecordingProfileService.cs）；默认配置 id 为 default、名称“默认”。
const targetedRecordingFramesItemId = "frames.present";
const targetedRecordingCatalog = [
  { kind: "frames", id: targetedRecordingFramesItemId, unit: "ms", sources: [], naturalIntervalMs: null },
  { kind: "software", id: "cpu.usage", unit: "%", sources: ["resource-breakdown"], naturalIntervalMs: null },
  { kind: "software", id: "memory.usage", unit: "B", sources: ["resource-breakdown"], naturalIntervalMs: null },
  { kind: "metric", id: "cpu.usage", unit: "%", sources: ["cpu.usage@cpu0"], naturalIntervalMs: null },
  { kind: "metric", id: "cpu.temperature", unit: "°C", sources: ["cpu.temperature@cpu0", "cpu.temperature@board"], naturalIntervalMs: 2000 },
  { kind: "metric", id: "memory.usage", unit: "%", sources: ["memory.usage@system"], naturalIntervalMs: null }
];
const targetedRecordingDefaultItems = () => [
  { kind: "frames", id: targetedRecordingFramesItemId },
  { kind: "software", id: "cpu.usage" },
  { kind: "software", id: "memory.usage" },
  { kind: "metric", id: "cpu.usage" },
  { kind: "metric", id: "cpu.temperature" }
].map((item) => ({ ...item, source: "auto", intervalMs: null, syncPrecision: "mid" }));
const targetedRecordingDefaultProfile = () =>
  ({ id: "default", name: "默认", defaultIntervalMs: 1000, items: targetedRecordingDefaultItems() });
let targetedRecordingProfiles = [targetedRecordingDefaultProfile()];
let targetedRecordingProfileSequence = 0;
let targetedRecordingTargetRunning = true;
const defaultAppSettings = {
  version: "1.0.37",
  memoryProtection: defaultMemoryProtectionSettings(),
  memoryCpuTier: defaultMemoryCpuTierSettings(),
  performance: { gpuOverflowUsagePercent: 95, gpuOverflowDedicatedMemoryPercent: 80, realtimeActionBudgetMode: "fixed", realtimeActionBudgetCount: 32, realtimeActionBudgetPercent: 5, keepSmartRecordHistory: false },
  reports: { ruleOverrides: [] },
  control: { keepAlive: false, reassertionIntervalSeconds: 120 },
  debug: {
    debugModeEnabled: scenario.debugMode
  },
  update: { mode: "automatic", periodicCheckEnabled: false, checkIntervalHours: 24 }
};
const appSettings = structuredClone(defaultAppSettings);
const memoryProtection = createMemoryProtectionFixture(() => appSettings.memoryProtection);
const softwarePolicies = new Map();
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
    updatedAt: "2026-10-27T05:00:00Z",
    serviceRecovery: null,
    storagePath: "F:\\MockUserData\\settings.local.json",
    source: { kind: 255, sourceVersion: settings.version, inputSha256: scenario.settingsRevision,
      effectiveSha256: scenario.settingsRevision, rewritePerformed: false, recoveryDisposition: "none",
      recoveryArtifactPath: null, lastKnownGoodPath: null },
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
      label: "造成单次系统中断过长",
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
    const url = new URL(request.url ?? "/", `http://127.0.0.1:${server.address().port}`);
    if (url.pathname === "/__test/scenario" && request.method === "POST") {
      let patch = JSON.parse(await readBody(request));
      if (patch.preset) {
        const generated = generateScenario(realDataProfile, patch.seed ?? 1, patch.preset);
        patch = { ...patch, ...generated.scenarioPatch, generatedData: generated.generatedData };
      } else if (patch.scenarioPatch && patch.generatedData) {
        patch = { ...patch.scenarioPatch, generatedData: patch.generatedData };
      }
      if (patch.generatedData) {
        patch = { ...patch, resourceSegmentCount: patch.generatedData.names.length,
          metricCatalogExtra: generatedMetricDefinitions(patch.generatedData).filter(definition => !metricCatalog.some(base => base.id === definition.id)),
          metricFieldDevices: generatedFieldDevices(patch.generatedData) };
      }
      if (Object.hasOwn(patch, "slice")) sliceReplay = await loadSliceReplay(patch.slice || process.env.RM_REAL_SLICE);
      applyScenarioPatch(patch);
      return json(response, 200, scenario);
    }
    if (url.pathname === "/__test/scenario/reset" && request.method === "POST") {
      resetScenario();
      sliceReplay = await loadSliceReplay(process.env.RM_REAL_SLICE);
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
    if (url.pathname === "/__test/metrics/publish" && request.method === "POST") {
      // A journey can release one original subscription payload at a time while
      // periodic publication is paused, so pending/reading transitions are observable.
      const { kind } = JSON.parse(await readBody(request));
      if (!["catalog", "ids", "fields"].includes(kind)) return json(response, 400, { error: "invalid metric publication kind" });
      journeyControlledPaths.add(kind === "catalog" ? "/api/metrics/fields/catalog/subscribe" : "/api/metrics/subscribe");
      const queries = [];
      for (const publisher of ndjsonPublishers.values()) {
        for (const subscriber of publisher.subscribers) {
          const target = subscriber.requestUrl;
          const selected = kind === "catalog"
            ? target.pathname === "/api/metrics/fields/catalog/subscribe"
            : target.pathname === "/api/metrics/subscribe" && target.searchParams.has("fields") === (kind === "fields");
          if (selected && subscriber.publish(now(), { force: true })) queries.push(`${target.pathname}${target.search}`);
        }
      }
      return json(response, 200, { publications: queries.length, queries });
    }
    if (url.pathname === "/__test/targeted-recordings/target-running" && request.method === "POST") {
      // 旅程用它让示例软件“退出”或“重新运行”：不在运行时 /targets 为空，开始录制回 409 target-not-running。
      const body = JSON.parse(await readBody(request));
      if (typeof body.running !== "boolean") return json(response, 400, { error: "running must be a boolean" });
      targetedRecordingTargetRunning = body.running;
      return json(response, 200, { running: targetedRecordingTargetRunning });
    }
    if (url.pathname === "/__test/targeted-recordings/complete-pending" && request.method === "POST") {
      // 立即做 15 s 计时器本来要做的事：以「达到最长时长」结束计时器等待的那条录制。
      // 旅程用它代替真等 15 s；没有等待中的计时器时返回 409，不猜要结束哪条。
      if (!targetedRecordingCompletionTimer) return json(response, 409, { error: "no pending recording completion" });
      clearTimeout(targetedRecordingCompletionTimer);
      targetedRecordingCompletionTimer = null;
      completeTargetedRecording(targetedRecordingCompletionId, "max-duration");
      return json(response, 200, targetedRecordings.get(targetedRecordingCompletionId)?.header ?? null);
    }
    // 按需发一帧：先合并 patch 进场景，再让该路径的每个发布者立刻发布（manualPublishPaths 里的路径只在这里发帧）。
    if (url.pathname === "/__test/subscriptions/publish" && request.method === "POST") {
      const body = JSON.parse(await readBody(request));
      if (typeof body.path !== "string") return json(response, 400, { error: "path" });
      journeyControlledPaths.add(body.path);
      if (body.patch) applyScenarioPatch(body.patch);
      const publishers = [...ndjsonPublishers.values()].filter((publisher) => publisher.path === body.path);
      for (const publisher of publishers) publishNdjsonBoundary(publisher);
      return json(response, 200, { published: publishers.length, publishedAt: Date.now() });
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
  port = server.address().port;
  process.send?.({ type: "frontend-harness-ready", port });
  console.log(`frontend harness listening on http://127.0.0.1:${port}`);
});

async function handleApi(request, response, url) {
  const path = url.pathname;
  const replayed = request.method === "GET" && !scenario.readmeShowcase ? sliceReplay?.response(path + url.search) : null;
  if (replayed) {
    response.writeHead(replayed.status ?? 200, { "Content-Type": replayed.contentType || "application/json", "Cache-Control": "no-store" });
    return response.end(replayed.encoding === "text" ? String(replayed.body)
      : replayed.encoding === "json" || replayed.contentType?.toLowerCase().includes("json") ? JSON.stringify(replayed.body) : String(replayed.body));
  }
  if (scenario.trustedAccess === "key-rejected") {
    return json(response, 401, { error: "access-key-invalid" });
  }
  if (scenario.readmeShowcase) {
    const value = showcase.showcaseRestResponse(path, request.method, url);
    if (value !== undefined) return json(response, 200, value);
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
      gpuLaunchInterceptionReconciliation: scenario.gpuPlacementEnabled,
      runtimeEffectOwners: scenario.runtimeEffectOwners,
      publicServiceCoordination: false,
      optimizationRuntime: scenario.optimizationRuntime === true
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
        unrecordedReportCount: 0,
        trustedCount: 0,
        protectedCount: 0,
        sampleIntervalSeconds: 10
      }
    });
  }
  // 保护列表：模拟后端没有任何保护条目；要测保护的 journey 自己路由有内容的列表和写入。
  if (path === "/api/optimization/protection" && request.method === "GET") {
    return json(response, 200, []);
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
      // 界面以管理员身份运行、不带密钥：后端按管理员授权修改启动模块。
      const saved = await saveStartupModuleSelection(id, JSON.parse(await readBody(request)));
      return saved.body === null ? json(response, saved.statusCode, {}) : json(response, saved.statusCode, saved.body);
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
    if (scenario.readmeShowcase) {
      // 虚构整机只给每个根的完整树（一次读完）；往下钻的单个节点不在截图里，按不存在处理。
      const tree = showcase.showcaseDiskTree(Number(rootIndex));
      if (!tree || (url.searchParams.has("node") && url.searchParams.get("node") !== String(tree.rootNodeId))) {
        return json(response, 404, { error: "showcase disk layout was not found" });
      }
      response.writeHead(200, { "Content-Type": "application/octet-stream", "Cache-Control": "no-store" });
      response.end(createDiskUsageLayout(tree.rootPath, tree.rootNodeId, tree.rootSize, tree.tiles.map(tile =>
        diskUsageTile(tile.nodeId, tile.parentNodeId, tile.depth, tile.sizeBytes, tile.name, tile.isDirectory, tile.fileCount))));
      return;
    }
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
    const scan = JSON.parse(await readBody(request));
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
      domainKey: "disk-usage:scan",
      subject: { targetKind: scan.scope === "allVolumes" ? "AllVolumes" : "Path",
        targetName: scan.scope === "allVolumes" ? null : scan.target,
        targetId: null, scope: scan.scope, mode: scan.mode, targetPath: scan.target || null },
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
  if (path === "/api/settings/memory-cpu-tier" && request.method === "GET") {
    if (scenario.memoryCpuTier === "error") return text(response, 503, "memory-cpu tier unavailable");
    return json(response, 200, memoryCpuTierReading(appSettings.memoryCpuTier ?? defaultMemoryCpuTierSettings(), scenario.memoryCpuTier));
  }
  if (path === "/api/settings/report-rules" && request.method === "GET") {
    return json(response, 200, { rules: reportRules });
  }
  if (path === "/api/memory-protection" && request.method === "GET") {
    return json(response, 200, memoryProtection.status());
  }
  if (path === "/api/memory-protection/normal-running-point" && ["GET", "POST"].includes(request.method)) {
    if (request.method === "GET") return json(response, 200, memoryProtection.readPoint());
    await delay(normalizeDelay(scenario.memoryProtectionPointChangeDelayMs));
    const created = memoryProtection.createPoint();
    if (created === null) return json(response, 409, { error: "normal-running-point-limit" });
    publishMemoryProtectionStatus();
    publishMemoryProtectionPoint();
    publishMemoryProtectionPoints();
    return json(response, 200, created);
  }
  if (path === "/api/memory-protection/normal-running-points" && request.method === "GET") {
    return json(response, 200, memoryProtection.listPoints());
  }
  if (path === "/api/memory-protection/normal-running-points/active" && request.method === "PUT") {
    const body = JSON.parse(await readBody(request));
    await delay(normalizeDelay(scenario.memoryProtectionPointChangeDelayMs));
    const before = memoryProtection.readPoint()?.id ?? null;
    const selected = memoryProtection.selectPoint(body?.id);
    if (selected === null) return json(response, 404, { error: "normal-running-point-not-found" });
    publishMemoryProtectionStatus();
    if (before !== selected.id) { publishMemoryProtectionPoint(); publishMemoryProtectionPoints(); }
    return json(response, 200, selected);
  }
  const savedPoint = /^\/api\/memory-protection\/normal-running-points\/([^/]+)$/.exec(path);
  if (savedPoint && savedPoint[1] !== "active" && request.method === "DELETE") {
    await delay(normalizeDelay(scenario.memoryProtectionPointChangeDelayMs));
    const before = memoryProtection.readPoint()?.id ?? null;
    if (!memoryProtection.deletePoint(decodeURIComponent(savedPoint[1]))) return json(response, 404, { error: "normal-running-point-not-found" });
    publishMemoryProtectionStatus();
    if ((memoryProtection.readPoint()?.id ?? null) !== before) publishMemoryProtectionPoint();
    publishMemoryProtectionPoints();
    response.writeHead(204);
    return response.end();
  }
  if (path === "/api/memory-protection/reserve/rebuild" && request.method === "POST") {
    const status = memoryProtection.rebuild();
    publishMemoryProtectionStatus();
    return json(response, status.reserve.built ? 200 : 409, status);
  }
  if (path === "/api/settings/app" || (path === "/api/memory-protection" && request.method === "PATCH")) {
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
      if (path === "/api/memory-protection" && (!isRecord(patch.changes)
        || Object.keys(patch.changes).length !== 1 || !Object.hasOwn(patch.changes, "memoryProtection"))) {
        return json(response, 400, { error: "expected-memory-protection-settings-patch" });
      }
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
      const typeProblemPath = invalidMemoryProtectionPatch(patch.changes)
        ?? (Object.hasOwn(patch.changes, "memoryCpuTier") ? invalidMemoryCpuTierSettings(patch.changes.memoryCpuTier) : null)
        ?? settingsValueTypeProblem(patch.changes);
      if (typeProblemPath !== null) {
        return json(response, 409, { ...appSettingsResult("rejectedBeforeCommit"),
          runtimeFailureCode: "invalid-settings-value-type", runtimeFailurePath: typeProblemPath });
      }
      if (Object.hasOwn(patch.changes, "memoryProtection")) {
        const memoryProblem = memoryProtection.validate({ ...appSettings.memoryProtection, ...patch.changes.memoryProtection });
        if (memoryProblem) return json(response, 409, { ...appSettingsResult("rejectedBeforeCommit"), runtimeFailureCode: memoryProblem });
      }
      mergeRecord(appSettings, patch.changes);
      // memoryCpuTier 整节替换：清除一项单独设置就是补丁里少了这个键，逐键合并会把旧值留下。
      if (Object.hasOwn(patch.changes, "memoryCpuTier")) appSettings.memoryCpuTier = structuredClone(patch.changes.memoryCpuTier);
      if (appSettings.memoryProtection) appSettings.memoryProtection.reserves ??= [];
      if (Object.hasOwn(patch.changes, "memoryProtection")) memoryProtection.prepare();
      publishMemoryProtectionStatus();
      scenario.settingsRevision = nextSettingsRevision(scenario.settingsRevision);
      scenario.debugMode = appSettings.debug?.debugModeEnabled === true;
      appSettingsRuntimeDisposition = scenario.settingsRuntimeDisposition;
      return json(response, settingsResultStatus(appSettingsRuntimeDisposition), appSettingsResult());
    }
    if (request.method === "PUT") {
      const settings = JSON.parse(await readBody(request));
      const updateProblem = invalidUpdateSettingsPatch(settings.update);
      if (updateProblem) return json(response, 400, { error: updateProblem });
      const memoryProblem = memoryProtection.validate(settings.memoryProtection ?? defaultMemoryProtectionSettings());
      if (memoryProblem) return json(response, 409, { ...appSettingsResult("rejectedBeforeCommit"), runtimeFailureCode: memoryProblem });
      for (const key of Object.keys(appSettings)) delete appSettings[key];
      Object.assign(appSettings, settings);
      appSettings.memoryProtection ??= defaultMemoryProtectionSettings();
      appSettings.memoryProtection.reserves ??= [];
      memoryProtection.prepare();
      publishMemoryProtectionStatus();
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
  // 清空历史（docs/report_lifecycle_2026-10-28.md）：回复删除的条数；mock 的优化报告集是空的，删 0 条。
  if (path === "/api/optimization/reports/history/clear" && request.method === "POST") {
    return json(response, 200, { removedCount: 0 });
  }
  if (path === "/api/optimization/smart/records/history" && request.method === "GET") {
    const limit = Number(url.searchParams.get("limit"));
    const before = url.searchParams.has("beforeSequence") ? Number(url.searchParams.get("beforeSequence")) : null;
    if (!Number.isInteger(limit) || limit < 1 || limit > 500 || (before !== null && !Number.isInteger(before))) {
      return json(response, 400, { error: "invalid history query" });
    }
    const older = scenario.smartRecordHistory.filter((entry) => before === null || entry.sequence < before);
    const records = older.slice(0, limit);
    return json(response, 200, { records, nextBeforeSequence: older.length > limit ? records.at(-1).sequence : null });
  }
  if (path === "/api/optimization/smart/records/history/clear" && request.method === "POST") {
    const removedCount = scenario.smartRecordHistory.length;
    scenario.smartRecordHistory = [];
    return json(response, 200, { removedCount });
  }
  if (path === "/api/kernel-leaks/history/clear" && request.method === "POST") {
    const items = currentKernelLeakReportFrame()?.items;
    const removedCount = Array.isArray(items) ? items.filter((item) => item.state === "history").length : 0;
    kernelLeakHistoryCleared = true;
    for (const subscriber of [...kernelLeakReportSubscribers]) publishKernelLeakReportValue(subscriber);
    return json(response, 200, { removedCount });
  }
  if (path === "/api/memory-protection/records/history/clear" && request.method === "POST") {
    const removedCount = memoryProtection.clearRecordHistory();
    publishMemoryProtectionFrame({ type: "records", ...memoryProtection.records() });
    return json(response, 200, { removedCount });
  }
  if (path === "/api/notifications" && request.method === "GET") {
    return json(response, 200, { notifications: structuredClone(backendNotificationList) });
  }
  if (path === "/api/targeted-recordings/history/clear" && request.method === "POST") {
    let removedCount = 0;
    for (const [id, entry] of [...targetedRecordings]) {
      if (entry.header.status !== "completed") continue;
      targetedRecordings.delete(id);
      removedCount += 1;
    }
    return json(response, 200, { removedCount });
  }
  if (path === "/api/targeted-recordings/targets" && request.method === "GET") {
    return json(response, 200, targetedRecordingTargetRunning
      ? [{ softwareId: mockSoftware.id, name: "示例软件", processCount: 2 }]
      : []);
  }
  if (path === "/api/targeted-recordings/items/catalog" && request.method === "GET") {
    return json(response, 200, structuredClone(targetedRecordingCatalog));
  }
  if (path === "/api/targeted-recordings/profiles" && request.method === "GET") {
    return json(response, 200, structuredClone(targetedRecordingProfiles));
  }
  if (path === "/api/targeted-recordings/profiles" && request.method === "POST") {
    const body = JSON.parse(await readBody(request));
    const name = typeof body.name === "string" ? body.name.trim() : "";
    if (!name || name.length > 64) return json(response, 400, { error: "targeted-recording-profile-invalid-name" });
    if (targetedRecordingProfiles.length >= 64) return json(response, 400, { error: "targeted-recording-profiles-too-many" });
    let source = null;
    if (body.copyFromId !== undefined && body.copyFromId !== null) {
      source = targetedRecordingProfiles.find((item) => item.id === body.copyFromId) ?? null;
      if (!source) return json(response, 404, { error: "targeted-recording-profile-not-found" });
    }
    const defaultIntervalMs = body.defaultIntervalMs ?? source?.defaultIntervalMs ?? 1000;
    if (!targetedRecordingIntervalValid(defaultIntervalMs)) return json(response, 400, { error: "targeted-recording-interval-out-of-range" });
    const items = body.items ?? (source ? structuredClone(source.items) : targetedRecordingDefaultItems());
    const itemsError = targetedRecordingItemsError(items);
    if (itemsError) return json(response, 400, { error: itemsError });
    const created = { id: `profile-${++targetedRecordingProfileSequence}`, name, defaultIntervalMs, items: normalizeTargetedRecordingItems(items) };
    targetedRecordingProfiles.push(created);
    return json(response, 201, created);
  }
  const targetedProfileMatch = path.match(/^\/api\/targeted-recordings\/profiles\/([^/]+)$/);
  if (targetedProfileMatch && request.method === "PATCH") {
    const profile = targetedRecordingProfiles.find((item) => item.id === decodeURIComponent(targetedProfileMatch[1]));
    if (!profile) return json(response, 404, { error: "targeted-recording-profile-not-found" });
    const body = JSON.parse(await readBody(request));
    const next = structuredClone(profile);
    if (body.name !== undefined) {
      const name = typeof body.name === "string" ? body.name.trim() : "";
      if (!name || name.length > 64) return json(response, 400, { error: "targeted-recording-profile-invalid-name" });
      next.name = name;
    }
    if (body.defaultIntervalMs !== undefined) {
      if (!targetedRecordingIntervalValid(body.defaultIntervalMs)) return json(response, 400, { error: "targeted-recording-interval-out-of-range" });
      next.defaultIntervalMs = body.defaultIntervalMs;
    }
    if (body.items !== undefined) {
      const itemsError = targetedRecordingItemsError(body.items);
      if (itemsError) return json(response, 400, { error: itemsError });
      next.items = normalizeTargetedRecordingItems(body.items);
    }
    Object.assign(profile, next);
    return json(response, 200, profile);
  }
  if (targetedProfileMatch && request.method === "DELETE") {
    const id = decodeURIComponent(targetedProfileMatch[1]);
    if (!targetedRecordingProfiles.some((item) => item.id === id)) return json(response, 404, { error: "targeted-recording-profile-not-found" });
    if (targetedRecordingProfiles.length === 1) return json(response, 409, { error: "targeted-recording-last-profile" });
    targetedRecordingProfiles = targetedRecordingProfiles.filter((item) => item.id !== id);
    response.writeHead(204);
    response.end();
    return;
  }
  if (path === "/api/targeted-recordings" && request.method === "GET") {
    return json(response, 200, [...targetedRecordings.values()].map((entry) => entry.header));
  }
  if (path === "/api/targeted-recordings" && request.method === "POST") {
    const body = JSON.parse(await readBody(request));
    let softwareId = typeof body.softwareId === "string" ? body.softwareId : "";
    const duration = body.maximumDurationSeconds ?? 600;
    if (!softwareId.trim() || !Number.isInteger(duration) || duration < 1 || duration > 3600) {
      response.writeHead(400); response.end(); return;
    }
    if ([...targetedRecordings.values()].some((entry) => entry.header.status === "recording")) {
      response.writeHead(409); response.end(); return;
    }
    if (softwareId.toLowerCase() !== mockSoftware.id.toLowerCase()) { response.writeHead(404); response.end(); return; }
    softwareId = mockSoftware.id;
    // 后端：profileId 和 items 二选一；都没有时用第一份配置。
    let profile = null;
    let items;
    if (body.items !== undefined) {
      const itemsError = targetedRecordingItemsError(body.items);
      if (itemsError) return json(response, 400, { error: itemsError });
      items = normalizeTargetedRecordingItems(body.items);
    } else {
      profile = body.profileId === undefined ? targetedRecordingProfiles[0]
        : targetedRecordingProfiles.find((item) => item.id === body.profileId) ?? null;
      if (!profile) return json(response, 404, { error: "targeted-recording-profile-not-found" });
      items = structuredClone(profile.items);
    }
    if (!targetedRecordingTargetRunning) return json(response, 409, { error: "target-not-running" });
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
        incomplete: false,
        profileId: profile?.id ?? null,
        profileName: profile?.name ?? null,
        items: items.map((item) => targetedRecordingItemReport(item, profile?.defaultIntervalMs ?? 1000, false)),
        processes: [],
        storedBytes: 0,
        maximumReportKib: 4096
      },
      selectedItems: items,
      defaultIntervalMs: profile?.defaultIntervalMs ?? 1000,
      frameIntervals: [],
      fpsDistribution: [],
      resourceSamples: []
    };
    targetedRecordings.set(id, entry);
    if (targetedRecordingCompletionTimer) clearTimeout(targetedRecordingCompletionTimer);
    targetedRecordingCompletionId = id;
    targetedRecordingCompletionTimer = setTimeout(() => {
      targetedRecordingCompletionTimer = null;
      completeTargetedRecording(id, "max-duration");
    }, duration * 1000);
    return json(response, 200, entry.header);
  }
  const targetedRecordingMatch = path.match(/^\/api\/targeted-recordings\/([^/]+)$/);
  const targetedRecordingStopMatch = path.match(/^\/api\/targeted-recordings\/([^/]+)\/stop$/);
  if (targetedRecordingStopMatch && request.method === "POST") {
    const entry = targetedRecordings.get(decodeURIComponent(targetedRecordingStopMatch[1]));
    if (!entry || entry.header.status !== "recording") { response.writeHead(404); response.end(); return; }
    if (targetedRecordingCompletionTimer) clearTimeout(targetedRecordingCompletionTimer);
    targetedRecordingCompletionTimer = null;
    completeTargetedRecording(entry.header.id, "user-stop");
    return json(response, 200, entry.header);
  }
  if (targetedRecordingMatch && request.method === "GET") {
    const entry = targetedRecordings.get(decodeURIComponent(targetedRecordingMatch[1]));
    return entry ? json(response, 200, targetedRecordingReport(entry)) : json(response, 404, { error: "recording not found" });
  }
  if (targetedRecordingMatch && request.method === "DELETE") {
    const id = decodeURIComponent(targetedRecordingMatch[1]);
    const entry = targetedRecordings.get(id);
    if (!entry || entry.header.status !== "completed") { response.writeHead(404); response.end(); return; }
    targetedRecordings.delete(id);
    response.writeHead(204);
    response.end();
    return;
  }
  const targetedExport = path.match(/^\/api\/targeted-recordings\/([^/]+)\/export$/);
  if (targetedExport && request.method === "GET") {
    const entry = targetedRecordings.get(decodeURIComponent(targetedExport[1]));
    if (!entry) { response.writeHead(404); response.end(); return; }
    const format = url.searchParams.get("format") ?? "json";
    if (format === "json") return json(response, 200, { schemaVersion: 1, ...targetedRecordingReport(entry) });
    // 各格式的表头照后端的导出；内容是固定的几行。
    const csv = {
      csv: "kind,utc,processId,processStartKey,source,swapChain,frameTimeMs,scope,metric,value\r\nsummary,,,,,,,,averageFps,100\r\n",
      "long-csv": "time_ms,item,process,value,unit\r\n0,software:cpu.usage,,25,%\r\n0,system:cpu.usage,,40,%\r\n",
      "frames-csv": "time_ms,process_id,process_start_key,source,swap_chain,frame_time_ms\r\n0,123,1234,0,1,9\r\n",
      presentmon: "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped,TimeInSeconds,MsBetweenPresents\r\nexample.exe,123,0x1,Other,-1,0,0,0.000,9.000\r\n"
    }[format];
    if (csv === undefined) return json(response, 400, { error: "unknown export format" });
    response.writeHead(200, { "Content-Type": "text/csv; charset=utf-8" });
    response.end(csv);
    return;
  }
  if (path === "/api/metrics/catalog") {
    return json(response, 200, metricCatalogBody());
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
    return json(response, 200, metricFieldCatalogBody());
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
  const softwarePolicyWrite = path.match(/^\/api\/gpu-placement\/software\/([^/]+)\/policy$/);
  const softwarePolicyDefault = path.match(/^\/api\/gpu-placement\/software\/([^/]+)\/policy\/default$/);
  if (softwarePolicyDefault && request.method === "GET") {
    const policy = defaultSoftwarePolicy(decodeURIComponent(softwarePolicyDefault[1]), url.searchParams.get("softwareName") ?? "");
    if (["game", "high_performance"].includes(url.searchParams.get("softwareKind"))) policy.runtimeHotSwitchEnabled = false;
    return json(response, 200, policy);
  }
  const performanceWrite = path === "/api/cpu/topology/performance-overrides" || path === "/api/gpu/performance-overrides";
  if ((request.method === "PUT" && (softwarePolicyWrite || path === "/api/gpu-placement/process-policy" || performanceWrite))
    || (request.method === "DELETE" && performanceWrite)) {
    let input = request.method === "PUT" ? JSON.parse(await readBody(request)) : null;
    if (softwarePolicyWrite) {
      const id = decodeURIComponent(softwarePolicyWrite[1]);
      input = { ...defaultSoftwarePolicy(id, input.softwareName ?? ""), ...input, softwareId: id, updatedAt: now() };
      softwarePolicies.set(id.toLowerCase(), structuredClone(input));
    }
    const policy = path === "/api/cpu/topology/performance-overrides"
      ? { cpuName: input?.cpuName ?? url.searchParams.get("cpuName"), scoresByCoreIndex: Object.fromEntries((input?.scores ?? []).map(item => [item.coreIndex, item.performanceScore])), updatedAt: now(), storagePath: "frontend-harness" }
      : path === "/api/gpu/performance-overrides"
        ? { scoresByGpuId: Object.fromEntries((input?.scores ?? []).map(item => [item.gpuId, item.performanceScore])), updatedAt: now(), storagePath: "frontend-harness" }
        : input;
    const disposition = scenario.runtimePolicyDisposition;
    const startup = path === "/api/gpu-placement/process-policy" ? scenario.runtimePolicyStartupDisposition : "notAttempted";
    return json(response, disposition === "savedNotApplied" || startup === "applyFailed" ? 409 : disposition === "committedWithDeliveryFailures" ? 202 : 200, {
      policy, startupInterception: null, runtimeApplicationDisposition: disposition,
      startupInterceptionDisposition: startup,
      runtimePlanVersion: disposition === "savedNotApplied" ? null : 1,
      runtimePublicationSequence: disposition === "savedNotApplied" ? null : 1,
      runtimeDeliveryFailureCount: disposition === "committedWithDeliveryFailures" ? 1 : 0,
      failureCode: disposition === "savedNotApplied" ? "runtime-plan-publication-failed" : startup === "applyFailed" ? "startup-interception-apply-failed" : null
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
      gpus: scenario.generatedData ? generatedGpuScores(scenario.generatedData) : scenario.unknownGpuScore ? [{
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
        isPresetMatch: false,
        hasDedicatedMemoryMetrics: scenario.gpuDedicatedMemoryMetrics
      }] : [],
      storagePath: "frontend-harness"
    });
  }
  if (path === "/api/resource-monitor/snapshot") {
    const capturedAt = resourceSlotCapturedAt;
    if (!resourceMeasuresKnown(url)) return json(response, 400, { error: "One or more subscription queries are invalid." });
    const residualMetricIds = residualDetailMetricIds(url);
    if (residualMetricIds === null) return residualDetailsRejected(response, url);
    if (url.searchParams.get("scope") === "details") return json(response, 200, resourceProcessDetails(url, capturedAt, residualMetricIds));
    const breakdown = resourceBreakdown(
      capturedAt,
      processDetailSelection(url, residualMetricIds),
      url.searchParams.getAll("measures"),
      url.searchParams.getAll("modes"), { ids: url.searchParams.getAll("ids") });
    return json(response, 200, {
      version: 10,
      capturedAt: breakdown.capturedAt,
      breakdown
    });
  }
  if (path === "/api/resource-breakdown/snapshot") {
    const residualMetricIds = residualDetailMetricIds(url);
    if (residualMetricIds === null) return residualDetailsRejected(response, url);
    return json(
      response,
      200,
      resourceBreakdown(
        resourceSlotCapturedAt,
        processDetailSelection(url, residualMetricIds), [], url.searchParams.getAll("modes"),
        { ids: url.searchParams.getAll("ids") }));
  }
  if (path === "/api/resource-facts/snapshot") {
    if (!resourceFactItemsKnown(url)) return json(response, 400, { error: "unknown resource fact item" });
    return json(response, 200, resourceFacts(url, resourceSlotCapturedAt));
  }
  if (path === "/api/local-system/status") {
    if (scenario.readmeShowcase) return json(response, 200, showcase.showcaseLocalSystemStatus(now()));
    return json(response, 200, { capturedAt: now(), bootedAt: now(), uptimeSeconds: 3600 });
  }
  if (path === "/api/cpu/topology") {
    return json(response, 200, scenario.cpuTopologyEmpty ? { state: "unavailable", reasonCode: "cpu-topology-not-published" } : fixtureCpuTopology());
  }
  if (path === "/api/cpu/residency") {
    return json(response, 200, scenario.cpuResidencyUnavailableReason ? { state: "unavailable", reasonCode: scenario.cpuResidencyUnavailableReason } : fixtureCpuResidency());
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
    if (scenario.cpuTopologyEmpty) return json(response, 200, { state: "unavailable", reasonCode: "cpu-topology-not-published" });
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
  if (path === "/api/gpu-placement/process-history/observe" && request.method === "POST") {
    return json(response, 200, { softwareId: mockSoftware.id, softwareName: mockSoftware.name, processes: [] });
  }
  const softwareSettingsRead = path.match(/^\/api\/gpu-placement\/software\/([^/]+)$/);
  if (softwareSettingsRead && request.method === "GET") {
    const softwareId = decodeURIComponent(softwareSettingsRead[1]);
    const softwareName = url.searchParams.get("softwareName") ?? mockSoftware.name;
    const softwarePolicy = softwarePolicies.get(softwareId.toLowerCase()) ?? defaultSoftwarePolicy(softwareId, softwareName);
    return json(response, 200, {
      softwareId,
      softwareName,
      softwarePolicy,
      processPolicies: [],
      processHistory: { softwareId, softwareName, processes: [], updatedAt: "2026-10-27T05:00:00Z" },
      startupInterceptions: [],
      targetInventory: { state: "current", exactTargets: [] },
      processCapabilities: [],
      // 后端 CompiledBaseScorePlan.Resolve：五层固定顺序，模拟后端没有软件目录，进程键是历史进程和进程策略的并集（这里都为空）。
      baseScore: mockBaseScoreResolution(softwareId, url.searchParams.get("softwareKind"), softwarePolicy.baseScoreOverride ?? null),
      processBaseScores: {}
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
      kind: "web_view2_runtime",
      version: "1.0.0",
      executablePath: "C:/Fixture/WebView2/msedgewebview2.exe",
      runtimeDirectory: "C:/Fixture/WebView2",
      source: "managed",
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
  if (path === "/api/software" && scenario.generatedData) {
    return json(response, 200, scenario.generatedData.names.map((name, index) => ({ ...mockSoftware,
      id: `frontend-harness.software${index ? `.${index}` : ""}`, name, processIds: [4242 + index], processNames: [`${name}.exe`], issues: [] })));
  }
  if (path === "/api/software") {
    return json(response, 200, [{
      ...mockSoftware,
      issues: scenario.softwareIssues ? mockSoftware.issues : []
    }, ...scenario.extraSoftware.map(({ id, name }, index) => ({ ...mockSoftware, id, name,
      executablePaths: [`C:\\FrontendHarness\\${id}.exe`], processIds: [5000 + index], processNames: [`${id}.exe`], issues: [] }))]);
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
    subject: { targetKind: "Component", targetName: null, targetId: "frontend-harness", scope: null, mode: null, targetPath: null },
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
      subject: { targetKind: "Software", targetName: "Frontend Harness", targetId: "frontend-harness", scope: null, mode: null, targetPath: null },
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
      subject: { targetKind: "Component", targetName: null, targetId: "failed-runtime", scope: null, mode: null, targetPath: null },
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
    subject: { targetKind: "Component", targetName: null, targetId: `frontend-harness-${index + 1}`, scope: null, mode: null, targetPath: null },
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
    speed: null,
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
  const enabled = scenario.trustedAccess !== "default-key-off";
  const defaultKeyId = enabled ? `default-${trustedAccessDefaultSerial}` : null;
  return {
    keys: enabled ? [{
      id: defaultKeyId, name: "Native default", purpose: "control", scopes: trustedAccessDefaultScopes,
      createdAt: "2026-10-25T08:00:00.000Z", isDefault: true
    }] : [],
    defaultEnabled: enabled,
    defaultKeyId,
    availableScopes: trustedAccessAvailableScopes,
    administratorHolderScopes: ["machine.write", "injection.configure"]
  };
}

// 和后端 POST /api/access-keys/default 一样：关闭时撤销默认密钥，启用或轮换时生成新的默认密钥。
// 界面以管理员身份运行、不带密钥，它的请求和订阅流不受默认密钥影响。
function setTrustedAccessDefault(body) {
  if (!body || typeof body.enabled !== "boolean" || typeof body.rotate !== "boolean"
    || Object.keys(body).some(key => key !== "enabled" && key !== "rotate") || (!body.enabled && body.rotate)) {
    return { statusCode: 400, body: { error: "default-key-request-invalid" } };
  }
  if (!body.enabled) {
    scenario.trustedAccess = "default-key-off";
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
  smartStatusSegment: ["success", "cycle-failed", "native-plan-failed"],
  kernelLeakReport: Object.keys(kernelLeakReportFrames),
  memoryProtection: memoryProtectionScenarios,
  memoryCpuTier: memoryCpuTierScenarios,
  deviceTopologyMachineIdentity: [null, "workstation", "mobile_workstation", "laptop", "desktop", "unknown"],
  deviceTopologyWireless: [null, "standards", "unknown"],
  metricCatalogAddresses: ["none", "reading-source"],
  runtimePolicyDisposition: ["committedAndPublished", "committedWithDeliveryFailures", "savedNotApplied"],
  runtimePolicyStartupDisposition: ["applied", "notAttempted", "applyFailed"],
  settingsRuntimeDisposition: ["committedAndApplied", "committedWithCapabilityConstraints", "committedWithDeliveryFailures", "savedNotApplied"],
  gpuDedicatedMemoryMetrics: [true, false, null],
  metricCatalogComponent: ["none", "missing", "installed"],
  physicalMemoryResidual: ["off", "ready", "unavailable", "absent"]
};

function applyScenarioPatch(patch) {
  for (const path of [...(patch.manualPublishPaths ?? []), ...(patch.pausedSubscriptionPaths ?? [])]) journeyControlledPaths.add(path);
  if (Object.hasOwn(patch, "generatedData")) applyGeneratedProcessFields(patch.generatedData);
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
  if (Object.hasOwn(patch, "memoryProtection")) memoryProtection.scenario(patch.memoryProtection);
  // 测试用：下一次重放带的记录容量（换场景后恢复后端默认 4096）。
  if (Object.hasOwn(patch, "memoryProtectionRecordCapacity")) memoryProtection.setRecordCapacity(patch.memoryProtectionRecordCapacity);
  // 换场景、改容量相当于后端重启后重新订阅：已有订阅收到完整重放。
  if (Object.hasOwn(patch, "memoryProtection") || Object.hasOwn(patch, "memoryProtectionRecordCapacity")) replayMemoryProtection();
  // 测试用：照后端 Begin/Finish 的方式发布当前处理编号和最近一次判断（{ decisionReason?, currentActionId? }）。
  if (Object.hasOwn(patch, "memoryProtectionDecision")) {
    memoryProtection.publishDecision(patch.memoryProtectionDecision);
    publishMemoryProtectionStatus();
  }
  // 测试用：按处理事件发布新增或更新的记录（字段缺省见 memoryProtectionFixture 的 upsertRecord），一条一帧。
  // 容量满了没记下时，未记下条数变了，后端发 records 帧。
  for (const fields of patch.memoryProtectionRecords ?? []) {
    const record = memoryProtection.upsertRecord(fields);
    publishMemoryProtectionFrame(record === null ? { type: "records", ...memoryProtection.records() } : { type: "record", record });
  }
  // 测试用：通知所有者的整份列表（docs/notifications_2026-10-29.md，从旧到新，序号由测试给），订阅随即收到整份列表。
  if (Object.hasOwn(patch, "backendNotifications")) {
    backendNotificationList = structuredClone(patch.backendNotifications);
    for (const subscriber of [...backendNotificationSubscribers]) publishBackendNotifications(subscriber);
  }
  // 测试用：正常运行点的上限（后端是常量 32），列表照此报告、新建照此拒绝。
  if (Object.hasOwn(patch, "memoryProtectionPointLimit")) {
    memoryProtection.setPointLimit(patch.memoryProtectionPointLimit);
    publishMemoryProtectionPoints();
  }
  // 启动模块登记的起始值（[{ softwareId, moduleIds }]），和界面经原生桥保存的登记同一份。
  for (const selection of patch.startupModuleSelections ?? []) {
    startupModuleSelections.set(selection.softwareId.toLowerCase(), { softwareId: selection.softwareId, moduleIds: [...selection.moduleIds].sort() });
  }
  settleMockValueSlots(patch);
  if (Object.hasOwn(patch, "manualPublishPaths")) rescheduleAllNdjsonPublishers();
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
  if (Object.hasOwn(patch, "kernelLeakReport")) {
    kernelLeakHistoryCleared = false;
    for (const subscriber of [...kernelLeakReportSubscribers]) publishKernelLeakReportValue(subscriber);
  }
  if (["metricFieldDevices", "metricCatalogAddresses", "metricCatalogExtra", "gpuPressureFixture", "gpuDedicatedMemoryMetrics", "metricCatalogComponent"]
    .some((key) => Object.hasOwn(patch, key))) publishMetricCatalogNow();
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

function publishMemoryProtectionFrame(frame) {
  for (const subscriber of [...memoryProtectionSubscribers]) writeMemoryProtectionFrame(subscriber, frame);
}

function publishMemoryProtectionStatus() {
  publishMemoryProtectionFrame({ type: "status", status: memoryProtection.liveStatus() });
}

function publishMemoryProtectionPoint() {
  publishMemoryProtectionFrame({ type: "normalRunningPoint", normalRunningPoint: memoryProtection.readPoint() });
}

// HostMemoryProtectionPointsFrame：已保存的点或生效的点一变就发，跟在 normalRunningPoint 帧之后。
function publishMemoryProtectionPoints() {
  publishMemoryProtectionFrame({ type: "normalRunningPoints", normalRunningPoints: memoryProtection.listPoints() });
}

// SubscriptionChannelEndpoints 的重放顺序：status、records（最新的在前）、normalRunningPoint、normalRunningPoints。
function replayMemoryProtectionTo(subscriber) {
  writeMemoryProtectionFrame(subscriber, { type: "status", status: memoryProtection.liveStatus() });
  writeMemoryProtectionFrame(subscriber, { type: "records", ...memoryProtection.records() });
  writeMemoryProtectionFrame(subscriber, { type: "normalRunningPoint", normalRunningPoint: memoryProtection.readPoint() });
  writeMemoryProtectionFrame(subscriber, { type: "normalRunningPoints", normalRunningPoints: memoryProtection.listPoints() });
}

function replayMemoryProtection() {
  for (const subscriber of [...memoryProtectionSubscribers]) replayMemoryProtectionTo(subscriber);
}

function writeMemoryProtectionFrame(subscriber, frame) {
  const { response, requestUrl, write } = subscriber;
  if (response.destroyed || response.writableEnded) return;
  write(frame);
  recordLogicalPublication(requestUrl);
}

// 设置的统一类型检查（AppSettingsPatch）：改动的值和当前设置的 JSON 类型不符时，整次改动以
// invalid-settings-value-type 拒绝，并给出第一个不符的值的路径。当前值为 null 或不存在的字段、改成 null 的值
// 不在这里检查（内存保护分区的删除由 invalidMemoryProtectionPatch 处理，其余分区沿用各自已有的处理）。
function settingsValueTypeProblem(changes) {
  const kind = value => value === null ? "null" : Array.isArray(value) ? "array" : typeof value;
  const pending = [{ path: "$", patch: changes, current: appSettings }];
  let visited = 0;
  while (pending.length > 0) {
    if (++visited > 10000) throw new Error("settings patch is too large for the mock type check");
    const { path, patch, current } = pending.shift();
    if (current === null || current === undefined || patch === null) continue;
    if (kind(patch) !== kind(current)) return path;
    if (kind(current) === "object") {
      for (const [key, value] of Object.entries(patch)) {
        if (Object.hasOwn(current, key)) pending.push({ path: `${path}.${key}`, patch: value, current: current[key] });
      }
    } else if (kind(current) === "array" && current.length > 0) {
      patch.forEach((value, index) => pending.push({ path: `${path}[${index}]`, patch: value, current: current[0] }));
    }
  }
  return null;
}

function publishProductUpdateStatus() {
  for (const subscriber of [...productUpdateStatusSubscribers]) publishProductUpdateStatusValue(subscriber);
}

function publishKernelLeakReportValue(subscriber) {
  const { response, requestUrl, write } = subscriber;
  if (response.destroyed || response.writableEnded) return;
  write(currentKernelLeakReportFrame());
  recordLogicalPublication(requestUrl);
}

function currentKernelLeakReportFrame() {
  const frame = structuredClone(kernelLeakReportFrames[scenario.kernelLeakReport]);
  if (kernelLeakHistoryCleared && Array.isArray(frame?.items)) {
    frame.items = frame.items.filter((item) => item.state !== "history");
  }
  return frame;
}

function publishProductUpdateStatusValue(subscriber) {
  const { response, requestUrl, write } = subscriber;
  if (response.destroyed || response.writableEnded) return;
  write(currentProductUpdateStatus());
  recordLogicalPublication(requestUrl);
}

function resetScenario() {
  journeyControlledPaths.clear();
  applyGeneratedProcessFields(null);
  startupModuleSelections.clear();
  trustedAccessDefaultSerial = 1;
  if (diskScanCompletionTimer) clearTimeout(diskScanCompletionTimer);
  if (targetedRecordingCompletionTimer) clearTimeout(targetedRecordingCompletionTimer);
  diskScanCompletionTimer = null;
  targetedRecordingCompletionTimer = null;
  targetedRecordingCompletionId = null;
  diskScanOperation = null;
  diskScanSequence = 0;
  targetedRecordingSequence = 0;
  targetedRecordings.clear();
  targetedRecordingProfiles = [targetedRecordingDefaultProfile()];
  targetedRecordingProfileSequence = 0;
  targetedRecordingTargetRunning = true;
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
  kernelLeakHistoryCleared = false;
  rescheduleAllNdjsonPublishers();
  for (const key of Object.keys(appSettings)) {
    delete appSettings[key];
  }
  Object.assign(appSettings, structuredClone(defaultAppSettings));
  memoryProtection.scenario("no-point");
  replayMemoryProtection();
  backendNotificationList = [];
  softwarePolicies.clear();
  appSettingsRuntimeDisposition = "committedAndApplied";
  for (const key of Object.keys(requestTelemetry)) {
    delete requestTelemetry[key];
  }
  deviceTopologyRevision = 0;
  committedDeviceTopology = null;
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
    || Object.hasOwn(patch, "resourceSoftwareMix")
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
  return { id, unit, selectable: true, candidateSources, hasDedicatedMemoryMetrics: null };
}

/*
 * 指标目录（后端 MetricCatalog）：GET /api/metrics/catalog 的响应体，也是目录订阅信封里的 metrics（两者逐字相同）。
 * gpu.N.* 定义带后端认定的专用显存指标能力；componentState 是 cpu.frequency 的组件安装信息。
 */
function metricCatalogBody(componentState = scenario.metricCatalogComponent) {
  return [...metricCatalog,
    ...(scenario.gpuPressureFixture ? [
      metric("gpu.0.usage", "GPU 占用", "GPU", "%"),
      metric("gpu.0.vram", "GPU 显存", "GPU", "B"),
      metric("gpu.0.vramPercent", "GPU 显存占用", "GPU", "%")] : []),
    ...(scenario.metricCatalogAddresses === "reading-source" ? [readingSourceAddress()] : []),
    ...(scenario.readmeShowcase ? showcase.showcaseMetricDefinitions() : []),
    ...scenario.metricCatalogExtra]
    .map((definition) => {
      const next = /^gpu\.\d+\./u.test(definition.id)
        ? { ...definition, hasDedicatedMemoryMetrics: scenario.gpuDedicatedMemoryMetrics }
        : definition;
      if (definition.id !== "cpu.frequency" || componentState === "none") return next;
      return componentState === "missing"
        ? { ...next, selectable: false, requiredComponentId: "hardware-sensors-pawnio-provider",
          disabledReason: { domain: 3, code: 2, args: [] } }
        : { ...next, selectable: false, requiredComponentId: null, disabledReason: { domain: 3, code: 3, args: [] } };
    });
}

// 字段目录（后端 MetricDeviceFieldCatalog）：GET /api/metrics/fields/catalog 的响应体，也是信封里的 fields。
function metricFieldCatalogBody() {
  return scenario.metricFieldDevices.map(({ deviceIdentity, device, fields }) => ({
    deviceIdentity,
    device,
    // 读取项和值上的相同；夹具没写的字段为 null（后端合同里 reading 可空）。
    fields: fields.map(({ name, unit, valueKind, reading }) => ({ name: normalizeFieldName(name), unit, valueKind, meaning: "", reading: reading ?? null }))
  }));
}

/*
 * 目录修订号（后端 HardwareMetricCatalogState.Revision）：不透明字符串，覆盖全部公开内容（设备、字段、指标定义、
 * 能力、组件安装信息），内容变化时换新；同一修订号就是同一份内容。指标快照帧和目录订阅帧带同一个值。
 */
let metricCatalogRevisionSerial = 0;
let metricCatalogRevisionIdentity = null;
function metricCatalogRevision() {
  const identity = JSON.stringify({ fields: metricFieldCatalogBody(), metrics: metricCatalogBody() });
  if (identity !== metricCatalogRevisionIdentity) {
    metricCatalogRevisionIdentity = identity;
    metricCatalogRevisionSerial += 1;
  }
  return `mock-catalog-${metricCatalogRevisionSerial}`;
}

// 目录订阅的一帧（后端 MetricCatalogWireSnapshot）：同一份目录的修订号、字段目录和指标目录。
// 一个修订号只提交一次：内容没变时返回同一个对象，按变化推送的订阅据此不重发。
let committedMetricCatalogPush = null;
function metricCatalogPush() {
  const catalogRevision = metricCatalogRevision();
  if (committedMetricCatalogPush?.catalogRevision !== catalogRevision) {
    committedMetricCatalogPush = { catalogRevision, fields: metricFieldCatalogBody(), metrics: metricCatalogBody() };
  }
  return committedMetricCatalogPush;
}

// 后端在目录提交时立即唤醒目录订阅（不等下一次发现探测）；场景改动就是这里的提交，内容没变的订阅者不收帧。
function publishMetricCatalogNow() {
  const publishedAt = now();
  for (const [key, publisher] of ndjsonPublishers) {
    if (!key.startsWith("/api/metrics/fields/catalog/subscribe?")) continue;
    for (const subscriber of [...publisher.subscribers]) subscriber.publish(publishedAt);
  }
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

// 进程字段目录（后端 ProcessMetricFieldRegistry 与 ProcessGpuResourceFieldRegistry 的 wire 形状，metric-source round 4）：
// System.Text.Json 写全部公开属性；supportsResourceFacts = resourceFactTotalMetricId 不为 null；
// additiveAcrossProcesses 是后端的声明（没有声明为 null）；GPU 字段按适配器和引擎展开，带 gpuEngine / engineClass。
function catalogField(name, valueKind, unit, sourceId, { reading = null, total = null, additive = null, gpuEngine = null, engineClass = null, sourceFieldName = null } = {}) {
  return { name, valueKind, unit, sourceId, resource: "process", vendor: "all", supportsResourceFacts: total !== null,
    gpuEngine, engineClass, sourceFieldName, additiveAcrossProcesses: additive, resourceFactTotalMetricId: total, reading };
}
const fixtureGpuLuid = 0x17;
const fixtureGpuLuidText = fixtureGpuLuid.toString(16).padStart(16, "0");
const gpuEngineFieldName = (engine) => `process_all_gpu_residency_attributed_gpu_0_luid_${fixtureGpuLuidText}_physical_0_engine_${engine}`;
const gpuResidentFieldName = (source) => `${source}_gpu_0_luid_${fixtureGpuLuidText}`;
const processFieldCatalog = [
  catalogField("process_all_network_receive_bytes_attributed", "unsigned64", "B", "windows.etw.network", { total: "network.traffic", additive: true }),
  catalogField("process_all_network_send_bytes_attributed", "unsigned64", "B", "windows.etw.network", { total: "network.traffic", additive: true }),
  catalogField("process_all_network_receive_rate_attributed", "float64", "B/s", "windows.etw.network", { total: "network.traffic", additive: true }),
  catalogField("process_all_network_send_rate_attributed", "float64", "B/s", "windows.etw.network", { total: "network.traffic", additive: true }),
  catalogField("process_all_physical_disk_read_bytes_attributed", "unsigned64", "B", "windows.etw.physical-disk", { total: "disk.io", additive: true }),
  catalogField("process_all_physical_disk_write_bytes_attributed", "unsigned64", "B", "windows.etw.physical-disk", { total: "disk.io", additive: true }),
  catalogField("process_all_gpu_residency_attributed", "float64", "%", "windows.pdh.gpu-engine"),
  catalogField("process_all_cpu_time_actual", "float64", "s", "windows.process.query", { additive: true }),
  catalogField("process_all_working_set_actual", "unsigned64", "B", "windows.process.query",
    { reading: { resource: "memory", quantity: "working_set_including_shared" }, total: "memory.usage", additive: false }),
  catalogField("process_all_private_working_set_actual", "unsigned64", "B", "windows.process.query",
    { reading: { resource: "memory", quantity: "private_working_set" }, total: "memory.usage", additive: true }),
  catalogField("process_all_private_commit_actual", "unsigned64", "B", "windows.process.query",
    { reading: { resource: "memory", quantity: "private_commit" }, total: "virtualMemory.usage", additive: true }),
  catalogField("process_all_peak_private_commit_actual", "unsigned64", "B", "windows.process.query",
    { reading: { resource: "memory", quantity: "peak_private_commit" }, additive: false }),
  catalogField("process_all_handle_count_actual", "unsigned64", "count", "windows.process.query"),
  catalogField("process_all_cpu_usage_actual", "float64", "%", "windows.process.cpu-interval", { total: "cpu.usage", additive: true }),
  ...[[0, null], [1, "Compute"]].map(([engine, engineClass]) => catalogField(gpuEngineFieldName(engine), "float64", "%", "windows.pdh.gpu-engine", {
    reading: { resource: "gpu", quantity: "engine_busy_percent" }, total: "gpu.0.usage", additive: true,
    gpuEngine: { adapterLuid: fixtureGpuLuid, physicalIndex: 0, engineIndex: engine }, engineClass, sourceFieldName: "process_all_gpu_residency_attributed" })),
  ...[["process_all_gpu_resident_bytes_attributed", "dedicated_resident_apportioned"],
    ["process_all_gpu_resident_private_bytes_attributed", "dedicated_resident_private"],
    ["process_all_gpu_resident_shared_bytes_attributed", "dedicated_resident_shared_apportioned"]].map(([source, quantity]) =>
    catalogField(gpuResidentFieldName(source), "float64", "B", "windows.etw.gpu-allocation",
      { reading: { resource: "gpu", quantity }, total: "gpu.0.vram", additive: true, sourceFieldName: source }))
];
const catalogFieldByName = new Map(processFieldCatalog.map(field => [field.name, field]));
const originalProcessFieldCatalog = processFieldCatalog.slice();
const generatedProcessFieldNames = new Set();
function applyGeneratedProcessFields(data) {
  processFieldCatalog.splice(0, processFieldCatalog.length, ...originalProcessFieldCatalog.filter(field => !data || field.gpuEngine === null));
  for (const name of generatedProcessFieldNames) { catalogFieldByName.delete(name); delete processMemoryFacts[name]; }
  catalogFieldByName.clear();
  for (const field of processFieldCatalog) catalogFieldByName.set(field.name, field);
  generatedProcessFieldNames.clear();
  for (const engine of data?.engines ?? []) {
    const luid = 4096 + engine.gpuIndex;
    const name = `process_all_gpu_residency_attributed_gpu_${engine.gpuIndex}_luid_${luid.toString(16).padStart(16, "0")}_physical_0_engine_${engine.index}`;
    const gpuEngine = { adapterLuid: luid, physicalIndex: 0, engineIndex: engine.index };
    const field = catalogField(name, "float64", "%", "windows.pdh.gpu-engine", {
      reading: { resource: "gpu", quantity: "engine_busy_percent" }, total: `gpu.${engine.gpuIndex}.usage`, additive: true,
      gpuEngine, engineClass: engine.engineClass, sourceFieldName: "process_all_gpu_residency_attributed" });
    generatedProcessFieldNames.add(name); processFieldCatalog.push(field); catalogFieldByName.set(name, field);
    processMemoryFacts[name] = { totalMetricId: field.resourceFactTotalMetricId, totalValue: engine.usagePercent, capacityValue: 100,
      processSum: engine.usagePercent, unit: "%", windowMilliseconds: data.intervalsMs[0], additive: true,
      quantity: "engine_busy_percent", gpuEngine, engineClass: engine.engineClass };
  }
}

// 自动的整机内存指标实际读的进程字段（后端在资源事实和资源条上写的 processFieldName）。
const automaticProcessFields = { "memory.usage": "process_all_private_working_set_actual", "virtualMemory.usage": "process_all_private_commit_actual" };

// 资源事实接受的进程字段：整机比较事实、整机量、比较基数和全部进程之和（按软件平分）。
// 吞吐类（网络、物理磁盘）的比较基数是本帧总量 max(1, total)，带 2 秒采样窗口；GPU 引擎带自己的窗口。
const GiB = 1024 ** 3;
const throughput = (total, unit) => ({ totalValue: total, capacityValue: Math.max(1, total), unit, windowMilliseconds: 2000 });
const processMemoryFacts = {
  process_all_working_set_actual: { totalMetricId: "memory.usage", totalValue: 8 * GiB, capacityValue: 16 * GiB, processSum: 0.625 * GiB },
  process_all_private_working_set_actual: { totalMetricId: "memory.usage", totalValue: 8 * GiB, capacityValue: 16 * GiB, processSum: 0.5 * GiB },
  process_all_private_commit_actual: { totalMetricId: "virtualMemory.usage", totalValue: 6 * GiB, capacityValue: 24 * GiB, processSum: GiB },
  process_all_cpu_usage_actual: { totalMetricId: "cpu.usage", totalValue: 24, capacityValue: 100, processSum: 12, unit: "%" },
  process_all_network_receive_rate_attributed: { totalMetricId: "network.traffic", ...throughput(4096, "B/s"), processSum: 3072 },
  process_all_network_send_rate_attributed: { totalMetricId: "network.traffic", ...throughput(2048, "B/s"), processSum: 2048 },
  process_all_network_receive_bytes_attributed: { totalMetricId: "network.traffic", ...throughput(8192, "B"), processSum: 6144 },
  process_all_network_send_bytes_attributed: { totalMetricId: "network.traffic", ...throughput(4096, "B"), processSum: 4096 },
  process_all_physical_disk_read_bytes_attributed: { totalMetricId: "disk.io", ...throughput(1048576, "B"), processSum: 786432 },
  process_all_physical_disk_write_bytes_attributed: { totalMetricId: "disk.io", ...throughput(524288, "B"), processSum: 524288 },
  [gpuEngineFieldName(0)]: { totalMetricId: "gpu.0.usage", totalValue: 40, capacityValue: 100, processSum: 20, unit: "%", windowMilliseconds: 1000 },
  [gpuEngineFieldName(1)]: { totalMetricId: "gpu.0.usage", totalValue: 10, capacityValue: 100, processSum: 10, unit: "%", windowMilliseconds: 1000 },
  [gpuResidentFieldName("process_all_gpu_resident_bytes_attributed")]: { totalMetricId: "gpu.0.vram", totalValue: 2 * GiB, capacityValue: 8 * GiB, processSum: 1.5 * GiB },
  [gpuResidentFieldName("process_all_gpu_resident_private_bytes_attributed")]: { totalMetricId: "gpu.0.vram", totalValue: 2 * GiB, capacityValue: 8 * GiB, processSum: GiB },
  [gpuResidentFieldName("process_all_gpu_resident_shared_bytes_attributed")]: { totalMetricId: "gpu.0.vram", totalValue: 2 * GiB, capacityValue: 8 * GiB, processSum: 0.5 * GiB }
};
for (const [field, fact] of Object.entries(processMemoryFacts)) {
  const definition = catalogFieldByName.get(field);
  Object.assign(fact, { additive: definition.additiveAcrossProcesses, quantity: definition.reading?.quantity ?? null,
    gpuEngine: definition.gpuEngine, engineClass: definition.engineClass, windowMilliseconds: fact.windowMilliseconds ?? null });
}

// 后端只接受能作资源事实的进程字段作 measures；峰值提交量、未知字段整条请求 400。
function resourceMeasuresKnown(url) {
  return normalizeMetricIds(url.searchParams.getAll("measures")).every(field => Object.hasOwn(processMemoryFacts, field.toLowerCase()));
}

// 后端 ParseResourceResidualDetailMetricIds：residualDetails 逗号分隔或重复，去空白、丢空项、不分大小写去重；
// 值是资源条 id（ResourceBreakdownMetricIds.IsSupported），或声明可加且有整机比较指标的进程字段（解析为那个整机指标）。
// 其余值（未知指标、不可加的度量、页帧项 memory.residual.details）整条请求 400；返回 null 表示拒绝。
const residualDetailBarIds = new Set(["cpu.usage", "memory.usage", "virtualmemory.usage", "disk.io", "disk.read", "disk.write",
  "network.traffic", "network.receive", "network.send", "network.raw.traffic", "network.raw.receive", "network.raw.send"]);
function residualDetailMetricIds(url) {
  const ids = new Set();
  for (const metricId of url.searchParams.getAll("residualDetails").flatMap(value => value.split(",")).map(value => value.trim()).filter(Boolean)) {
    const id = metricId.toLowerCase();
    if (residualDetailBarIds.has(id) || /^gpu\.\d+\.(usage|vram)$/.test(id)) { ids.add(id); continue; }
    const field = processFieldCatalog.find(candidate => candidate.name.toLowerCase() === id);
    if (!field?.resourceFactTotalMetricId || field.additiveAcrossProcesses !== true) return null;
    ids.add(field.resourceFactTotalMetricId.toLowerCase());
  }
  return ids;
}

// 后端 IncludesProcessDetails：一条的系统/驱动保留行（软件 id 是 resource-residual:<这条的整机指标>）只由 residualDetails 选，
// 其余软件行只由 processDetails 选；processDetails 里写保留行的软件 id 就是一个不认识的软件，什么也不选。
// 只在已请求的条和度量里挑，不增加任何采样。
const residualSoftwarePrefix = "resource-residual:";
function processDetailSelection(url, residualMetricIds) {
  const softwareIds = new Set(normalizeMetricIds(url.searchParams.getAll("processDetails")).map(id => id.toLowerCase()));
  return { size: softwareIds.size + residualMetricIds.size, residualMetricIds,
    // resourceBreakdown 按调用参数分开记最近发出的条，选择要能区分。
    toJSON: () => ({ processDetails: [...softwareIds].sort(), residualDetails: [...residualMetricIds].sort() }),
    has: softwareId => {
      const id = softwareId.toLowerCase();
      return id.startsWith(residualSoftwarePrefix) ? residualMetricIds.has(id.slice(residualSoftwarePrefix.length)) : softwareIds.has(id);
    } };
}

function residualDetailsRejected(response, url) {
  const invalid = url.searchParams.getAll("residualDetails").flatMap(value => value.split(",")).map(value => value.trim()).filter(Boolean)
    .find(value => residualDetailMetricIds(new URL(`http://mock/?residualDetails=${encodeURIComponent(value)}`)) === null);
  return json(response, 400, { error: `Resource residual details metric '${invalid}' is unsupported.` });
}

function resourceFactItemsKnown(url) {
  return url.searchParams.getAll("items").every(id => ["process.inventory", "process.attribution", "cpu.usage", "memory.usage", "disk.io", "network.traffic"].includes(id)
    || Object.hasOwn(processMemoryFacts, id) || /^gpu\.\d+\.(usage|vram)$/.test(id));
}

function metricSnapshot(requestedIds) {
  const allItems = {
    "cpu.usage": { id: "cpu.usage", unit: "%", status: 1, numericValue: scenario.metricCpuUsage, percent: scenario.metricCpuUsage },
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
  if (scenario.readmeShowcase) Object.assign(allItems, showcase.showcaseMetricItems());
  if (scenario.generatedData) Object.assign(allItems, generatedMetricItems({ metrics: generatedReadings() }));
  const definitions = [...metricCatalog, ...scenario.metricCatalogExtra, ...(scenario.metricCatalogAddresses === "reading-source" ? [readingSourceAddress()] : []),
    ...(scenario.readmeShowcase ? showcase.showcaseMetricDefinitions() : [])];
  const knownMetricIds = new Map([...definitions.map(item => item.id), ...Object.keys(allItems)]
    .map(metricId => [metricId.toLowerCase(), metricId]));
  const selectedIds = normalizeMetricIds(requestedIds).map(id => knownMetricIds.get(id.toLowerCase()) ?? id);
  const statuses = new Map(Object.entries(scenario.metricStatuses).map(([id, status]) => [id.toLowerCase(), status]));
  const items = Object.fromEntries(selectedIds.map(id => {
    const current = allItems[id];
    const status = statuses.get(id.toLowerCase()) ?? (current ? current.status : 4);
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
    catalogRevision: metricCatalogRevision(),
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
    visualLayoutKind: scenario.cpuTopologyLayoutKind,
    visualLayoutSource: "frontend-harness",
    physicalCoreCount: 2,
    logicalProcessorCount: 4,
    ccdCount: 1,
    simultaneousMultithreading: true,
    ccds: [{
      id: "ccd:0",
      index: 0,
      usagePercent: 31 + scenario.cpuTopologyUsageOffset,
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
      usagePercent: (index === 0 ? 42 : 20) + scenario.cpuTopologyUsageOffset,
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

function resourceProcessDetails(url, capturedAt, residualMetricIds) {
  const requested = processDetailSelection(url, residualMetricIds);
  if (requested.size === 0) return { version: 1, capturedAt: null, metrics: [] };
  const measures = url.searchParams.getAll("measures");
  // 明细按 measures 给出这个字段的进程，整机总量读不到也照样给。
  const breakdown = resourceBreakdown(capturedAt, requested, measures, url.searchParams.getAll("modes"), { keepMissingTotals: true, ids: url.searchParams.getAll("ids") });
  const metrics = breakdown.bars
    .filter(bar => bar.capturedAt !== null)
    .map(bar => ({ metricId: bar.metricId, totalMetricId: bar.totalMetricId ?? bar.metricId,
      totalValue: bar.totalValue, capacityValue: bar.capacityValue, processSumValue: bar.processSumValue,
      residualValue: bar.residualValue, additiveAcrossProcesses: bar.additiveAcrossProcesses, capturedAt: bar.capturedAt,
      software: bar.software
      .filter(row => requested.has(breakdown.softwareCatalog[row[0]][0].toLowerCase()))
      .map(row => ({ softwareId: breakdown.softwareCatalog[row[0]][0], processes: row[5] })) }));
  // 和后端一样：明细的 residualDetails 选了 memory.usage 的保留量，且选了 memory.usage、物理私有内存度量或页帧项
  // memory.residual.details 时才附上最近发布的页帧帧，带它自己的采集时间和状态；"absent" 是还没有发布过帧。
  const selected = new Set(normalizeMetricIds([...url.searchParams.getAll("ids"), ...measures]).map(id => id.toLowerCase()));
  const physicalPrivate = ["memory.usage", "memory.residual.details", "process_all_private_working_set_actual"].some(id => selected.has(id));
  const frame = scenario.physicalMemoryResidual !== "off" && scenario.physicalMemoryResidual !== "absent"
    && residualMetricIds.has("memory.usage") && physicalPrivate
    ? physicalMemoryResidualFrame(scenario.physicalMemoryResidualCapturedAt) : null;
  return { version: 1, capturedAt: metrics.length ? capturedAt : frame?.capturedAt ?? null, ...(frame ? { physicalMemoryResidual: frame } : {}), metrics };
}

// 合成的物理内存保留量（只用 1/16 GiB 的整数倍，字节数都是整数）。计数器分项是条上的 PID -1 行，
// 标识沿用后端 PhysicalMemoryResidualKinds；池标记后缀是小端 ULONG 的八位大写十六进制：
// 4346744E = "NtFC"，656C6946 = "File"，01020304 含不可打印字节。各分项加未分类正好等于保留量。
const physicalMemoryResidualSoftwareId = "resource-residual:memory.usage";
const physicalMemoryFixture = {
  capacityBytes: 32 * GiB,
  usedBytes: 12 * GiB,
  attributedPrivateBytes: 4 * GiB,
  counterLeaves: [
    ["system-memory/nonpaged-pool/tag/4346744E", 2.5 * GiB, "NtFC"],
    ["system-memory/nonpaged-pool/tag/656C6946", 0.5 * GiB, "File"],
    ["system-memory/nonpaged-pool/tag/01020304", 0.25 * GiB, "01020304"],
    ["system-memory/nonpaged-pool/other-tags", 0.25 * GiB, ""],
    ["system-memory/nonpaged-pool/unclassified", 0.125 * GiB, ""],
    ["system-memory/paged-pool-resident", 1 * GiB, ""],
    ["system-memory/driver-resident", 0.375 * GiB, ""],
    ["system-memory/code-resident", 0.125 * GiB, ""],
    ["system-memory/unclassified", 2.875 * GiB, ""]
  ],
  // PhysicalMemoryPfnAggregation 的顺序：Active 各用途、非分页池分项、已修改页、未归属的进程私有内存。
  pfnParts: [
    ["system-memory/mapped-file", 1.5 * GiB],
    ["system-memory/shareable", 0.75 * GiB],
    ["system-memory/page-table", 0.25 * GiB],
    ["system-memory/paged-pool-resident", 1 * GiB],
    ["system-memory/system-pte", 0.125 * GiB],
    ["system-memory/metafile", 0.0625 * GiB],
    ["system-memory/driver-locked", 0.0625 * GiB],
    ["system-memory/kernel-stack", 0.0625 * GiB],
    ["system-memory/working-set-internals", 0.0625 * GiB],
    ["system-memory/nonpaged-pool/tag/4346744E", 2.5 * GiB],
    ["system-memory/nonpaged-pool/tag/656C6946", 0.5 * GiB],
    ["system-memory/nonpaged-pool/tag/01020304", 0.25 * GiB],
    ["system-memory/nonpaged-pool/other-tags", 0.25 * GiB],
    ["system-memory/nonpaged-pool/unclassified", 0.125 * GiB],
    ["system-memory/modified", 0.25 * GiB],
    ["system-memory/process-private-not-attributed", 0.125 * GiB]
  ]
};

function physicalMemoryResidualFrame(capturedAt) {
  const fixture = physicalMemoryFixture;
  if (scenario.physicalMemoryResidual === "unavailable") {
    return { version: 1, source: "pfn", status: "unavailable", reasonCode: scenario.physicalMemoryResidualReason, capturedAt,
      usedBytes: null, attributedProcessPrivateBytes: null, residualBytes: null, unclassifiedBytes: null, parts: [] };
  }
  const residualBytes = fixture.usedBytes - fixture.attributedPrivateBytes;
  const partBytes = fixture.pfnParts.reduce((sum, [, bytes]) => sum + bytes, 0);
  return { version: 1, source: "pfn", status: "ready", reasonCode: null, capturedAt,
    usedBytes: fixture.usedBytes, attributedProcessPrivateBytes: fixture.attributedPrivateBytes, residualBytes,
    unclassifiedBytes: residualBytes - partBytes, parts: fixture.pfnParts.map(([kind, bytes]) => ({ kind, bytes })) };
}

// 物理内存条：前面是示例软件（合计为已归属的进程私有内存），最后是保留量这一行；带上它的 id 时附计数器分项。
function physicalMemoryBar(softwareCatalog, segmentCount, processDetailSoftwareIds, capturedAt) {
  const fixture = physicalMemoryFixture;
  const percent = (value) => value * 100 / fixture.capacityBytes;
  let residualIndex = softwareCatalog.findIndex(row => row[0] === physicalMemoryResidualSoftwareId);
  if (residualIndex < 0) residualIndex = softwareCatalog.push([physicalMemoryResidualSoftwareId, "", "windows_system", "system-residual"]) - 1;
  const software = scenario.physicalMemorySoftware === null
    ? createResourceSoftwareRows(segmentCount, fixture.attributedPrivateBytes, percent(fixture.attributedPrivateBytes),
      (value) => `${value}`, processDetailSoftwareIds)
    : scenario.physicalMemorySoftware.map(([softwareId, name, kind, displayKind, bytes], index) => {
      const catalogIndex = softwareCatalog.push([softwareId, name, kind, displayKind]) - 1;
      const processId = 7300 + index;
      const processes = processDetailSoftwareIds.has(softwareId.toLowerCase())
        ? [[processId, "svchost.exe", "C:\\Windows\\System32\\svchost.exe", bytes, percent(bytes), 100, "SYSTEM", "x64", "direct", 0,
          String(132000000000000000n + BigInt(processId))]] : [];
      return [catalogIndex, bytes, percent(bytes), 1, 0, processes];
    });
  const attributed = software.reduce((sum, row) => sum + row[1], 0);
  const residual = fixture.usedBytes - attributed;
  const leaves = processDetailSoftwareIds.has(physicalMemoryResidualSoftwareId)
    ? (scenario.physicalMemoryCounterLeaves ?? fixture.counterLeaves)
      .map(([kind, bytes, name]) => [-1, name, null, bytes, percent(bytes), bytes * 100 / residual, null, null, kind, 0, null])
    : [];
  return {
    metricId: "memory.usage",
    processFieldName: automaticProcessFields["memory.usage"],
    residualSource: "counters",
    unit: "B",
    scaleMode: "capacity",
    totalValue: fixture.usedBytes,
    capacityValue: fixture.capacityBytes,
    totalSystemPercent: percent(fixture.usedBytes),
    ...barFacts({ processSumValue: attributed, residualValue: residual, additiveAcrossProcesses: true, capturedAt }),
    software: [...software, [residualIndex, residual, percent(residual), 0, 0, leaves]]
  };
}

// CPU 条按 scenario.cpuSoftware 列出的软件行，最后是 cpuResidualPercent 的系统/驱动保留（后端的残差行下面是一项 PID -1 的 system-residual，
// 只在 residualDetails 选了 cpu.usage 时写出）。
function listedCpuBar(softwareCatalog, processDetailSoftwareIds, capturedAt) {
  const residualId = "resource-residual:cpu.usage";
  const software = scenario.cpuSoftware.map(([softwareId, name, kind, displayKind, percent], index) => {
    const catalogIndex = softwareCatalog.push([softwareId, name, kind, displayKind]) - 1;
    const processId = 8300 + index;
    const processes = processDetailSoftwareIds.has(softwareId.toLowerCase())
      ? [[processId, `${name || softwareId}.exe`, null, percent, percent, 100, "测试用户", "x64", "direct", 0, String(132000000000000000n + BigInt(processId))]] : [];
    return [catalogIndex, percent, percent, 1, 0, processes];
  });
  const attributed = software.reduce((sum, row) => sum + row[1], 0);
  const residual = scenario.cpuResidualPercent;
  if (residual > 0) {
    const residualIndex = softwareCatalog.push([residualId, "", "windows_system", "system-residual"]) - 1;
    const leaves = processDetailSoftwareIds.has(residualId) ? [[-1, "", null, residual, residual, 100, null, null, "system-residual", 0, null]] : [];
    software.push([residualIndex, residual, residual, 0, 0, leaves]);
  }
  const total = attributed + residual;
  return { metricId: "cpu.usage", unit: "%", scaleMode: "capacity", totalValue: total, capacityValue: 100, totalSystemPercent: total,
    ...barFacts({ processSumValue: attributed, residualValue: residual, additiveAcrossProcesses: true, capturedAt }), software };
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

const lastServedResourceBars = new Map();

function resourceBreakdown(...args) {
  const breakdown = resourceBreakdownFixture(...args);
  const requestedModes = new Map((args[3] ?? []).map(value => {
    const [id, scale] = value.split(":");
    return [id.toLowerCase(), scale?.toLowerCase()];
  }));
  const unavailable = new Set(scenario.resourceUnavailableBarIds);
  const frozen = new Set(scenario.resourceFrozenBarIds);
  // 每种调用（资源条、明细、资源表事实）各记各的：同一条在不同调用里的形状不同。
  const call = JSON.stringify(args.slice(1).map((value) => value instanceof Set ? [...value].sort() : value));
  const bars = breakdown.bars.map((current) => {
    const scale = requestedModes.get(current.metricId.toLowerCase());
    const bar = scale === "capacity" || scale === "active" ? { ...current, scaleMode: scale } : current;
    const key = `${call}|${bar.metricId}`;
    if (frozen.has(bar.metricId) && lastServedResourceBars.has(key)) return lastServedResourceBars.get(key);
    return unavailable.has(bar.metricId)
      ? { ...bar, totalValue: null, capacityValue: null, totalSystemPercent: null, sharedValue: null,
        processSumValue: null, residualValue: null, software: [], unavailableReason: "hardware-source-unavailable" }
      : bar;
  });
  for (const bar of bars) lastServedResourceBars.set(`${call}|${bar.metricId}`, bar);
  return { ...breakdown, bars };
}

function resourceBreakdownFixture(capturedAt = now(), processDetailSoftwareIds = new Set(), measures = [], modes = [], { keepMissingTotals = false, ids = [] } = {}) {
  const generation = Math.max(
    0,
    Math.floor(Number(scenario.resourceBreakdownGeneration) || 0));
  if (scenario.readmeShowcase) return showcase.showcaseResourceBreakdown(capturedAt, [...ids, ...measures], processDetailSoftwareIds);
  if (scenario.resourceSoftwareMix === "audit") {
    return auditResourceBreakdown(capturedAt, generation, processDetailSoftwareIds, measures, modes, keepMissingTotals);
  }
  const cpuValue = scenario.resourceCpuValue ?? [12, 38, 21, 55][generation % 4];
  const segmentCount = normalizeFixtureCount(scenario.resourceSegmentCount, 1, scenario.generatedData?.names.length ?? 1000);
  const softwareCatalog = Array.from({ length: segmentCount }, (_, index) => [
    index === 0 ? "frontend-harness.software" : `frontend-harness.software.${index}`,
    index === 0 ? "示例软件" : `示例软件 ${index}`,
    "other",
    "other"
  ]);
  if (scenario.generatedData) softwareCatalog.forEach((row, index) => { row[1] = scenario.generatedData.names[index]; });
  const visibleIds = new Set(normalizeMetricIds([...ids, ...measures]).map(id => id.toLowerCase()));
  const processFields = [...visibleIds].filter(id => Object.hasOwn(processMemoryFacts, id));
  const bars = [
      scenario.cpuSoftware !== null ? listedCpuBar(softwareCatalog, processDetailSoftwareIds, capturedAt) : {
        metricId: "cpu.usage",
        unit: "%",
        scaleMode: "capacity",
        totalValue: cpuValue,
        capacityValue: 100,
        totalSystemPercent: cpuValue,
        totalDisplay: `${cpuValue}%`,
        ...barFacts({ processSumValue: cpuValue, residualValue: 0, additiveAcrossProcesses: true, capturedAt }),
        software: createResourceSoftwareRows(
          segmentCount,
          cpuValue,
          cpuValue,
          (value) => `${value.toFixed(3)}%`,
          processDetailSoftwareIds)
      },
      scenario.physicalMemoryResidual !== "off" ? physicalMemoryBar(softwareCatalog, segmentCount, processDetailSoftwareIds, capturedAt) : {
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
        ...barFacts({ processSumValue: scenario.resourceSharedMemory ? 8 * 1024 ** 3 : 8, residualValue: 0, additiveAcrossProcesses: true, capturedAt }),
        software: createResourceSoftwareRows(
          segmentCount,
          scenario.resourceSharedMemory ? 8 * 1024 ** 3 : 8,
          25,
          (value) => `${(value * 1024).toFixed(2)} MB`,
          processDetailSoftwareIds).map(row => scenario.resourceSharedMemory ? [...row, row[1] / 2] : row)
      },
      ...(scenario.gpuPressureFixture ? [
        { metricId: "gpu.0.usage", unit: "%", scaleMode: "capacity", totalValue: 96, capacityValue: 100, totalSystemPercent: 96,
          ...barFacts({ processSumValue: 96, residualValue: 0, additiveAcrossProcesses: true, capturedAt }),
          software: createResourceSoftwareRows(segmentCount, 96, 96, value => String(value) + "%", processDetailSoftwareIds) },
        { metricId: "gpu.0.vram", unit: "B", scaleMode: "capacity", totalValue: 4 * GiB, capacityValue: 8 * GiB, totalSystemPercent: 50,
          ...barFacts({ processSumValue: 4 * GiB, residualValue: 0, additiveAcrossProcesses: true, capturedAt, quantity: "dedicated_memory_used" }),
          software: createResourceSoftwareRows(segmentCount, 4 * GiB, 50, value => String(value) + " B", processDetailSoftwareIds) }
      ] : []),
      ...resourceMeasureBars(softwareCatalog, processFields, modes, processDetailSoftwareIds, keepMissingTotals, capturedAt)
    ].filter(bar => visibleIds.has(bar.metricId.toLowerCase()));
  if (scenario.generatedData) {
    const weights = scenario.generatedData.weights;
    const weightSum = weights.reduce((sum, value) => sum + value, 0);
    for (const metricId of visibleIds) {
      const item = generatedReadings()[metricId];
      if (!item) continue;
      if (item.numericValue === null) {
        const index = bars.findIndex(bar => bar.metricId === metricId);
        if (index >= 0) bars.splice(index, 1);
        continue;
      }
      const total = item.numericValue;
      const capacity = item.percent > 0 ? total * 100 / item.percent : item.unit === "%" ? 100 : Math.max(total, 1);
      const percent = capacity > 0 ? total * 100 / capacity : 0;
      const bar = { metricId, unit: item.unit ?? "%", scaleMode: "capacity", totalValue: total, capacityValue: capacity, totalSystemPercent: percent,
        ...barFacts({ processSumValue: total, residualValue: 0, additiveAcrossProcesses: true, capturedAt }),
        software: weights.map((weight, index) => [index, total * weight / weightSum, percent * weight / weightSum, 1, 0, []]) };
      const index = bars.findIndex(bar => bar.metricId === metricId);
      if (index >= 0) bars[index] = bar; else bars.push(bar);
    }
  }
  for (const metricId of visibleIds) {
    if (bars.some(bar => bar.metricId === metricId) || !/^(?:cpu\.usage|memory\.usage|virtualmemory\.usage|disk\.(?:io|read|write)|network\.(?:raw\.)?(?:traffic|receive|send)|gpu\.\d+\.(?:usage|vram))$/.test(metricId)) continue;
    const throughput = metricId.startsWith("disk.") || metricId.startsWith("network.");
    const scaleMode = new Map(modes.map(value => value.split(":"))).get(metricId) ?? (throughput ? "active" : "capacity");
    bars.push({ metricId, unit: throughput ? "B/s" : /^(?:cpu|gpu\.\d+)\.usage$/.test(metricId) ? "%" : "B",
      scaleMode, totalValue: null, capacityValue: null, totalSystemPercent: null,
      ...barFacts({ processSumValue: null, residualValue: null, additiveAcrossProcesses: true, capturedAt: null }),
      unavailableReason: "resource-breakdown-total-unavailable", software: [] });
  }
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
  return { version: 8, capturedAt: bars.some(bar => bar.capturedAt !== null) ? capturedAt : null, softwareCatalog: selectedCatalog, bars };
}

// 混合数据（resourceSoftwareMix: "audit"）：各展示分组都有、最后几段很小、末尾是系统/驱动保留残差；
// 每一代按各自的相位小幅变化，顺序不变，用来看配色、小段、分隔线和数据更新时的过渡；代数除以 4 余 3 时第三个软件退出。
const auditSoftware = [
  ["frontend-harness.audit.browser", "Microsoft Edge", "other", "other", 4.2, 9.5],
  ["frontend-harness.audit.editor", "Visual Studio Code", "adapted", "adapted", 2.6, 6.1],
  ["frontend-harness.audit.game", "Steam Client Bootstrapper", "other", "other", 1.4, 3.2],
  ["frontend-harness.audit.service", "", "windows_service", "windows_service", 1.1, 2.4],
  ["frontend-harness.audit.shell", "Windows Explorer", "windows_system", "windows_system", 0.6, 1.8],
  ["frontend-harness.audit.defender", "Microsoft Defender", "windows_component", "windows_component", 0.45, 1.1],
  ["frontend-harness.audit.chat", "Discord", "adapted", "adapted", 0.12, 0.4],
  ["frontend-harness.audit.tray", "Logitech G HUB", "other", "other", 0.05, 0.15],
  ["frontend-harness.audit.tiny", "OneDrive", "other", "other", 0.02, 0.06],
  ["frontend-harness.audit.tiniest", "Everything", "adapted", "adapted", 0.008, 0.02]
];

function auditResourceBreakdown(capturedAt, generation, processDetailSoftwareIds, measures, modes, keepMissingTotals) {
  const softwareCatalog = [
    ...auditSoftware.map(([id, name, kind, displayKind]) => [id, name, kind, displayKind]),
    ["resource-residual:memory.usage", "", "windows_system", "system-residual"],
    ["resource-residual:cpu.usage", "", "windows_system", "system-residual"]
  ];
  const factor = index => 1 + 0.28 * Math.sin(generation * 1.7 + index * 0.9);
  const rows = (column, capacity, residualIndex, residualValue) => {
    const software = auditSoftware.flatMap((entry, index) => generation % 4 === 3 && index === 2 ? [] : [entry]).map((entry) => {
      const index = auditSoftware.indexOf(entry);
      const value = entry[column] * factor(index);
      const processes = processDetailSoftwareIds.has(entry[0]) ? [[5000 + index, `${entry[0].split(".").pop()}.exe`,
        null, value, value * 100 / capacity, 100, "测试用户", "x64", "direct", 0, String(132000000000000000n + BigInt(5000 + index))]] : [];
      return [index, value, value * 100 / capacity, processes.length || 1, 0, processes];
    });
    // 后端的残差行下面是一项 PID -1 的 system-residual；只在 residualDetails 选了这条的整机指标时写出。
    const residual = processDetailSoftwareIds.has(softwareCatalog[residualIndex][0])
      ? [[-1, "", null, residualValue, residualValue * 100 / capacity, 100, null, null, "system-residual", 0, null]] : [];
    return [...software, [residualIndex, residualValue, residualValue * 100 / capacity, 0, 0, residual]];
  };
  const memory = rows(4, 32, auditSoftware.length, 6.8 + 0.6 * Math.sin(generation));
  const cpu = rows(5, 100, auditSoftware.length + 1, 4 + 1.5 * Math.cos(generation));
  const total = items => items.reduce((sum, row) => sum + row[1], 0);
  return {
    version: 8,
    capturedAt,
    softwareCatalog,
    bars: [
      { metricId: "cpu.usage", unit: "%", scaleMode: "capacity", totalValue: total(cpu), capacityValue: 100,
        ...barFacts({ processSumValue: total(cpu.slice(0, -1)), residualValue: cpu.at(-1)[1], additiveAcrossProcesses: true, capturedAt }),
        totalSystemPercent: total(cpu), totalDisplay: `${total(cpu).toFixed(1)}%`, software: cpu },
      { metricId: "memory.usage", processFieldName: automaticProcessFields["memory.usage"], unit: "GB", scaleMode: "capacity",
        residualSource: physicalResidualSource("memory.usage"),
        ...barFacts({ processSumValue: total(memory.slice(0, -1)), residualValue: memory.at(-1)[1], additiveAcrossProcesses: true, capturedAt }),
        totalValue: total(memory), capacityValue: 32, totalSystemPercent: total(memory) * 100 / 32,
        totalDisplay: `${total(memory).toFixed(1)} GB`, software: memory },
      ...resourceMeasureBars(softwareCatalog, measures, modes, processDetailSoftwareIds, keepMissingTotals, capturedAt)
    ]
  };
}

// 按进程字段订阅的资源条（后端 ResourceBreakdownWireSnapshot.ProjectProcessMemoryBars 的形状）：
// metricId 和 processFieldName 是字段名，totalMetricId 是它比较的整机事实；第一个软件拿全部进程之和，
// 差额进 resource-residual:<totalMetricId>。缩放看 modes=，键可以是字段名或它的 totalMetricId，同时出现时字段名优先，
// active 按整机已用量比较。后端只认目录中能作资源事实的字段（别的在请求时已经 400）；
// 总量读不到时 bars 清空数值和片段；details 保留投影的每指标事实。
function resourceMeasureBars(softwareCatalog, measures, modes = [], processDetailSoftwareIds = new Set(), keepMissingTotals = false, capturedAt = now()) {
  const modeByKey = new Map(modes.map(value => value.split(":")).filter(parts => parts.length === 2));
  const known = [...new Set(measures)].filter(field => Object.hasOwn(processMemoryFacts, field));
  return known.map(field => {
    const fact = processMemoryFacts[field];
    const scaleMode = modeByKey.get(field) ?? modeByKey.get(fact.totalMetricId) ?? "capacity";
    const missing = scenario.resourceBarMeasureTotalsMissing.includes(field);
    const gpuMissing = missing && field.startsWith("process_all_gpu_");
    if (missing && (!keepMissingTotals || gpuMissing)) return {
      metricId: field, totalMetricId: fact.totalMetricId, processFieldName: field, unit: fact.unit ?? "B", scaleMode,
      ...(physicalResidualSource(field) ? { residualSource: physicalResidualSource(field) } : {}),
      totalValue: null, capacityValue: null, totalSystemPercent: null, sharedValue: null,
      ...barFacts({ processSumValue: null, residualValue: null, additiveAcrossProcesses: fact.additive, capturedAt,
        quantity: fact.quantity, windowMilliseconds: fact.windowMilliseconds, gpuEngine: fact.gpuEngine, engineClass: fact.engineClass }),
      unavailableReason: gpuMissing ? "gpu-measure-total-unavailable" : "system-total-unavailable", software: []
    };
    const base = scaleMode === "active" ? fact.totalValue : fact.capacityValue;
    const percent = value => value * 100 / base;
    const detailed = processDetailSoftwareIds.has(softwareCatalog[0][0].toLowerCase());
    const residualId = `resource-residual:${fact.totalMetricId}`;
    let residualIndex = softwareCatalog.findIndex(row => row[0] === residualId);
    if (residualIndex < 0) residualIndex = softwareCatalog.push([residualId, "", "windows_system", "system-residual"]) - 1;
    // 不可加口径没有残差（后端 Balance），也就没有残差条目。
    const residual = fact.additive && !missing ? fact.totalValue - fact.processSum : null;
    // 和后端 r3 一样：只选物理私有内存度量、不带 memory.usage 时，它的保留量同样按计数器分项给出（PID -1 的行，
    // 标识在 attributionKind），分项与这个度量自己的保留量平衡，最后的未分类是余数；后端只在差额为正时取分项。
    const counters = scenario.physicalMemoryResidual !== "off" && field === "process_all_private_working_set_actual";
    const leaves = counters && residual > 0 && processDetailSoftwareIds.has(residualId) ? physicalMemoryFixture.counterLeaves.slice(0, -1) : [];
    if (leaves.length) leaves.push(["system-memory/unclassified", residual - leaves.reduce((sum, [, bytes]) => sum + bytes, 0), ""]);
    return {
      metricId: field,
      totalMetricId: fact.totalMetricId,
      processFieldName: field,
      ...(physicalResidualSource(field) ? { residualSource: physicalResidualSource(field) } : {}),
      unit: fact.unit ?? "B",
      scaleMode,
      totalValue: missing ? null : fact.totalValue,
      capacityValue: fact.capacityValue,
      totalSystemPercent: percent(fact.totalValue),
      sharedValue: null,
      ...barFacts({ processSumValue: fact.processSum, residualValue: residual, additiveAcrossProcesses: fact.additive, capturedAt,
        quantity: fact.quantity, windowMilliseconds: fact.windowMilliseconds, gpuEngine: fact.gpuEngine, engineClass: fact.engineClass }),
      software: [[0, fact.processSum, percent(fact.processSum), 1, 5, detailed ? [[4242, "example.exe", "C:\\FrontendHarness\\example.exe",
        fact.processSum, percent(fact.processSum), 100, "测试用户", "x64", "direct", 0, String(132000000000000000n + 4242n)]] : []],
        ...(residual === null || residual === 0 ? [] : [[residualIndex, residual, percent(residual), 0, 0,
          leaves.map(([kind, bytes, name]) => [-1, name, null, bytes, percent(bytes), bytes * 100 / residual, null, null, kind, 0, null])]])]
    };
  });
}

// 资源条上每个指标自己的事实（ResourceBreakdownWireSnapshotJsonConverter.WriteBars）：之和、残差、可加性、时刻总写；
// 口径、窗口和 GPU 引擎只在有值时写，engineClass 跟 gpuEngine 一起写。
function barFacts({ processSumValue, residualValue, additiveAcrossProcesses, capturedAt, quantity = null, windowMilliseconds = null, gpuEngine = null, engineClass = null }) {
  return { processSumValue, residualValue, additiveAcrossProcesses, capturedAt,
    ...(quantity === null ? {} : { quantity }), ...(windowMilliseconds === null ? {} : { windowMilliseconds }),
    ...(gpuEngine === null ? {} : { gpuEngine, engineClass }) };
}

// 资源事实帧（后端 ResourceFactsSnapshot，version 2，metric-source round 4）。每个指标按 Balance 结算：
// processSumValue 是本帧发布的进程之和；可加口径 residualValue = 总量 − 之和（带符号），非零时记在软件条目
// resource-residual:<totalMetricId> 下的一个 PID -1、attributionKind 为 system-residual 的叶子，并进身份目录；
// 不可加口径（工作集）残差为 null、没有残差条目。总量读不到（resourceTableTotalsMissing）时：GPU 显存发不可用原因
// gpu-dedicated-memory-total-unavailable、不发行；其余指标保留进程行、没有残差，原因是 system-total-unavailable。
function resourceFacts(url, capturedAt = now()) {
  if (scenario.readmeShowcase) return showcase.showcaseResourceFacts(new Set(url.searchParams.getAll("items")), capturedAt);
  const count = scenario.resourceTableVariant === "empty" ? 0 : normalizeFixtureCount(scenario.resourceTableRowCount, scenario.resourceTableVariant === "many" ? 80 : 1, scenario.generatedData?.names.length ?? 1000);
  const software = Array.from({ length: count }, (_, index) => ({ softwareId: `frontend-harness.software${index ? `.${index}` : ""}`,
    name: index ? `示例软件 ${index}` : "示例软件", kind: "other", attributionKind: "running" }));
  if (scenario.generatedData) software.forEach((item, index) => { item.name = scenario.generatedData.names[index]; });
  const processes = software.map((_, index) => ({ processId: 4242 + index, processStartKey: String(132000000000000000n + BigInt(4242 + index)),
    name: `example-${index}.exe`, executablePath: "C:\\FrontendHarness\\example.exe", userName: "测试用户", architecture: "x64", attributionKind: "process" }));
  if (scenario.generatedData) processes.forEach((item, index) => { item.name = `${scenario.generatedData.names[index]}.exe`; });
  const items = new Set(url.searchParams.getAll("items"));
  const attributions = items.has("process.attribution") ? software.slice(0, count).map((identity, index) => ({ softwareId: identity.softwareId, processIndex: index, baseScore: 0 })) : [];

  function balance({ metricId, unit, totalMetricId, processFieldName, totalValue, capacityValue, processSum, additive, quantity = null,
    windowMilliseconds = null, gpuEngine = null, engineClass = null, sharedValue = null, residualRows = null, counterLeaves = null }) {
    const missing = scenario.resourceTableTotalsMissing.includes(metricId) || generatedReadings()[metricId]?.numericValue === null;
    const total = missing ? null : totalValue;
    const percent = value => capacityValue > 0 ? value * 100 / capacityValue : 0;
    const facts = { totalMetricId, quantity, windowMilliseconds, gpuEngine, engineClass, capturedAt, residualSource: physicalResidualSource(metricId), additiveAcrossProcesses: additive };
    if (total === null && (!Object.hasOwn(processMemoryFacts, metricId) || metricId.startsWith("process_all_gpu_"))) {
      return { metricId, unit, processFieldName, totalValue: null, capacityValue: null, totalSystemPercent: null, sharedValue: null, software: [],
        ...facts, processSumValue: null, residualValue: null,
        unavailableReason: scenario.resourceTableTotalsReasons[metricId] ?? (Object.hasOwn(processMemoryFacts, metricId) ? "gpu-measure-total-unavailable"
          : /^gpu\.\d+\.vram$/.test(metricId) ? "gpu-dedicated-memory-total-unavailable" : "system-total-unavailable") };
    }
    const shared = sharedValue === null ? null : sharedValue / Math.max(1, count);
    const weights = scenario.generatedData?.weights;
    const weightSum = weights?.reduce((sum, value) => sum + value, 0) ?? 1;
    const rows = software.slice(0, count).map((identity, index) => {
      const share = weights ? processSum * weights[index] / weightSum : count > 0 ? processSum / count : 0;
      return { softwareId: identity.softwareId, value: share, systemPercent: percent(share),
      processCount: 1, baseScore: 0, sharedValue: shared,
      processes: [{ processIndex: index, value: share, systemPercent: percent(share), softwarePercent: 100, baseScore: 0, sharedValue: shared }] };
    });
    const sum = count > 0 ? processSum : 0;
    const residual = additive && total !== null ? total - sum : null;
    if (residual !== null && residual !== 0 && residualRows !== null) {
      rows.push(...residualRows);
    } else if (residual !== null && residual !== 0) {
      const residualId = `resource-residual:${totalMetricId}`;
      if (!software.some(identity => identity.softwareId === residualId)) software.push({ softwareId: residualId, name: "", kind: "windows_system", attributionKind: "system-residual" });
      // 物理内存的保留量按计数器分项给出（后端 Balance 用同一个分项提供方）：差额为正时取除未分类以外的分项，
      // 分项是 PID -1 的行、标识在 attributionKind；余数记为未分类。没有分项时是一个 system-residual 叶子。
      const counted = counterLeaves !== null && residual > 0;
      const parts = counted ? counterLeaves.filter(([kind]) => kind !== "system-memory/unclassified").map(([kind, value, name]) => ({ kind, value, name })) : [];
      const remainder = residual - parts.reduce((total, part) => total + part.value, 0);
      if (remainder !== 0) parts.push({ kind: counted ? "system-memory/unclassified" : "system-residual", value: remainder, name: "" });
      const segments = parts.map(part => {
        const processIndex = processes.push({ processId: -1, processStartKey: null, name: part.name, executablePath: null, userName: null, architecture: null, attributionKind: part.kind }) - 1;
        attributions.push({ softwareId: residualId, processIndex, baseScore: 0 });
        return { processIndex, value: part.value, systemPercent: percent(part.value), softwarePercent: part.value * 100 / residual, baseScore: 0, sharedValue: null };
      });
      rows.push({ softwareId: residualId, value: residual, systemPercent: percent(residual), processCount: 0, baseScore: 0, sharedValue: null, processes: segments });
    }
    return { metricId, unit, processFieldName, totalValue: total, capacityValue, totalSystemPercent: total === null ? null : percent(total),
      sharedValue: sharedValue === null ? null : count > 0 ? sharedValue : 0, software: rows, ...facts, processSumValue: sum, residualValue: residual,
      unavailableReason: total === null ? "system-total-unavailable" : null };
  }

  const metrics = [...items].filter(id => id !== "process.inventory" && id !== "process.attribution").map(metricId => {
    const fact = processMemoryFacts[metricId];
    if (fact) return balance({ metricId, unit: fact.unit ?? "B", totalMetricId: fact.totalMetricId, processFieldName: metricId, totalValue: count > 0 ? fact.totalValue : 0,
      capacityValue: fact.capacityValue, processSum: fact.processSum, additive: fact.additive, quantity: fact.quantity,
      windowMilliseconds: fact.windowMilliseconds, gpuEngine: fact.gpuEngine, engineClass: fact.engineClass,
      counterLeaves: scenario.physicalMemoryResidual !== "off" && metricId === "process_all_private_working_set_actual" ? physicalMemoryFixture.counterLeaves : null });
    if (metricId === "memory.usage" && scenario.physicalMemoryResidual !== "off") {
      // 物理内存（自动选择）：示例软件合计为已归属的进程私有内存，差额是保留量这一行，下面是计数器分项（PID -1）。
      const fixture = physicalMemoryFixture;
      return balance({ metricId, unit: "B", totalMetricId: metricId, processFieldName: automaticProcessFields[metricId], totalValue: fixture.usedBytes,
        capacityValue: fixture.capacityBytes, processSum: fixture.attributedPrivateBytes, additive: true, counterLeaves: fixture.counterLeaves });
    }
    // 自动指标：内存类按字节，其余按百分比或速率；accounting 场景下总量是进程之和的两倍，差额进残差。
    const memory = metricId === "memory.usage" || metricId.endsWith(".vram");
    const throughputMetric = metricId === "disk.io" || metricId === "network.traffic";
    const generated = generatedReadings()[metricId];
    const processSum = count > 0 ? generated?.numericValue ?? (memory ? 536870912 : 12) : 0;
    // 计数器叶子替代通用残差行；整机总量包含这些叶子，使 r4 的残差事实与行值一致。
    const residualRows = metricId === "memory.usage" && scenario.resourceMemoryResidualLeaves
      && !scenario.resourceTableTotalsMissing.includes(metricId)
      ? memoryResidualLeafRows(software, processes, attributions) : null;
    const totalValue = residualRows !== null
      ? processSum + residualRows.reduce((sum, row) => sum + row.value, 0)
      : scenario.resourceTableAccountingFixture ? processSum * 2 : processSum;
    return balance({ metricId, unit: memory ? "B" : throughputMetric ? "B/s" : "%", totalMetricId: metricId,
      processFieldName: automaticProcessFields[metricId] ?? null, totalValue, processSum, additive: true,
      capacityValue: generated ? (generated.percent > 0 ? generated.numericValue * 100 / generated.percent : memory ? 64 * GiB : 100)
        : memory ? 1073741824 * (scenario.resourceTableAccountingFixture ? 2 : 1) : throughputMetric ? Math.max(1, totalValue) : 100,
      quantity: metricId.endsWith(".vram") ? "dedicated_memory_used" : null,
      sharedValue: memory ? 134217728 : null, residualRows });
  });
  return { version: 2, capturedAt, software, processes, metrics, attributions };
}

// 后端 PhysicalMemoryResidualBreakdownProvider 的形状：残差软件 processCount 0，每个计数器叶子一个 PID -1 的进程段，
// attributionKind 是叶子 kind，名字为空，只有池标签带四字节标签名。
function memoryResidualLeafRows(software, processes, residualAttributions) {
  const softwareId = "resource-residual:memory.usage";
  const capacity = 1073741824;
  const leaves = [["system-memory/nonpaged-pool/tag/4346744E", "NtFC", 25165824], ["system-memory/paged-pool-resident", "", 16777216],
    ["system-memory/driver-resident", "", 16777216], ["system-memory/unclassified", "", 8388608]];
  const total = leaves.reduce((sum, leaf) => sum + leaf[2], 0);
  if (!software.some(identity => identity.softwareId === softwareId)) software.push({ softwareId, name: "", kind: "windows_system", attributionKind: "system-residual" });
  const segments = leaves.map(([kind, name, bytes]) => {
    const processIndex = processes.push({ processId: -1, processStartKey: null, name, executablePath: null, userName: null, architecture: null, attributionKind: kind }) - 1;
    residualAttributions.push({ softwareId, processIndex, baseScore: 0 });
    return { processIndex, value: bytes, systemPercent: bytes * 100 / capacity, softwarePercent: bytes * 100 / total, baseScore: 0, sharedValue: null };
  });
  return [{ softwareId, value: total, systemPercent: total * 100 / capacity, processCount: 0, baseScore: 0, sharedValue: null, processes: segments }];
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
/*
 * 拓扑订阅的帧（后端 DeviceTopologyCaptureOwner 的变化状态，push-only-on-change）：每次采集和上一次提交比较，
 * 结构或健康真的变了才提交、推送；没变的采集不推，失败没变也不再推，恢复会推。
 * - 比较不含 stateRevision、lastSuccessAt、lastAttemptAt、snapshot.capturedAt 和 informationFields.observedAt；
 * - 结构帧的 informationFields 只留 Fixed 与 EventDriven 字段，Sampled 字段（卷可用空间、页面文件用量、
 *   网络连接、地址与路由寿命）只经各自的采样订阅发；
 * - 快照内容没变时沿用已提交的那份（capturedAt 不动）；
 * - 提交帧的 lastSuccessAt 是提交这份内容的那次成功采集的时间，不是心跳；失败帧沿用最后一次成功的时间。
 * GET /api/device-topology/state 是一次新采集的完整状态（含 Sampled 字段），不经过这里。
 */
const sampledVolatilityKind = 2; // 后端 ReadVolatilityKind.Sampled，wire 上是数字枚举。
let committedDeviceTopology = null;

function observeDeviceTopology(capturedAt) {
  const observed = deviceTopologyStructureFrame(fixtureDeviceTopologyState(capturedAt));
  const key = deviceTopologyChangeKey(observed);
  if (committedDeviceTopology?.key === key) return committedDeviceTopology.state;
  const previous = committedDeviceTopology?.state;
  const state = { ...observed };
  if (state.state === "failed") state.lastSuccessAt = previous?.lastSuccessAt ?? null;
  if (isRecord(previous?.snapshot) && isRecord(state.snapshot)
    && JSON.stringify({ ...previous.snapshot, capturedAt: null }) === JSON.stringify({ ...state.snapshot, capturedAt: null })) {
    state.snapshot = previous.snapshot;
  }
  committedDeviceTopology = { key, state };
  return state;
}

function deviceTopologyStructureFrame(state) {
  if (!isRecord(state.informationFields)) return state;
  const devices = state.informationFields.devices
    .map(device => ({ ...device, fields: device.fields.filter(field => field.definition?.volatility?.kind !== sampledVolatilityKind) }))
    .filter(device => device.fields.length > 0);
  return { ...state, informationFields: { ...state.informationFields, devices } };
}

function deviceTopologyChangeKey(state) {
  const { stateRevision: _revision, lastSuccessAt: _success, lastAttemptAt: _attempt, ...content } = state;
  return JSON.stringify({ ...content,
    snapshot: isRecord(content.snapshot) ? { ...content.snapshot, capturedAt: null } : content.snapshot,
    informationFields: isRecord(content.informationFields) ? { ...content.informationFields, observedAt: null } : content.informationFields });
}

function fixtureDeviceTopologyState(capturedAt = now()) {
  return applyTopologyHealthScenario(fixtureDeviceTopologyCapture(capturedAt));
}

// 采集失败：后端 PublishFailure 保留当前快照，写 failed、failureCode 和这次的诊断。
function applyTopologyHealthScenario(state) {
  if (!scenario.deviceTopologyFailure || !isRecord(state.snapshot)) return state;
  return { ...state, state: "failed", failureCode: "device-topology-capture-failed", lastSuccessAt: null,
    attemptDiagnostics: [{ sourceId: "display-coordinator", status: "required-incomplete",
      code: "device-topology-display-coordinator-incomplete", messageCode: { domain: 5, code: 10, args: ["Warming"] } }] };
}

function fixtureDeviceTopologyCapture(capturedAt) {
  if (scenario.deviceTopologyInvalid) return { schemaVersion: "invalid" };
  if (scenario.readmeShowcase) return showcase.showcaseDeviceTopology(capturedAt, ++deviceTopologyRevision);
  if (recordedTopologyState) {
    return applyTopologyFactScenarios({ ...structuredClone(recordedTopologyState),
      snapshot: { ...structuredClone(recordedTopologyState.snapshot), capturedAt },
      stateRevision: ++deviceTopologyRevision, lastSuccessAt: capturedAt, lastAttemptAt: capturedAt });
  }
  return applyTopologyFactScenarios({
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
  });
}

function identityFact(value, reason = "source-unavailable") {
  return { value, reason: value === null ? reason : null };
}

// The five samples use the backend machineIdentity contract, never snapshot.system.
function fixtureMachineIdentity(formFactor) {
  if (formFactor === "unknown") return { formFactor, evidence: {
    chassisTypes: identityFact(null), systemBatteryPresent: identityFact(null), cpuModels: identityFact(null),
    gpuModels: identityFact(null), gpuAttachments: identityFact(null), workstationComponents: []
  } };
  const portable = formFactor === "laptop" || formFactor === "mobile_workstation";
  const workstationComponents = formFactor === "workstation" ? [
    { component: "cpu", model: "AMD Ryzen Threadripper PRO 9955WX 16-Cores", ruleId: "workstation-cpu" },
    { component: "gpu", model: "NVIDIA RTX PRO 6000 Blackwell Workstation Edition", ruleId: "professional-gpu" }
  ] : formFactor === "mobile_workstation" ? [
    { component: "gpu", model: "NVIDIA RTX PRO 5000 Blackwell Generation Laptop GPU", ruleId: "professional-gpu" }
  ] : [];
  return { formFactor, evidence: {
    chassisTypes: identityFact([portable ? 10 : formFactor === "workstation" ? 7 : 3]),
    systemBatteryPresent: identityFact(portable),
    cpuModels: identityFact(workstationComponents.filter(item => item.component === "cpu").map(item => item.model)),
    gpuModels: identityFact(workstationComponents.filter(item => item.component === "gpu").map(item => item.model)),
    gpuAttachments: identityFact([]), workstationComponents
  } };
}

const graphicsDockName = "NVIDIA RTX PRO 6000 External Adapter";
const externalMachineLink = String.raw`USB4\FIXTURE_PEER_LINK`;

function applyTopologyFactScenarios(state) {
  const snapshot = state.snapshot;
  if (scenario.deviceTopologyMachineIdentity !== null)
    snapshot.machineIdentity = fixtureMachineIdentity(scenario.deviceTopologyMachineIdentity);
  if (scenario.deviceTopologyGraphicsDock) {
    snapshot.gpuInformation = [...(snapshot.gpuInformation ?? []), {
      pnpInstanceId: String.raw`PCI\FIXTURE_GRAPHICS_DOCK`, adapterLuid: "0000000000000400", name: graphicsDockName,
      pciVendorId: "10DE", pciDeviceId: "1234", driverVersion: null,
      adapterTypeFlags: 1024, isDetachable: true, attachmentKind: "graphics_dock",
      attachmentEvidenceSource: "d3dkmt-adapter-type"
    }];
    snapshot.machineIdentity = { ...snapshot.machineIdentity, evidence: { ...snapshot.machineIdentity.evidence,
      gpuModels: identityFact([...(snapshot.machineIdentity.evidence.gpuModels.value ?? []), graphicsDockName]),
      gpuAttachments: identityFact([...(snapshot.machineIdentity.evidence.gpuAttachments.value ?? []), {
        model: graphicsDockName, isDetachable: true, adapterTypeFlags: 1024,
        attachmentKind: "graphics_dock", evidenceSource: "d3dkmt-adapter-type"
      }])
    } };
  }
  if (scenario.deviceTopologyNumericUsb) snapshot.ports = fixtureNumericUsbPorts();
  if (scenario.deviceTopologyTransport) snapshot.ports = fixtureTransportPorts();
  if (scenario.deviceTopologyBiosVersion !== null) snapshot.system = { ...snapshot.system, biosVersion: scenario.deviceTopologyBiosVersion };
  if (scenario.deviceTopologyStorage) {
    snapshot.ports = [...snapshot.ports, fixtureStorageVolumePort()];
    state.informationFields = fixtureVolumeInformation(state.lastSuccessAt ?? snapshot.capturedAt);
  }
  if (scenario.deviceTopologyMobile) {
    snapshot.ports = [...snapshot.ports, fixtureMobilePort()];
    state.informationFields = { ...state.informationFields,
      devices: [...(state.informationFields?.devices ?? []), fixtureMobileInformation()] };
  }
  if (scenario.deviceTopologyWireless !== null)
    snapshot.ports = [...snapshot.ports, fixtureWirelessPort(scenario.deviceTopologyWireless)];
  if (scenario.deviceTopologyExternalMachine) {
    snapshot.externalMachines = [{ localLinkDeviceId: externalMachineLink, peerIdentity: null,
      formFactor: "workstation", model: null, components: [{ component: "soc", model: null }],
      evidence: { source: "windows-usb4net", compatibleId: String.raw`USB4\USB4NET`, mediaConnectStateRaw: 1,
        peerPropertiesReason: "windows-peer-properties-unavailable" }
    }];
    snapshot.ports = [...snapshot.ports, { ...fixtureWirelessPort("unknown"), id: "fixture-peer-link",
      displayName: "Fixture USB4 network link", deviceId: externalMachineLink, protocol: "USB4NET",
      transport: fixtureKnownTransport("usb4", "enumerator-usb4", externalMachineLink, "USB4"),
      compatibleIds: [String.raw`USB4\USB4NET`],
      network: { interfaceName: "USB4NET", connectionState: "connected", transmitLinkSpeed: null,
          receiveLinkSpeed: null, permanentAddress: null, activeMtuBytes: null,
          hardwareInterface: true, connectorPresent: null, wireless: null }
    }];
  }
  // 后端每个端口都写 informationIdentity（没有信息对象时为 null）；录下来的旧状态和夹具端口没有这个键，按没有对象补上。
  snapshot.ports = snapshot.ports.map(port => Object.hasOwn(port, "informationIdentity") ? port : { ...port, informationIdentity: null });
  return state;
}

function fixtureWirelessPort(variant) {
  return { ...fixtureHdmiPort(), id: "fixture-wireless", isPhysicalConnector: false,
    displayName: "Misleading Wi-Fi 7 Name", connectorKind: "internal", busKind: "network", hardwareKind: "Network Adapter",
    protocol: "Wi-Fi 7", speed: null, physicalMaximumSpeed: null, deviceId: String.raw`PCI\FIXTURE_WLAN`, pnpClass: "Net",
    transport: fixtureKnownTransport("pci", "enumerator-pci", String.raw`PCI\FIXTURE_WLAN`, "PCI"),
    display: null, network: { interfaceName: "Misleading Wi-Fi 7 Name", connectionState: "connected",
      transmitLinkSpeed: null, receiveLinkSpeed: null, permanentAddress: null, activeMtuBytes: null,
      hardwareInterface: true, connectorPresent: null,
      wireless: { source: "wlan-api", interfaceStateRaw: 1,
        supportedPhyTypesRaw: variant === "standards" ? [7, 8, 10] : null,
        supportedStandards: variant === "standards" ? ["802.11n", "802.11ac", "802.11ax"] : null,
        negotiatedPhyTypeRaw: variant === "standards" ? 8 : null,
        negotiatedStandard: variant === "standards" ? "802.11ac" : null }
    }
  };
}

// 带分区和卷的 USB 移动硬盘，形状同后端结构快照：卷的 freeBytes 在结构里恒为 null（DeviceTopologyStructure）。
const fixtureVolumeIdentity = "\\\\?\\Volume{5c1f0e2a-7b3d-4e8f-9a61-0d2c4b6e8f10}\\";
function fixtureStorageVolumePort() {
  const uas = fixtureNumericUsbPorts().find(port => port.id === "fixture-usb-storage-uas");
  return { ...uas, id: "fixture-usb-storage-volumes", displayName: "Fixture portable SSD",
    deviceId: String.raw`USBSTOR\FIXTURE_PORTABLE_SSD`, pnpClass: "DiskDrive",
    topologyPath: { domain: 5, code: 64, args: ["root/fixture-usb-storage-volumes"] },
    usb: { ...uas.usb, portNumber: 20, deviceAddress: 20, productName: "Fixture portable SSD" },
    storage: { physicalDeviceId: String.raw`\\.\PHYSICALDRIVE7`, model: "Fixture Portable SSD", manufacturer: "Frontend Harness",
      serialNumber: null, firmwareRevision: "1.0", mediaType: "SSD", busType: "USB", capacityBytes: 1_000_204_886_016,
      bytesPerSector: 512, partitionStyle: "GPT", healthStatus: "Healthy", source: { domain: 5, code: 59, args: [] },
      partitions: [{ deviceId: "Disk #7, Partition #0", partitionNumber: 1, type: "GPT: Basic Data",
        capacityBytes: 1_000_202_788_864, startingOffsetBytes: 1_048_576, bootable: false, bootPartition: false, primaryPartition: true,
        volumes: [{ driveLetter: "F:", label: "Portable", fileSystem: "exFAT", capacityBytes: 1_000_185_827_328,
          freeBytes: null, mountState: "mounted", volumeSerialNumber: null }] }] } };
}

// 卷 F: 的信息字段（后端 DeviceInformationFieldRegistry 的定义，volatility 是数字枚举）：挂载路径是 EventDriven，
// 可用空间是 Sampled。完整状态两者都有；结构帧去掉 Sampled 的那个。
function fixtureVolumeInformation(observedAt) {
  const definition = (name, valueKind, unit, quantity, sourceId, volatility) =>
    ({ name, resource: "volume", vendor: "all", valueKind, unit, quantity, sourceId, volatility });
  return { observedAt, devices: [{ deviceIdentity: fixtureVolumeIdentity, objectType: "volume", fields: [
    { definition: definition("volume_all_mount_paths_reported", "text-list", "path", "volume.topology.mount_paths_reported",
      "windows.volume-metadata", { kind: 1, event: "Windows storage layout or volume configuration changed" }), value: ["F:\\"] },
    { definition: definition("volume_all_free_bytes_actual", "unsigned-integer", "B", "volume.space.free_bytes_actual",
      "windows.volume-space", { kind: sampledVolatilityKind }), value: scenario.deviceTopologyVolumeFreeBytes }
  ] }] };
}

// 一部 USB 手机，形状同后端结构快照：smartDevice 里没有电量和存储剩余（DeviceTopologyStructure 去掉了），
// 它们是端口信息对象（informationIdentity）上的两个 Sampled 字段。
const fixtureMobileIdentity = String.raw`windows-pnp|USB\VID_18D1&PID_4EE1\FIXTURE0001`;
function fixtureMobilePort() {
  // 以数值 USB 夹具里的手机为底（MTP 接口），不是存储设备：外置存储适配器排在手机之前，大容量存储接口会被它认走。
  const mobile = fixtureNumericUsbPorts().find(port => port.id === "fixture-usb-mobile");
  return { ...mobile, id: "fixture-usb-phone", displayName: "Fixture phone", deviceId: String.raw`USB\VID_18D1&PID_4EE1\FIXTURE0001`,
    informationIdentity: fixtureMobileIdentity, pnpClass: "WPD",
    topologyPath: { domain: 5, code: 64, args: ["root/fixture-usb-phone"] },
    usb: { ...mobile.usb, portNumber: 21, deviceAddress: 21, productName: "Fixture phone" },
    smartDevice: { deviceType: "phone", manufacturer: "Frontend Harness", model: "Fixture Phone", serialNumber: null,
      firmwareVersion: "14", protocol: "MTP", transport: "USB", source: { domain: 5, code: 59, args: [] },
      storages: [{ name: "Internal shared storage", capacityBytes: 128_000_000_000, fileSystem: null }] } };
}

function fixtureMobileInformation() {
  const definition = (name, valueKind, unit, quantity) =>
    ({ name, resource: "mobile_device", vendor: "all", valueKind, unit, quantity, sourceId: "windows.device-topology.smart-device-storage",
      volatility: { kind: sampledVolatilityKind } });
  return { deviceIdentity: fixtureMobileIdentity, objectType: "mobile_device", fields: [
    { definition: definition("mobile_all_battery_percent_reported", "number", "%", "mobile.topology.battery-percent-reported"),
      value: scenario.deviceTopologyMobileBatteryPercent },
    { definition: definition("mobile_all_storage_free_bytes_reported", "array", "storage[]",
      "mobile.topology.smart-device-storage-name-and-free-bytes-reported-as-decimal-string-or-null"),
      value: [{ name: "Internal shared storage", freeBytes: scenario.deviceTopologyMobileStorageFreeBytes }] }
  ] };
}

function fixtureNumericUsbPorts() {
  const samples = [
    ["keyboard", [[3, 1, 1]], 0], ["mouse", [[3, 1, 2]], 0],
    ["audio", [[1, 1, 0], [1, 2, 0], [3, 0, 0]], 239], ["hub", [[9, 0, 0]], 9],
    ["storage-uas", [[8, 6, 98]], 0], ["storage-bot", [[8, 6, 80]], 0],
    ["camera", [[14, 1, 0], [14, 2, 0], [1, 1, 0]], 239],
    ["mobile", [[6, 1, 1], [2, 2, 1], [10, 0, 0], [255, 0, 0]], 239],
    ["vendor", [[255, 128, 254]], 239], ["unknown", null, null]
  ];
  return samples.map(([kind, interfaces, deviceClassCode], index) => {
    const port = { ...fixtureHdmiPort(), id: `fixture-usb-${kind}`, displayName: `Fixture ${kind}`,
      deviceId: `USBPORT\\FIXTURE_${kind}`, connectorKind: "usb-a", busKind: "usb", hardwareKind: "USB Device",
      protocol: "USB 3.x", speed: "5 Gbps", physicalMaximumSpeed: "5 Gbps", display: null,
      transport: { code: "usb", reason: null, ruleId: "usb-hub-port", evidence: [{ source: "usb-hub-ioctl",
        deviceId: String.raw`\\?\usb#root_hub30#fixture`, depth: 0, enumeratorName: null,
        busTypeGuid: null, service: null, parentDeviceId: null }] },
      topologyPath: { domain: 5, code: 64, args: [`root/fixture-usb-${kind}`] },
      usb: { hubDevicePath: String.raw`\\?\usb#root_hub30#fixture`, portNumber: index + 1,
        deviceConnected: true, connectionStatus: { domain: 5, code: 47, args: [] }, negotiatedSpeed: "5 Gbps",
        deviceAddress: index + 1, vendorId: "1234", productId: "5678", deviceIsHub: kind === "hub",
        supportedProtocols: null, operatingAtSuperSpeedOrHigher: null, superSpeedCapableOrHigher: null,
        operatingAtSuperSpeedPlusOrHigher: null, superSpeedPlusCapableOrHigher: null,
        portIsUserConnectable: null, portIsDebugCapable: null, portHasMultipleCompanions: null,
        portConnectorIsTypeC: null, companionPorts: [], deviceSpecification: "USB 3.2", deviceRevision: "1.0",
        // Deliberately wrong legacy summaries must never select the adapter or label.
        deviceClass: "Misleading Mass Storage / Composite", interfaceProtocols: ["Misleading Audio Streaming / HID Boot Keyboard"],
        deviceClassCode, deviceSubClassCode: deviceClassCode === 239 ? 2 : deviceClassCode === null ? null : 0,
        deviceProtocolCode: deviceClassCode === 239 ? 1 : deviceClassCode === null ? null : 0,
        interfaces: interfaces?.map(([classCode, subClassCode, protocolCode], interfaceNumber) =>
          ({ interfaceNumber, alternateSetting: 0, classCode, subClassCode, protocolCode })) ?? null,
        manufacturerName: "Frontend Harness", productName: `Fixture ${kind}`, serialNumber: null,
        downstreamHubDevicePath: kind === "hub" ? String.raw`\\?\usb#fixture_hub` : null, endpoints: null }
    };
    if (kind === "keyboard" || kind === "mouse") port.hid = { hidType: kind, hidSpecification: "HID 1.11",
      inputPollingIntervalMicroseconds: 1000, theoreticalReportRateHz: 1000, reportedDpi: null, reportedScanRateHz: null,
      standardCapabilitySource: { domain: 5, code: 60, args: [] }, vendorCapabilitySource: null };
    return port;
  });
}

function fixtureKnownTransport(code, ruleId, deviceId, enumeratorName) {
  return { code, reason: null, ruleId, evidence: [{ source: "setupapi-cfgmgr32", deviceId, depth: 0,
    enumeratorName, busTypeGuid: null, service: null, parentDeviceId: null }] };
}

// Transport facts are supplied independently of the deliberately misleading device properties.
function fixtureTransportPorts() {
  const childId = String.raw`HID\FIXTURE_TRANSPORT_CHILD`;
  const parentId = String.raw`ACPI\FIXTURE_TRANSPORT_PARENT`;
  const samples = [
    ["code", "Misleading NVMe USB4 transport name", String.raw`PCI\FIXTURE_TRANSPORT_CODE`, "stornvme",
      fixtureKnownTransport("acpi", "enumerator-acpi", String.raw`PCI\FIXTURE_TRANSPORT_CODE`, "ACPI")],
    ["unknown", "Misleading Bluetooth NVMe transport name", String.raw`PCI\FIXTURE_TRANSPORT_UNKNOWN`, "stornvme",
      { code: null, reason: "not-queried", ruleId: null, evidence: [] }],
    ["pci", "Fixture ordinary PCI transport", String.raw`PCI\FIXTURE_TRANSPORT_PCI`, null,
      fixtureKnownTransport("pci", "enumerator-pci", String.raw`PCI\FIXTURE_TRANSPORT_PCI`, "PCI")],
    ["parent", "Fixture parent-chain transport", childId, null,
      { code: "i2c_hid", reason: null, ruleId: "service-hidi2c", evidence: [
        { source: "setupapi-cfgmgr32", deviceId: childId, depth: 0, enumeratorName: "HID",
          busTypeGuid: "eeaf37d0-1963-47c4-aa48-72476db7cf49", service: null, parentDeviceId: parentId },
        { source: "setupapi-cfgmgr32", deviceId: parentId, depth: 1, enumeratorName: "ACPI",
          busTypeGuid: "d7b46895-001a-4942-891f-a7d46610a843", service: "hidi2c", parentDeviceId: null }
      ] }]
  ];
  return samples.map(([kind, displayName, deviceId, service, transport]) => ({ ...fixtureHdmiPort(),
    id: `fixture-transport-${kind}`, displayName, deviceId, service, transport,
    isPhysicalConnector: false, connectorKind: "internal", busKind: "internal", hardwareKind: "Fixture Device",
    pnpClass: "FixtureDevice", protocol: "Misleading legacy protocol", display: null,
    speed: null, physicalMaximumSpeed: null,
    topologyPath: { domain: 5, code: 64, args: [`root/fixture-transport-${kind}`] }
  }));
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
  if (entry.selectedItems) {
    entry.header.items = entry.selectedItems.map((item) => targetedRecordingItemReport(item, entry.defaultIntervalMs, true));
    entry.header.processes = [{ processId: 123, processStartKey: 1234, name: "example.exe",
      firstSeenAt: entry.header.startedAt, exitedAt: null }];
    entry.header.storedBytes = 2048;
  }
}

function targetedRecordingIntervalValid(value) {
  return Number.isInteger(value) && value >= 100 && value <= 60000;
}

// 录制项列表的校验，代码和后端一致；没有问题时返回 null。
function targetedRecordingItemsError(items) {
  if (!Array.isArray(items) || items.length === 0) return "targeted-recording-profile-items-empty";
  if (items.length > 256) return "targeted-recording-profile-items-too-many";
  const seen = new Set();
  for (const item of items) {
    if (!["frames", "software", "metric"].includes(item?.kind)) return "targeted-recording-item-invalid-kind";
    const entry = targetedRecordingCatalog.find((candidate) => candidate.kind === item.kind && candidate.id === item.id);
    if (!entry) return "targeted-recording-item-unknown";
    const key = `${item.kind}:${item.id}`;
    if (seen.has(key)) return "targeted-recording-item-duplicate";
    seen.add(key);
    const source = item.source ?? "auto";
    if (source !== "auto" && !entry.sources.includes(source)) return "targeted-recording-item-source-invalid";
    if (item.syncPrecision !== undefined && !["low", "mid", "high"].includes(item.syncPrecision)) {
      return "targeted-recording-item-invalid-precision";
    }
    if (item.kind !== "frames" && item.intervalMs !== undefined && item.intervalMs !== null
      && !targetedRecordingIntervalValid(item.intervalMs)) return "targeted-recording-interval-out-of-range";
  }
  return null;
}

function normalizeTargetedRecordingItems(items) {
  return items.map((item) => ({ kind: item.kind, id: item.id, source: item.source ?? "auto",
    intervalMs: item.kind === "frames" ? null : item.intervalMs ?? null, syncPrecision: item.syncPrecision ?? "mid" }));
}

// 报告头里一项的录制信息：完成后才有点数；整机温度在示例采样里是 null，记一段“没有值”的空缺。
function targetedRecordingItemReport(item, defaultIntervalMs, completed) {
  const entry = targetedRecordingCatalog.find((candidate) => candidate.kind === item.kind && candidate.id === item.id);
  const frames = item.kind === "frames";
  const actualSource = item.source !== "auto" ? item.source
    : frames ? "etw" : entry?.sources[0] ?? item.source;
  const intervalOrigin = frames ? "per-present"
    : item.intervalMs !== null ? "item"
    : entry?.naturalIntervalMs !== null && entry?.naturalIntervalMs !== undefined ? "natural" : "profile-default";
  return {
    kind: item.kind,
    id: item.id,
    unit: entry?.unit ?? "",
    selectedSource: item.source,
    actualSources: completed ? [actualSource] : [],
    selectedIntervalMs: frames ? null : item.intervalMs,
    intervalOrigin,
    actualIntervalMs: frames ? null : item.intervalMs ?? entry?.naturalIntervalMs ?? defaultIntervalMs,
    syncPrecision: item.syncPrecision,
    pointCount: completed ? (frames ? 3 : 2) : 0,
    gaps: completed && item.kind === "metric" && item.id === "cpu.temperature"
      ? [{ fromMs: 0, toMs: 1000, reason: "value-unavailable" }] : []
  };
}

// Deterministic report values, using the production wire shape including nullable metrics.
// Frame times differ (9, 10, 11 ms) so the distribution views have a spread; the summary keeps the 100 FPS average.
function targetedRecordingReport(entry) {
  const start = Date.parse(entry.header.startedAt);
  const end = Date.parse(entry.header.endedAt ?? now());
  const capturedAt = new Date(Math.max(start, end)).toISOString();
  return {
    recording: entry.header,
    summary: { intervalCount: 3, durationMs: 30, averageFps: 100,
      onePercentLowFps: 100, pointOnePercentLowFps: 100,
      p50FrameTimeMs: 10, p95FrameTimeMs: 10, p99FrameTimeMs: 10, maxFrameTimeMs: 10 },
    frameIntervals: [0, 1, 2].map((index) => ({ processId: 123, processStartKey: 1234,
      source: 0, swapChain: 1, endedAt: new Date(start + (end - start) * index / 2).toISOString(), durationMs: [9, 10, 11][index] })),
    fpsDistribution: [{ fromFps: 100, toFps: 110, count: 3 }],
    resourceSamples: [entry.header.startedAt, capturedAt].map((capturedAt) => ({
      capturedAt, ...targetedRecordingSampleValues(entry),
      processes: [{ processId: 123, processStartKey: 1234 }]
    }))
  };
}

// 按录制项录的报告，采样里只带选中的项；没有录制项的旧录制保持原来的四个值。
function targetedRecordingSampleValues(entry) {
  const software = { "cpu.usage": 25, "memory.usage": 1048576 };
  const system = { "cpu.usage": 40, "cpu.temperature": null, "memory.usage": 50 };
  if (!entry.selectedItems) {
    return { software: { "cpu.usage": 25, "memory.usage": 1048576 }, system: { "cpu.usage": 40, "cpu.temperature": null } };
  }
  const values = { software: {}, system: {} };
  for (const item of entry.selectedItems) {
    if (item.kind === "software") values.software[item.id] = software[item.id] ?? 1;
    if (item.kind === "metric") values.system[item.id] = item.id in system ? system[item.id] : 1;
  }
  return values;
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
    const kernelLeakReport = requestUrl.pathname === "/api/kernel-leaks/subscribe";
    const memoryProtectionStatus = requestUrl.pathname === "/api/memory-protection/subscribe";
    const backendNotifications = requestUrl.pathname === "/api/notifications/subscribe";
    const eventOnly = operation || managementInventoryRevision || productUpdateStatus || kernelLeakReport || memoryProtectionStatus
      || backendNotifications;
    let createPayload;
    try { createPayload = eventOnly ? null : subscriptionPayloadFactory(requestUrl, item.fields); }
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
      kernelLeakReport,
      memoryProtectionStatus,
      backendNotifications,
      createPayload
    });
  }

  response.writeHead(200, {
    "Content-Type": "application/x-ndjson; charset=utf-8",
    "Cache-Control": "no-store",
    // A canceled POST must close its streaming connection, not leave an unread
    // response on a reusable socket with live logical subscriptions.
    "Connection": "close",
    "X-Content-Type-Options": "nosniff"
  });
  for (const rejection of rejections) writeSubscriptionChannelFrame(response, rejection.id, rejection.value);
  if (subscriptions.length === 0) return response.end();
  subscriptionChannelResponses.add(response);
  response.once("close", () => subscriptionChannelResponses.delete(response));
  const releases = [];
  for (const subscription of subscriptions) {
    const releaseTelemetry = trackLogicalSubscription(subscription.requestUrl);
    const replayPath = subscription.requestUrl.pathname + subscription.requestUrl.search;
    if (canReplay(replayPath) && sliceReplay?.frames(replayPath).length) {
      const release = sliceReplay.subscribe(replayPath, value => {
        if (response.destroyed || response.writableEnded || !canReplay(replayPath)) return;
        writeSubscriptionChannelFrame(response, subscription.id, value);
        recordLogicalPublication(subscription.requestUrl);
      });
      releases.push(release);
    }
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

    if (subscription.memoryProtectionStatus) {
      const subscriber = {
        response,
        requestUrl: subscription.requestUrl,
        write: (value) => writeSubscriptionChannelFrame(response, subscription.id, value)
      };
      memoryProtectionSubscribers.add(subscriber);
      replayMemoryProtectionTo(subscriber);
      releases.push(() => {
        memoryProtectionSubscribers.delete(subscriber);
        releaseTelemetry();
      });
      continue;
    }

    if (subscription.backendNotifications) {
      const subscriber = {
        response,
        requestUrl: subscription.requestUrl,
        write: (value) => writeSubscriptionChannelFrame(response, subscription.id, value)
      };
      backendNotificationSubscribers.add(subscriber);
      publishBackendNotifications(subscriber);
      releases.push(() => {
        backendNotificationSubscribers.delete(subscriber);
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

    if (subscription.kernelLeakReport) {
      const subscriber = {
        response,
        requestUrl: subscription.requestUrl,
        write: (value) => writeSubscriptionChannelFrame(response, subscription.id, value)
      };
      kernelLeakReportSubscribers.add(subscriber);
      publishKernelLeakReportValue(subscriber);
      releases.push(() => {
        kernelLeakReportSubscribers.delete(subscriber);
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
      // 按变化推送的路径（pushOnChange）：这个订阅者上次收到的值。
      lastValue: undefined,
      publish(publishedAt, { force = false } = {}) {
        if (response.destroyed || response.writableEnded
          || (canReplay(replayPath) && sliceReplay?.frames(replayPath).length)
          || Date.now() < this.detailsReadyAt
          || (!force && scenario.pausedSubscriptionPaths.includes(subscription.requestUrl.pathname))) {
          return false;
        }
        const value = subscription.createPayload(publishedAt);
        // 当前值没换（同一个已提交对象）就不发；手动放行（force）照发当前值。
        if (subscription.createPayload.pushOnChange && !force && value === this.lastValue) return false;
        this.lastValue = value;
        writeSubscriptionChannelFrame(
          response,
          subscription.id,
          value);
        recordLogicalPublication(subscription.requestUrl);
        return true;
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
  "/api/local-system/status/subscribe",
  "/api/device-topology/state/subscribe",
  "/api/device-topology/fields/subscribe",
  "/api/cpu/topology/subscribe",
  "/api/cpu/residency/subscribe",
  "/api/optimization/smart/state/subscribe",
  "/api/optimization/smart/status/subscribe",
  "/api/operations/subscribe",
  "/api/management/inventory-revisions/subscribe",
  "/api/access-requests/subscribe",
  "/api/updates/product/status/subscribe",
  "/api/kernel-leaks/subscribe",
  "/api/memory-protection/subscribe",
  "/api/notifications/subscribe"
]);

// 调度段的 failure 与事实排除，形状同后端 tests/App/Fixtures/Optimization/smart-status.json 的三例
// （模式和其余字段由 smartStatus 给）。native-plan-failed 比夹具多一项 GPU 排除，带适配器标识
// （码取自 tests/App/Fixtures/Optimization/smart-exclusions.json），用来检查明细里的适配器一行。
const smartStatusSegments = {
  success: {
    failure: null,
    factExclusionCount: 0,
    factExclusionCounts: [],
    factExclusions: []
  },
  "cycle-failed": {
    failure: { kind: "cycle-failed", exceptionType: "IOException", nativePlanStatus: null },
    factExclusionCount: 0,
    factExclusionCounts: [],
    factExclusions: []
  },
  "native-plan-failed": {
    failure: { kind: "native-plan-failed", exceptionType: "InvalidDataException", nativePlanStatus: "capacity-exceeded" },
    factExclusionCount: 3,
    factExclusionCounts: [
      { fact: "inventory", reason: "inventory-overflow", count: 1 },
      { fact: "gpu-usage", reason: "gpu-row-missing-or-invalid", count: 1 },
      { fact: "native-plan", reason: "capacity-exceeded", count: 1 }
    ],
    factExclusions: [
      { processId: 43003, processStartKey: "9007199254740993", targetId: "process:43003:9007199254740993", fact: "inventory", reason: "inventory-overflow", adapterKey: null },
      { processId: 42, processStartKey: "9007199254740993", targetId: null, fact: "gpu-usage", reason: "gpu-row-missing-or-invalid", adapterKey: "9007199254740995" },
      { processId: null, processStartKey: null, targetId: null, fact: "native-plan", reason: "capacity-exceeded", adapterKey: null }
    ]
  }
};

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
    ...structuredClone(smartStatusSegments[scenario.smartStatusSegment]),
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
  if (!subscriptionChannelSelectors.has(selector)
    && (scenario.readmeShowcase || !sliceReplay?.frames(value).length)) {
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

// 后端只在内容变化时推的订阅：发帧函数返回当前已提交的值，内容没变时是同一个对象；
// 订阅者按对象是否换了决定发不发（订阅时的第一帧总发）。
function pushOnChange(createPayload) {
  createPayload.pushOnChange = true;
  return createPayload;
}

/*
 * 选中的设备信息字段（后端 FieldSubscriptionCore.SubscribeInformationChangesAsync）：选择必须非空、每个字段只选一次、
 * 都是当前声明过的字段，否则整个请求 400；帧只含选中的设备和字段，订阅时一帧当前值，之后只在内容变化时发
 * （内容不含观测时间）。
 */
function selectedInformationFields(fields) {
  if (!Array.isArray(fields) || fields.length === 0) throw new Error("Select at least one information field.");
  const keys = fields.map(field => typeof field?.deviceIdentity === "string" && typeof field?.fieldName === "string"
    && field.deviceIdentity && field.fieldName ? JSON.stringify([field.deviceIdentity, field.fieldName]) : null);
  if (keys.includes(null) || new Set(keys).size !== keys.length) throw new Error("Select each declared field once.");
  const selected = new Set(keys);
  const project = (information) => (information?.devices ?? []).map(device => ({ ...device,
    fields: device.fields.filter(field => selected.has(JSON.stringify([device.deviceIdentity, field.definition.name]))) }))
    .filter(device => device.fields.length > 0);
  const declared = project(fixtureDeviceTopologyState(now()).informationFields)
    .reduce((count, device) => count + device.fields.length, 0);
  if (declared !== selected.size) throw new Error("Select a declared information field.");
  let current = null;
  return pushOnChange((publishedAt) => {
    const devices = project(fixtureDeviceTopologyState(publishedAt).informationFields);
    if (current === null || JSON.stringify(current.devices) !== JSON.stringify(devices)) current = { observedAt: publishedAt, devices };
    return current;
  });
}

function subscriptionPayloadFactory(url, fields) {
  const showcaseFactory = scenario.readmeShowcase ? showcase.showcaseSubscription(url.pathname) : null;
  if (showcaseFactory) return showcaseFactory;
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
      // 和后端一样：订阅时推当前值一帧，之后只在目录提交了新内容时推（推送周期上的发现探测和
      // publishMetricCatalogNow 都只在修订号变了时出帧）。
      return pushOnChange(() => metricCatalogPush());
    case "/api/metrics/gpu-specialized/subscribe":
      return (publishedAt) => ({
        version: 2,
        capturedAt: publishedAt,
        adapters: []
      });
    case "/api/resource-monitor/subscribe": {
      // 和后端一样，不支持的 measures（例如峰值提交量）和 residualDetails 在条和明细上都拒绝。
      if (!resourceMeasuresKnown(url)) return null;
      const residualMetricIds = residualDetailMetricIds(url);
      // 后端解析 residualDetails 时抛出，整个订阅通道回 400「One or more subscription queries are invalid.」。
      if (residualMetricIds === null) throw new Error("Resource residual details metric is unsupported.");
      if (url.searchParams.get("scope") === "details") return publishedAt => resourceProcessDetails(url, publishedAt, residualMetricIds);
      const processDetails = processDetailSelection(url, residualMetricIds);
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
      // 结构与健康：每个推送周期是一次采集，只有提交了变化才出帧（observeDeviceTopology）。
      return pushOnChange((publishedAt) => observeDeviceTopology(publishedAt));
    case "/api/device-topology/fields/subscribe":
      // 不选字段：整份信息字段（含 Sampled 字段），每次采样完成一帧，观测时间就是这次采样。
      if (fields === undefined) return (publishedAt) => ({ ...fixtureDeviceTopologyState(publishedAt).informationFields, observedAt: publishedAt });
      return selectedInformationFields(fields);
    case "/api/cpu/topology/subscribe":
      return (publishedAt) => scenario.cpuTopologyEmpty ? null : fixtureCpuTopology(publishedAt);
    case "/api/cpu/residency/subscribe":
      return (publishedAt) => scenario.cpuResidencyUnavailableReason
        ? { state: "unavailable", reasonCode: scenario.cpuResidencyUnavailableReason }
        : fixtureCpuResidency(publishedAt);
    case "/api/local-system/status/subscribe":
      return (publishedAt) => ({
        capturedAt: publishedAt,
        bootedAt: new Date(Date.parse(publishedAt) - 3_600_000).toISOString(),
        uptimeSeconds: 3_600
      });
    case "/api/optimization/smart/state/subscribe":
      return () => ({
        version: 3,
        lastRunAt: null,
        lastRestoreAt: null,
        coordinator: null,
        // 后端每条记录都带 sequence、state、recordedAt（没写进历史表时全为 null）；旅程只写有值的那几条。
        appliedPlacements: scenario.smartPlacements.map((placement) => ({
          ...placement,
          records: placement.records.map((record) => ({ sequence: null, state: null, recordedAt: null, ...record }))
        }))
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
    path: key.split("?")[0],
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

// 手动发帧的路径变了（或场景重置）：每个发布者按新的场景重新排期，退出手动的路径恢复按间隔发帧。
function rescheduleAllNdjsonPublishers() {
  for (const publisher of ndjsonPublishers.values()) {
    if (publisher.subscribers.size > 0) rescheduleNdjsonPublisher(publisher);
  }
}

function rescheduleNdjsonPublisher(publisher) {
  const intervalMs = Math.min(
    ...[...publisher.subscribers].map((subscriber) => subscriber.intervalMs));
  if (publisher.timer !== null) {
    clearTimeout(publisher.timer);
  }
  publisher.intervalMs = intervalMs;
  publisher.timer = null;
  if (scenario.manualPublishPaths.includes(publisher.path)) return;
  const current = Date.now();
  const nextBoundary = (Math.floor(current / intervalMs) + 1) * intervalMs;
  const rhythm = scenario.generatedData?.intervalsMs;
  const delayMs = rhythm?.length ? rhythm[(publisher.generatedFrame ?? 0) % rhythm.length] : Math.max(0, nextBoundary - current);
  publisher.generatedFrame = (publisher.generatedFrame ?? 0) + 1;
  publisher.timer = setTimeout(() => publishNdjsonBoundary(publisher), delayMs);
}

function publishNdjsonBoundary(publisher) {
  publisher.timer = null;
  if (scenario.generatedData && ["/api/metrics/subscribe", "/api/resource-facts/subscribe", "/api/resource-monitor/subscribe"].includes(publisher.path)) {
    scenario.generatedData.frameIndex = (scenario.generatedData.frameIndex ?? 0) + 1;
  }
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

function generatedReadings() {
  const data = scenario.generatedData;
  if (!data) return {};
  return data.metricUpdates?.length ? data.metricUpdates[(data.frameIndex ?? 0) % data.metricUpdates.length] : data.metrics;
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

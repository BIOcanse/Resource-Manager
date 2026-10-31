/*
 * README 截图用的虚构整机（模拟后端场景 readmeShowcase: true）。
 *
 * 只供 captureReadmeScreenshots.ts 用：旅程不打开这个场景，原有夹具一个值都不变。
 * 这里的每个名字和数字都是编的，不来自任何真实电脑：没有真实序列号、设备实例路径、MAC 地址、
 * 用户名或本机文件夹；处理器、显卡、内存、磁盘的型号、容量、频率和性能分也都是虚构值。
 * 形状照后端合同（与 mockFrontendServer.ts 里同名接口的夹具相同），模拟后端在场景打开时改用这里的值。
 */

import { mockBaseScoreResolution } from "./baseScoreFixture.ts";
import type { DeviceTopologyPort, DeviceTopologySnapshotState, DeviceTopologySnapshot, OptimizationReportTarget, OptimizationReportEvidence } from "../../src/types.ts";
import type { ControlCapability, ControlNumberRange, ControlActualValue } from "../../src/features/control/controlTypes.ts";

type ShowcaseSoftwareRow = [id: string, name: string, kind: string, cpu: number, memory: number,
  gpu0: number, gpu1: number, vram1: number, disk: number, network: number, processCount: number];
export type ShowcaseSoftwareCatalogRow = [id: string, name: string, kind: string, displayKind: string];
export type ShowcaseProcessRow = [processId: number, name: string, executablePath: null, value: number, systemPercent: number,
  softwarePercent: number, userName: string, architecture: string, attributionKind: string, baseScore: number, processStartKey: string];
export type ShowcaseBreakdownSoftwareRow = [catalogIndex: number, value: number, percent: number, processCount: number,
  baseScore: number, processRows: ShowcaseProcessRow[], shared?: number | null];
interface ShowcaseResourceBar {
  metricId: string; unit: string; scaleMode: string; totalValue: number; capacityValue: number;
  totalSystemPercent: number; software: ShowcaseBreakdownSoftwareRow[]; processFieldName?: string;
}
interface ShowcaseFactProcess { processId: number; processStartKey: string | null; name: string; executablePath: null;
  userName: string | null; architecture: string | null; attributionKind: string }
type ShowcaseDiskNode = [name: string, bytes: number, fileCount: number, children?: ShowcaseDiskNode[] | null];
type ShowcaseDiskRoot = [rootPath: string, children: ShowcaseDiskNode[]];
interface ShowcaseDiskOrder { nodeId: number; parentNodeId: number; depth: number; name: string; isDirectory: boolean; ownBytes: number; fileCount: number }
interface ShowcaseDiskTile { nodeId: number; parentNodeId: number; depth: number; name: string; isDirectory: boolean; sizeBytes: number; fileCount: number }

const GiB = 1024 ** 3;
const MiB = 1024 ** 2;

// 虚构整机：16 核 32 线程处理器（两个 CCD），48 GiB 内存，一块核显和一块 12 GiB 独显。
export const showcaseMachine = {
  model: "Atlas Tower",
  cpuName: "Demo 16-Core Processor",
  integratedGpuName: "Demo Integrated Graphics",
  discreteGpuName: "Demo Graphics X60",
  memoryCapacity: 48 * GiB,
  virtualMemoryCapacity: 72 * GiB,
  vramCapacity: 12 * GiB,
  uptimeSeconds: 2 * 86_400 + 5 * 3_600 + 42 * 60 + 17
};

/*
 * 虚构软件：[id, 名字, 类别, CPU %, 物理内存 GiB, GPU0 %, GPU1 %, GPU1 显存 GiB, 磁盘 B/s, 网络 bit/s, 进程数]。
 * 名字全是编的；类别决定资源条配色和软件页的分组。
 */
const software: ShowcaseSoftwareRow[] = [
  ["showcase.kestrel-rally", "Kestrel Rally", "game", 13.6, 5.42, 0, 58.4, 6.21, 2.6 * MiB, 3.4e6, 3],
  ["showcase.cedar-notes", "Cedar Notes", "other", 6.2, 2.86, 0, 3.4, 0.46, 0.4 * MiB, 0.9e6, 9],
  ["showcase.juniper-studio", "Juniper Studio", "high_performance", 8.9, 4.07, 0, 9.1, 1.18, 5.1 * MiB, 0, 6],
  ["showcase.harbor-browser", "Harbor Browser", "other", 3.8, 3.41, 2.9, 1.6, 0.58, 0.2 * MiB, 7.6e6, 22],
  ["showcase.resource-manager", "Resource Manager", "adapted", 1.3, 0.34, 0, 0, 0, 0.05 * MiB, 0, 3],
  ["showcase.glacier-backup", "Glacier Backup", "other", 2.1, 0.39, 0, 0, 0, 14.2 * MiB, 11.8e6, 2],
  ["showcase.basalt-chat", "Basalt Chat", "other", 1.1, 0.81, 0.4, 0.5, 0.09, 0.02 * MiB, 0.3e6, 5],
  ["showcase.coral-player", "Coral Player", "other", 1.7, 0.47, 4.6, 0, 0, 0.9 * MiB, 0, 2],
  ["showcase.delta-mail", "Delta Mail", "other", 0.6, 0.55, 0, 0, 0, 0.01 * MiB, 0.1e6, 4],
  ["showcase.fern-reader", "Fern Reader", "other", 0.3, 0.42, 0, 0, 0, 0, 0, 1],
  ["showcase.quartz-terminal", "Quartz Terminal", "other", 0.4, 0.21, 0.2, 0, 0, 0, 0, 2],
  ["showcase.lumen-vault", "Lumen Vault", "other", 0.1, 0.24, 0, 0, 0, 0, 0, 1],
  ["showcase.window-manager", "Desktop Window Manager", "windows_system", 2.2, 0.19, 3.8, 1.2, 0.31, 0, 0, 1],
  ["showcase.explorer", "Windows Explorer", "windows_system", 0.8, 0.31, 0.3, 0, 0, 0.03 * MiB, 0, 1],
  ["showcase.search-service", "Windows Search", "windows_service", 0.5, 0.12, 0, 0, 0, 0.3 * MiB, 0, 1]
];
const residual = { cpu: 3.7, memory: 4.1, virtualMemory: 6.3 };

// 软件基础分（后端按类别给的默认值，虚构）：GPU 调度模型把显卡上各软件的基础分加起来。
const baseScore = (row: ShowcaseSoftwareRow) => ({ game: 60, high_performance: 40, adapted: 20, other: 10 } as Partial<Record<string, number>>)[row[2]] ?? 5;
const column = { cpu: 3, memory: 4, gpu0: 5, gpu1: 6, vram1: 7, disk: 8, network: 9 } as const;
type ShowcaseResourceKey = keyof typeof column;
const softwareIds = software.map((row) => row[0]);
const processName = (row: ShowcaseSoftwareRow) => `${row[1] ? row[1].toLowerCase().replaceAll(" ", "-") : "search-host"}.exe`;

/** 指标目录的额外定义（后端 MetricCatalog 的形状）：默认卡片布局按这些 id 展开。 */
export function showcaseMetricDefinitions() {
  const gpuDevice = (index: number) => `showcase-gpu-${index}`;
  const define = (id: string, unit: string, extra: { deviceIdentity?: string; hasDedicatedMemoryMetrics?: boolean | null } = {}) => ({ id, unit, selectable: true, candidateSources: [], ...extra });
  const gpu = (index: number) => [
    ["usage", "%"], ["graphicsClock", "MHz"], ["graphicsClockPercent", "%"], ["power", "W"], ["temperature", "°C"],
    ["hotspotTemperature", "°C"], ["fanRpm", "RPM"], ["fanPercent", "%"], ["coreVoltage", "V"], ["current", "A"],
    ["vram", "B"], ["vramPercent", "%"], ["memoryClock", "MHz"]
  ].filter(([suffix]) => index === 1 || ["usage", "graphicsClock", "temperature", "power"].includes(suffix))
    .map(([suffix, unit]) => define(`gpu.${index}.${suffix}`, unit, { deviceIdentity: gpuDevice(index) }));
  return [
    define("cpu.temperature", "°C"), define("cpu.actualPower", "W"), define("cpu.fanRpm", "RPM"), define("cpu.fanPercent", "%"),
    define("cpu.coreVoltage", "V"), define("cpu.packageCurrent", "A"), define("memory.temperature", "°C"),
    define("virtualMemory.usage", "B"), define("virtualMemory.percent", "%"),
    define("disk.total.activePercent", "%"), define("disk.total.readBytesPerSec", "B/s"), define("disk.total.writeBytesPerSec", "B/s"),
    define("network.total.receiveBytesPerSec", "B/s"), define("network.total.sendBytesPerSec", "B/s"),
    ...gpu(0), ...gpu(1)
  ];
}

/** 指标目录里两块显卡的设备（字段目录的形状），只带显示名，卡片和选择器用它命名显卡。 */
export const showcaseFieldDevices = [
  { deviceIdentity: "showcase-gpu-0", device: { kind: "gpu_adapter", address: null, displayName: showcaseMachine.integratedGpuName, ordinal: 0 }, fields: [] },
  { deviceIdentity: "showcase-gpu-1", device: { kind: "gpu_adapter", address: null, displayName: showcaseMachine.discreteGpuName, ordinal: 1 }, fields: [] }
];

const sum = (key: ShowcaseResourceKey) => software.reduce((total, row) => total + row[column[key]], 0);

/** 指标当前值（后端 MetricSnapshot 的 items，状态 1 = 有值）。 */
export function showcaseMetricItems() {
  const cpu = sum("cpu") + residual.cpu;
  const memory = (sum("memory") + residual.memory) * GiB;
  const virtualMemory = memory + residual.virtualMemory * GiB;
  const vram = sum("vram1") * GiB;
  const item = (id: string, unit: string, numericValue: number, extra: { percent?: number; total?: number } = {}) => ({ id, unit, status: 1, numericValue, ...extra });
  return {
    "cpu.usage": item("cpu.usage", "%", cpu, { percent: cpu }),
    "cpu.frequency": item("cpu.frequency", "MHz", 4386),
    "cpu.frequencyPercent": item("cpu.frequencyPercent", "%", 83.4, { percent: 83.4 }),
    "cpu.temperature": item("cpu.temperature", "°C", 71.5),
    "cpu.actualPower": item("cpu.actualPower", "W", 64.8),
    "cpu.fanRpm": item("cpu.fanRpm", "RPM", 3270),
    "cpu.fanPercent": item("cpu.fanPercent", "%", 62, { percent: 62 }),
    "cpu.coreVoltage": item("cpu.coreVoltage", "V", 1.118),
    "cpu.packageCurrent": item("cpu.packageCurrent", "A", 47.6),
    "memory.usage": item("memory.usage", "B", memory, { percent: memory * 100 / showcaseMachine.memoryCapacity, total: showcaseMachine.memoryCapacity }),
    "memory.percent": item("memory.percent", "%", memory * 100 / showcaseMachine.memoryCapacity, { percent: memory * 100 / showcaseMachine.memoryCapacity }),
    "memory.temperature": item("memory.temperature", "°C", 52),
    "virtualMemory.usage": item("virtualMemory.usage", "B", virtualMemory,
      { percent: virtualMemory * 100 / showcaseMachine.virtualMemoryCapacity, total: showcaseMachine.virtualMemoryCapacity }),
    "virtualMemory.percent": item("virtualMemory.percent", "%", virtualMemory * 100 / showcaseMachine.virtualMemoryCapacity,
      { percent: virtualMemory * 100 / showcaseMachine.virtualMemoryCapacity }),
    "disk.total.activePercent": item("disk.total.activePercent", "%", 7.3, { percent: 7.3 }),
    "disk.total.readBytesPerSec": item("disk.total.readBytesPerSec", "B/s", 18.4 * MiB),
    "disk.total.writeBytesPerSec": item("disk.total.writeBytesPerSec", "B/s", 6.2 * MiB),
    "network.total.receiveBytesPerSec": item("network.total.receiveBytesPerSec", "B/s", 2.94 * MiB),
    "network.total.sendBytesPerSec": item("network.total.sendBytesPerSec", "B/s", 0.41 * MiB),
    "gpu.0.usage": item("gpu.0.usage", "%", sum("gpu0"), { percent: sum("gpu0") }),
    "gpu.0.graphicsClock": item("gpu.0.graphicsClock", "MHz", 800),
    "gpu.0.temperature": item("gpu.0.temperature", "°C", 58.5),
    "gpu.0.power": item("gpu.0.power", "W", 4.6),
    "gpu.1.usage": item("gpu.1.usage", "%", sum("gpu1"), { percent: sum("gpu1") }),
    "gpu.1.graphicsClock": item("gpu.1.graphicsClock", "MHz", 2385),
    "gpu.1.graphicsClockPercent": item("gpu.1.graphicsClockPercent", "%", 91.2, { percent: 91.2 }),
    "gpu.1.power": item("gpu.1.power", "W", 96.4),
    "gpu.1.temperature": item("gpu.1.temperature", "°C", 68),
    "gpu.1.hotspotTemperature": item("gpu.1.hotspotTemperature", "°C", 79.5),
    "gpu.1.fanRpm": item("gpu.1.fanRpm", "RPM", 2860),
    "gpu.1.fanPercent": item("gpu.1.fanPercent", "%", 57, { percent: 57 }),
    "gpu.1.coreVoltage": item("gpu.1.coreVoltage", "V", 0.955),
    "gpu.1.current": item("gpu.1.current", "A", 101.2),
    "gpu.1.vram": item("gpu.1.vram", "B", vram, { percent: vram * 100 / showcaseMachine.vramCapacity, total: showcaseMachine.vramCapacity }),
    "gpu.1.vramPercent": item("gpu.1.vramPercent", "%", vram * 100 / showcaseMachine.vramCapacity, { percent: vram * 100 / showcaseMachine.vramCapacity }),
    "gpu.1.memoryClock": item("gpu.1.memoryClock", "MHz", 10501)
  };
}

/*
 * 资源条（后端 ResourceBreakdownWireSnapshot，version 8）。ids 是界面请求的条；processDetails 里的软件带进程明细。
 * 物理内存和 CPU 条末尾是系统/驱动保留残差，和真机一样。
 */
export function showcaseResourceBreakdown(capturedAt: string, ids: readonly string[], processDetails: Pick<ReadonlySet<string>, "has">) {
  const residualIds = ["resource-residual:cpu.usage", "resource-residual:memory.usage", "resource-residual:virtualMemory.usage"];
  const catalog = [...software.map(([id, name, kind]): ShowcaseSoftwareCatalogRow => [id, name, kind, kind]),
    ...residualIds.map((id): ShowcaseSoftwareCatalogRow => [id, "", "windows_system", "windows_system"])];
  const wanted = new Set(ids.map((id) => id.toLowerCase()));
  const rows = (key: ShowcaseResourceKey, capacity: number, scale = 1, residualIndex: number | null = null, residualValue = 0): ShowcaseBreakdownSoftwareRow[] => [
    ...software.map((row, index): ShowcaseBreakdownSoftwareRow => [index, row[column[key]] * scale, row[column[key]] * scale * 100 / capacity, row[10], baseScore(row),
      processDetails.has(row[0].toLowerCase()) ? [processRow(row, row[column[key]] * scale, capacity)] : []]).filter((row) => row[1] > 0),
    ...(residualIndex === null ? [] : [[residualIndex, residualValue, residualValue * 100 / capacity, 0, 0, []] satisfies ShowcaseBreakdownSoftwareRow])
  ];
  // 按活动量缩放的条（磁盘、网络）没有固定容量：容量就是当前总量，各段的百分比相对于它。
  const bar = (metricId: string, unit: string, scaleMode: string, capacity: number | null, rows: ShowcaseBreakdownSoftwareRow[], extra: { processFieldName?: string } = {}): ShowcaseResourceBar => {
    const total = rows.reduce((value, row) => value + row[1], 0);
    const scale = capacity ?? total;
    const software = capacity === null ? rows.map((row) => [row[0], row[1], row[1] * 100 / scale, ...row.slice(3)] as ShowcaseBreakdownSoftwareRow) : rows;
    return { metricId, unit, scaleMode, totalValue: total, capacityValue: scale, totalSystemPercent: total * 100 / scale, software, ...extra };
  };
  const memoryCapacity = showcaseMachine.memoryCapacity;
  const bars = [
    bar("cpu.usage", "%", "capacity", 100, rows("cpu", 100, 1, software.length, residual.cpu)),
    bar("memory.usage", "B", "capacity", memoryCapacity, rows("memory", memoryCapacity, GiB, software.length + 1, residual.memory * GiB),
      { processFieldName: "process_all_private_working_set_actual" }),
    bar("virtualMemory.usage", "B", "capacity", showcaseMachine.virtualMemoryCapacity,
      rows("memory", showcaseMachine.virtualMemoryCapacity, GiB * 1.18, software.length + 2, (residual.memory + residual.virtualMemory) * GiB),
      { processFieldName: "process_all_private_commit_actual" }),
    bar("gpu.0.usage", "%", "capacity", 100, rows("gpu0", 100)),
    bar("gpu.1.usage", "%", "capacity", 100, rows("gpu1", 100)),
    bar("gpu.1.vram", "B", "capacity", showcaseMachine.vramCapacity, rows("vram1", showcaseMachine.vramCapacity, GiB)),
    bar("disk.io", "B/s", "active", null, rows("disk", 1)),
    bar("network.traffic", "B/s", "active", null, rows("network", 1, 1 / 8))
  ].filter((entry) => wanted.has(entry.metricId.toLowerCase()));
  const selected: ShowcaseSoftwareCatalogRow[] = [];
  const indexes = new Map<number, number>();
  for (const entry of bars) entry.software = entry.software.map((row) => {
    if (!indexes.has(row[0])) { indexes.set(row[0], selected.length); selected.push(catalog[row[0]]); }
    return [indexes.get(row[0]), ...row.slice(1)] as ShowcaseBreakdownSoftwareRow;
  });
  return { version: 8, capturedAt: bars.length ? capturedAt : null, softwareCatalog: selected, bars };
}

function processRow(row: ShowcaseSoftwareRow, value: number, capacity: number): ShowcaseProcessRow {
  const index = softwareIds.indexOf(row[0]);
  const processId = 6100 + index * 12;
  return [processId, processName(row), null, value, value * 100 / capacity, 100, "Demo User", "x64", "direct", 0,
    String(133900000000000000n + BigInt(processId))];
}

/*
 * 资源表（后端 ResourceFacts，version 2）：每个软件一个进程，系统/驱动保留单独一行。
 * items 是界面请求的列（cpu.usage、memory.usage、gpu.N.usage、gpu.N.vram、disk.io、network.traffic 与 process.*）。
 */
export function showcaseResourceFacts(items: ReadonlySet<string>, capturedAt: string) {
  const identities = software.map(([softwareId, name, kind]) => ({ softwareId, name, kind, attributionKind: "running" }));
  const processes: ShowcaseFactProcess[] = software.map((row, index) => ({ processId: 6100 + index * 12, processStartKey: String(133900000000000000n + BigInt(6100 + index * 12)),
    name: processName(row), executablePath: null, userName: "Demo User", architecture: "x64", attributionKind: "process" }));
  const residualId = "resource-residual:memory.usage";
  const metricShape: Record<string, [ShowcaseResourceKey, string, number | null, number]> = {
    "cpu.usage": ["cpu", "%", 100, 1],
    "memory.usage": ["memory", "B", showcaseMachine.memoryCapacity, GiB],
    "gpu.0.usage": ["gpu0", "%", 100, 1],
    "gpu.1.usage": ["gpu1", "%", 100, 1],
    "gpu.1.vram": ["vram1", "B", showcaseMachine.vramCapacity, GiB],
    "disk.io": ["disk", "B/s", null, 1],
    "network.traffic": ["network", "B/s", null, 1 / 8]
  };
  const attributions: { softwareId: string; processIndex: number; baseScore: number }[] = [];
  const metrics = [...items].filter((id) => Object.hasOwn(metricShape, id)).map((metricId) => {
    const [key, unit, capacity, scale] = metricShape[metricId];
    // 没有这一项用量的软件不出行（界面显示“--”），和后端一样。
    const activeTotal = software.reduce((value, row) => value + row[column[key]] * scale, 0);
    const base = capacity ?? activeTotal;
    const percent = (value: number) => value * 100 / base;
    const rows = software.flatMap((row, index) => {
      const value = row[column[key]] * scale;
      return value > 0 ? [{ softwareId: row[0], value, systemPercent: percent(value), processCount: row[10], baseScore: baseScore(row), sharedValue: null,
        processes: [{ processIndex: index, value, systemPercent: percent(value), softwarePercent: 100, baseScore: baseScore(row), sharedValue: null }] }] : [];
    });
    let total = rows.reduce((value, row) => value + row.value, 0) + (metricId === "cpu.usage" ? residual.cpu : 0);
    if (metricId === "memory.usage") {
      const value = residual.memory * GiB;
      total += value;
      if (!identities.some((identity) => identity.softwareId === residualId)) {
        identities.push({ softwareId: residualId, name: "", kind: "windows_system", attributionKind: "system-residual" });
        const processIndex = processes.push({ processId: -1, processStartKey: null, name: "", executablePath: null, userName: null,
          architecture: null, attributionKind: "system-residual" }) - 1;
        attributions.push({ softwareId: residualId, processIndex, baseScore: 0 });
      }
      const processIndex = processes.findIndex((process) => process.attributionKind === "system-residual");
      rows.push({ softwareId: residualId, value, systemPercent: percent(value), processCount: 0, baseScore: 0, sharedValue: null,
        processes: [{ processIndex, value, systemPercent: percent(value), softwarePercent: 100, baseScore: 0, sharedValue: null }] });
    }
    return { metricId, unit, totalMetricId: null, processFieldName: metricId === "memory.usage" ? "process_all_private_working_set_actual" : null,
      residualSource: null, totalValue: total, capacityValue: base, totalSystemPercent: total * 100 / base,
      processSumValue: null, sharedValue: null, software: rows };
  });
  if (items.has("process.attribution")) attributions.unshift(...software.map((row, index) => ({ softwareId: row[0], processIndex: index, baseScore: baseScore(row) })));
  return { version: 2, capturedAt, software: identities, processes, metrics, attributions };
}

/** 本机运行状态（开机时长）。 */
export function showcaseLocalSystemStatus(capturedAt: string) {
  return { capturedAt, bootedAt: new Date(Date.parse(capturedAt) - showcaseMachine.uptimeSeconds * 1000).toISOString(), uptimeSeconds: showcaseMachine.uptimeSeconds };
}

/*
 * 软件清单（后端 SoftwareRecord）：[id, 名字, 类别, 发行者, 版本, 能否卸载]。
 * 资源表里出现的软件在这里也有同一个 id；根目录和可执行文件路径都是按名字编出来的。
 */
const inventory: [id: string, name: string, kind: string, publisher: string, version: string, canUninstall: boolean][] = [
  ["showcase.kestrel-rally", "Kestrel Rally", "game", "Kestrel Games", "1.18.4", true],
  ["showcase.indigo-skies", "Indigo Skies", "game", "Lantern Works", "2.3.0", true],
  ["showcase.marble-tactics", "Marble Tactics", "game", "Marble Forge", "0.9.12", true],
  ["showcase.nova-drift", "Nova Drift Arena", "game", "Starlit Studio", "5.0.2", true],
  ["showcase.pine-valley", "Pine Valley Farm", "game", "Little Acorn", "1.4.7", true],
  ["showcase.juniper-studio", "Juniper Studio", "high_performance", "Juniper Media", "11.2.0", true],
  ["showcase.atlas-render", "Atlas Render", "high_performance", "Atlas Visual", "4.6.1", true],
  ["showcase.cobalt-compile", "Cobalt Build Tools", "high_performance", "Cobalt Systems", "17.9.3", true],
  ["showcase.resource-manager", "Resource Manager", "adapted", "Resource Manager", "1.0.33", false],
  ["showcase.cedar-notes", "Cedar Notes", "other", "Cedar Labs", "4.2.1", true],
  ["showcase.harbor-browser", "Harbor Browser", "other", "Harbor Web", "128.0.4", true],
  ["showcase.basalt-chat", "Basalt Chat", "other", "Basalt Messaging", "3.7.0", true],
  ["showcase.coral-player", "Coral Player", "other", "Coral Audio", "26.01", true],
  ["showcase.delta-mail", "Delta Mail", "other", "Delta Office", "2.14.5", true],
  ["showcase.fern-reader", "Fern Reader", "other", "Fern Docs", "6.0.3", true],
  ["showcase.glacier-backup", "Glacier Backup", "other", "Glacier Data", "8.1.0", true],
  ["showcase.lumen-vault", "Lumen Vault", "other", "Lumen Security", "1.9.2", true],
  ["showcase.quartz-terminal", "Quartz Terminal", "other", "Quartz Tools", "0.21.4", true],
  ["showcase.amber-photo", "Amber Photo", "other", "Amber Imaging", "12.0.6", true],
  ["showcase.birch-sync", "Birch Sync", "other", "Birch Cloud", "3.3.18", true],
  ["showcase.cinder-calendar", "Cinder Calendar", "other", "Cinder Apps", "5.1.0", true],
  ["showcase.dune-zip", "Dune Archiver", "other", "Dune Utilities", "24.07", true],
  ["showcase.ember-paint", "Ember Paint", "other", "Ember Creative", "7.4.2", true],
  ["showcase.fjord-vpn", "Fjord Connect", "other", "Fjord Networks", "2.8.11", true],
  ["showcase.garnet-pdf", "Garnet PDF", "other", "Garnet Docs", "9.2.3", true],
  ["showcase.hazel-clip", "Hazel Clipboard", "other", "Hazel Labs", "1.2.0", true],
  ["showcase.iris-capture", "Iris Capture", "other", "Iris Media", "30.1.2", true],
  ["showcase.jade-notes", "Jade Markdown", "other", "Jade Writing", "1.6.5", true],
  ["showcase.koi-music", "Koi Music", "other", "Koi Sound", "4.0.9", true],
  ["showcase.unknown-tool", "Opal Helper", "other", "", "", false],
  ["showcase.unknown-agent", "Slate Agent", "other", "", "", false]
];
const unconfirmed = new Set(["showcase.unknown-tool", "showcase.unknown-agent"]);

export function showcaseSoftware() {
  return inventory.map(([id, name, kind, publisher, version, canUninstall]) => {
    const folder = `C:\\Program Files\\${name}`;
    const executable = `${folder}\\${name.toLowerCase().replaceAll(" ", "-")}.exe`;
    return {
      id, name, kind, state: "installed",
      sourceFacts: [{ kind: kind === "adapted" ? "adapter" : "windows_registry" }],
      registrationFacts: kind === "adapted" ? { kind: "adapter", processCount: 3, serviceCount: 1, endpointState: "online" } : null,
      ...(publisher ? { publisher } : {}), ...(version ? { version } : {}),
      requiresRootPathConfirmation: unconfirmed.has(id),
      identityConfirmed: !unconfirmed.has(id),
      executablePaths: [executable],
      rootPaths: [folder],
      issues: [],
      operations: canUninstall
        ? { canUninstall: true, uninstallKind: "WindowsUninstaller", uninstallConstraint: "none" }
        : { canUninstall: false, uninstallKind: "None", uninstallConstraint: kind === "adapted" ? "self" : "missing_uninstall_entry" }
    };
  });
}

/** 模拟后端在场景打开时先问这里；返回 undefined 表示这个接口仍用原有夹具。 */
export function showcaseRestResponse(path: string, method: string, _url?: URL) {
  // 报告的读取和手动刷新返回同一份概览。
  if ((path === "/api/optimization/reports" && method === "GET") || (path === "/api/optimization/reports/refresh" && method === "POST")) {
    return showcaseOptimizationReports(new Date().toISOString());
  }
  if (method !== "GET") return undefined;
  if (path === "/api/gpu/performance-scores") return showcaseGpuPerformanceScores(new Date().toISOString());
  if (path === "/api/gpu/performance-overrides") return { scoresByGpuId: {}, updatedAt: new Date().toISOString(), storagePath: "showcase" };
  if (path === "/api/metrics/gpu-specialized") return showcaseGpuSpecialized(new Date().toISOString());
  if (path === "/api/cpu/topology") return showcaseCpuTopology(new Date().toISOString());
  if (path === "/api/cpu/residency") return showcaseCpuResidency(new Date().toISOString());
  if (path === "/api/cpu/baseline-ratio") return showcaseCpuBaseline();
  if (path === "/api/cpu/topology/exclusive-bindings") return { capturedAt: new Date().toISOString(), cpuName: showcaseMachine.cpuName, bindings: [] };
  if (path === "/api/disk-usage/volumes") return showcaseDiskVolumes;
  if (path === "/api/disk-usage/summary") return showcaseDiskSummary();
  if (path === "/api/control/objects") return showcaseControlObjects(new Date().toISOString());
  if (/^\/api\/control\/objects\/fan%3A0\/curve$/iu.test(path)) return showcaseFanCurve;
  if (path === "/api/control/state") return { desired: { objects: [] }, lastApply: { outcomes: [], appliedAt: new Date().toISOString() } };
  if (path === "/api/software") return showcaseSoftware();
  // 受保护的软件：没有。
  if (path === "/api/optimization/protection") return [];
  const placement = path.match(/^\/api\/gpu-placement\/software\/(showcase\.[a-z-]+)$/u);
  if (placement) return showcasePlacementPolicy(placement[1]);
  return undefined;
}

// 单个软件的调度策略（后端 GpuPlacementSoftwareView）：全部是默认值，没有进程历史。
function showcasePlacementPolicy(softwareId: string) {
  const row = inventory.find((item) => item[0] === softwareId);
  const softwareName = row?.[1] ?? softwareId;
  return {
    softwareId, softwareName,
    softwarePolicy: {
      softwareId, softwareName, allowedProviders: [], startupTargetGpu: "system_default_gpu", runtimeHotSwitchEnabled: false,
      preferredRuntimeSwitchMethod: "future_frame_takeover", baseScoreOverride: null, processOverrideAllowed: false,
      cpuSchedulingExcluded: false, memorySchedulingExcluded: false, gpuSchedulingExcluded: false,
      cpuMaximumOccupancyMode: "all_cores", cpuManualExclusivePositionIds: [], cpuManualLockedPositionIds: []
    },
    processPolicies: [],
    processHistory: { softwareId, softwareName, processes: [] },
    targetInventory: { state: "current", exactTargets: [] },
    processCapabilities: [],
    baseScore: mockBaseScoreResolution(softwareId, row?.[2] ?? null, null),
    processBaseScores: {}
  };
}

/*
 * 优化报告（后端 OptimizationOverview）：一条后台软件内存占用高、一条系统中断占用高、一条持续网络流量；
 * 状态与模拟后端默认夹具相同（规则全部可用、刚评估完）。
 */
export function showcaseOptimizationReports(capturedAt: string) {
  const minutesAgo = (minutes: number) => new Date(Date.parse(capturedAt) - minutes * 60_000).toISOString();
  const context = { contextKind: "normal", foregroundProcessId: null, foregroundProcessName: null, foregroundExecutablePath: null,
    foregroundSoftwareId: null, foregroundSoftwareName: null, confidence: "high", evidence: [] };
  const softwareTarget = (softwareId: string, extra: Partial<OptimizationReportTarget> = {}) => {
    const row = software.find((entry) => entry[0] === softwareId)!;
    const index = softwareIds.indexOf(softwareId);
    return { targetType: "software", targetKey: `software:${softwareId}`, name: row[1], softwareId, softwareName: row[1],
      softwareKind: row[2], displayKind: row[2], processNames: [processName(row).replace(/\.exe$/u, "")],
      processIds: [6100 + index * 12], driveLetter: null, ...extra };
  };
  const report = (id: string, type: string, severity: string, firstMinutes: number, target: OptimizationReportTarget, evidence: OptimizationReportEvidence) => ({
    id, type, state: "active", trusted: false, severity, confidence: "high",
    createdAt: minutesAgo(firstMinutes), updatedAt: capturedAt, firstObservedAt: minutesAgo(firstMinutes), lastObservedAt: capturedAt,
    title: "", message: "", target, suggestedActions: [], context,
    evidence: { process: null, details: [], ...evidence }
  });
  const reports = [
    report("showcase-report-memory", "background_high_usage", "warning", 46, softwareTarget("showcase.harbor-browser"),
      { resourceKind: "memory", currentValue: 3.41 * GiB, averageValue: 3.18 * GiB, peakValue: 4.02 * GiB, valueUnit: "B",
        activeSampleCount: 241, sampleCount: 276, durationSeconds: 2760 }),
    report("showcase-report-interrupts", "system_interrupt_pressure", "warning", 18,
      { targetType: "system", targetKey: "system:interrupts", name: "", softwareId: null, softwareName: null, softwareKind: null,
        displayKind: null, processNames: [], processIds: [], driveLetter: null },
      { resourceKind: "system_interrupt", currentValue: 6.8, averageValue: 5.9, peakValue: 9.4, valueUnit: "%",
        activeSampleCount: 92, sampleCount: 108, durationSeconds: 1080 }),
    report("showcase-report-network", "rolling_network_traffic", "info", 95, softwareTarget("showcase.glacier-backup"),
      { resourceKind: "network", currentValue: 1.41 * GiB, averageValue: 1.41 * GiB, peakValue: 1.41 * GiB, valueUnit: "B",
        activeSampleCount: 570, sampleCount: 570, durationSeconds: 5700 })
  ];
  return {
    capturedAt, reports, trustedTargets: [], protectedTargets: [],
    status: { lastEvaluationAt: capturedAt, lastObservedAt: capturedAt, evaluationState: "completed", evaluationFailureReason: null,
      unavailableRules: [], configuredRuleCount: 7, availableRuleCount: 7, activeReportCount: reports.length, unrecordedReportCount: 0, trustedCount: 0,
      protectedCount: 0, sampleIntervalSeconds: 10 }
  };
}

/*
 * 控制面（后端 ControlObjectCatalog）：核显、独显、处理器和一个风扇。
 * 只读的温度墙作为不可调的能力列出（界面显示读数）；可调项都在固件管理下，没有用户设定。
 */
const controlReadOnly = { supported: false, unavailableReason: { code: "nvidia.M63" }, unavailableKind: "platform" } satisfies Partial<ControlCapability>;
const capability = (id: string, valueKind: ControlCapability["valueKind"], channel: string, range: ControlNumberRange | null = null, extra: Partial<ControlCapability> = {}): ControlCapability => ({
  id, valueKind, supported: true, unavailableReason: null, unavailableKind: null, requiredComponentId: null,
  range, channel, requiredAccessLevel: "normal", curvePointLimit: null, ...extra
});
const numberRange = (minimum: number, maximum: number, step: number, unit: string, defaultValue: number | null): ControlNumberRange => ({ minimum, maximum, step, unit, defaultValue });

export function showcaseControlObjects(readAt: string) {
  return {
    readAt,
    objects: [
      { id: "gpu:0", kind: "gpu", reportedName: showcaseMachine.integratedGpuName, adapterIndex: 0, platform: { operatingSystem: "windows", vendor: "amd" },
        gpuAttachment: "integrated", terms: [], detail: null, isControllable: true,
        capabilities: [capability("gpu.curve-optimizer", "number", "amd-smu", numberRange(-30, 30, 1, "steps", 0))] },
      { id: "gpu:1", kind: "gpu", reportedName: showcaseMachine.discreteGpuName, adapterIndex: 1, platform: { operatingSystem: "windows", vendor: "nvidia" },
        gpuAttachment: "discrete", terms: [], detail: null, isControllable: true,
        capabilities: [
          capability("gpu.temperature-limit", "number", "nvml", numberRange(84, 84, 1, "°C", 84), controlReadOnly),
          capability("gpu.slowdown-temperature", "number", "nvml", numberRange(97, 97, 1, "°C", 97), controlReadOnly),
          capability("gpu.shutdown-temperature", "number", "nvml", numberRange(100, 100, 1, "°C", 100), controlReadOnly),
          capability("gpu.core-clock-offset", "number", "nvapi", numberRange(-200, 300, 5, "MHz", 0)),
          capability("gpu.memory-clock-offset", "number", "nvapi", numberRange(-500, 1000, 25, "MHz", 0)),
          capability("gpu.power-limit", "number", "nvml", numberRange(90, 150, 1, "W", 125)),
          capability("gpu.dynamic-boost-enabled", "toggle", "nvapi")
        ] },
      { id: "cpu:0", kind: "cpu", reportedName: showcaseMachine.cpuName, platform: { operatingSystem: "windows", vendor: "amd" },
        gpuAttachment: null, terms: [], detail: null, isControllable: true,
        capabilities: [
          capability("cpu.power-limit", "number", "amd-smu", numberRange(45, 142, 1, "W", 120)),
          capability("cpu.temperature-limit", "number", "amd-smu", numberRange(70, 95, 1, "°C", 95)),
          capability("cpu.curve-optimizer", "number", "amd-smu", numberRange(-30, 30, 1, "steps", 0))
        ] },
      { id: "fan:0", kind: "fan", reportedName: null, ordinal: 0, platform: { operatingSystem: "windows", vendor: "oem" },
        gpuAttachment: null, terms: [], detail: null, isControllable: true,
        firmwareCurveKind: "xy", softwareTakeoverCoversAllFans: false,
        fanManagementModes: [
          { id: "firmware-management", available: true, unavailableKind: null, unavailableReason: null, items: [] },
          { id: "firmware-write", available: true, unavailableKind: null, unavailableReason: null, items: [{ capabilityId: "fan.curve", curveExecution: "firmware" }] },
          { id: "software-management", available: true, unavailableKind: null, unavailableReason: null,
            items: [{ capabilityId: "fan.curve", curveExecution: "software" }, { capabilityId: "fan.duty", curveExecution: null }] }
        ],
        capabilities: [
          capability("fan.curve", "curve", "fan-core", null, { curvePointLimit: 11 }),
          capability("fan.duty", "number", "fan-core", numberRange(0, 100, 1, "%", null))
        ] }
    ]
  };
}

/** 风扇当前的固件曲线：温度 °C → 转速 %。 */
export const showcaseFanCurve = [[0, 20], [40, 24], [50, 31], [55, 38], [60, 46], [65, 55], [70, 64], [75, 75], [80, 86], [85, 95], [90, 100]]
  .map(([temperatureCelsius, percent]) => ({ temperatureCelsius, percent }));

/** 控制面读回的当前值（control.actual.v1 的 values）。 */
export function showcaseControlActual(readAt: string) {
  const value = (objectId: string, capabilityId: string, extra: Omit<ControlActualValue, "objectId" | "capabilityId">): ControlActualValue => ({ objectId, capabilityId, ...extra });
  return { readAt, values: [
    value("gpu:0", "gpu.curve-optimizer", { number: 0, unit: "steps" }),
    value("gpu:1", "gpu.temperature-limit", { number: 84, unit: "°C" }),
    value("gpu:1", "gpu.slowdown-temperature", { number: 97, unit: "°C" }),
    value("gpu:1", "gpu.shutdown-temperature", { number: 100, unit: "°C" }),
    value("gpu:1", "gpu.core-clock-offset", { number: 0, unit: "MHz" }),
    value("gpu:1", "gpu.memory-clock-offset", { number: 0, unit: "MHz" }),
    value("gpu:1", "gpu.power-limit", { number: 125, unit: "W" }),
    value("gpu:1", "gpu.dynamic-boost-enabled", { toggle: true }),
    value("cpu:0", "cpu.power-limit", { number: 120, unit: "W" }),
    value("cpu:0", "cpu.temperature-limit", { number: 95, unit: "°C" }),
    value("cpu:0", "cpu.curve-optimizer", { number: 0, unit: "steps" }),
    value("fan:0", "fan.duty", { number: 46, unit: "%" })
  ] };
}

/** 订阅接口：返回发帧函数（参数是发帧时间），null 表示这个接口仍用原有夹具。 */
export function showcaseSubscription(pathname: string) {
  switch (pathname) {
    case "/api/local-system/status/subscribe": return (publishedAt: string) => showcaseLocalSystemStatus(publishedAt);
    case "/api/control/actual/subscribe": return (publishedAt: string) => showcaseControlActual(publishedAt);
    case "/api/cpu/topology/subscribe": return (publishedAt: string) => showcaseCpuTopology(publishedAt);
    case "/api/cpu/residency/subscribe": return (publishedAt: string) => showcaseCpuResidency(publishedAt);
    case "/api/metrics/gpu-specialized/subscribe": return (publishedAt: string) => showcaseGpuSpecialized(publishedAt);
    default: return null;
  }
}

/*
 * 磁盘占用：三个卷，上一次全盘快速扫描读了 C: 和 D: 两个根。
 * 目录树按 [名字, 字节, 文件数, 子项] 写，文件的子项为 null；编码成二进制布局由模拟后端的 createDiskUsageLayout 做。
 */
const GB = 1e9;
export const showcaseDiskVolumes = [
  { volumeId: "C:", label: "System", fileSystem: "NTFS", volumeKind: "physical", totalBytes: 1_000_202_039_296, freeBytes: 389_118_582_784, isReady: true, supportsMasterFileTable: true },
  { volumeId: "D:", label: "Data", fileSystem: "NTFS", volumeKind: "physical", totalBytes: 2_000_396_746_752, freeBytes: 1_164_327_157_760, isReady: true, supportsMasterFileTable: true },
  { volumeId: "E:", label: "Portable", fileSystem: "exFAT", volumeKind: "external", totalBytes: 256_052_966_400, freeBytes: 201_349_201_920, isReady: true, supportsMasterFileTable: false }
];
const diskTrees: ShowcaseDiskRoot[] = [
  ["C:\\", [
    ["Users", 0, 0, [["Demo", 0, 0, [["Videos", 81.4 * GB, 412], ["Documents", 42.7 * GB, 18_204], ["Pictures", 36.1 * GB, 9_871], ["Downloads", 27.9 * GB, 1_342],
      ["AppData", 19.3 * GB, 96_408]]], ["Public", 7.6 * GB, 214]]],
    ["Games", 0, 0, [["Kestrel Rally", 88.2 * GB, 6_120], ["Indigo Skies", 51.4 * GB, 3_904], ["Marble Tactics", 22.3 * GB, 2_288], ["Nova Drift Arena", 11.9 * GB, 1_017]]],
    ["Program Files", 0, 0, [["Juniper Studio", 31.2 * GB, 14_206], ["Cobalt Build Tools", 27.4 * GB, 88_513], ["Atlas Render", 24.1 * GB, 9_632],
      ["Common Files", 6.2 * GB, 4_115], ["Harbor Browser", 1.3 * GB, 612], ["Cedar Notes", 0.9 * GB, 344]]],
    ["Windows", 0, 0, [["WinSxS", 14.6 * GB, 61_027], ["System32", 9.8 * GB, 18_440], ["Installer", 6.1 * GB, 1_288], ["SysWOW64", 3.2 * GB, 7_016]]],
    ["Program Files (x86)", 21.7 * GB, 23_512],
    ["ProgramData", 18.6 * GB, 41_870],
    ["pagefile.sys", 19.2 * GB, 1, null],
    ["hiberfil.sys", 12.8 * GB, 1, null],
    ["Recovery", 1.1 * GB, 12]
  ]],
  ["D:\\", [
    ["Media", 0, 0, [["Footage", 214.6 * GB, 1_870], ["Music", 61.3 * GB, 12_604], ["Photo Library", 97.8 * GB, 31_455]]],
    ["Projects", 0, 0, [["Studio Renders", 118.2 * GB, 4_207], ["Source", 46.9 * GB, 402_118], ["Assets", 72.4 * GB, 25_631]]],
    ["Backups", 0, 0, [["Laptop Image", 128.7 * GB, 14], ["Phone", 38.5 * GB, 9_982]]],
    ["Game Library", 52.6 * GB, 3_311],
    ["Installers", 5.1 * GB, 77]
  ]]
];

/** 一个根的完整目录树（节点 id 在两个根之间不重复）；没有这个根时为 null。 */
export function showcaseDiskTree(rootIndex: number) {
  const entry = diskTrees[rootIndex];
  if (!entry) return null;
  const [rootPath, children] = entry;
  const tiles: ShowcaseDiskTile[] = [];
  let nextId = rootIndex * 1000;
  const rootNodeId = nextId++;
  // 显式栈：每项是 [节点描述, 父节点 id, 深度]；目录大小是子项之和加上它自己写的字节数。
  const pending = children.map((child): [ShowcaseDiskNode, number, number] => [child, rootNodeId, 1]);
  const sizes = new Map<number, number>();
  const order: ShowcaseDiskOrder[] = [];
  while (pending.length > 0) {
    const [[name, bytes, fileCount, grandchildren], parentNodeId, depth] = pending.shift()!;
    const nodeId = nextId++;
    const isDirectory = grandchildren !== null;
    order.push({ nodeId, parentNodeId, depth, name, isDirectory, ownBytes: bytes, fileCount });
    for (const child of grandchildren ?? []) pending.push([child, nodeId, depth + 1]);
  }
  // 子项总在父项之后入列，倒序累加即可得到每个目录的大小和文件数。
  const counts = new Map<number, number>();
  for (let index = order.length - 1; index >= 0; index -= 1) {
    const node = order[index];
    const size = node.ownBytes + (sizes.get(node.nodeId) ?? 0);
    const count = node.fileCount + (counts.get(node.nodeId) ?? 0);
    sizes.set(node.nodeId, size);
    counts.set(node.nodeId, count);
    sizes.set(node.parentNodeId, (sizes.get(node.parentNodeId) ?? 0) + size);
    counts.set(node.parentNodeId, (counts.get(node.parentNodeId) ?? 0) + count);
  }
  // 根下面还有一些零散的小文件，不单独列出。
  const rootSize = sizes.get(rootNodeId)! + 3.4 * GB;
  tiles.push({ nodeId: rootNodeId, parentNodeId: -1, depth: 0, sizeBytes: rootSize, name: "", isDirectory: true, fileCount: counts.get(rootNodeId)! + 1_906 });
  for (const node of order) tiles.push({ nodeId: node.nodeId, parentNodeId: node.parentNodeId, depth: node.depth,
    sizeBytes: sizes.get(node.nodeId)!, name: node.name, isDirectory: node.isDirectory, fileCount: counts.get(node.nodeId)! });
  return { rootPath, rootNodeId, rootSize, tiles };
}

export function showcaseDiskSummary() {
  const trees = diskTrees.map((_, index) => showcaseDiskTree(index)!);
  const totalBytes = trees.reduce((total, tree) => total + tree.rootSize, 0);
  return { scope: "allVolumes", mode: "fast", target: "", roots: trees.map((tree) => tree.rootPath), scanKind: "masterFileTable", skipped: [],
    completedAt: new Date(Date.now() - 26 * 60_000).toISOString(), durationSeconds: 7.4, totalBytes, scannedBytes: totalBytes,
    fileCount: 1_284_311, directoryCount: 214_906, skippedCount: 0 };
}

/*
 * GPU 性能分（后端 GpuPerformanceScoreSnapshot）：分数是编的，不对应任何真实型号；两块都当作内置表里有的型号。
 */
export function showcaseGpuPerformanceScores(capturedAt: string) {
  const gpu = (index: number, name: string, score: number, isIntegrated: boolean) => ({
    gpuId: `gpu:${index}`, index, name, defaultPerformanceScore: score, performanceScore: score, rasterPerformanceScore: score,
    hasPerformanceOverride: false, isIntegrated, source: "3dmark:steel-nomad-dx12:2026-09-13", matchedPreset: name, isPresetMatch: true,
    hasDedicatedMemoryMetrics: !isIntegrated
  });
  return { capturedAt, storagePath: "showcase", gpus: [
    gpu(0, showcaseMachine.integratedGpuName, 640, true),
    gpu(1, showcaseMachine.discreteGpuName, 4870, false)
  ] };
}

/** 显卡专用单元占用（后端 GpuSpecializedTelemetrySnapshot，version 3）。 */
export function showcaseGpuSpecialized(capturedAt: string) {
  const counter = (counterId: string, value: number, providerId: string) => ({ counterId, counterClass: 1, value, unit: "%", providerId, isIntrusive: false, engineName: null });
  return { version: 3, capturedAt, adapters: [
    { adapterIndex: 0, adapterName: showcaseMachine.integratedGpuName, vendorId: "0x1002", architecture: "rdna", counters: [counter("gpu.0.amd.rdna.usage", 11.6, "adlx")] },
    { adapterIndex: 1, adapterName: showcaseMachine.discreteGpuName, vendorId: "0x10de", architecture: "ada", counters: [
      counter("gpu.1.nvidia.rt.usage", 18.4, "nvapi"), counter("gpu.1.nvidia.cuda.usage", 42.7, "nvapi")] }
  ] };
}

/*
 * CPU 拓扑（后端 CpuTopologySnapshot）：两个 CCD，各 8 核 16 线程；占用、性能分和缓存大小都是编的。
 */
const coresPerCcd = 8;
const ccdCount = 2;
const coreUsage = [71, 64, 58, 52, 47, 39, 33, 28, 22, 18, 15, 12, 9, 7, 5, 3];
export function showcaseCpuTopology(capturedAt: string) {
  const coreCount = coresPerCcd * ccdCount;
  const cores = Array.from({ length: coreCount }, (_, index) => index);
  const ccdOf = (core: number) => Math.floor(core / coresPerCcd);
  const threads = (core: number) => [core * 2, core * 2 + 1];
  return {
    capturedAt,
    cpuName: showcaseMachine.cpuName,
    specification: { name: showcaseMachine.cpuName, vendor: "Demo", family: "Demo", physicalCoreCount: coreCount, logicalProcessorCount: coreCount * 2,
      maxClockSpeedMhz: 5200, currentClockSpeedMhz: 4386, l2CacheSizeKb: 16_384, l3CacheSizeKb: 65_536, source: "showcase" },
    topologySource: "showcase", usageSource: "showcase", affinityTargetKind: "logical_processor_mask",
    visualLayoutKind: "grid", visualLayoutSource: "showcase",
    physicalCoreCount: coreCount, logicalProcessorCount: coreCount * 2, ccdCount, simultaneousMultithreading: true,
    ccds: Array.from({ length: ccdCount }, (_, ccd) => {
      const ccdCores = cores.filter((core) => ccdOf(core) === ccd);
      return { id: `ccd:${ccd}`, index: ccd, usagePercent: ccdCores.reduce((total, core) => total + coreUsage[core], 0) / ccdCores.length,
        physicalCoreIndexes: ccdCores, logicalProcessorIds: ccdCores.flatMap(threads), source: "showcase" };
    }),
    physicalCores: cores.map((index) => ({
      id: `core:${index}`, index, ccdId: `ccd:${ccdOf(index)}`, efficiencyClass: 0,
      performanceScore: ccdOf(index) === 0 ? 100 - (index % 4) : 92 - (index % 3), usagePercent: coreUsage[index],
      logicalProcessorIds: threads(index),
      cacheLevels: [
        { level: 1, sizeKb: 80, logicalProcessorIds: threads(index), scope: "private" },
        { level: 2, sizeKb: 1024, logicalProcessorIds: threads(index), scope: "private" },
        { level: 3, sizeKb: 32_768, logicalProcessorIds: cores.filter((core) => ccdOf(core) === ccdOf(index)).flatMap(threads), scope: "shared" }
      ]
    })),
    logicalProcessors: cores.flatMap((core) => threads(core).map((id, thread) => ({
      id, processorGroup: 0, groupRelativeIndex: id, physicalCoreId: `core:${core}`, ccdId: `ccd:${ccdOf(core)}`,
      performanceScore: ccdOf(core) === 0 ? 100 - (core % 4) : 92 - (core % 3),
      usagePercent: Math.max(0, coreUsage[core] - thread * 9), affinitySelectable: true
    })))
  };
}

/** 最近 5 秒各进程在核心上的执行时间（后端 CpuResidencySnapshot）：前几个占 CPU 的虚构软件。 */
export function showcaseCpuResidency(capturedAt: string) {
  const measuredFrom = new Date(Date.parse(capturedAt) - 5_000).toISOString();
  const placements: [softwareId: string, slices: [core: number, milliseconds: number][]][] = [
    ["showcase.kestrel-rally", [[0, 1900], [1, 1500], [2, 1100], [3, 900]]],
    ["showcase.juniper-studio", [[4, 1300], [5, 1000], [8, 700], [9, 600]]],
    ["showcase.cedar-notes", [[6, 800], [10, 500]]],
    ["showcase.harbor-browser", [[7, 600], [11, 400], [12, 200]]],
    ["showcase.glacier-backup", [[13, 300], [14, 150]]]
  ];
  return {
    capturedAt, window: "00:00:05", sessionGeneration: 3, measuredFrom, measuredThrough: capturedAt,
    processes: placements.map(([softwareId, slices], order) => {
      const row = software.find((entry) => entry[0] === softwareId)!;
      const processId = 6100 + softwareIds.indexOf(softwareId) * 12;
      const total = slices.reduce((sum, [, ms]) => sum + ms, 0);
      const share = (ms: number) => ms * 100 / total;
      const ccds = [...new Set(slices.map(([core]) => Math.floor(core / coresPerCcd)))];
      return {
        processInstanceId: `3:${order + 1}`, processId, processStartKey: String(133900000000000000n + BigInt(processId)), processName: processName(row),
        executionTimeMilliseconds: total, switchCount: slices.length * 40, threadCount: slices.length,
        primaryCcdId: `ccd:${Math.floor(slices[0][0] / coresPerCcd)}`, primaryPhysicalCoreId: `core:${slices[0][0]}`, primaryLogicalProcessorId: slices[0][0] * 2,
        ccds: ccds.map((ccd) => {
          const ms = slices.filter(([core]) => Math.floor(core / coresPerCcd) === ccd).reduce((sum, [, value]) => sum + value, 0);
          return { ccdId: `ccd:${ccd}`, executionTimeMilliseconds: ms, switchCount: 40, sharePercent: share(ms) };
        }),
        physicalCores: slices.map(([core, ms]) => ({ physicalCoreId: `core:${core}`, ccdId: `ccd:${Math.floor(core / coresPerCcd)}`,
          executionTimeMilliseconds: ms, switchCount: 40, sharePercent: share(ms) })),
        // 每个核心上的时间按 6:4 落在它的两个逻辑处理器上。
        logicalProcessors: slices.flatMap(([core, ms]) => [[core * 2, ms * 0.6], [core * 2 + 1, ms * 0.4]].map(([logicalProcessorId, part]) => ({
          logicalProcessorId, physicalCoreId: `core:${core}`, ccdId: `ccd:${Math.floor(core / coresPerCcd)}`,
          executionTimeMilliseconds: part, switchCount: 20, sharePercent: share(part) }))),
        threads: slices.map(([core, ms], thread) => ({ threadInstanceId: `3:${order + 1}:${thread}`, threadId: processId + thread + 1,
          executionTimeMilliseconds: ms, switchCount: 40, primaryCcdId: `ccd:${Math.floor(core / coresPerCcd)}`, primaryPhysicalCoreId: `core:${core}`,
          primaryLogicalProcessorId: core * 2, physicalCoreIds: [`core:${core}`] }))
      };
    })
  };
}

/** CPU 基线比例（后端 CpuBaselineRatioSettings）：内置数据集里的双 CCD 默认值。 */
export function showcaseCpuBaseline() {
  return { cpuName: showcaseMachine.cpuName, defaultRatio: 0.5, overrideRatio: null, defaultSource: "amd:symmetric-dual-ccd",
    matchedModel: showcaseMachine.cpuName, datasetVersion: "2026-09-02", ratio: 0.5, multiplier: 2, defaultMultiplier: 2 };
}

/*
 * 设备拓扑（后端 DeviceTopologyState 4.0.0）：一台虚构台式机，机身上四个 USB 口和一个 HDMI 口；
 * 一个 USB-C 扩展坞下面接了键盘、接收器和移动固态硬盘。设备 ID、集线器路径、厂商与产品编号都是编的，没有序列号。
 */
type ShowcaseUsbKind = "keyboard" | "mouse" | "audio" | "hub" | "storage" | "camera";
const usbInterfaces: Record<ShowcaseUsbKind, [number, number, number][]> = {
  keyboard: [[3, 1, 1]], mouse: [[3, 1, 2]], audio: [[1, 1, 0], [1, 2, 0], [3, 0, 0]], hub: [[9, 0, 0]],
  storage: [[8, 6, 98]], camera: [[14, 1, 0], [14, 2, 0], [1, 1, 0]]
};
const usbClassNames = { keyboard: "HID keyboard", mouse: "HID mouse", audio: "Audio / HID", hub: "USB hub", storage: "Mass storage (UAS)",
  camera: "Video / Audio" };
const deviceClassCodes = { keyboard: 0, mouse: 0, audio: 239, hub: 9, storage: 0, camera: 239 };
function showcasePort(id: string, extra: Partial<DeviceTopologyPort>): DeviceTopologyPort {
  return {
    id, isPhysicalConnector: true, displayName: id, connectorKind: "usb-a", busKind: "usb",
    transport: { code: "usb", reason: null, ruleId: "usb-hub-port", evidence: [] },
    hardwareKind: "USB Device", protocol: "USB 3.x", speed: null, physicalMaximumSpeed: null,
    deviceId: `SHOWCASE\\${id.toUpperCase()}`, informationIdentity: null, pnpClass: null, manufacturer: null, service: null, status: "OK",
    confidence: { domain: 5, code: 24, args: [] }, source: { domain: 5, code: 35, args: [] },
    upstreamDeviceId: null, upstreamDisplayName: null, topologyPath: { domain: 5, code: 64, args: [`root/${id}`] },
    nativeParentDeviceId: null, nativeParentDisplayName: null, locationInfo: null, locationPaths: [], classGuid: null,
    display: null, network: null, idResolution: null, advancedInterconnect: null, usb: null, hardwareIds: [], compatibleIds: [],
    devNodeStatus: null, problemCode: null, hid: null, camera: null, smartDevice: null, storage: null,
    ...extra
  };
}
interface ShowcaseUsbPortOptions {
  hub: string; portNumber: number; connector?: string; kind?: ShowcaseUsbKind | null; product?: string | null;
  maker?: string | null; speed?: string; maximum?: string; downstreamHub?: string | null;
}
function showcaseUsbPort(id: string, { hub, portNumber, connector = "usb-a", kind = null, product = null, maker = null, speed = "5 Gbps",
  maximum = "10 Gbps", downstreamHub = null }: ShowcaseUsbPortOptions) {
  const connected = kind !== null;
  const usb2 = speed === "480 Mbps" || speed === "12 Mbps";
  const port = showcasePort(id, {
    connectorKind: connector, displayName: product ?? "USB", manufacturer: maker,
    transport: { code: "usb", reason: null, ruleId: "usb-hub-port", evidence: [{ source: "usb-hub-ioctl", deviceId: hub, depth: 0,
      enumeratorName: null, busTypeGuid: null, service: null, parentDeviceId: null }] },
    protocol: usb2 ? "USB 2.0" : "USB 3.x", speed: connected ? speed : null, physicalMaximumSpeed: maximum,
    usb: {
      hubDevicePath: hub, portNumber, deviceConnected: connected,
      connectionStatus: { domain: 5, code: connected ? 47 : 46, args: [] }, negotiatedSpeed: connected ? speed : null,
      deviceAddress: connected ? portNumber + 1 : 0, vendorId: connected ? "F00D" : null, productId: connected ? `0${portNumber}0${id.length % 10}` : null,
      deviceIsHub: kind === "hub", supportedProtocols: null, operatingAtSuperSpeedOrHigher: null, superSpeedCapableOrHigher: null,
      operatingAtSuperSpeedPlusOrHigher: null, superSpeedPlusCapableOrHigher: null, portIsUserConnectable: true,
      portIsDebugCapable: null, portHasMultipleCompanions: null, portConnectorIsTypeC: connector === "usb-c", companionPorts: [],
      deviceSpecification: connected ? (usb2 ? "USB 2.0" : "USB 3.2") : "", deviceRevision: connected ? "1.0" : "",
      deviceClass: connected ? usbClassNames[kind] : "", interfaceProtocols: [],
      deviceClassCode: connected ? deviceClassCodes[kind] : null,
      deviceSubClassCode: connected ? (deviceClassCodes[kind] === 239 ? 2 : 0) : null,
      deviceProtocolCode: connected ? (deviceClassCodes[kind] === 239 ? 1 : 0) : null,
      interfaces: connected ? usbInterfaces[kind].map(([classCode, subClassCode, protocolCode], interfaceNumber) =>
        ({ interfaceNumber, alternateSetting: 0, classCode, subClassCode, protocolCode })) : null,
      manufacturerName: maker, productName: product, serialNumber: null, downstreamHubDevicePath: downstreamHub, endpoints: null
    }
  });
  if (kind === "keyboard" || kind === "mouse") port.hid = { hidType: kind, hidSpecification: "HID 1.11",
    inputPollingIntervalMicroseconds: 1000, theoreticalReportRateHz: 1000, reportedDpi: null, reportedScanRateHz: null,
    standardCapabilitySource: { domain: 5, code: 60, args: [] }, vendorCapabilitySource: null };
  return port;
}

export function showcaseDeviceTopology(capturedAt: string, revision: number): DeviceTopologySnapshotState & { snapshot: DeviceTopologySnapshot } {
  const root = "showcase-root-hub";
  const dock = "showcase-dock-hub";
  const ports = [
    showcaseUsbPort("showcase-usb-c-1", { hub: root, portNumber: 1, connector: "usb-c", kind: "hub", product: "Demo USB-C Dock", maker: "Demo Devices",
      speed: "10 Gbps", maximum: "10 Gbps", downstreamHub: dock }),
    showcaseUsbPort("showcase-dock-1", { hub: dock, portNumber: 1, kind: "keyboard", product: "Demo Mechanical Keyboard", maker: "Demo Devices",
      speed: "12 Mbps", maximum: "5 Gbps" }),
    showcaseUsbPort("showcase-dock-2", { hub: dock, portNumber: 2, kind: "mouse", product: "Demo Wireless Receiver", maker: "Demo Devices",
      speed: "12 Mbps", maximum: "5 Gbps" }),
    showcaseUsbPort("showcase-dock-3", { hub: dock, portNumber: 3, kind: "storage", product: "Demo Portable SSD", maker: "Demo Storage",
      speed: "10 Gbps", maximum: "10 Gbps" }),
    showcaseUsbPort("showcase-dock-4", { hub: dock, portNumber: 4, maximum: "5 Gbps" }),
    showcaseUsbPort("showcase-usb-a-2", { hub: root, portNumber: 2, kind: "audio", product: "Demo Studio Headset", maker: "Demo Audio",
      speed: "480 Mbps", maximum: "10 Gbps" }),
    showcaseUsbPort("showcase-usb-a-3", { hub: root, portNumber: 3, kind: "camera", product: "Demo HD Webcam", maker: "Demo Vision",
      speed: "480 Mbps", maximum: "10 Gbps" }),
    showcaseUsbPort("showcase-usb-a-4", { hub: root, portNumber: 4, maximum: "10 Gbps" }),
    showcasePort("showcase-hdmi-1", { displayName: "HDMI", connectorKind: "hdmi", busKind: "display", hardwareKind: "display-output",
      protocol: "HDMI 2.1", physicalMaximumSpeed: "48 Gbps", transport: { code: null, reason: "not-queried", ruleId: null, evidence: [] } })
  ];
  return {
    schemaVersion: "4.0.0", state: "ready",
    snapshot: {
      capturedAt,
      system: { manufacturer: "Demo Devices", model: showcaseMachine.model, brandDisplayName: "Demo Devices", brandLogoText: "DD",
        biosVersion: "1.04", baseBoardManufacturer: "Demo Devices", baseBoardProduct: "Atlas Board X1" },
      gpuInformation: [], externalMachines: null,
      machineIdentity: { formFactor: "desktop", evidence: {
        chassisTypes: { value: [3], reason: null }, systemBatteryPresent: { value: false, reason: null },
        cpuModels: { value: [], reason: null }, gpuModels: { value: [], reason: null },
        gpuAttachments: { value: [], reason: null }, workstationComponents: [] } },
      ports, notes: []
    },
    contentGeneration: 1, stateRevision: revision, source: "live", lastSuccessAt: capturedAt, lastAttemptAt: capturedAt,
    failureCode: null, informationFields: { observedAt: null, devices: [] }, attemptDiagnostics: []
  };
}

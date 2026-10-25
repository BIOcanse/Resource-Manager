import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import { createRoot } from "solid-js";
import { decodeResourceBreakdownWireSnapshot, decodeResourceProcessDetailsWireSnapshot, type ResourceBreakdownWireSnapshot } from "../src/api/resourceBreakdownWire.ts";
import { createResourceFactsProjection, decodeResourceFacts, projectResourceFacts } from "../src/features/resourceTable/resourceFacts.ts";
import { resourceProcessDisplayName, resourceTableRowName } from "../src/presentation/metricLabels.ts";
import { applyLanguage } from "../src/text.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { enrichSoftwareContextTarget } from "../src/components/softwareContextTarget.ts";

// Semantic goldens are independent of the production copy lookup and cover every shipped language.
const expected = {
  "zh-CN": ["GPU / WDDM / 桌面合成（未细分）", "系统 / 内核 / 驱动", "系统提交 / 内核", "系统 / 驱动", "worker.exe · 未归属 GPU 进程（DXGKrnl/VidMm）"],
  "en-US": ["GPU / WDDM / desktop composition (unclassified)", "System / kernel / drivers", "System commit / kernel", "System / drivers", "worker.exe · Unattributed GPU process (DXGKrnl/VidMm)"],
  "zh-TW": ["GPU / WDDM / 桌面合成（未細分）", "系統 / 核心 / 驅動程式", "系統提交 / 核心", "系統 / 驅動程式", "worker.exe · 未歸屬 GPU 程序（DXGKrnl/VidMm）"],
  "de-DE": ["GPU / WDDM / Desktopkomposition (nicht aufgeschlüsselt)", "System / Kernel / Treiber", "System-Zusicherung / Kernel", "System / Treiber", "worker.exe · Nicht zugeordneter GPU-Prozess (DXGKrnl/VidMm)"],
  "es-ES": ["GPU / WDDM / composición del escritorio (sin clasificar)", "Sistema / núcleo / controladores", "Memoria comprometida del sistema / núcleo", "Sistema / controladores", "worker.exe · Proceso de GPU sin atribuir (DXGKrnl/VidMm)"],
  "fr-FR": ["GPU / WDDM / composition du bureau (non classés)", "Système / noyau / pilotes", "Mémoire engagée du système / noyau", "Système / pilotes", "worker.exe · Processus GPU non attribué (DXGKrnl/VidMm)"],
  "ja-JP": ["GPU / WDDM / デスクトップ合成（未分類）", "システム / カーネル / ドライバー", "システムのコミット / カーネル", "システム / ドライバー", "worker.exe · 未帰属の GPU プロセス（DXGKrnl/VidMm）"],
  "ko-KR": ["GPU / WDDM / 데스크톱 합성 (미분류)", "시스템 / 커널 / 드라이버", "시스템 커밋 / 커널", "시스템 / 드라이버", "worker.exe · 미귀속 GPU 프로세스 (DXGKrnl/VidMm)"],
  "ru-RU": ["GPU / WDDM / композиция рабочего стола (без детализации)", "Система / ядро / драйверы", "Выделенная память системы / ядро", "Система / драйверы", "worker.exe · Нераспределённый процесс GPU (DXGKrnl/VidMm)"]
} as const;
const metricIds = ["gpu.12.vram", "memory.usage", "virtualMemory.usage", "cpu.usage"];
const wire: ResourceBreakdownWireSnapshot = {
  version: 8, capturedAt: null,
  softwareCatalog: metricIds.map(id => [`resource-residual:${id}`, "", "system_residual", "system_residual"]),
  bars: metricIds.map((metricId, index) => ({ metricId, unit: "B", scaleMode: "capacity", totalValue: 10, capacityValue: 100, totalSystemPercent: 10,
    software: [[index, 10, 10, 0, 0, [[-1, "", null, 10, 10, 100, null, null, "system-residual", 0, null]]]] }))
};
wire.softwareCatalog.push(["unattributed", "", "unattributed", "unattributed"]);
wire.bars[0].software.push([4, 10, 10, 2, 0, [
  [42, "worker.exe", "C:/worker.exe", 5, 5, 50, null, "x64", "etw-residual-process", 0, "123"],
  [43, "ordinary.exe", "C:/ordinary.exe", 5, 5, 50, null, "x64", "process", 0, "124"]
]]);
const bars = decodeResourceBreakdownWireSnapshot(wire).bars!;
assert.ok(bars.every(bar => bar.software[0].processes[0].name === ""));
const details = decodeResourceProcessDetailsWireSnapshot({ version: 1, capturedAt: null, metrics: wire.bars.map(bar => ({
  metricId: bar.metricId, software: bar.software.map(group => ({ softwareId: wire.softwareCatalog[group[0]][0], processes: group[5] }))
})) });
assert.deepEqual(details.metrics.map(metric => metric.software[0].processes[0].name), ["", "", "", ""]);

const processCatalog = bars.flatMap(bar => bar.software.flatMap(group => group.processes));
const facts = decodeResourceFacts({ version: 2, capturedAt: null, attributions: [],
  software: wire.softwareCatalog.map(([softwareId, name, kind, attributionKind]) => ({ softwareId, name, kind, attributionKind })),
  processes: processCatalog.map(process => ({ ...process, userName: process.userName ?? null, architecture: process.architecture ?? null })),
  metrics: bars.map(bar => ({ ...bar, totalMetricId: null, processSumValue: null, sharedValue: null, software: bar.software.map(group => ({ ...group, sharedValue: null,
    processes: group.processes.map(process => ({ ...process, processIndex: processCatalog.indexOf(process), sharedValue: null })) })) }))
}).facts!;
const columns = [{ id: "name", visible: true }, { id: "memory", visible: true }, { id: "gpu.12.vram", visible: true }];
const projection = createRoot(dispose => ({ dispose,
  rows: createResourceFactsProjection(() => facts, () => columns, () => "process", () => ({ columnId: "name", direction: "asc" }))
}));
const initialIds = projection.rows()!.rows.map(row => row.id).sort();
const languages = languageOptions.filter(option => option.id !== "system");
assert.deepEqual(languages.map(language => language.id).sort(), Object.keys(expected).sort());
try {
  for (const language of languages) {
    await applyLanguage(language.id);
    const names = expected[language.id as keyof typeof expected];
    const projected = projection.rows()!;
    assert.deepEqual(projected.rows.map(row => row.id).sort(), initialIds, `${language.id}: identity must not depend on presentation`);
    for (const [index, metricId] of metricIds.entries()) {
      const process = bars[index].software[0].processes[0];
      assert.equal(resourceProcessDisplayName(process, metricId), names[index], `${language.id}: ${metricId}`);
      assert.equal(resourceProcessDisplayName({ ...process, name: "obsolete backend prose" }, metricId), names[index], "static names must never be used as a compatibility fallback");
      const row = projected.rows.find(row => row.softwareId === `resource-residual:${metricId}`)!;
      assert.equal(resourceTableRowName(row), names[index], `${language.id}: process name must not be replaced with the software group name`);
      assert.deepEqual(row.processNames, [], `${language.id}: categories have no factual process name`);
      assert.equal(row.processId, null);
      const grouped = projectResourceFacts(facts, columns, "software", "name", "asc");
      assert.deepEqual(grouped.rows.find(row => row.kind === "software" && row.softwareId === `resource-residual:${metricId}`)!.processNames, []);
      assert.deepEqual(enrichSoftwareContextTarget({ kind: "software", name: names[index], softwareId: row.softwareId }, grouped.rows).processNames, []);
    }
    const etw = projected.rows.find(row => row.processId === 42)!;
    assert.equal(resourceTableRowName(etw), names[4]);
    assert.deepEqual(etw.processNames, ["worker.exe"], `${language.id}: actions must receive the factual process name`);
    assert.deepEqual(enrichSoftwareContextTarget({ kind: "software", name: "unattributed", softwareId: "unattributed" }, projected.rows).processNames!.sort(), ["ordinary.exe", "worker.exe"]);
    assert.equal(etw.processStartKey, "123");
    assert.equal(resourceTableRowName(projected.rows.find(row => row.processId === 43)), "ordinary.exe");
    assert.equal(resourceProcessDisplayName({ name: " Actual Process.EXE ", attributionKind: "process" }, "memory.usage"), " Actual Process.EXE ");
    assert.equal(resourceProcessDisplayName({ name: "name.exe", attributionKind: "other-kind" }, "cpu.usage"), "name.exe");
    assert.equal(resourceProcessDisplayName({ name: "", attributionKind: "system-residual" }, "network.traffic"), names[3]);
  }
} finally { projection.dispose(); }
assert.equal(processCatalog.find(process => process.processId === 42)!.name, "worker.exe", "presentation cannot mutate the machine identity");

// The tested production helper supplies canvas labels, tooltip/accessibility text and the projected table name.
const component = readFileSync(new URL("../src/features/resourceBreakdown/ResourceBreakdown.tsx", import.meta.url), "utf8");
assert.match(component, /label: item\.width >= 10 \? resourceProcessDisplayName\(item\.segment, metricId\)/);
assert.match(component, /\$\{resourceProcessDisplayName\(process, bar\.metricId\)\}/);
assert.match(component, /aria-label=\{resourceProcessTooltip\(props\.bar, item\(\)\.segment\)\}/);
const table = readFileSync(new URL("../src/features/resourceTable/ResourceTable.tsx", import.meta.url), "utf8");
assert.match(table, /resourceTableRowName as rowName/);
assert.match(table, /title=\{rowName\(row\(\)\)\}/);
assert.match(table, /processNames: row\.processNames \?\? \[\]/);
console.log(`Synthetic process presentation passed: ${languages.length} languages, four static categories, ETW/ordinary names, three decoders, reactive table names/search and stable identity.`);

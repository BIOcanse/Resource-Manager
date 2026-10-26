import assert from "node:assert/strict";
import { decodePhysicalMemoryResidualFrame } from "../src/api/resourceBreakdownWire.ts";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { resourceProcessDisplayName } from "../src/presentation/metricLabels.ts";
import {
  isPhysicalMemoryResidualKind,
  physicalMemoryPoolTagText,
  physicalMemoryResidualKindName,
  physicalMemoryResidualLeafName,
  physicalMemoryResidualReason,
  physicalMemoryResidualView
} from "../src/presentation/physicalMemoryResidual.ts";
import { isReservedProcess, isReservedSegment } from "../src/features/resourceBreakdown/resourceProcessIdentity.ts";
import { applyLanguage, uiText } from "../src/text.ts";

// 物理内存保留量分项的名称与页帧明细的三种状态（docs/runtime_process_attribution_and_io_network_telemetry.md，
// Physical memory residual）。名称的期望值独立写在这里，不从生产文案取，覆盖全部九种语言。

// 池标记：小端 ULONG 的八位十六进制还原成四个字符；有不可打印字节时原样给出十六进制。
assert.equal(physicalMemoryPoolTagText("4346744E"), "NtFC");
assert.equal(physicalMemoryPoolTagText("656C6946"), "File");
assert.equal(physicalMemoryPoolTagText("01020304"), "01020304");
assert.equal(physicalMemoryPoolTagText("2043694D"), "MiC ");
assert.equal(physicalMemoryPoolTagText("7F414141"), "7F414141", "DEL is not printable");
assert.equal(isPhysicalMemoryResidualKind("system-memory/unclassified"), true);
assert.equal(isPhysicalMemoryResidualKind("system-residual"), false);
assert.equal(isPhysicalMemoryResidualKind(undefined), false);

// 保留量分项的判定在 resourceProcessIdentity.ts（与 bars-polish 同一份）：按所属条目，不按分项标识的前缀。
const leaf = { processId: -1, name: "", value: 1, systemPercent: 1, softwarePercent: 100, attributionKind: "system-memory/unclassified" };
assert.equal(isReservedSegment({ softwareId: "resource-residual:memory.usage" }), true);
assert.equal(isReservedSegment({ softwareId: "frontend-harness.software" }), false);
assert.equal(isReservedProcess({ softwareId: "resource-residual:memory.usage" }, leaf as never), true);
assert.equal(isReservedProcess({ softwareId: "frontend-harness.software" }, { ...leaf, processId: 42, attributionKind: "process" } as never), false);

const counterKinds = ["system-memory/nonpaged-pool", "system-memory/nonpaged-pool/tag/4346744E", "system-memory/nonpaged-pool/other-tags",
  "system-memory/nonpaged-pool/unclassified", "system-memory/paged-pool-resident", "system-memory/driver-resident",
  "system-memory/code-resident", "system-memory/unclassified"];
const pfnKinds = ["system-memory/mapped-file", "system-memory/shareable", "system-memory/page-table", "system-memory/paged-pool-resident",
  "system-memory/system-pte", "system-memory/metafile", "system-memory/driver-locked", "system-memory/kernel-stack",
  "system-memory/working-set-internals", "system-memory/modified", "system-memory/process-private-not-attributed"];

// [非分页池 · 池标记 NtFC, 分页池（驻留）, 驱动程序（驻留）, 系统代码（驻留）, 未分类, 内核栈, components-exceed-residual]
const expected = {
  "zh-CN": ["非分页池 · 池标记 NtFC", "分页池（驻留）", "驱动程序（驻留）", "系统代码（驻留）", "未分类", "内核栈",
    "测得的各类别之和超过系统/驱动保留量，本次不作拆分。"],
  "zh-TW": ["非分頁集區 · 集區標記 NtFC", "分頁集區（常駐）", "驅動程式（常駐）", "系統程式碼（常駐）", "未分類", "核心堆疊",
    "測得的各類別之和超過系統/驅動程式保留量，本次不予拆分。"],
  "en-US": ["Nonpaged pool · Pool tag NtFC", "Paged pool (resident)", "Drivers (resident)", "System code (resident)", "Unclassified", "Kernel stacks",
    "The measured categories exceed the system/driver reserved amount; no breakdown is given for this reading."],
  "ja-JP": ["非ページプール · プールタグ NtFC", "ページプール（常駐）", "ドライバー（常駐）", "システムコード（常駐）", "未分類", "カーネルスタック",
    "測定した各区分の合計がシステム/ドライバー予約を超えたため、今回は内訳を示しません。"],
  "ko-KR": ["비페이징 풀 · 풀 태그 NtFC", "페이징 풀(상주)", "드라이버(상주)", "시스템 코드(상주)", "미분류", "커널 스택",
    "측정된 분류의 합이 시스템/드라이버 예약량을 초과하여 이번에는 세부 정보를 제공하지 않습니다."],
  "fr-FR": ["Réserve non paginée · Balise de réserve NtFC", "Réserve paginée (résidente)", "Pilotes (résidents)", "Code système (résident)", "Non classé", "Piles du noyau",
    "Les catégories mesurées dépassent la part réservée par le système et les pilotes ; aucune répartition n’est donnée pour ce relevé."],
  "de-DE": ["Nicht ausgelagerter Pool · Pooltag NtFC", "Ausgelagerter Pool (resident)", "Treiber (resident)", "Systemcode (resident)", "Nicht klassifiziert", "Kernelstapel",
    "Die gemessenen Kategorien übersteigen die System-/Treiberreserve; für diesen Messwert wird keine Aufschlüsselung angegeben."],
  "es-ES": ["Bloque no paginado · Etiqueta de bloque NtFC", "Bloque paginado (residente)", "Controladores (residentes)", "Código del sistema (residente)", "Sin clasificar", "Pilas del kernel",
    "Las categorías medidas superan la reserva del sistema y los controladores; no se ofrece desglose para esta lectura."],
  "ru-RU": ["Невыгружаемый пул · Тег пула NtFC", "Выгружаемый пул (резидентный)", "Драйверы (резидентные)", "Системный код (резидентный)", "Не классифицировано", "Стеки ядра",
    "Измеренные категории превышают резерв системы и драйверов; для этого показания распределение не приводится."]
} as const;

const GiB = 1024 ** 3;
const ready = decodePhysicalMemoryResidualFrame({
  version: 1, source: "pfn", status: "ready", reasonCode: null, capturedAt: "2026-10-25T12:00:00Z",
  usedBytes: 12 * GiB, attributedProcessPrivateBytes: 4 * GiB, residualBytes: 8 * GiB, unclassifiedBytes: 1 * GiB,
  parts: [
    { kind: "system-memory/mapped-file", bytes: 2 * GiB },
    { kind: "system-memory/nonpaged-pool/tag/4346744E", bytes: 3 * GiB },
    { kind: "system-memory/nonpaged-pool/tag/01020304", bytes: 0.5 * GiB },
    { kind: "system-memory/nonpaged-pool/other-tags", bytes: 0.5 * GiB },
    { kind: "system-memory/modified", bytes: 1 * GiB }
  ]
});
const withheld = decodePhysicalMemoryResidualFrame({
  version: 1, source: "pfn", status: "ready", reasonCode: "process-private-below-attributed", capturedAt: "2026-10-25T12:00:10Z",
  usedBytes: 12 * GiB, attributedProcessPrivateBytes: 4 * GiB, residualBytes: 8 * GiB, unclassifiedBytes: 8 * GiB, parts: []
});
const unavailable = decodePhysicalMemoryResidualFrame({
  version: 1, source: "pfn", status: "unavailable", reasonCode: "components-exceed-residual", capturedAt: "2026-10-25T12:00:20Z",
  usedBytes: null, attributedProcessPrivateBytes: null, residualBytes: null, unclassifiedBytes: null, parts: []
});

const languages = languageOptions.filter(option => option.id !== "system");
assert.deepEqual(languages.map(language => language.id).sort(), Object.keys(expected).sort());
for (const language of languages) {
  await applyLanguage(language.id);
  const names = expected[language.id as keyof typeof expected];
  assert.equal(physicalMemoryResidualKindName("system-memory/nonpaged-pool/tag/4346744E"), names[0], language.id);
  assert.equal(physicalMemoryResidualKindName("system-memory/paged-pool-resident"), names[1], language.id);
  assert.equal(physicalMemoryResidualKindName("system-memory/driver-resident"), names[2], language.id);
  assert.equal(physicalMemoryResidualKindName("system-memory/code-resident"), names[3], language.id);
  assert.equal(physicalMemoryResidualKindName("system-memory/unclassified"), names[4], language.id);
  assert.equal(physicalMemoryResidualKindName("system-memory/kernel-stack"), names[5], language.id);
  assert.equal(physicalMemoryResidualReason("components-exceed-residual"), names[6], language.id);

  // 每个登记的分项都有本语言的名称，不回显标识；组内分项单独出现时带组名。
  for (const kind of [...counterKinds, ...pfnKinds]) {
    const name = physicalMemoryResidualKindName(kind);
    assert.ok(name.trim() && name !== kind && !name.includes("system-memory"), `${language.id}: ${kind}`);
  }
  assert.ok(physicalMemoryResidualKindName("system-memory/nonpaged-pool/other-tags").startsWith(uiText.physicalMemoryResidual.kinds.nonpagedPool));
  assert.ok(physicalMemoryResidualKindName("system-memory/nonpaged-pool/tag/01020304").endsWith(" 01020304"), "nonprintable tags show the exact hex");
  // 认不出的标识原样显示，不编名称。
  assert.equal(physicalMemoryResidualKindName("system-memory/future-kind"), "system-memory/future-kind");

  // 资源条和资源表的分项行：PID -1，名称只来自标识，后端的 name（池标记的原始四字节）不用。
  assert.equal(resourceProcessDisplayName({ name: "NtFC", attributionKind: "system-memory/nonpaged-pool/tag/4346744E" }, "memory.usage"), names[0]);
  assert.equal(resourceProcessDisplayName({ name: "", attributionKind: "system-memory/unclassified" }, "memory.usage"), names[4]);

  // 原因代码：固定代码逐个有说明；带参数的四类写出参数；未登记的代码给出“未登记”说明（代码本身由界面原样显示）。
  for (const code of Object.keys(uiText.physicalMemoryResidual.reasons)) assert.notEqual(physicalMemoryResidualReason(code), uiText.physicalMemoryResidual.unknownReason);
  assert.ok(physicalMemoryResidualReason("native-error:5").includes("5"));
  assert.ok(physicalMemoryResidualReason("profile-privilege:0xC0000061").includes("0xC0000061"));
  assert.ok(physicalMemoryResidualReason("privilege-restore:0xC0000022").includes("0xC0000022"));
  const superfetch = physicalMemoryResidualReason("superfetch-79:0xC0000004");
  assert.ok(superfetch.includes("79") && superfetch.includes("0xC0000004"));
  assert.equal(physicalMemoryResidualReason("pool-tags:overflow"), uiText.physicalMemoryResidual.unknownReason);

  // 三种状态。没有帧：尚未采集，不是零。
  assert.deepEqual(physicalMemoryResidualView(undefined), { state: "not-collected" });

  const unavailableView = physicalMemoryResidualView(unavailable);
  assert.deepEqual(unavailableView, { state: "unavailable", capturedAt: "2026-10-25T12:00:20Z", reason: names[6], reasonCode: "components-exceed-residual" });

  const readyView = physicalMemoryResidualView(ready);
  assert.equal(readyView.state, "ready");
  if (readyView.state !== "ready") throw new Error("unreachable");
  assert.equal(readyView.capturedAt, "2026-10-25T12:00:00Z");
  assert.equal(readyView.residualBytes, 8 * GiB);
  assert.equal(readyView.usedBytes, 12 * GiB);
  assert.equal(readyView.attributedProcessPrivateBytes, 4 * GiB);
  assert.equal(readyView.reason, null);
  // 帧里的顺序；相邻的非分页池分项在一个组标题下；末尾是未分类。数值、百分比都来自帧。
  assert.deepEqual(readyView.rows.map(row => row.type === "group" ? `group:${row.group}` : `${row.kind}:${row.bytes}:${row.residualPercent}:${row.grouped}`), [
    `system-memory/mapped-file:${2 * GiB}:25:false`,
    "group:nonpaged-pool",
    `system-memory/nonpaged-pool/tag/4346744E:${3 * GiB}:37.5:true`,
    `system-memory/nonpaged-pool/tag/01020304:${0.5 * GiB}:6.25:true`,
    `system-memory/nonpaged-pool/other-tags:${0.5 * GiB}:6.25:true`,
    `system-memory/modified:${1 * GiB}:12.5:false`,
    `unclassified:${1 * GiB}:12.5:false`
  ]);
  const labels = readyView.rows.map(row => row.label);
  assert.equal(labels[1], uiText.physicalMemoryResidual.kinds.nonpagedPool);
  assert.equal(labels[2], uiText.physicalMemoryResidual.kinds.poolTag("NtFC"));
  assert.equal(labels[3], uiText.physicalMemoryResidual.kinds.poolTag("01020304"));
  assert.equal(labels.at(-1), names[4]);

  // 就绪帧也可能带原因代码（进程私有差额为负，未列出）。
  const withheldView = physicalMemoryResidualView(withheld);
  assert.equal(withheldView.state, "ready");
  if (withheldView.state !== "ready") throw new Error("unreachable");
  assert.deepEqual(withheldView.reason, { text: uiText.physicalMemoryResidual.reasons["process-private-below-attributed"], reasonCode: "process-private-below-attributed" });
  assert.deepEqual(withheldView.rows.map(row => row.type === "part" ? row.kind : row.type), ["unclassified"]);
}

process.stdout.write("physicalMemoryResidual tests passed\n");

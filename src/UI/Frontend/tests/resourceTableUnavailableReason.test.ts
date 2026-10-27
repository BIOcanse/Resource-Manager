import assert from "node:assert/strict";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { loadAppCopy } from "../src/i18n/copy/appCopyLoader.ts";
import { applyLanguage } from "../src/i18n/appTextStore.ts";
import { decodeResourceFacts, projectResourceFacts } from "../src/features/resourceTable/resourceFacts.ts";
import { resourceTableCellPresentation } from "../src/presentation/metricLabels.ts";
import { factsMetricKeys } from "./resourceFactsWireFixture.ts";

// 总计格读不到时（用户 10-27 截图：GPU1 显存的总计格写着 gpu-dedicated-memor…）：格子里写原因的那句话或无读数，
// 原因代码只在提示里。原因表和资源条共用（resourceBarUnavailableReason.ts）；不认识的代码格子里写无读数，提示里写原码。
const capturedAt = "2026-10-27T04:31:07Z";
const unavailable = (metricId: string, unit: string, reason: string) => ({
  metricId, unit, processFieldName: null, totalValue: null, capacityValue: null, totalSystemPercent: null, sharedValue: null, software: [],
  ...factsMetricKeys({ totalMetricId: metricId, processSumValue: null, residualValue: null, additiveAcrossProcesses: true,
    capturedAt: null, unavailableReason: reason }) });
const frame = decodeResourceFacts({
  version: 2, capturedAt, attributions: [], software: [], processes: [],
  metrics: [
    // 后端给没有独立显存读数的 GPU 发的原因（WindowsResourceBreakdownSampler.Bars.cs），前端原来那张表里没有。
    unavailable("gpu.1.vram", "B", "gpu-dedicated-memory-metrics-unsupported"),
    // 真实切片 20261027T043049Z 里总量为 null 的两个。
    unavailable("disk.io", "B/s", "disk-io-window-incomplete"),
    unavailable("gpu.0.usage", "%", "hardware-source-unavailable"),
    unavailable("network.traffic", "B/s", "future-backend-reason")
  ]
}).facts!;
const columns = ["gpu.1.vram", "disk", "gpu.0.usage", "network"].map(id => ({ id, visible: true }));
const expected: Record<string, { code: string; family: string | null }> = {
  "gpu.1.vram": { code: "gpu-dedicated-memory-metrics-unsupported", family: "unsupported" },
  disk: { code: "disk-io-window-incomplete", family: "incomplete" },
  "gpu.0.usage": { code: "hardware-source-unavailable", family: "sourceUnavailable" },
  network: { code: "future-backend-reason", family: null }
};

const languages = languageOptions.filter(option => option.id !== "system");
assert.equal(languages.length, 9);
for (const { id } of languages) {
  const copy = await loadAppCopy(id);
  await applyLanguage(id);
  for (const mode of ["software", "process"] as const) {
    const summary = projectResourceFacts(frame, columns, mode, "impact", "desc").rows[0];
    for (const [columnId, { code, family }] of Object.entries(expected)) {
      const cell = summary.values[columnId];
      assert.equal(cell.unavailableReason, code, `${id} ${columnId}: the reason is kept as sent`);
      const presentation = resourceTableCellPresentation(cell, columnId, { label: columnId, basis: undefined });
      const reason = family === null ? null
        : (copy.resourceBreakdown.unavailableReason as unknown as Record<string, string>)[family];
      assert.ok(!presentation.display.includes(code) && !/[a-z]+-[a-z]+-/.test(presentation.display),
        `${id} ${columnId}: no reason code in the cell: ${presentation.display}`);
      assert.equal(presentation.display, reason ?? copy.dashboard.noReading, `${id} ${columnId}: the cell text`);
      assert.equal(presentation.unavailable, true);
      assert.ok(presentation.title.includes(copy.backendMessage.reasonCode(code)),
        `${id} ${columnId}: the raw code is on hover: ${presentation.title}`);
      assert.equal(presentation.title.split("\n").filter(line => line === reason).length, 0,
        `${id} ${columnId}: the reason sentence is not repeated in the tooltip`);
    }
  }
  const sample = copy.backendMessage.reasonCode("x");
  assert.ok(sample.endsWith("x") && sample.length > 2, id);
}
await applyLanguage("zh-CN");
console.log("Unavailable resource-table totals show the shared reason text or no reading, with the raw code on hover, in nine languages passed");

import assert from "node:assert/strict";

export default async function smartDetailsPlacementFactsJourney(page, baseUrl) {
  const failures = [];
  page.on("pageerror", error => failures.push(String(error)));
  const updatedAt = "2026-10-24T12:00:00.000Z";
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      optimizationRuntime: true,
      smartPlacements: [{
        targetId: "software:frontend-harness.software",
        processName: "Harness App", processId: 42, processStartKey: "133700000000000000",
        softwareId: "frontend-harness.software",
        resourceKind: "cpu",
        records: [{ kind: "cpu_affinity", recordId: "placement-fixture" }],
        appliedAt: updatedAt,
        updatedAt
      }]
    })
  });
  assert.equal(response.status, 200);

  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: "详细信息", exact: true }).click();
  await page.getByRole("tab", { name: "报告", exact: true }).click();

  const report = page.locator(".smart-details-report");
  const record = report.locator(".smart-details-record");
  await record.waitFor();
  const summaryLabels = await report.locator(".smart-details-summary .smart-details-tile > span")
    .allTextContents();
  assert.deepEqual(summaryLabels.map(label => label.trim()), ["已完成"]);
  assert.equal(await report.locator(".smart-details-summary .smart-details-tile").count(), 1);
  assert.equal(await record.locator(".smart-details-record-metrics span").count(), 0,
    "A placement receipt without execution metadata must not be labeled completed or needs-attention");
  assert.match(await record.textContent() ?? "", /Harness App（进程 42）/);

  await record.getByRole("button", { name: "详细信息", exact: true }).click();
  const dialog = page.getByRole("dialog", { name: "Harness App（进程 42）详细信息", exact: true });
  await dialog.waitFor();
  const detailRows = dialog.locator(".user-details-row");
  const labels = await detailRows.locator("dt").allTextContents();
  assert.deepEqual(labels.map(label => label.trim()), ["对象", "调整内容", "影响资源", "更新时间"]);
  const dialogText = await dialog.textContent() ?? "";
  assert.doesNotMatch(dialogText, /执行情况|需要关注|内存释放|显存释放|0 项|0 B/);
  if (failures.length) throw new Error(failures.join("\n"));

  return { appliedPlacementFacts: labels.map(label => label.trim()), fabricatedExecutionMetrics: 0, pageErrors: failures };
}

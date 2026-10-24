import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";

// 详细信息 → CPU：基准比例面板读出默认值与来源，保存、拒绝越界值、恢复默认，
// 以及运行计划未发布（409 savedNotApplied）和读取失败后的重试。
export default async function cpuBaselineRatioJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ runtimeEffectOwners: true })
  });
  const writes = [];
  let failNextRead = true;
  let rejectNextSave = false;
  await page.route("**/api/cpu/baseline-ratio", async route => {
    const method = route.request().method();
    if (method === "GET" && failNextRead) {
      failNextRead = false;
      return route.fulfill({ status: 503, body: "unavailable" });
    }
    if (method !== "GET") writes.push({ method, body: route.request().postData() });
    if (method === "PUT" && rejectNextSave) {
      rejectNextSave = false;
      return route.fulfill({ status: 409, json: {
        settings: null, savedOverrideRatio: 0.6, runtimeApplicationDisposition: "savedNotApplied"
      } });
    }
    await route.continue();
  });

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const app = await loadAppCopy("zh-CN");
  const text = app.cpuBaseline;
  await page.getByRole("navigation").getByRole("button", { name: app.page.details, exact: true }).click();
  await page.getByRole("tab", { name: app.misc.detailsTab.cpu, exact: true }).click();
  const panel = page.getByRole("region", { name: text.title, exact: true });
  const tile = label => panel.locator(".smart-details-tile").filter({ has: page.getByText(label, { exact: true }) })
    .locator("strong");

  await panel.getByText(text.readFailed, { exact: true }).waitFor();
  await panel.getByRole("button", { name: text.retry, exact: true }).click();
  await tile(text.currentRatio).getByText(`0.5 · ${text.defaultActive}`, { exact: true }).waitFor();
  assert.equal(await tile(text.multiplier).textContent(), "×2");
  assert.equal(await tile(text.defaultSource).textContent(), text.sources["amd:symmetric-dual-ccd"]);
  assert.equal(await tile(text.matchedModel).textContent(), text.noMatchedModel);
  assert.equal(await tile(text.datasetVersion).textContent(), "2026-09-02");

  const input = panel.getByRole("spinbutton", { name: text.ratioInput });
  await panel.getByRole("button", { name: text.edit, exact: true }).click();
  assert.equal(await input.inputValue(), "0.5");
  assert.equal(await panel.getByRole("button", { name: text.reset, exact: true }).count(), 0,
    "Restore default appears only when a custom value exists");
  await input.fill("1.5");
  await panel.getByRole("button", { name: text.save, exact: true }).click();
  await panel.getByText(text.invalidRatio, { exact: true }).waitFor();
  assert.equal(writes.length, 0, "An out-of-range ratio is rejected before any request");

  await input.fill("0.75");
  await panel.getByRole("button", { name: text.save, exact: true }).click();
  await tile(text.currentRatio).getByText(`0.75 · ${text.overrideActive}`, { exact: true }).waitFor();
  assert.equal(await tile(text.multiplier).textContent(), "×1.333");
  assert.deepEqual(JSON.parse(writes.at(-1).body), { ratio: 0.75 });
  assert.equal(await panel.getByText(text.invalidRatio, { exact: true }).count(), 0);

  rejectNextSave = true;
  await panel.getByRole("button", { name: text.edit, exact: true }).click();
  await input.fill("0.6");
  await panel.getByRole("button", { name: text.save, exact: true }).click();
  await panel.getByText(text.savedNotApplied, { exact: true }).waitFor();
  // 运行计划没有发布，显示仍是生效中的值。
  await tile(text.currentRatio).getByText(`0.75 · ${text.overrideActive}`, { exact: true }).waitFor();

  await panel.getByRole("button", { name: text.edit, exact: true }).click();
  await panel.getByRole("button", { name: text.reset, exact: true }).click();
  await tile(text.currentRatio).getByText(`0.5 · ${text.defaultActive}`, { exact: true }).waitFor();
  assert.equal(writes.at(-1).method, "DELETE");
  assert.equal(await panel.getByText(text.savedNotApplied, { exact: true }).count(), 0);
  assert.deepEqual(errors, []);
  return { writes: writes.map(write => write.method) };
}

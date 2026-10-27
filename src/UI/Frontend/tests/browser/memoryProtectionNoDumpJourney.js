import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { waitForContentSettled } from "./contentSettled.js";

// 软件页“不转储”：存在已有的软件策略里，随完整策略保存；保存别的软件设置时保留它。
// 它只在全局处理方式为“转储后结束”时起作用：界面写明这一点；全局为“只结束”时开关照样显示，并说明现在不起作用。
export default async function memoryProtectionNoDumpJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  const app = await loadAppCopy("zh-CN");
  const copy = app.memoryProtection.software;
  const policyPath = `${baseUrl}/api/gpu-placement/software/frontend-harness.software`;
  const writes = [];
  page.on("request", request => {
    if (request.method() === "PUT" && new URL(request.url()).pathname.endsWith("/frontend-harness.software/policy")) {
      writes.push(request.postDataJSON());
    }
  });
  await page.route("**/api/optimization/protection", route => route.fulfill({ json: [] }));
  await scenario({ gpuPlacementEnabled: true, runtimeEffectOwners: true });
  await page.setViewportSize({ width: 1280, height: 800 });

  async function scenario(body) {
    const response = await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) });
    assert.equal(response.ok, true, `scenario rejected: ${JSON.stringify(body)}`);
  }
  async function openPolicy() {
    await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
    await page.getByRole("button", { name: app.page.components, exact: true }).click();
    await page.getByRole("tablist", { name: app.management.categoryNav }).getByRole("tab", { name: new RegExp(app.softwareKind.other) }).click();
    await page.locator(".software-item").filter({ hasText: "示例软件" })
      .getByRole("button", { name: app.management.softwareSettings, exact: true }).click();
    const dialog = page.getByRole("dialog", { name: "示例软件" });
    await dialog.getByRole("tab", { name: app.softwareDetail.tab.policy, exact: true }).click();
    await dialog.getByRole("heading", { name: copy.title, exact: true }).waitFor();
    await waitForContentSettled(page);
    return dialog;
  }
  const group = dialog => dialog.locator(".software-memory-protection-group");
  const noDump = dialog => group(dialog).getByRole("checkbox", { name: copy.noDump, exact: true });
  const save = async dialog => {
    const saved = page.waitForResponse(response => response.request().method() === "PUT" && response.url().endsWith("/policy"));
    await dialog.getByRole("button", { name: app.softwareDetail.save, exact: true }).click();
    assert.equal((await saved).status(), 200);
    await dialog.getByRole("button", { name: app.softwareDetail.save, exact: true }).and(page.locator(":disabled")).waitFor();
  };
  const savedFlag = async () => (await (await fetch(policyPath)).json()).softwarePolicy.hostMemoryProtectionNoDump;

  // 全局“转储后结束”：说明它只在这种方式下起作用，没有“现在不起作用”的提示。
  await scenario({ memoryProtection: "disabled-ready" });
  let dialog = await openPolicy();
  assert.equal(await noDump(dialog).isChecked(), false, "default is off");
  assert.equal(await noDump(dialog).isEnabled(), true);
  await group(dialog).getByText(copy.noDumpHint, { exact: true }).waitFor();
  await page.waitForFunction(() => document.querySelector(".software-memory-protection-group") !== null);
  assert.equal(await group(dialog).getByText(copy.noDumpInactiveNow, { exact: true }).count(), 0);
  await noDump(dialog).check();
  await save(dialog);
  assert.equal(writes.at(-1).hostMemoryProtectionNoDump, true, "the full policy carries the flag");
  assert.equal(writes.at(-1).softwareId, "frontend-harness.software");
  assert.equal(await savedFlag(), true);

  // 保存别的软件设置：完整策略里照样带着不转储。
  const cpu = dialog.getByRole("checkbox", { name: app.softwareDetail.participation.cpu, exact: true });
  await cpu.check();
  await save(dialog);
  assert.equal(writes.at(-1).cpuSchedulingExcluded, true);
  assert.equal(writes.at(-1).hostMemoryProtectionNoDump, true, "saving another setting keeps the no-dump flag");
  assert.equal(await savedFlag(), true);

  // 全局“只结束”：开关仍在、仍可操作，并说明现在不起作用；关掉后保存，往返回到 false。
  await scenario({ memoryProtection: "terminate-only" });
  dialog = await openPolicy();
  await group(dialog).getByText(copy.noDumpInactiveNow, { exact: true }).waitFor();
  await group(dialog).getByText(copy.noDumpHint, { exact: true }).waitFor();
  assert.equal(await noDump(dialog).isVisible(), true, "the switch is not hidden under terminate-only");
  assert.equal(await noDump(dialog).isEnabled(), true);
  assert.equal(await noDump(dialog).isChecked(), true, "the saved flag is read back");
  await noDump(dialog).uncheck();
  await save(dialog);
  assert.equal(writes.at(-1).hostMemoryProtectionNoDump, false);
  assert.equal(writes.at(-1).cpuSchedulingExcluded, true, "turning the flag off keeps the other choices");
  assert.equal(await savedFlag(), false);

  assert.equal(errors.length, 0, `page errors: ${errors.join("\n")}`);
  return { writes: writes.length, roundTrip: [true, true, false], terminateOnlyNote: true };
}

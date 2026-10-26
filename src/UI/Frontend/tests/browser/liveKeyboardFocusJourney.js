import assert from "node:assert/strict";
import { seedFrontendPreferences, customRefresh } from "./frontendPreferencesFixture.js";
import { observeSubscriptionFrames } from "./subscriptionFrameProbe.js";
import { waitForContentSettled } from "./contentSettled.js";

export default async function liveKeyboardFocusJourney(page, baseUrl) {
  const failures = [];
  await seedFrontendPreferences(page, value => { value.refresh.monitor = customRefresh(500); });
  await observeSubscriptionFrames(page, baseUrl);
  assert((await page.request.post(`${baseUrl}/__test/scenario`, { data: { displayEdid: "ready" } })).ok());
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "详细信息", exact: true }).click();
  const topology = page.locator(".device-topology-panel");
  await topology.locator(".device-port-item").first().click();
  await topology.locator(".device-details-button").click();
  const dialog = page.locator(".user-details-modal:visible");
  await dialog.waitFor();
  assert(await dialog.locator("summary").count() > 0, "The device dialog must include its EDID summaries");
  const modal = await tabSequence(dialog, ".modal-header button", ".modal-actions button", "/api/device-topology/state/subscribe").catch(error => { failures.push(String(error)); });
  await page.keyboard.press("Escape");
  await dialog.waitFor({ state: "hidden" });
  await page.getByRole("tab", { name: "CPU 调度模型", exact: true }).click();
  const cpu = page.locator(".cpu-topology-panel");
  await cpu.locator(".cpu-logical-chip").last().waitFor();
  const processor = await tabSequence(cpu, ".cpu-ccd-header", ".cpu-logical-chip:not([disabled])", "/api/cpu/topology/subscribe").catch(error => { failures.push(String(error)); });
  await page.request.post(`${baseUrl}/__test/scenario`, { data: { cpuTopologyLayoutKind: "ring_bus" } });
  await cpu.locator(".cpu-ring-summary").waitFor();
  const ring = await tabSequence(cpu, ".cpu-ring-summary", ".cpu-ring-logical-chip:not([disabled])", "/api/cpu/topology/subscribe").catch(error => { failures.push(String(error)); });
  // The source already has topology data; switching tabs destroys the old view.
  await page.getByRole("tab", { name: "设备拓扑", exact: true }).click();
  await waitForContentSettled(page);
  await page.waitForFunction(() => [...document.querySelectorAll(".cpu-ring-core-main")].every(element => element.closest("[inert]")));
  const firstFrame = await page.evaluate(() => {
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => { observer.disconnect(); reject(new Error("CPU view did not mount within 6500 ms")); }, 6500);
      const observer = new MutationObserver(() => {
        const panel = [...document.querySelectorAll('[data-frontend-visibility-surface="visible.details.cpu-model.surface"]')].find(element => !element.closest("[inert]"));
        if (!panel) return;
        observer.disconnect();
        clearTimeout(timeout);
        requestAnimationFrame(() => resolve({
          cores: panel.querySelectorAll(".cpu-ring-core-main").length,
          logical: panel.querySelectorAll(".cpu-ring-logical-chip").length,
          hasName: panel.querySelector(".cpu-topology-meta strong")?.textContent === "Frontend Harness CPU"
        }));
      });
      observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ["inert"] });
      const tab = [...document.querySelectorAll('[role="tab"]')].find(element => !element.closest("[inert]") && element.textContent.trim() === "CPU 调度模型");
      tab.click();
    });
  });
  assert.deepEqual(firstFrame, { cores: 2, logical: 4, hasName: true }, "a warm CPU view renders the topology on its first frame");
  await waitForContentSettled(page);
  assert.deepEqual(failures, []);
  return { modal, processor, ring, firstFrame };

  async function tabSequence(root, firstSelector, lastSelector, path) {
    await root.locator(firstSelector).first().focus();
    const end = await root.locator(lastSelector).last().elementHandle();
    const sequence = [];
    for (let index = 0; index < 80; index++) {
      const active = await page.evaluateHandle(() => document.activeElement);
      const before = await page.evaluate(path => window.__subscriptionFrameProbe.counts[path] ?? 0, path);
      if (path === "/api/cpu/topology/subscribe") {
        const response = await page.request.post(`${baseUrl}/__test/scenario`, { data: { cpuTopologyUsageOffset: index % 2 } });
        assert(response.ok());
      }
      await page.waitForFunction(({ path, before }) => (window.__subscriptionFrameProbe.counts[path] ?? 0) > before, { path, before }, { timeout: 6500 });
      if (path === "/api/cpu/topology/subscribe") {
        await root.locator(".cpu-core-main strong, .cpu-ring-core-main strong").first().getByText(`${42 + index % 2}.0%`, { exact: true }).waitFor();
      }
      assert(await active.evaluate(element => element === document.activeElement && element.isConnected), `${path}: publication reset keyboard focus after ${JSON.stringify(sequence)}`);
      sequence.push(await active.evaluate(element => element.getAttribute("aria-label") ?? element.textContent?.trim()));
      if (await end.evaluate(element => element === document.activeElement)) return sequence;
      await page.keyboard.press("Tab");
    }
    assert.fail(`${path}: Tab did not reach the final control: ${JSON.stringify(sequence)}`);
  }
}

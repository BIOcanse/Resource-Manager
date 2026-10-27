import assert from "node:assert/strict";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { pointPreferences } from "../../src/preferences/frontendPreferences.ts";
import { waitForContentSettled } from "./contentSettled.js";
import { fanModeFacts } from "./fanModeFacts.js";
import { ResourceManager } from "../../src/preferences/generated/frontendPoints.values.ts";

export default async function pageValueRetentionJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  const preferences = pointPreferences();
  // This adapter answers the native session AND preferences contracts. Visibility
  // travels through the production HostMessageTransport, not a product test hook.
  await page.addInitScript(preferences => {
    const listeners = new Set();
    const pendingNativeReads = new Set();
    let loginStartupEnabled = true;
    window.unboundWebView = {
      postMessage(message) {
        let data;
        if (message?.type === "host.backend-session.request") data = {
          type: "host.backend-session", schemaVersion: 1, publicationRevision: "1",
          state: "ready", epoch: "page-retention-fixture"
        };
        if (["preferences.get", "preferences.save"].includes(message?.type)) {
          if (message.value) preferences = message.value;
          data = { type: "preferences.result", requestId: message.requestId, value: preferences };
        }
        if (["shell.loginStartup.get", "shell.loginStartup.set"].includes(message?.type)) {
          if (typeof message.enabled === "boolean") loginStartupEnabled = message.enabled;
          data = { type: "shell.loginStartup.result", requestId: message.requestId, enabled: loginStartupEnabled };
        }
        if (data) {
          const answer = () => queueMicrotask(() => { for (const listener of [...listeners]) listener({ data }); });
          if (message.type === "shell.loginStartup.get" && window.__retentionFreeze) pendingNativeReads.add(answer);
          else answer();
        }
      },
      addEventListener(type, listener) { if (type === "message") listeners.add(listener); },
      removeEventListener(type, listener) { if (type === "message") listeners.delete(listener); }
    };
    window.__sendVisibility = visible => {
      for (const listener of [...listeners]) listener({ data: `host.visibility:${visible ? "visible" : "hidden"}` });
    };
    const pendingReaders = new Set();
    window.__retentionDeliveredChunks = 0;
    window.__retentionControlStarts = 0;
    window.__retentionControlStops = 0;
    window.__retentionFreeze = false;
    window.__freezeRetentionFrames = frozen => {
      window.__retentionFreeze = frozen;
      if (!frozen) {
        for (const resume of pendingReaders) resume(); pendingReaders.clear();
        for (const answer of pendingNativeReads) answer(); pendingNativeReads.clear();
      }
    };
    const fetch = window.fetch;
    window.__retentionReads = 0;
    window.fetch = async function(input, init) {
      const url = new URL(input instanceof Request ? input.url : String(input), location.href);
      const track = url.pathname.startsWith("/api/") && (init?.method ?? "GET") === "GET";
      if (url.pathname === "/api/subscriptions/stream"
        && JSON.parse(init.body).subscriptions.some(item => item.path.startsWith("/api/control/actual/subscribe"))) {
        window.__retentionControlStarts++;
        init.signal.addEventListener("abort", () => window.__retentionControlStops++, { once: true });
      }
      if (track) window.__retentionReads++;
      try {
        const response = await fetch.apply(this, arguments);
        if (url.pathname === "/api/subscriptions/stream" && response.body) {
          const getReader = response.body.getReader.bind(response.body);
          response.body.getReader = (...args) => {
            const reader = getReader(...args);
            const read = reader.read.bind(reader);
            reader.read = async (...args) => {
              const chunk = await read(...args);
              // Hold transport delivery, never replace payloads or product values.
              if (window.__retentionFreeze) await new Promise(resolve => pendingReaders.add(resolve));
              if (!chunk.done) window.__retentionDeliveredChunks++;
              return chunk;
            };
            return reader;
          };
        }
        return response;
      }
      finally { if (track) window.__retentionReads--; }
    };
  }, preferences);

  await page.route("**/api/control/objects", async route => {
    const response = await route.fetch();
    const catalog = await response.json();
    catalog.objects.push(...Array.from({ length: 6 }, (_, index) => ({
      id: `retention-fan-${index}`, kind: "fan", reportedName: `Retention Fan ${index}`,
      platform: { operatingSystem: "windows", vendor: "oem" }, terms: ["cpu"], isControllable: true,
      capabilities: [{ id: "fan.curve", valueKind: "curve", supported: true, requiredAccessLevel: "normal" },
        { id: "fan.duty", valueKind: "number", supported: true, requiredAccessLevel: "normal",
          range: { minimum: 0, maximum: 100, step: 1, unit: "%", defaultValue: 50 } }],
      ...fanModeFacts(["fan.curve", "fan.duty"])
    })));
    await route.fulfill({ response, json: catalog });
  });
  await page.route("**/api/updates/product/versions", route => route.fulfill({ json: {
    installedVersion: "1.0.0", status: "loaded", complete: true, checkedAt: "2026-10-27T00:00:00Z",
    options: [{ choice: "latest", version: "1.1.0", series: "1.1", channel: "stable", available: true,
      selectable: true, eligibility: "eligible", assetName: "Fixture.zip",
      downloadUrl: "https://example.invalid/Fixture.zip", checksumUrl: "https://example.invalid/Fixture.sha256" }]
  } }));
  await page.request.post(`${baseUrl}/__test/scenario`, { data: { optimizationRuntime: true, gpuPressureFixture: true, diskUsageMultiRoot: true } });
  await page.setViewportSize({ width: 1100, height: 650 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const results = [];
  const failures = [];
  const primary = id => `.shell-page[data-page-id="${id}"]`;
  const plans = [
    { name: "CPU model", page: "details", tab: "CPU 调度模型", root: ".cpu-topology-panel:not(.cpu-baseline-panel)", ready: ".cpu-core", values: ".cpu-topology-header span,.cpu-topology-meta,.cpu-core" },
    { name: "CPU baseline ratio", page: "details", tab: "CPU 调度模型", root: ".cpu-baseline-panel", ready: ".smart-details-summary", values: ".smart-details-summary" },
    { name: "Monitor dashboard", page: "monitor", numeric: true, root: ".dashboard", ready: ".metric-value", values: ".metric-value" },
    { name: "Monitor resource bars", page: "monitor", root: ".resource-breakdown-panel", ready: ".resource-breakdown-header", values: ".resource-breakdown-header" },
    { name: "Monitor software table", page: "monitor", root: ".resource-table-panel", ready: ".resource-table-row[data-resource-row-id]", values: ".resource-table-row[data-resource-row-id]" },
    { name: "Monitor process table", page: "monitor", mode: "进程级", root: ".resource-table-panel", ready: ".resource-table-row[data-resource-row-id]", values: ".resource-table-row[data-resource-row-id]" },
    { name: "Monitor performance table", page: "monitor", mode: "性能", root: ".resource-table-panel", ready: ".resource-performance-summary-grid", values: ".resource-performance-summary" },
    { name: "Control", page: "control", root: ".control-page", ready: ".control-object", values: ".control-object header,.control-readings,.control-ownership-choice" },
    { name: "Disk usage", page: "diskUsage", root: ".disk-usage-page", ready: ".disk-usage-result .panel-header", values: ".disk-usage-volumes,.disk-usage-result" }
  ];
  // Enumerate delivered tabs/sections, rather than maintaining a smaller subset.
  for (const id of ["details", "components"]) {
    await page.locator(primary(id)).click();
    await settle();
    const tabs = await page.locator(`#pageSubnav .page-subnav-group[data-page-id="${id}"] [role="tab"]`).evaluateAll(nodes => nodes.map(node => ({ id: node.id, label: node.textContent.trim() })));
    for (const tab of tabs) {
      if (id === "details" && tab.label === "CPU 调度模型") continue;
      const isBrowserRuntime = tab.id.includes("BrowserRuntime");
      plans.push(id === "details"
        ? { name: `Details / ${tab.label}`, page: id, tabId: tab.id, root: ".details-page", ready: tab.label.includes("GPU") ? ".gpu-position" : tab.label.includes("设备") ? ".device-topology-layout" : tab.label.includes("定向") ? ".targeted-report-list-row,.targeted-report-history .content-state.empty" : tab.label.includes("内核") ? ".kernel-leak-summary" : ".smart-details-summary", values: tab.label.includes("GPU") ? ".gpu-position" : tab.label.includes("设备") ? ".device-topology-identity,.device-topology-layout" : tab.label.includes("定向") ? ".targeted-report-history" : tab.label.includes("内核") ? ".kernel-leak-summary,.kernel-leak-observation,.kernel-leak-report .content-state" : ".smart-details-summary,.smart-details-record" }
        : { name: `Components and software / ${tab.label}`, page: id, tabId: tab.id, root: isBrowserRuntime ? "#browserRuntimePage" : "#componentsPage", ready: isBrowserRuntime ? ".browser-runtime-row,.browser-runtime-empty" : "#managementCount", values: isBrowserRuntime ? ".browser-runtime-row,.browser-runtime-empty" : "#managementCount,.management-list" });
    }
  }
  await page.locator(primary("settings")).click();
  await settle();
  const sections = await page.locator("#pageStage .settings-nav button").allTextContents();
  for (const section of sections) plans.push({ name: `Settings / ${section.trim()}`, page: "settings", section: section.trim(), root: ".settings-page", ready: ".settings-readonly-boundary", values: ".settings-readonly-boundary" });

  try {
    for (const plan of plans) {
      let releaseReads = () => {};
      let held = false;
      try {
        await page.locator(primary(plan.page)).click();
        if (plan.tab) await page.getByRole("tab", { name: plan.tab, exact: true }).click();
        if (plan.tabId) await page.locator(`#${plan.tabId}`).click();
        if (plan.section) await page.locator("#pageStage .settings-nav").getByRole("button", { name: plan.section, exact: true }).click();
        if (plan.mode) await page.locator("#pageStage .resource-table-mode-switch").getByRole("radio", { name: plan.mode, exact: true }).click();
        const rootSelector = `#pageStage ${plan.root}`;
        await page.locator(rootSelector).waitFor();
        await page.locator(rootSelector).evaluate(node => node.scrollIntoView({ block: "start", behavior: "instant" }));
        await page.waitForFunction(({ root, ready, numeric }) => {
          const node = document.querySelector(root);
          return node && node.querySelector(ready)
            && (!numeric || [...node.querySelectorAll(ready)].some(n => /\d/.test(n.textContent)))
            && ![...node.querySelectorAll("#managementCount")].some(n => /--/.test(n.textContent));
        }, { root: rootSelector, ready: plan.ready, numeric: plan.numeric }, { timeout: 15_000 });
        await settle();
        await page.evaluate(() => window.__freezeRetentionFrames(true));
        await settle();
        let gate = new Promise(resolve => { releaseReads = resolve; });
        const holdReads = async route => {
          if (route.request().method() === "GET") await gate;
          try { await route.fallback(); }
          catch (error) {
            // Demand withdrawal may cancel an intercepted GET while it is gated.
            if (!/Route is already handled|Target.*closed|interception.*cancel/i.test(String(error))) throw error;
          }
        };
        await page.route("**/api/**", holdReads);
        held = true;
        const options = { ...plan, cooldownMs: ResourceManager.FrontendPoints.VisibilityCooldownMs, rootSelector, away: primary(plan.page === "monitor" ? "components" : "monitor"), primary: primary(plan.page) };
        const before = await page.evaluate(readSurface, options);
        // Restored geometry is deliberately set before hiding; page navigation
        // owns its own scroll policy, host visibility must never reset it.
        await page.evaluate(({ rootSelector, page: pageId }) => {
          const root = document.querySelector(rootSelector);
          if (pageId === "control") {
            root.querySelector(".control-toolbar [aria-expanded]")?.click();
            root.querySelectorAll('.control-fan-modes [role="radio"]')[2]?.click();
          }
          const input = root.querySelector('input[type="text"]:not([disabled]),input:not([type]):not([disabled]),textarea:not([disabled])');
          if (input) { input.value = "retention draft"; input.dispatchEvent(new Event("input", { bubbles: true })); }
          window.__retentionFormNodes = [...root.querySelectorAll("input,textarea,select,details")];
          const details = root.querySelector("details"); if (details) details.open = true;
          const shell = document.querySelector(".app-shell");
          shell.scrollTop = Math.min(240, shell.scrollHeight - shell.clientHeight);
        }, options);
        const localBefore = await page.evaluate(readSurface, options);
        const flicker = plan.page === "control" ? await page.evaluate(async options => {
          window.__sendVisibility(false);
          await new Promise(resolve => setTimeout(resolve, 1000));
          const during = window.__retentionRead(options);
          window.__sendVisibility(true);
          return new Promise(resolve => requestAnimationFrame(() => resolve({ during, after: window.__retentionRead(options),
            sameForms: window.__retentionFormNodes.every((node, index) => node === document.querySelector(options.rootSelector)?.querySelectorAll("input,textarea,select,details")[index])
          })));
        }, options) : null;
        if (flicker) {
          assert.equal(flicker.during.controlStops, localBefore.controlStops, "1s overlay does not stop visible work");
          assert.equal(flicker.after.controlStarts, localBefore.controlStarts, "1s overlay does not restart work");
          assert.equal(flicker.sameForms, true, "1s overlay keeps the editor DOM");
          assert.deepEqual(flicker.after.forms, localBefore.forms);
          assert.equal(flicker.after.scroll, localBefore.scroll);
        }
        const hidden = await page.evaluate(async options => {
          const root = document.querySelector(options.rootSelector);
          const data = root?.querySelector(options.values);
          window.dispatchEvent(new Event("blur"));
          window.__sendVisibility(false);
          await new Promise(resolve => setTimeout(resolve, options.cooldownMs + 100));
          const whileHidden = window.__retentionRead(options);
          window.__sendVisibility(true);
          window.dispatchEvent(new Event("focus"));
          return new Promise(resolve => requestAnimationFrame(() => resolve({
            ...window.__retentionRead(options), whileHidden,
            sameRoot: root === document.querySelector(options.rootSelector),
            sameData: data === document.querySelector(options.rootSelector)?.querySelector(options.values)
          })));
        }, options);
        if (plan.page === "control") assert.ok(hidden.whileHidden.controlStops > localBefore.controlStops,
          "visible control sampling stops after the declared cooldown");
        const visibility = hidden.deliveredChunks === localBefore.deliveredChunks && localBefore.values.length > 0 && hidden.sameRoot && hidden.sameData
          && JSON.stringify(hidden.values) === JSON.stringify(localBefore.values)
          && JSON.stringify(hidden.forms) === JSON.stringify(localBefore.forms)
          && hidden.scroll === localBefore.scroll && JSON.stringify(hidden.selected) === JSON.stringify(localBefore.selected)
          && JSON.stringify(hidden.whileHidden.values) === JSON.stringify(localBefore.values);
        // Complete delayed refreshes too: preserving only the instant before their
        // responses would miss the snipping-overlay card/scroll reconstruction.
        releaseReads();
        await settle();
        const refreshed = await page.evaluate(options => ({ ...window.__retentionRead(options),
          sameForms: window.__retentionFormNodes.every((node, index) => node === document.querySelector(options.rootSelector)?.querySelectorAll("input,textarea,select,details")[index])
        }), options);
        const refreshedView = refreshed.sameForms && refreshed.scroll === localBefore.scroll
          && JSON.stringify(refreshed.forms) === JSON.stringify(localBefore.forms);
        gate = new Promise(resolve => { releaseReads = resolve; });
        const returned = await page.evaluate(async options => {
          document.querySelector(options.away).click();
          document.querySelector(options.primary).click();
          // A page keeps its selected secondary tab across a page switch; read it before re-selecting.
          const landed = document.querySelector(`#pageSubnav .page-subnav-group[data-page-id="${options.page}"] [role="tab"][aria-selected="true"]`);
          const landedTab = landed ? (options.tabId ? landed.id : landed.textContent.trim()) : null;
          if (options.tabId) document.getElementById(options.tabId).click();
          if (options.tab) [...document.querySelectorAll('#pageSubnav [role="tab"]')].find(n => n.textContent.trim() === options.tab).click();
          if (options.section) [...document.querySelectorAll('#pageStage .settings-nav button')].find(n => n.textContent.trim() === options.section).click();
          if (options.mode) [...document.querySelectorAll('#pageStage .resource-table-mode-switch [role="radio"]')].find(n => n.textContent.trim() === options.mode).click();
          return new Promise(resolve => requestAnimationFrame(() => resolve({ ...window.__retentionRead(options), landedTab })));
        }, options);
        if (plan.tab || plan.tabId) assert.equal(returned.landedTab, plan.tabId ?? plan.tab, `${plan.name}: returning to the page keeps its selected tab`);
        const reentry = sameCurrentValues(before, returned) && returned.deliveredChunks === before.deliveredChunks;
        const secondaryReturn = (plan.tab || plan.tabId || plan.section) ? await page.evaluate(async options => {
          const buttons = options.section
            ? [...document.querySelectorAll("#pageStage .settings-nav button")]
            : [...document.querySelectorAll(`#pageSubnav .page-subnav-group[data-page-id="${options.page}"] [role="tab"]`)];
          const target = buttons.find(node => options.tabId ? node.id === options.tabId : node.textContent.trim() === (options.tab ?? options.section));
          buttons.find(node => node !== target && !node.disabled).click();
          target.click();
          return new Promise(resolve => requestAnimationFrame(() => resolve(window.__retentionRead(options))));
        }, options) : null;
        const tabReentry = secondaryReturn === null || (sameCurrentValues(before, secondaryReturn)
          && secondaryReturn.deliveredChunks === before.deliveredChunks);
        const result = { page: plan.name, reentry, tabReentry, visibility: visibility && refreshedView, before, returned, secondaryReturn, localBefore, flicker, hidden, refreshed };
        results.push(result);
        console.log("PAGE_RETENTION", JSON.stringify(result));
        if (!reentry || !tabReentry || !visibility || !refreshedView) failures.push({ page: plan.name, reentry, tabReentry, visibility, refreshedView });
        await page.evaluate(() => window.__freezeRetentionFrames(false));
        releaseReads(); held = false;
        await page.unroute("**/api/**", holdReads);
        await settle();
      } catch (error) {
        failures.push({ page: plan.name, preparation: String(error), text: (await page.locator("#pageStage").innerText()).slice(0, 5000) });
        console.log("PAGE_RETENTION_PREPARATION", plan.name, String(error));
      } finally {
        releaseReads();
        await page.evaluate(() => { window.__sendVisibility(true); window.__freezeRetentionFrames(false); });
        if (held) await page.unroute("**/api/**");
      }
    }
  } finally {
    if (process.env.RM_RETENTION_EVIDENCE_DIR) await writeFile(join(process.env.RM_RETENTION_EVIDENCE_DIR, "page-retention.json"), JSON.stringify({ results, failures, errors }, null, 2));
  }
  assert.deepEqual(errors, [], "page errors");
  assert.deepEqual(failures, [], "Every backend surface retains its first frame and hidden view state");
  return { sweep: results.map(({ page, reentry, tabReentry, visibility }) => ({ page, reentry, tabReentry, visibility })) };

  async function settle() {
    await page.waitForFunction(() => window.__retentionReads === 0, null, { timeout: 10_000 });
    await waitForContentSettled(page);
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  }
}

function readSurface(options) {
  const read = options => {
    const root = document.querySelector(options.rootSelector);
    const nodes = root ? [...root.querySelectorAll(options.values)] : [];
    const text = node => node.textContent.replace(/\s+/g, " ").trim();
    let opacity = 1;
    let ancestor = root;
    let depth = 0;
    while (ancestor && depth++ < 32) {
      opacity *= Number(getComputedStyle(ancestor).opacity);
      ancestor = ancestor.parentElement;
    }
    return {
      paint: { opacity, width: root?.getBoundingClientRect().width ?? 0, height: root?.getBoundingClientRect().height ?? 0 },
      values: nodes.map(text),
      rows: nodes.every(n => n.hasAttribute("data-resource-row-id"))
        ? Object.fromEntries(nodes.map(n => [n.getAttribute("data-resource-row-id"), text(n)])) : null,
      forms: root ? [...root.querySelectorAll("input,textarea,select,details")].map(n => ({ label: n.getAttribute("aria-label") ?? n.name, value: n.value, checked: n.checked, open: n.open })) : [],
      deliveredChunks: window.__retentionDeliveredChunks,
      controlStarts: window.__retentionControlStarts,
      controlStops: window.__retentionControlStops,
      scroll: document.querySelector(".app-shell").scrollTop,
      selected: [...document.querySelectorAll('#pageSubnav [aria-selected="true"],#pageStage .settings-nav [aria-current="page"]')].map(text)
    };
  };
  window.__retentionRead = read;
  return read(options);
}

function sameCurrentValues(before, after) {
  if (!after.values.length) return false;
  // Virtual row pools may have different overscan sizes after navigation.
  // Every rendered identity must still expose its accepted value immediately.
  if (before.rows && after.rows) return Object.entries(after.rows)
    .every(([id, value]) => before.rows[id] === value);
  return JSON.stringify(before.values) === JSON.stringify(after.values);
}

import assert from "node:assert/strict";
import { componentFixture } from "./componentFixtures.js";

export default async function managementSourceStatesJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  const modes = { components: "ready", software: "ready", "browser-runtimes": "ready" };
  const counts = { components: 0, software: 0, "browser-runtimes": 0 };
  const software = { id: "manual:states", name: "Source state fixture", kind: "other", state: "manual", sourceFacts: [{ kind: "manual" }],
    registrationFacts: { kind: "manual", processCount: 0, serviceCount: 0 }, requiresRootPathConfirmation: false, identityConfirmed: true, rootPaths: [],
    operations: { canUninstall: false, uninstallKind: "None", uninstallConstraint: "manual_registration" } };
  const runtime = { id: "fixture-browser", name: "Source runtime fixture", kind: "chromium_browser", version: "1", executablePath: "browser.exe",
    runtimeDirectory: "fixture", source: "known_install", nativeWebView2Compatible: false, selected: true };
  for (const name of Object.keys(modes)) {
    await page.route(url => url.pathname === "/api/" + name, route => {
      counts[name]++;
      if (modes[name] === "error") return route.fulfill({ status: 503, json: { error: "fixture inventory unavailable" } });
      const empty = modes[name] === "empty";
      const value = name === "components" ? (empty ? [] : [componentFixture()])
        : name === "software" ? (empty ? [] : [software])
        : { sharedRuntime: null, browserFallback: empty ? null : runtime, candidates: empty ? [] : [runtime], installComponentId: "shared-webview2-runtime", capturedAt: new Date().toISOString() };
      return route.fulfill({ json: value });
    });
  }
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const nav = page.getByRole("navigation", { name: "页面切换", exact: true });
  const enter = async tab => {
    await nav.getByRole("button", { name: "组件与软件", exact: true }).click();
    await page.locator("#managementTab-" + tab).click();
  };
  const leave = () => nav.getByRole("button", { name: "监视控制台", exact: true }).click();
  for (const [name, tab, pageId, item] of [
    ["components", "Dependency", "componentsPage", ".component-item"],
    ["software", "other", "componentsPage", ".software-item"],
    ["browser-runtimes", "BrowserRuntime", "browserRuntimePage", ".browser-runtime-list .browser-runtime-item"]
  ]) {
    await enter(tab);
    const panel = page.locator("#" + pageId);
    if (name === "browser-runtimes") await panel.getByText("Source runtime fixture", { exact: true }).first().waitFor();
    else await panel.locator(item).first().waitFor();
    await leave(); modes[name] = "error";
    await enter(tab); await panel.locator(".observation-state.error").first().waitFor();
    if (name === "browser-runtimes") assert.equal(await panel.getByText("Source runtime fixture", { exact: true }).count(), 0);
    else {
      assert.equal(await panel.locator(item).count(), 0, "failure replaces previously rendered inventory");
      assert.equal(await panel.locator("#managementCount").innerText(), "--");
      assert.match(await page.locator("#managementTab-" + tab).innerText(), /--/);
    }
    const before = counts[name];
    modes[name] = "empty";
    await fetch(baseUrl + "/__test/management-inventory-revisions/publish", {
      method: "POST", headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ [name === "components" ? "componentRevision" : name === "software" ? "softwareRevision" : "browserRuntimeRevision"]: "1" })
    });
    await page.waitForFunction(id => document.querySelectorAll("#" + id + " .observation-state.error").length === 0, pageId);
    assert.ok(counts[name] > before, "error keeps demand mounted for revision recovery");
    if (name === "browser-runtimes") await panel.locator(".browser-runtime-empty").first().waitFor();
    else assert.match(await panel.locator("#managementCount").innerText(), /0/);
    await leave(); modes[name] = "ready";
  }
  // The global usability probe measures the settled destination, after its leaving layer is gone.
  await page.waitForFunction(() => document.querySelectorAll(".page-leaving-layer > .content-transition-leaving").length === 0
    && getComputedStyle(document.querySelector("#pageStage")).transform === "none");
  assert.deepEqual(errors, []);
  return { counts, failureReplaced: true, emptyAccepted: true, revisionRecoveredWhileError: true };
}

import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { waitForCondition, waitForNextPaint } from "./journeyWaits.js";

// 用户点「刷新」会刷新便携登记；它还没返回时删除一条手动登记，删除后的普通读取会并入同一次刷新。
// 便携刷新失败只能落到刷新本身，删除成功就要报成功，不能带上别人的失败。
export default async function managementSharedRefreshJourney(page, baseUrl) {
  const assert = (value, message) => { if (!value) throw new Error(message); };
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ runtimeEffectOwners: true })
  });
  assert(response.ok, "Could not configure management scenario");
  await page.route("**/api/components", route => route.fulfill({ json: [] }));
  await page.route("**/api/browser-runtimes*", route => route.fulfill({ json: {
    sharedRuntime: null, browserFallback: null, candidates: [], installComponentId: "shared-webview2-runtime",
    capturedAt: new Date().toISOString()
  } }));
  const record = { id: "manual:shared-refresh", name: "Shared refresh fixture", kind: "other", state: "manual",
    sourceFacts: [{ kind: "manual" }], registrationFacts: { kind: "manual", processCount: 0, serviceCount: 0 },
    requiresRootPathConfirmation: false, identityConfirmed: true, rootPaths: [],
    operations: { canUninstall: false, uninstallKind: "None", uninstallConstraint: "manual_registration" } };
  await page.addInitScript(pathname => {
    window.__manualDeleteBodyConsumed = 0;
    const originalFetch = window.fetch;
    window.fetch = function(input, init) {
      const response = originalFetch.apply(this, arguments);
      const url = new URL(input instanceof Request ? input.url : String(input), location.href);
      const method = init?.method ?? (input instanceof Request ? input.method : "GET");
      if (url.pathname !== pathname || method.toUpperCase() !== "DELETE") return response;
      return response.then(value => {
        // Observe the application's actual text() promise; do not read a clone or
        // consume an extra body. httpTransport.requestJson uses this exact method.
        const text = value.text.bind(value);
        value.text = (...args) => text(...args).then(body => {
          window.__manualDeleteBodyConsumed++;
          return body;
        });
        return value;
      });
    };
  }, `/api/software/manual/${encodeURIComponent(record.id)}`);
  let releasePortable;
  const portableGate = new Promise(resolve => { releasePortable = resolve; });
  let portableReads = 0;
  await page.route(url => url.pathname === "/api/software", async route => {
    if (new URL(route.request().url()).searchParams.get("refresh") === "true") {
      portableReads++;
      await portableGate;
      return route.fulfill({ status: 503, json: { error: "fixture portable refresh failure" } });
    }
    return route.fulfill({ json: [record] });
  });
  await page.route("**/api/software/manual/*", route => route.fulfill({ status: 204 }));

  const app = await loadAppCopy("zh-CN");
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: app.page.components, exact: true }).click();
  await page.locator("#managementTab-other").click();
  const card = page.locator(".software-item").filter({ hasText: record.name });
  await card.getByRole("button", { name: app.managementFeedback.removeManual, exact: true }).waitFor();
  const refresh = page.locator("#componentsPage").getByRole("button", { name: app.management.refresh, exact: true });
  await refresh.waitFor();
  await page.waitForFunction(() => {
    const button = [...document.querySelectorAll("#componentsPage button.panel-refresh-button")].at(-1);
    return button && !button.disabled;
  });
  const refreshStarted = page.waitForRequest(request => {
    const url = new URL(request.url());
    return url.pathname === "/api/software" && url.searchParams.get("refresh") === "true";
  }, { timeout: 5_000 });
  await refresh.click();
  await refreshStarted;
  await waitForCondition(page, async () => portableReads === 1,
    { timeoutMs: 5_000, description: "portable refresh route entered" });
  assert(portableReads === 1, "The user refresh did not request the portable registration refresh");

  await card.getByRole("button", { name: app.managementFeedback.removeManual, exact: true }).click();
  const confirm = page.getByRole("dialog").filter({ hasText: app.managementFeedback.confirmManual });
  const deleted = page.waitForResponse(response => new URL(response.url()).pathname.startsWith("/api/software/manual/"));
  const deleteFinished = page.waitForEvent("requestfinished", {
    predicate: request => request.method() === "DELETE"
      && new URL(request.url()).pathname === `/api/software/manual/${encodeURIComponent(record.id)}`,
    timeout: 5_000
  });
  await confirm.getByRole("button", { name: app.feedback.confirm, exact: true }).click();
  await deleted;
  await deleteFinished;
  await waitForCondition(page, async () => page.evaluate(() => window.__manualDeleteBodyConsumed === 1),
    { timeoutMs: 5_000, description: "manual DELETE body consumed by application" });
  // Body completion resumes requestJson -> requestNoContent -> removeManualSoftware
  // -> removeManualRegistration through promise microtasks. Its next instruction
  // calls refreshSoftware, whose existing-promise branch synchronously joins the
  // held refresh. The microtask checkpoint completes before these paint events;
  // no timer or later task exists between body consumption and the shared join.
  await waitForNextPaint(page, { timeoutMs: 5_000 });
  releasePortable();

  await page.locator(".toast.success").filter({ hasText: app.managementFeedback.manualRemoved }).first().waitFor();
  await page.locator(".toast.error").filter({ hasText: app.stores.refreshFailed }).first().waitFor();
  const wrongFailures = await page.locator(".toast.error").filter({ hasText: app.managementFeedback.manualRemoveFailed }).count();
  assert(wrongFailures === 0, "The removal reported the concurrent portable refresh failure as its own");
  assert(errors.length === 0, errors.join("\n"));
  return { portableReads, removalReportedSuccess: true, refreshFailureReported: true, pageErrors: errors };
}

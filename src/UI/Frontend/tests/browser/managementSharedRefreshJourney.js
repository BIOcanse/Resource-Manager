import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";

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
  await refresh.click();
  const deadline = Date.now() + 5000;
  while (portableReads === 0 && Date.now() < deadline) await page.waitForTimeout(50);
  assert(portableReads === 1, "The user refresh did not request the portable registration refresh");

  await card.getByRole("button", { name: app.managementFeedback.removeManual, exact: true }).click();
  const confirm = page.getByRole("dialog").filter({ hasText: app.managementFeedback.confirmManual });
  const deleted = page.waitForResponse(response => new URL(response.url()).pathname.startsWith("/api/software/manual/"));
  await confirm.getByRole("button", { name: app.feedback.confirm, exact: true }).click();
  await deleted;
  // 删除后的读取要先并入进行中的刷新，再放行便携刷新的失败。
  await page.waitForTimeout(300);
  releasePortable();

  await page.locator(".toast.success").filter({ hasText: app.managementFeedback.manualRemoved }).first().waitFor();
  await page.locator(".toast.error").filter({ hasText: app.stores.refreshFailed }).first().waitFor();
  const wrongFailures = await page.locator(".toast.error").filter({ hasText: app.managementFeedback.manualRemoveFailed }).count();
  assert(wrongFailures === 0, "The removal reported the concurrent portable refresh failure as its own");
  assert(errors.length === 0, errors.join("\n"));
  return { portableReads, removalReportedSuccess: true, refreshFailureReported: true, pageErrors: errors };
}

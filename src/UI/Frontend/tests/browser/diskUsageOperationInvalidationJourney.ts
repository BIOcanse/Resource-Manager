import type { Page } from "playwright-core";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { formatBytes } from "../../src/presentation/byteUnits.ts";

export default async function diskUsageOperationInvalidationJourney(page: Page, baseUrl: string) {
  const failures: string[] = [];
  const treeReads: string[] = [];
  page.on("request", request => {
    const url = new URL(request.url());
    if (url.pathname === "/api/disk-usage/tree") treeReads.push(url.search);
    if (url.pathname === "/api/disk-usage/layout") failures.push("Requested a server treemap layout");
  });
  page.on("pageerror", error => failures.push(`pageerror: ${String(error)}`));
  page.on("console", message => {
    if (message.type() === "error") failures.push(`console: ${message.text()}`);
  });
  const assert = (condition: unknown, message: string) => {
    if (!condition) throw new Error(message);
  };
  const patchScenario = async (value: Record<string, unknown>) => {
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(value)
    });
    assert(response.ok, `Unable to update the disk scan fixture: ${response.status}`);
  };
  const waitForRootSize = (value: string) => page.waitForFunction(expected => {
    const header = document.querySelector<HTMLElement>(".disk-usage-result .optimization-heading > span")!;
    return header?.textContent?.includes(expected) === true;
  }, value, { timeout: 7000 });

  const app = await loadAppCopy("zh-CN");
  await patchScenario({
    diskUsageMultiRoot: true,
    diskUsageScanLifecycle: true,
    diskUsageScanCompletionDelayMs: 3000
  });
  await page.setViewportSize({ width: 1200, height: 900 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  const navigation = page.locator(".shell-pages");
  await navigation.getByRole("button", { name: app.page.diskUsage, exact: true }).click();

  const initialSize = formatBytes(18_000_000, "storage", "native");
  const refreshedSize = formatBytes(19_000_000, "storage", "native");
  await waitForRootSize(initialSize);
  const openedTreeReads = treeReads.length;
  const scanResponse = page.waitForResponse(response =>
    response.request().method() === "POST"
      && new URL(response.url()).pathname === "/api/disk-usage/scan");
  await page.locator(".disk-usage-setup-actions button").last().click();
  const submitted = await scanResponse;
  assert(submitted.ok(), `The scan was not accepted: ${submitted.status()}`);
  await page.getByRole("button", { name: app.diskUsage.scanning, exact: true })
    .waitFor({ state: "visible", timeout: 3000 });

  await navigation.getByRole("button", { name: app.page.monitor, exact: true }).click();
  await navigation.getByRole("button", { name: app.page.diskUsage, exact: true }).click();
  await waitForRootSize(initialSize);
  await page.getByRole("button", { name: app.diskUsage.scanning, exact: true })
    .waitFor({ state: "visible", timeout: 2000 });
  assert(await navigation.getByRole("button", { name: app.page.diskUsage, exact: true })
    .getAttribute("aria-current") === "page",
  "The disk page was not mounted while the scan was still running");
  assert(treeReads.length === openedTreeReads, "Remounting rebuilt accepted scan facts");
  await waitForRootSize(refreshedSize);
  assert(treeReads.length === openedTreeReads + 1, "Scan invalidation did not issue exactly one new fact read");
  assert(treeReads.every(query => {
    const parameters = new URLSearchParams(query);
    return parameters.get("rootIndex") === "0" && parameters.get("maxNodes") === "400000"
      && parameters.get("maxBytes") === "33554432"
      && [...parameters.keys()].every(key => ["rootIndex", "maxNodes", "maxBytes"].includes(key));
  }), `Unexpected scan-tree queries: ${JSON.stringify(treeReads)}`);
  assert(failures.length === 0, `Browser failures: ${JSON.stringify(failures)}`);

  return {
    scanAccepted: true,
    pageWasLeftBeforeCompletion: true,
    reopenedRootSize: refreshedSize,
    treeReads,
    pageErrors: failures
  };
}

import { waitForContentSettled } from "./contentSettled.js";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";

export default async function diskUsageReadRecoveryJourney(page, baseUrl) {
  const assert = (value, message) => { if (!value) throw new Error(message); };
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  const patch = async value => {
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(value)
    });
    assert(response.ok, "Scenario update failed");
  };
  await page.setViewportSize({ width: 1200, height: 950 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  let language = "zh-CN";
  const checked = [];
  let pendingResultHeight = 0;
  for (const option of languageOptions.filter(option => option.id !== "system")) {
    if (language !== option.id) {
      const oldApp = await loadAppCopy(language);
      const settings = await loadSettingsText(language);
      await page.locator(".shell-pages").getByRole("button", { name: oldApp.page.settings, exact: true }).click();
      await page.locator(".settings-nav").getByRole("button", { name: settings.sections.appearance, exact: true }).click();
      await page.getByRole("button", { name: settings.appearance.languageSelectLabel, exact: true }).click();
      await page.getByRole("option", { name: `${option.nativeLabel} · ${option.label}`, exact: true }).click();
      await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
      await page.waitForFunction(expected => document.documentElement.lang === expected, option.id);
      language = option.id;
    }
    const copy = await loadAppCopy(language);
    const navigation = page.locator(".shell-pages");
    await patch({ diskUsageVolumesFailure: true, diskUsageResultFailure: true, diskUsageMultiRoot: true });
    let releaseInitialReads;
    const initialReadPattern = /\/api\/disk-usage\/(?:volumes|summary|tree)(?:\?|$)/;
    if (checked.length === 0) {
      const gate = new Promise(resolve => { releaseInitialReads = resolve; });
      await page.route(initialReadPattern, async route => { await gate; await route.continue(); });
    }
    await navigation.getByRole("button", { name: copy.page.diskUsage, exact: true }).click();
    const workspace = page.locator(".disk-usage-page");
    if (releaseInitialReads) {
      try {
        await workspace.locator(".disk-usage-result .content-state.waiting").waitFor({ state: "visible" });
        await workspace.locator(".disk-usage-volumes .content-state.waiting").waitFor({ state: "visible" });
        pendingResultHeight = await workspace.locator(".disk-usage-result").evaluate(node => node.getBoundingClientRect().height);
        assert(await workspace.getByText(copy.diskUsage.emptyTitle, { exact: true }).count() === 0, "Unread disk result cannot show the accepted-empty prompt");
        assert(await workspace.getByText(copy.diskUsage.noVolumes, { exact: true }).count() === 0, "Unread volume list cannot claim zero volumes");
      } finally {
        releaseInitialReads();
        await page.unrouteAll({ behavior: "wait" });
      }
    }
    const volumes = workspace.locator(".disk-usage-volumes");
    const resultError = workspace.locator(".disk-usage-read-error");
    await volumes.getByText(copy.diskUsage.volumesReadFailed, { exact: true }).waitFor();
    await resultError.getByText(copy.diskUsage.resultReadFailed, { exact: true }).waitFor();
    assert(await workspace.getByText(copy.diskUsage.noVolumes, { exact: true }).count() === 0, `${language}: failed read looked empty`);
    assert(await workspace.getByText(copy.diskUsage.retainedResultStale, { exact: true }).count() === 0, `${language}: no result was retained`);

    await patch({ diskUsageVolumesFailure: false, diskUsageResultFailure: false, diskUsageVolumesEmpty: true, diskUsageMultiRoot: false });
    await volumes.getByRole("button", { name: copy.diskUsage.retryRead, exact: true }).click();
    await resultError.getByRole("button", { name: copy.diskUsage.retryRead, exact: true }).click();
    await workspace.getByText(copy.diskUsage.noVolumes, { exact: true }).waitFor();
    await resultError.waitFor({ state: "hidden" });
    assert(await workspace.getByRole("alert").count() === 0, `${language}: successful empty read retained an error`);
    if (checked.length === 0) {
      const emptyHeight = await workspace.locator(".disk-usage-result").evaluate(node => node.getBoundingClientRect().height);
      assert(Math.abs(emptyHeight - pendingResultHeight) <= 1, `Disk pending/accepted-empty height changed: ${pendingResultHeight} -> ${emptyHeight}`);
    }

    await patch({ diskUsageVolumesEmpty: false, diskUsageMultiRoot: true });
    await navigation.getByRole("button", { name: copy.page.monitor, exact: true }).click();
    await navigation.getByRole("button", { name: copy.page.diskUsage, exact: true }).click();
    await waitForContentSettled(page);
    await workspace.locator(".disk-usage-result").waitFor();
    await workspace.locator(".disk-usage-volume").first().waitFor();
    await patch({ diskUsageVolumesFailure: true, diskUsageResultFailure: true });
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    // A completed scan invalidates authoritative facts; reset/zoom only lays out local facts.
    await patch({ diskUsageScanLifecycle: true, diskUsageScanCompletionDelayMs: 200 });
    await workspace.locator(".disk-usage-setup-actions button").last().click();
    await resultError.getByText(copy.diskUsage.retainedResultStale, { exact: true }).waitFor();
    await workspace.getByRole("button", { name: copy.diskUsage.resetView, exact: true }).click();
    await volumes.getByText(copy.diskUsage.retainedVolumesStale, { exact: true }).waitFor();
    await resultError.getByText(copy.diskUsage.retainedResultStale, { exact: true }).waitFor();
    assert(await workspace.locator(".disk-usage-result").count() === 1, `${language}: failed zoom discarded retained result`);
    assert(await workspace.locator(".disk-usage-volume").count() > 0, `${language}: failed volume read discarded retained list`);
    assert(await workspace.getByText(copy.diskUsage.scanFailed, { exact: true }).count() === 0, "Successful scan was reported as failed");
    await patch({ diskUsageVolumesFailure: false, diskUsageResultFailure: false });
    // Restoring the service and laying out retained facts must not heal the failed scan read.
    const treeReads = [];
    const trackTreeRead = request => {
      if (new URL(request.url()).pathname === "/api/disk-usage/tree") treeReads.push(request.url());
    };
    page.on("request", trackTreeRead);
    await workspace.getByRole("button", { name: copy.diskUsage.resetView, exact: true }).click();
    page.off("request", trackTreeRead);
    assert(treeReads.length === 0, "Reset retried a scan-tree read without explicit retry");
    await resultError.getByText(copy.diskUsage.retainedResultStale, { exact: true }).waitFor();
    await volumes.getByRole("button", { name: copy.diskUsage.retryRead, exact: true }).click();
    await resultError.getByRole("button", { name: copy.diskUsage.retryRead, exact: true }).click();
    await resultError.waitFor({ state: "hidden" });
    await volumes.getByRole("alert").waitFor({ state: "hidden" });
    checked.push(language);
  }
  assert(errors.length === 0, errors.join("\n"));
  return { languages: checked, pendingResultHeight, firstReadPending: true, failedReadDistinctFromEmpty: true, retainedDataStale: true, retry: true, postScanReadFailure: true };
}

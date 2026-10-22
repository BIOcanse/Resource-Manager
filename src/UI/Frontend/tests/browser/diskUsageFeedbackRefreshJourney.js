import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { formatBytes } from "../../src/presentation/byteUnits.ts";

export default async function diskUsageFeedbackRefreshJourney(page, baseUrl) {
  const failures = [];
  const settingsPatchBodies = [];
  const scanResponses = [];
  const rootSelections = [];
  page.on("pageerror", error => failures.push(`pageerror: ${String(error)}`));
  page.on("console", message => {
    const expectedScanFailure = message.location().url.endsWith("/api/disk-usage/scan")
      && message.text().includes("503");
    if (message.type() === "error" && !expectedScanFailure) failures.push(`console: ${message.text()}`);
  });
  page.on("request", request => {
    if (request.method() === "PATCH"
      && new URL(request.url()).pathname === "/api/settings/app") {
      settingsPatchBodies.push(request.postDataJSON());
    }
  });
  page.on("response", response => {
    if (response.request().method() === "POST"
      && new URL(response.url()).pathname === "/api/disk-usage/scan") {
      scanResponses.push(response.status());
    }
  });

  const assert = (condition, message) => {
    if (!condition) throw new Error(message);
  };
  const patchScenario = async value => {
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(value)
    });
    assert(response.ok, `Unable to update the browser fixture: ${response.status}`);
  };
  const readStoredMonitorInterval = () => page.evaluate(async () => {
    const response = await fetch("/api/settings/app");
    const result = await response.json();
    return result.settings.performance.monitorRefreshIntervalMs.customValue;
  });
  const settingsInput = (settings) => page.getByRole("textbox", {
    name: `${settings.performance.refreshCadenceItems.monitor.label} ${settings.performance.refreshCadenceCustomLabel}`,
    exact: true
  });
  const readRequestTelemetry = () => page.evaluate(async () => {
    const response = await fetch("/__test/requests");
    return response.json();
  });
  const diskRootTotals = (app, rootSizeBytes) => app.diskUsage.scanTotals(
    formatBytes(rootSizeBytes, "storage", "native"),
    17,
    5);
  const waitForDiskRoot = (rootPath, totalsText) => page.waitForFunction(
    expected => {
      const title = document.querySelector(".disk-usage-result .optimization-heading h2");
      const totals = document.querySelector(".disk-usage-result .optimization-heading > span");
      return title?.textContent === expected.rootPath
        && totals?.innerText.includes(expected.totalsText) === true;
    },
    { rootPath, totalsText },
    { timeout: 7000 });
  const chooseDiskRoot = async (app, rootPath) => {
    const selector = page.getByRole("combobox", {
      name: app.diskUsage.rootSelectorLabel,
      exact: true
    });
    await selector.click();
    await page.getByRole("option", { name: rootPath, exact: true }).click();
    return selector;
  };

  await patchScenario({
    diskUsageScanFailure: true,
    diskUsageMultiRoot: true,
    performanceMonitorRefreshIntervalMs: 500
  });
  await page.setViewportSize({ width: 1200, height: 950 });
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });

  const zhApp = await loadAppCopy("zh-CN");
  const zhSettings = await loadSettingsText("zh-CN");
  await page.locator(".shell-pages")
    .getByRole("button", { name: zhApp.page.settings, exact: true }).click();
  await page.locator(".settings-nav")
    .getByRole("button", { name: zhSettings.sections.performance, exact: true }).click();

  const interval = settingsInput(zhSettings);
  await interval.waitFor({ state: "visible", timeout: 7000 });
  assert(await interval.inputValue() === "500", "The fixture did not start from the expected stored 500 ms interval");
  await interval.focus();
  await interval.press("Control+A");
  const typedValues = [];
  for (const [digit, expected] of [["2", "2"], ["0", "20"], ["0", "200"], ["0", "2000"]]) {
    await interval.press(digit);
    const actual = await interval.inputValue();
    typedValues.push(actual);
    assert(actual === expected,
      `Custom interval changed while typing: expected ${expected}, received ${actual}`);
    assert(await interval.evaluate(element => element === document.activeElement),
      `Custom interval lost focus while typing ${expected}`);
  }
  assert(settingsPatchBodies.length === 0,
    "Typing into the custom interval submitted a settings patch before commit");
  assert(await readStoredMonitorInterval() === 500,
    "The service value changed before the custom interval was committed and saved");

  const saveSettings = page.getByRole("button", { name: zhSettings.actions.save, exact: true });
  assert(await saveSettings.isDisabled(),
    "Typing into the interval enabled Save before the field draft was committed");
  await interval.press("Enter");
  assert(await interval.inputValue() === "2000", "Enter did not commit the typed interval draft");
  assert(!await saveSettings.isDisabled(),
    "Committing the interval did not update the page draft for its explicit Save action");
  assert(settingsPatchBodies.length === 0,
    "Committing the local interval draft persisted it before the page Save action");
  const saveResponsePromise = page.waitForResponse(response =>
    response.request().method() === "PATCH"
      && new URL(response.url()).pathname === "/api/settings/app");
  await saveSettings.click();
  const saveResponse = await saveResponsePromise;
  assert(saveResponse.ok(), `Saving the interval failed with ${saveResponse.status()}`);
  const intervalPatch = settingsPatchBodies.at(-1)?.changes?.performance?.monitorRefreshIntervalMs;
  assert(intervalPatch?.customValue === 2000,
    `The settings PATCH did not contain the committed 2000 ms interval: ${JSON.stringify(intervalPatch)}`);
  assert(await readStoredMonitorInterval() === 2000,
    "The mock settings service did not persist the 2000 ms interval");

  await page.reload({ waitUntil: "domcontentloaded" });
  await page.locator(".shell-pages")
    .getByRole("button", { name: zhApp.page.settings, exact: true }).click();
  await page.locator(".settings-nav")
    .getByRole("button", { name: zhSettings.sections.performance, exact: true }).click();
  assert(await settingsInput(zhSettings).inputValue() === "2000",
    "The saved 2000 ms interval did not survive a reload");

  const languages = languageOptions.filter(option => option.id !== "system");
  const englishApp = await loadAppCopy("en-US");
  const checkedLanguages = [];
  const checkedRootLanguages = [];
  let currentLanguage = "zh-CN";
  for (const option of languages) {
    if (option.id !== currentLanguage) {
      const oldApp = await loadAppCopy(currentLanguage);
      const oldSettings = await loadSettingsText(currentLanguage);
      await page.locator(".shell-pages")
        .getByRole("button", { name: oldApp.page.settings, exact: true }).click();
      await page.locator(".settings-nav")
        .getByRole("button", { name: oldSettings.sections.appearance, exact: true }).click();
      await page.getByRole("button", { name: oldSettings.appearance.languageSelectLabel, exact: true }).click();
      await page.getByRole("option", {
        name: `${option.nativeLabel} · ${option.label}`,
        exact: true
      }).click();
      const languageSave = page.getByRole("button", { name: oldSettings.actions.save, exact: true });
      await languageSave.click();
      await page.waitForFunction(language => document.documentElement.lang === language, option.id);
      currentLanguage = option.id;
    }

    const app = await loadAppCopy(option.id);
    await page.locator(".shell-pages")
      .getByRole("button", { name: app.page.diskUsage, exact: true }).click();
    const rootSelector = page.getByRole("combobox", {
      name: app.diskUsage.rootSelectorLabel,
      exact: true
    });
    await rootSelector.waitFor({ state: "visible", timeout: 7000 });
    assert(await rootSelector.getAttribute("aria-label") === app.diskUsage.rootSelectorLabel,
      `${option.id}: root selector did not expose the localized accessible name`);
    const cTotals = diskRootTotals(app, 18_000_000);
    const dTotals = diskRootTotals(app, 73_000_000);
    const scanWideTotal = formatBytes(91_000_000, "storage", "native");
    await waitForDiskRoot("C:\\", cTotals);
    const initialHeader = await page.locator(
      ".disk-usage-result .optimization-heading > span").innerText();
    assert(initialHeader.includes(app.diskUsage.scanKind.masterFileTable),
      `${option.id}: the scan method was not localized in the result header`);
    assert(initialHeader.includes(cTotals) && !initialHeader.includes(scanWideTotal),
      `${option.id}: the header did not pair the selected C: root size with scan-wide counts`);
    if (option.id !== "en-US") {
      const englishTotals = englishApp.diskUsage.scanTotals(
        formatBytes(18_000_000, "storage", "native"),
        17,
        5);
      assert(cTotals !== englishTotals,
        `${option.id}: the root summary copy unexpectedly matched the English base copy`);
    }

    if (option.id === languages[0].id) {
      await patchScenario({ diskUsageRootIndexOneDelayMs: 900 });
      const lateRootResponse = page.waitForResponse(response => {
        if (response.request().method() !== "GET") return false;
        const url = new URL(response.url());
        return url.pathname === "/api/disk-usage/layout"
          && url.searchParams.get("rootIndex") === "1";
      });
      await chooseDiskRoot(app, "D:\\");
      await page.waitForFunction(async () => {
        const response = await fetch("/__test/requests");
        const telemetry = await response.json();
        return (telemetry["/api/disk-usage/layout"]?.inFlightByQuery["?rootIndex=1"] ?? 0) > 0;
      }, null, { timeout: 5000 });
      // 先登记等待响应再点击：响应可能在请求返回后立刻到达，晚登记会错过。
      const resetResponse = page.waitForResponse(response => {
        const request = response.request();
        if (request.method() !== "GET") return false;
        const url = new URL(request.url());
        return url.pathname === "/api/disk-usage/layout"
          && url.searchParams.get("node") === "0";
      });
      await page.getByRole("button", { name: app.diskUsage.resetView, exact: true }).click();
      await resetResponse;
      await lateRootResponse;
      await waitForDiskRoot("C:\\", cTotals);
      await patchScenario({ diskUsageRootIndexOneDelayMs: 0 });
    }

    const beforeSecondRoot = await readRequestTelemetry();
    const previousRootQueryCount = beforeSecondRoot["/api/disk-usage/layout"]?.queries.length ?? 0;
    const selectedRoot = await chooseDiskRoot(app, "D:\\");
    await waitForDiskRoot("D:\\", dTotals);
    assert((await selectedRoot.innerText()).trim() === "D:\\",
      `${option.id}: the selector did not retain the D: selection`);
    const afterSecondRoot = await readRequestTelemetry();
    const newRootQueries = afterSecondRoot["/api/disk-usage/layout"]?.queries
      .slice(previousRootQueryCount) ?? [];
    assert(newRootQueries.some(query => new URLSearchParams(query).get("rootIndex") === "1"),
      `${option.id}: selecting D: did not request rootIndex=1`);
    const dHeader = await page.locator(
      ".disk-usage-result .optimization-heading > span").innerText();
    assert(dHeader.includes(dTotals) && !dHeader.includes(scanWideTotal),
      `${option.id}: the D: root size was confused with scan-wide byte totals`);

    const canvas = page.locator(".disk-usage-treemap-canvas");
    const center = await canvas.evaluate(element => {
      const rect = element.getBoundingClientRect();
      return { x: rect.width / 2, y: rect.height / 2 };
    });
    await canvas.dblclick({ position: center });
    await waitForDiskRoot("D:\\Projects", diskRootTotals(app, 22_000_000));
    await page.getByRole("button", { name: app.diskUsage.navigateUp, exact: true })
      .waitFor({ state: "visible", timeout: 5000 });
    await canvas.click({ position: center });

    const beforeReturn = await readRequestTelemetry();
    const previousReturnQueryCount = beforeReturn["/api/disk-usage/layout"]?.queries.length ?? 0;
    const returnedRoot = await chooseDiskRoot(app, "C:\\");
    await waitForDiskRoot("C:\\", cTotals);
    assert((await returnedRoot.innerText()).trim() === "C:\\",
      `${option.id}: switching back did not update the selected C: root`);
    assert(await page.getByRole("button", {
      name: app.diskUsage.navigateUp,
      exact: true
    }).count() === 0,
    `${option.id}: switching roots retained the old drill-up action`);

    const canvasNodeRequest = page.waitForRequest(request => {
      if (request.method() !== "GET") return false;
      const url = new URL(request.url());
      return url.pathname === "/api/disk-usage/layout"
        && url.searchParams.get("node") === "4";
    }, { timeout: 300 }).then(() => true, () => false);
    await canvas.focus();
    await canvas.press("Enter");
    const staleSelectionRequested = await canvasNodeRequest;
    assert(!staleSelectionRequested,
      `${option.id}: switching roots left a selected node from the previous tree`);
    const afterReturn = await readRequestTelemetry();
    const returnQueries = afterReturn["/api/disk-usage/layout"]?.queries
      .slice(previousReturnQueryCount) ?? [];
    assert(returnQueries.some(query => new URLSearchParams(query).get("rootIndex") === "0"),
      `${option.id}: switching back to C: did not request rootIndex=0`);
    checkedRootLanguages.push(option.id);
    rootSelections.push({
      language: option.id,
      requestedRootIndex: 1,
      selectedRoot: "D:\\",
      selectedBytes: 73_000_000,
      scanFileCount: 17,
      scanDirectoryCount: 5,
      returnedRoot: "C:\\"
    });

    const scanButton = page.locator(".disk-usage-setup-actions button").last();
    await scanButton.waitFor({ state: "visible", timeout: 7000 });
    await page.waitForFunction(() => {
      const buttons = document.querySelectorAll(".disk-usage-setup-actions button");
      return buttons.length > 0 && !buttons[buttons.length - 1].disabled;
    }, null, { timeout: 7000 });

    const scanResponsePromise = page.waitForResponse(response =>
      response.request().method() === "POST"
        && new URL(response.url()).pathname === "/api/disk-usage/scan");
    await scanButton.click();
    const alert = page.getByRole("alert");
    const scanResponse = await scanResponsePromise;
    assert(scanResponse.status() === 503,
      `${option.id}: the mock scan request did not return the configured failure`);
    await alert.waitFor({ state: "visible", timeout: 5000 });
    const alertText = (await alert.innerText()).trim();
    assert(alertText === app.diskUsage.scanFailed,
      `${option.id}: expected localized scan feedback ${JSON.stringify(app.diskUsage.scanFailed)}, received ${JSON.stringify(alertText)}`);
    checkedLanguages.push(option.id);
  }

  const requestTelemetry = await page.evaluate(async () => {
    const response = await fetch("/__test/requests");
    return response.json();
  });
  const scanTelemetry = requestTelemetry["/api/disk-usage/scan"];
  assert(scanResponses.length === languages.length
    && scanResponses.every(status => status === 503),
  `Unexpected scan response sequence: ${JSON.stringify(scanResponses)}`);
  assert(scanTelemetry?.started === languages.length
    && scanTelemetry?.completed === languages.length
    && scanTelemetry.statuses.every(status => status === 503),
  `Unexpected scan request telemetry: ${JSON.stringify(scanTelemetry)}`);
  assert(JSON.stringify(checkedLanguages) === JSON.stringify(languages.map(option => option.id)),
    `Not all selectable languages displayed scan failure copy: ${JSON.stringify(checkedLanguages)}`);
  assert(JSON.stringify(checkedRootLanguages) === JSON.stringify(languages.map(option => option.id)),
    `Not all selectable languages displayed and switched scanned roots: ${JSON.stringify(checkedRootLanguages)}`);
  assert(failures.length === 0, `Browser failures: ${JSON.stringify(failures)}`);

  return {
    typedValues,
    committedIntervalMs: 2000,
    savedAndReloaded: true,
    rootSelectorLanguages: checkedRootLanguages,
    rootSelections,
    scanFailureLanguages: checkedLanguages,
    scanFailureStatuses: scanResponses,
    pageErrors: failures
  };
}

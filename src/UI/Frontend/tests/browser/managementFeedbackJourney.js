import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { componentFixture, componentVersionFixture } from "./componentFixtures.js";

export default async function managementFeedbackJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  const response = await fetch(`${baseUrl}/__test/scenario`, {
    method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ runtimeEffectOwners: true })
  });
  assert.equal(response.ok, true);
  const runtimeId = "shared-webview2-runtime";
  await page.route("**/api/components", route => route.fulfill({ json: [
    componentFixture(runtimeId, { installerAvailable: true, installerPath: "C:/Fixture/MicrosoftEdgeWebview2Setup.exe" }),
    componentFixture("hardware-sensors-pawnio-provider"),
    componentFixture()
  ] }));
  await page.route("**/api/components/hardware-bridge/versions", route => route.fulfill({ json: componentVersionFixture() }));
  await page.route("**/api/browser-runtimes*", route => route.fulfill({ json: {
    sharedRuntime: null, browserFallback: null, candidates: [], installComponentId: runtimeId,
    capturedAt: new Date().toISOString()
  } }));
  const record = { id: "manual:feedback", name: "Manual fixture", kind: "other", state: "manual",
    sourceFacts: [{ kind: "manual" }], registrationFacts: { kind: "manual", processCount: 0, serviceCount: 0 },
    requiresRootPathConfirmation: false, identityConfirmed: true,
    softwareIdentityId: "feedback-app", rootPaths: [],
    operations: { canUninstall: false, uninstallKind: "None", uninstallConstraint: "manual_registration" } };
  await page.route("**/api/software", route => route.fulfill({ json: [record] }));
  let metadataReady = false;
  await page.route("**/api/software/metadata/feedback-app?*", route => {
    if (!metadataReady) return route.fulfill({ status: 503, json: { error: "fixture metadata failure" } });
    const language = new URL(route.request().url()).searchParams.get("language");
    return route.fulfill({ json: { catalogVersion: "1", found: true, metadata: {
      softwareIdentityId: "feedback-app", requestedLanguage: language, resolvedLanguage: language,
      summary: `Metadata ${language}`, description: null, publisher: null, homepageUrl: null,
      supportUrl: null, licenseName: null, licenseUrl: null, tags: [], sourceRefs: []
    } } });
  });
  let deletes = 0;
  await page.route("**/api/software/manual/*", route => {
    deletes++;
    return route.fulfill({ status: 204 });
  });

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "组件与软件", exact: true }).click();
  await page.locator("#managementTab-Dependency").click();
  const initialApp = await loadAppCopy("zh-CN");
  const sensorName = initialApp.status.component.names.hardwareSensorsPawnIo;
  for (const query of ["hardware_monitor", "HARDWARE_MONITOR"]) {
    await page.getByRole("searchbox").fill(query);
    await page.locator(".component-item").filter({ hasText: sensorName }).waitFor();
  }
  await page.getByRole("searchbox").fill("unmatched-provider-kind");
  await page.locator(".component-item").filter({ hasText: sensorName }).waitFor({ state: "hidden" });
  await page.getByRole("searchbox").fill("");
  let previous = "zh-CN";
  const languages = [];
  for (const option of languageOptions.filter(option => option.id !== "system")) {
    const oldApp = await loadAppCopy(previous);
    const oldSettings = await loadSettingsText(previous);
    await page.getByRole("navigation").getByRole("button", { name: oldApp.page.settings, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: oldSettings.appearance.languageSelectLabel, exact: true }).click();
    await page.getByRole("option", { name: `${option.nativeLabel} · ${option.label}`, exact: true }).click();
    if (option.id !== previous) await page.getByRole("button", { name: oldSettings.actions.save, exact: true }).click();
    await page.waitForFunction(language => document.documentElement.lang === language, option.id);
    const app = await loadAppCopy(option.id);
    await page.getByRole("navigation").getByRole("button", { name: app.page.components, exact: true }).click();
    await page.locator("#managementTab-BrowserRuntime").click();
    await page.getByRole("button", { name: app.browserRuntime.downloadShared, exact: true }).click();
    const acquisition = page.getByRole("dialog", { name: app.status.component.names.sharedWebView2Runtime, exact: true });
    await acquisition.getByText(app.componentAcquisition.installIntro, { exact: true }).waitFor();
    await acquisition.getByRole("button", { name: app.feedback.cancel, exact: true }).click();

    await page.locator("#managementTab-Dependency").click();
    const bridge = page.locator(".component-item").filter({ hasText: app.status.component.names.hardwareBridge });
    await bridge.getByRole("button", { name: app.managementPage.detailsOf(app.status.component.names.hardwareBridge), exact: true }).click();
    const componentDetail = page.getByRole("dialog", { name: app.status.component.names.hardwareBridge, exact: true });
    await componentDetail.getByText(app.componentCatalog.installNotes.hardwareBridge, { exact: true }).waitFor();
    await componentDetail.getByText(app.componentCatalog.capabilitiesCaption, { exact: true }).waitFor();
    await componentDetail.getByText(app.componentCatalog.capabilities.power, { exact: true }).waitFor();
    await componentDetail.locator(`button[aria-label="${app.softwareDetail.close}"]`).click();
    await bridge.getByRole("button", { name: app.management.downloadAndInstall, exact: true }).click();
    const versions = page.getByRole("dialog", { name: app.status.component.names.hardwareBridge, exact: true });
    await versions.getByText(app.componentCatalog.versionFailures.network, { exact: true }).waitFor();
    await versions.getByText(app.componentCatalog.versionFailures["no-stable-release"], { exact: true }).waitFor();
    await versions.getByRole("button", { name: app.feedback.cancel, exact: true }).click();

    await page.locator("#managementTab-other").click();
    const card = page.locator(".software-item").filter({ hasText: record.name });
    await card.getByRole("button", { name: app.managementFeedback.removeManual, exact: true }).waitFor();
    await card.getByRole("button", { name: app.management.softwareSettings, exact: true }).click();
    const detail = page.getByRole("dialog", { name: record.name, exact: true });
    await detail.getByText(app.managementFeedback.metadataFailed, { exact: true }).waitFor();
    metadataReady = true;
    await detail.getByRole("button", { name: app.managementFeedback.retry, exact: true }).click();
    await detail.getByText(`Metadata ${option.id}`, { exact: true }).waitFor();
    await detail.getByText(app.managementFeedback.metadataFailed, { exact: true }).waitFor({ state: "hidden" });
    metadataReady = false;
    await detail.getByRole("button", { name: app.managementFeedback.removeManual, exact: true }).click();
    const confirm = page.getByRole("dialog").filter({ hasText: app.managementFeedback.confirmManual });
    await confirm.getByText(app.managementFeedback.confirmManual, { exact: true }).waitFor();
    await confirm.getByRole("button", { name: app.feedback.cancel, exact: true }).click();
    await detail.locator(`button[aria-label="${app.softwareDetail.close}"]`).click();
    languages.push(option.id);
    previous = option.id;
  }
  assert.equal(deletes, 0, "Canceled registration removals must not write");
  assert.deepEqual(errors, []);
  return { languages, providerKindSearch: true, runtimeDownloadDialog: true, componentDetailAndVersionReasons: true, manualRemovalCopyAndCancel: true, metadataFailureAndRetry: true };
}

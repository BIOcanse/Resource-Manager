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
    body: JSON.stringify({ runtimeEffectOwners: true, operations: "mixed", componentPayloadFailure: true })
  });
  assert.equal(response.ok, true);
  const runtimeId = "shared-webview2-runtime";
  const payloadId = "amd-smu-pawnio-provider";
  const updateId = "intel-msr-pawnio-provider";
  const bridgeId = "hardware-bridge";
  let payloadRepaired = false;
  let bridgeUpdateAvailable = false;
  let verifications = 0;
  const payloadComponent = () => componentFixture(payloadId, {
    installed: true, canDownload: false, canInstall: false,
    state: payloadRepaired ? "installed" : "payload_missing",
    message: { domain: 1, code: payloadRepaired ? 5 : 25, args: [] }
  });
  const updateComponent = () => componentFixture(updateId, {
    installed: true, canDownload: false, canInstall: true, canVerify: false,
    state: "update_available", message: { domain: 1, code: 26, args: [] },
    definition: {
      externalTermsUrl: "https://example.invalid/component-terms",
      requiresExternalTermsAcknowledgement: true
    }
  });
  const bridgeComponent = () => componentFixture(bridgeId, bridgeUpdateAvailable ? {
    installed: true, canDownload: false, canInstall: true, canVerify: false,
    state: "payload_missing", message: { domain: 1, code: 26, args: [] }
  } : {});
  await page.route(`**/api/components/${payloadId}/verify`, route => {
    verifications++;
    payloadRepaired = true;
    return route.fulfill({ json: { state: "installed", message: { domain: 1, code: 13, args: [] } } });
  });
  let updateVersionLookups = 0;
  let bridgeVersionLookups = 0;
  const updateRequests = [];
  const bridgeUpdateRequests = [];
  const runtimeFilesUnavailableOperation = {
    id: "000000000000000000000000000000a3",
    kind: "component.install",
    domainKey: "component:failed-runtime",
    subject: { targetId: "failed-runtime", scope: null, mode: null, targetPath: null },
    state: "failed",
    configurationGeneration: "1",
    stateRevision: "1",
    attemptNumber: 1,
    maximumAttempts: 3,
    createdAt: "2026-08-22T12:00:00.000Z",
    updatedAt: "2026-08-22T12:00:01.000Z",
    completedAt: "2026-08-22T12:00:01.000Z",
    cancelRequested: false,
    progress: null,
    resultCode: null,
    resultMessage: null,
    errorMessage: { domain: 1, code: 24, args: [] }
  };
  await page.route(`**/api/components/${updateId}/versions`, route => {
    updateVersionLookups++;
    return route.fulfill({ json: componentVersionFixture(updateId) });
  });
  await page.route(`**/api/components/${updateId}/install`, route => {
    updateRequests.push(route.request().postDataJSON());
    return route.fulfill({ status: 202, json: runtimeFilesUnavailableOperation });
  });
  await page.route(`**/api/components/${bridgeId}/versions`, route => {
    bridgeVersionLookups++;
    return route.fulfill({ json: componentVersionFixture(bridgeId) });
  });
  await page.route(`**/api/components/${bridgeId}/install`, route => {
    bridgeUpdateRequests.push(route.request().postDataJSON());
    return route.fulfill({ status: 503, json: { error: "fixture missing-payload update request recorded" } });
  });
  await page.route("**/api/components", route => route.fulfill({ json: [
    componentFixture(runtimeId, { installerAvailable: true, installerPath: "C:/Fixture/MicrosoftEdgeWebview2Setup.exe" }),
    componentFixture("hardware-sensors-pawnio-provider"),
    payloadComponent(),
    updateComponent(),
    bridgeComponent()
  ] }));
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
    payloadRepaired = false;
    const refresh = page.locator("#componentsPage button.panel-refresh-button");
    await refresh.click();
    const payload = page.locator(".component-item").filter({ hasText: app.status.component.names.amdSmuPawnIo });
    await payload.getByText(app.status.state.payloadMissing, { exact: true }).waitFor();
    await payload.getByText(app.backendMessage.dependency.payloadMissing, { exact: true }).waitFor();
    await payload.getByRole("button", { name: app.componentCatalog.verifyAndRepair, exact: true }).click();
    await payload.getByText(app.status.state.payloadMissing, { exact: true }).waitFor({ state: "hidden" });
    await payload.getByText(app.management.installed, { exact: true }).waitFor();

    const updateTarget = page.locator(".component-item").filter({ hasText: app.status.component.names.intelMsrPawnIo });
    await updateTarget.getByText(app.status.state.updateAvailable, { exact: true }).waitFor();
    await updateTarget.getByText(app.backendMessage.dependency.updateAvailable, { exact: true }).waitFor();
    assert.equal(await updateTarget.getByRole("button", { name: app.componentCatalog.verifyAndRepair, exact: true }).count(), 0,
      `${option.id}: update entitlement must suppress Verify when canVerify is false`);
    const updateButton = updateTarget.getByRole("button", { name: app.componentCatalog.update, exact: true });
    await updateButton.waitFor();
    await updateButton.click();
    const updateDialog = page.getByRole("dialog", { name: app.status.component.names.intelMsrPawnIo, exact: true });
    await updateDialog.getByText(app.backendMessage.dependency.updateAvailable, { exact: true }).waitFor();
    assert.equal(await updateDialog.locator(".component-acquisition-versions").count(), 0,
      `${option.id}: updates must not offer version choices`);
    await updateDialog.getByRole("button", { name: app.componentAcquisition.agreeAndUpdate, exact: true }).waitFor();
    await updateDialog.getByRole("button", { name: app.feedback.cancel, exact: true }).click();
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

    bridgeUpdateAvailable = true;
    await refresh.click();
    const bridgeUpdateTarget = page.locator(".component-item").filter({ hasText: app.status.component.names.hardwareBridge });
    await bridgeUpdateTarget.getByText(app.status.state.payloadMissing, { exact: true }).waitFor();
    await bridgeUpdateTarget.getByText(app.backendMessage.dependency.updateAvailable, { exact: true }).waitFor();
    assert.equal(await bridgeUpdateTarget.getByRole("button", { name: app.componentCatalog.verifyAndRepair, exact: true }).count(), 0,
      `${option.id}: update entitlement must suppress Verify when canVerify is false`);
    const versionLookupsBeforeBridgeUpdate = bridgeVersionLookups;
    await bridgeUpdateTarget.getByRole("button", { name: app.componentCatalog.update, exact: true }).click();
    const bridgeUpdateDialog = page.getByRole("dialog", { name: app.status.component.names.hardwareBridge, exact: true });
    await bridgeUpdateDialog.getByText(app.backendMessage.dependency.updateAvailable, { exact: true }).waitFor();
    assert.equal(await bridgeUpdateDialog.locator(".component-acquisition-versions").count(), 0,
      `${option.id}: missing-payload updates must not offer version choices`);
    await bridgeUpdateDialog.getByRole("button", { name: app.componentCatalog.update, exact: true }).waitFor();
    await bridgeUpdateDialog.getByRole("button", { name: app.feedback.cancel, exact: true }).click();
    assert.equal(bridgeVersionLookups, versionLookupsBeforeBridgeUpdate,
      `${option.id}: missing-payload updates must not query version choices`);
    assert.equal(bridgeUpdateRequests.length, 0,
      `${option.id}: opening or canceling update consent must not submit an update`);
    bridgeUpdateAvailable = false;

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
  assert.equal(updateRequests.length, 0, "Update status and canceled dialogs must not submit an update");
  assert.equal(updateVersionLookups, 0, "Updates must not fetch version choices");
  const finalApp = await loadAppCopy(previous);
  await page.getByRole("navigation").getByRole("button", { name: finalApp.page.components, exact: true }).click();
  await page.locator("#managementTab-Dependency").click();
  const updateTarget = page.locator(".component-item").filter({ hasText: finalApp.status.component.names.intelMsrPawnIo });
  await updateTarget.getByRole("button", { name: finalApp.componentCatalog.update, exact: true }).click();
  const finalUpdateDialog = page.getByRole("dialog", { name: finalApp.status.component.names.intelMsrPawnIo, exact: true });
  const updateResponsePromise = page.waitForResponse(response =>
    new URL(response.url()).pathname === `/api/components/${updateId}/install`);
  await finalUpdateDialog.getByRole("button", { name: finalApp.componentAcquisition.agreeAndUpdate, exact: true }).click();
  const updateResponse = await updateResponsePromise;
  assert.equal(updateResponse.request().method(), "POST");
  assert.deepEqual(updateResponse.request().postDataJSON(), { acknowledgeExternalTerms: true, versionChoice: "verified" });
  assert.equal(updateRequests.length, 1, "Only the explicit update confirmation may submit an update");
  assert.equal(updateVersionLookups, 0, "Updates must not fetch version choices");
  const updateFailureToast = page.locator(".toast.error")
    .filter({ hasText: finalApp.backendMessage.dependency.runtimeFilesUnavailable });
  await updateFailureToast.waitFor({ state: "visible" });
  await updateFailureToast.getByText(finalApp.feedback.error, { exact: true }).waitFor();
  assert.equal(await page.locator(".toast.success")
    .filter({ hasText: finalApp.backendMessage.dependency.runtimeFilesUnavailable }).count(), 0,
  "A failed update must not display its dependency failure message as a success toast");
  bridgeUpdateAvailable = true;
  const finalRefresh = page.locator("#componentsPage button.panel-refresh-button");
  await finalRefresh.click();
  const bridgeUpdateTarget = page.locator(".component-item").filter({ hasText: finalApp.status.component.names.hardwareBridge });
  await bridgeUpdateTarget.getByText(finalApp.backendMessage.dependency.updateAvailable, { exact: true }).waitFor();
  const bridgeVersionLookupsBeforeSubmit = bridgeVersionLookups;
  await bridgeUpdateTarget.getByRole("button", { name: finalApp.componentCatalog.update, exact: true }).click();
  const finalBridgeUpdateDialog = page.getByRole("dialog", { name: finalApp.status.component.names.hardwareBridge, exact: true });
  const bridgeUpdateResponsePromise = page.waitForResponse(response =>
    new URL(response.url()).pathname === `/api/components/${bridgeId}/install`);
  await finalBridgeUpdateDialog.getByRole("button", { name: finalApp.componentCatalog.update, exact: true }).click();
  const bridgeUpdateResponse = await bridgeUpdateResponsePromise;
  assert.equal(bridgeUpdateResponse.request().method(), "POST");
  assert.deepEqual(bridgeUpdateResponse.request().postDataJSON(), { acknowledgeExternalTerms: true, versionChoice: "verified" });
  assert.equal(bridgeUpdateRequests.length, 1, "Only explicit consent may submit the missing-payload update");
  assert.equal(bridgeVersionLookups, bridgeVersionLookupsBeforeSubmit, "Missing-payload updates must not fetch version choices");
  assert.equal(deletes, 0, "Canceled registration removals must not write");
  assert.equal(verifications, languages.length, "Each language must invoke verification on the installed AMD payload once");
  assert.deepEqual(errors, []);
  return { languages, providerKindSearch: true, runtimeDownloadDialog: true, componentDetailAndVersionReasons: true, payloadMissingRepairInEachLanguage: true, updateAvailableAndExplicitRequest: true, missingPayloadUpdateEntitlementAndExplicitRequest: true, manualRemovalCopyAndCancel: true, metadataFailureAndRetry: true };
}

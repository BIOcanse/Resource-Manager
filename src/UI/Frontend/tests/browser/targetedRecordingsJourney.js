import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { resolve } from "node:path";

export default async function targetedRecordingsJourney(page, baseUrl) {
  const assert = (value, message) => { if (!value) throw new Error(message); };
  const failures = [];
  page.on("pageerror", error => failures.push(String(error)));
  // Deletion must go through the app confirm dialog; any browser dialog is a failure.
  page.on("dialog", dialog => { failures.push(`browser dialog: ${dialog.message()}`); void dialog.dismiss(); });
  await page.setViewportSize({ width: 1200, height: 900 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  let previous = "zh-CN";
  const checked = [];
  for (const option of languageOptions.filter(option => option.id !== "system")) {
    const language = option.id;
    if (language !== previous) {
      const old = await loadAppCopy(previous);
      const settings = await loadSettingsText(previous);
      await page.locator(".shell-pages").getByRole("button", { name: old.page.settings, exact: true }).click();
      await page.getByRole("button", { name: settings.sections.appearance, exact: true }).click();
      await page.getByRole("button", { name: settings.appearance.languageSelectLabel, exact: true }).click();
      await page.getByRole("option", { name: `${option.nativeLabel} · ${option.label}`, exact: true }).click();
      await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
      await page.waitForFunction(language => document.documentElement.lang === language, language);
    }
    const app = await loadAppCopy(language);
    const copy = app.targetedReport;
    await page.locator(".shell-pages").getByRole("button", { name: app.page.details, exact: true }).click();
    await page.getByRole("tab", { name: copy.tab, exact: true }).click();
    const panel = page.locator(".targeted-reports-page");
    await panel.getByText(copy.workflow, { exact: true }).waitFor();
    await panel.getByText(copy.empty, { exact: true }).waitFor();
    const targets = panel.getByRole("combobox", { name: copy.software, exact: true });
    // The software list is the app's standard select (a combobox button with a listbox popup), not a native select.
    const targetName = "示例软件";
    await targets.click();
    await page.getByRole("listbox", { name: copy.software, exact: true }).getByRole("option", { name: targetName, exact: true }).click();
    const visibleTarget = async () => (await targets.textContent()).trim();
    const control = panel.locator(".targeted-recording-control");
    const duration = control.getByRole("spinbutton", { name: copy.maximumSeconds, exact: true });
    assert(await duration.inputValue() === "600", `${language}: default duration is not 600`);
    if (language === "zh-CN") {
      const emptyTargets = route => route.fulfill({ json: [] });
      await page.route("**/api/software", emptyTargets);
      await panel.getByRole("button", { name: copy.refresh, exact: true }).click();
      await panel.getByText(copy.noSoftware, { exact: true }).waitFor();
      await page.unroute("**/api/software", emptyTargets);
      const otherRecording = route => route.fulfill({ json: [{
        id: "other", softwareId: "other-software", softwareName: "Other software",
        startedAt: new Date().toISOString(), endedAt: null, maximumDurationSeconds: 600,
        status: "recording", stopReason: null, incomplete: false
      }] });
      await page.route("**/api/targeted-recordings", otherRecording);
      await panel.getByRole("button", { name: copy.refresh, exact: true }).click();
      await control.getByRole("status").filter({ hasText: copy.oneAtATime }).waitFor();
      assert(await control.getByRole("button", { name: copy.start, exact: true }).isDisabled(), "Concurrent start remained enabled");
      await page.unroute("**/api/targeted-recordings", otherRecording);
      await panel.getByRole("button", { name: copy.refresh, exact: true }).click();
      await panel.getByText(copy.empty, { exact: true }).waitFor();
      assert(await visibleTarget() === targetName, "Refreshing targets lost the visible selection");
    }
    // This journey stops explicitly; a short deadline would expire during a queue memory pause.
    await duration.fill("3600");
    const response = page.waitForResponse(response => response.url().endsWith("/api/targeted-recordings") && response.request().method() === "POST");
    await control.getByRole("button", { name: copy.start, exact: true }).click();
    const started = await (await response).json();
    assert(started.softwareId === "frontend-harness.software" && started.maximumDurationSeconds === 3600,
      `${language}: wrong start payload`);
    await control.getByRole("status").filter({ hasText: copy.recording }).waitFor();
    const row = panel.locator(".targeted-report-list-row");
    await row.getByText("示例软件", { exact: true }).waitFor();
    const recordingStatus = row.locator(".targeted-report-open span");
    await recordingStatus.waitFor();
    assert((await recordingStatus.textContent()).endsWith(` · ${copy.recording}`),
      `${language}: list row did not show the recording status`);
    await panel.locator(".targeted-report-detail").waitFor();
    // The page must expose stop without reopening the software details.
    await row.getByRole("button", { name: copy.stop, exact: true }).click();
    await panel.locator(".targeted-report-detail").getByText(new RegExp(copy.stopReasons.user)).waitFor();
    await panel.locator(".targeted-report-summary").getByText(copy.averageFps, { exact: true }).waitFor();
    assert(await panel.getByRole("img", { name: copy.frameTime, exact: true }).count() === 1, `${language}: no frame chart`);
    assert(await panel.getByRole("img", { name: copy.distribution, exact: true }).count() === 1, `${language}: no distribution`);
    assert(await panel.locator(".targeted-report-resource-grid canvas").count() === 4, `${language}: no resource curves`);
    const memory = panel.getByRole("img", { name: `${copy.software} · ${app.metricLabel["memory.usage"]}`, exact: true });
    assert(await memory.count() === 1, `${language}: real memory metric label missing`);
    assert((await memory.locator("..").textContent()).includes("MiB"), `${language}: memory bytes were not formatted`);
    if (process.env.RM_BROWSER_ARTIFACT_DIR) {
      await page.screenshot({ path: resolve(process.env.RM_BROWSER_ARTIFACT_DIR, `${language}.png`), fullPage: true });
    }
    if (language === "zh-CN") {
      for (const [label, format] of [[copy.exportJson, "json"], [copy.exportCsv, "csv"]]) {
        const download = page.waitForEvent("download");
        const exported = page.waitForResponse(response => response.url().includes(`/${started.id}/export?format=${format}`));
        await panel.getByRole("button", { name: label, exact: true }).click();
        assert((await download).suggestedFilename().endsWith(`.${format}`), `Wrong ${format} download`);
        const response = await exported;
        assert(response.ok(), `${format} export failed`);
        if (format === "json") {
          const report = await response.json();
          assert(report.recording.id === started.id && report.summary.averageFps === 100, "JSON export lost report values");
        } else {
          const csv = await response.text();
          assert(csv.startsWith("kind,utc,processId,") && csv.includes("averageFps,100"), "CSV export lost report values");
        }
      }
      const noFrames = async route => {
        const response = await route.fetch();
        const report = await response.json();
        await route.fulfill({ json: { ...report, summary: null, frameIntervals: [], fpsDistribution: [] } });
      };
      await page.route(`**/api/targeted-recordings/${started.id}`, noFrames);
      await panel.getByRole("button", { name: copy.refresh, exact: true }).click();
      await panel.getByText(copy.noFrames, { exact: true }).waitFor();
      assert(await panel.locator(".targeted-report-resource-grid canvas").count() === 4, "Missing frames hid resources");
      await page.unroute(`**/api/targeted-recordings/${started.id}`, noFrames);
      // A failed list read must not be presented as an empty collection.
      const fail = route => route.fulfill({ status: 503, body: "fixture" });
      await page.route("**/api/targeted-recordings", fail);
      await panel.getByRole("button", { name: copy.refresh, exact: true }).click();
      const listError = panel.locator(".targeted-report-note.error[role='alert']");
      await listError.waitFor();
      assert(await panel.getByText(copy.empty, { exact: true }).count() === 0, "List failure appeared as empty");
      await page.unroute("**/api/targeted-recordings", fail);
      await listError.getByRole("button", { name: app.observationNotice.retry, exact: true }).click();
      await listError.waitFor({ state: "detached" });
      assert(await visibleTarget() === targetName, "Retry changed the visible recording target");
    }
    // Deletion is confirmed in the app's own confirm dialog (title, recording, consequence), not a browser dialog.
    const confirmDelete = page.getByRole("dialog", { name: copy.confirmDelete, exact: true });
    if (language === "zh-CN") {
      await row.getByRole("button", { name: copy.delete, exact: true }).click();
      await confirmDelete.getByRole("button", { name: app.feedback.cancel, exact: true }).click();
      await confirmDelete.waitFor({ state: "detached" });
      assert(await row.count() === 1, "Cancelling the delete confirmation removed the recording");
    }
    await row.getByRole("button", { name: copy.delete, exact: true }).click();
    const confirmText = await confirmDelete.textContent();
    assert(confirmText.includes("示例软件") && confirmText.includes(copy.deleteDetail),
      `${language}: delete confirmation does not name the recording and its consequence`);
    await confirmDelete.getByRole("button", { name: copy.delete, exact: true }).click();
    await panel.getByText(copy.empty, { exact: true }).waitFor();
    assert(await panel.locator(".targeted-report-detail").count() === 0, `${language}: deleted report remained visible`);
    checked.push(language);
    previous = language;
  }
  assert(failures.length === 0, JSON.stringify(failures));
  return { languages: checked, startRunningStopReport: true, charts: true, exports: true, deletion: true, listRecovery: true };
}

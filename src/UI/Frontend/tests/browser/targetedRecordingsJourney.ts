type ConcreteLanguageOption = (typeof languageOptions)[number] & { id: Parameters<typeof loadAppCopy>[0] };
import type { RecordingHeader, RecordingItemReport, RecordingProfile, RecordingReport, ProfilePatch } from "../../src/features/targetedReport/targetedRecordingApi.ts";
type AppCopy = Awaited<ReturnType<typeof loadAppCopy>>;
type ReportCopy = AppCopy["targetedReport"];
type JourneyAssert = (value: unknown, message: string) => asserts value;
type StartedRecording = RecordingHeader & { profileName: string; items: RecordingItemReport[] };
import type { Page, Locator, Route } from "playwright-core";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { resolve } from "node:path";

export default async function targetedRecordingsJourney(page: Page, baseUrl: string) {
  const assert: JourneyAssert = (value, message) => { if (!value) throw new Error(message); };
  const failures: string[] = [];
  page.on("pageerror", error => failures.push(String(error)));
  // Deletion must go through the app confirm dialog; any browser dialog is a failure.
  page.on("dialog", dialog => { failures.push(`browser dialog: ${dialog.message()}`); void dialog.dismiss(); });
  await page.setViewportSize({ width: 1200, height: 900 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  let previous: Parameters<typeof loadAppCopy>[0] = "zh-CN";
  const checked: string[] = [];
  for (const option of languageOptions.filter((option): option is ConcreteLanguageOption => option.id !== "system")) {
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
    // It lists the running registered software from /api/targeted-recordings/targets, each with its process count.
    const targetName = copy.targetOption("示例软件", 2);
    await targets.click();
    await page.getByRole("listbox", { name: copy.software, exact: true }).getByRole("option", { name: targetName, exact: true }).click();
    const visibleTarget = async () => (await targets.textContent())!.trim();
    const control = panel.locator(".targeted-recording-control");
    const duration = control.getByRole("spinbutton", { name: copy.maximumSeconds, exact: true });
    assert(await duration.inputValue() === "600", `${language}: default duration is not 600`);
    // The profile select shows its default, the first profile, instead of hiding it.
    const profileSelect = control.getByRole("combobox", { name: copy.profile, exact: true });
    assert((await profileSelect.textContent())!.trim() === "默认", `${language}: the first profile is not the visible default`);
    let profileId = "default";
    if (language === "zh-CN") {
      profileId = await checkProfiles(page, control, copy, assert);
      await profileSelect.click();
      await page.getByRole("listbox", { name: copy.profile, exact: true }).getByRole("option", { name: "旅程配置（改）", exact: true }).click();
      // Starting while the target is not running: the backend refuses with 409 target-not-running, the control says so
      // and rereads the running software, which is now empty.
      await page.request.post(`${baseUrl}/__test/targeted-recordings/target-running`, { data: { running: false } });
      const refused = page.waitForResponse(response => response.url().endsWith("/api/targeted-recordings") && response.request().method() === "POST");
      await control.getByRole("button", { name: copy.start, exact: true }).click();
      const refusal = await refused;
      assert(refusal.status() === 409 && (await refusal.json() as { error: string }).error === "target-not-running", "Start was not refused for a stopped target");
      assert((JSON.parse(refusal.request().postData()!) as { profileId: string }).profileId === profileId, "Start did not send the chosen profile");
      await control.getByText(copy.errors.targetNotRunning, { exact: true }).waitFor();
      await panel.getByText(copy.noSoftware, { exact: true }).waitFor();
      await page.request.post(`${baseUrl}/__test/targeted-recordings/target-running`, { data: { running: true } });
      await panel.getByRole("button", { name: copy.refresh, exact: true }).click();
      await page.waitForFunction(({ name }) => [...document.querySelectorAll<HTMLElement>(".targeted-reports-page [role='combobox']")]
        .some(element => element.textContent.trim() === name), { name: targetName });
      await control.getByText(copy.errors.targetNotRunning, { exact: true }).waitFor({ state: "detached" });
      const emptyTargets = (route: Route) => route.fulfill({ json: [] });
      await page.route("**/api/targeted-recordings/targets", emptyTargets);
      await panel.getByRole("button", { name: copy.refresh, exact: true }).click();
      await panel.getByText(copy.noSoftware, { exact: true }).waitFor();
      // Refresh reads targets and recordings independently. Finish both before
      // changing the list fixture, or the next refresh can join the old list read.
      await panel.getByText(copy.empty, { exact: true }).waitFor();
      await page.unroute("**/api/targeted-recordings/targets", emptyTargets);
      const otherRecording = (route: Route) => route.fulfill({ json: [{
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
      // The journey's own profile records one item; the result views below need the default profile's software series,
      // so the recording itself uses the default profile.
      await profileSelect.click();
      await page.getByRole("listbox", { name: copy.profile, exact: true }).getByRole("option", { name: "默认", exact: true }).click();
      profileId = "default";
    }
    // This journey stops explicitly; a short deadline would expire during a queue memory pause.
    await duration.fill("3600");
    const response = page.waitForResponse(response => response.url().endsWith("/api/targeted-recordings") && response.request().method() === "POST");
    await control.getByRole("button", { name: copy.start, exact: true }).click();
    const started = await (await response).json() as StartedRecording;
    assert(started.softwareId === "frontend-harness.software" && started.maximumDurationSeconds === 3600,
      `${language}: wrong start payload`);
    assert(started.profileId === profileId && started.items.length === 5,
      `${language}: the recording did not use the chosen profile`);
    await control.getByRole("status").filter({ hasText: copy.recording }).waitFor();
    const row = panel.locator(".targeted-report-list-row:not([data-exit-ghost])");
    await row.getByText("示例软件", { exact: true }).waitFor();
    const recordingStatus = row.locator(".targeted-report-open span");
    await recordingStatus.waitFor();
    assert((await recordingStatus.textContent())!.endsWith(` · ${copy.recording}`),
      `${language}: list row did not show the recording status`);
    await panel.locator(".targeted-report-detail:not([data-exit-ghost])").waitFor();
    // Clear history removes completed recordings only, so it is unavailable while the only recording is running.
    const clearHistory = panel.getByRole("button", { name: app.reportHistory.clear, exact: true });
    assert(await clearHistory.isDisabled(), `${language}: clear history was available with only a running recording`);
    // The page must expose stop without reopening the software details.
    await row.getByRole("button", { name: copy.stop, exact: true }).click();
    await panel.locator(".targeted-report-detail:not([data-exit-ghost])").getByText(new RegExp(copy.stopReasons.user)).waitFor();
    await panel.locator(".targeted-report-summary").getByText(copy.averageFps, { exact: true }).waitFor();
    await checkReportInfo(panel, copy, language, started, assert);
    await checkReportViews(page, panel, app, copy, language, assert);
    if (process.env.RM_BROWSER_ARTIFACT_DIR) {
      await page.screenshot({ path: resolve(process.env.RM_BROWSER_ARTIFACT_DIR, `${language}.png`), fullPage: true });
    }
    if (language === "zh-CN") {
      // Five export formats, in the order of the buttons; file names match the backend's Content-Disposition names.
      const exportGroup = panel.getByRole("group", { name: copy.exports, exact: true });
      assert(await exportGroup.getByRole("button").count() === 5, "The report does not offer five exports");
      for (const [label, format, fileName, csvHeader] of [
        [copy.exportPresentMon, "presentmon", `targeted-recording-${started.id}-presentmon-1.6.csv`, "Application,ProcessID,SwapChainAddress,"],
        [copy.exportLongCsv, "long-csv", `targeted-recording-${started.id}-long.csv`, "time_ms,item,process,value,unit"],
        [copy.exportFramesCsv, "frames-csv", `targeted-recording-${started.id}-frames.csv`, "time_ms,process_id,process_start_key,"],
        [copy.exportJson, "json", `targeted-recording-${started.id}.json`, null],
        [copy.exportCsv, "csv", `targeted-recording-${started.id}.csv`, "kind,utc,processId,"]
      ] as const) {
        const download = page.waitForEvent("download");
        const exported = page.waitForResponse(response => response.url().includes(`/${started.id}/export?format=${format}`));
        await exportGroup.getByRole("button", { name: label, exact: true }).click();
        assert((await download).suggestedFilename() === fileName, `Wrong ${format} download name`);
        const response = await exported;
        assert(response.ok(), `${format} export failed`);
        if (format === "json") {
          const report = await response.json() as RecordingReport & { schemaVersion: number };
          assert(report.schemaVersion === 1 && report.recording.id === started.id && report.summary!.averageFps === 100,
            "JSON export lost report values");
        } else {
          const csv = await response.text();
          assert(csv.startsWith(csvHeader!), `${format} export has the wrong header`);
        }
      }
      const noFrames = async (route: Route) => {
        const response = await route.fetch();
        const report = await response.json() as RecordingReport & { schemaVersion: number };
        await route.fulfill({ json: { ...report, summary: null, frameIntervals: [], fpsDistribution: [] } });
      };
      await page.route(`**/api/targeted-recordings/${started.id}`, noFrames);
      await panel.getByRole("button", { name: copy.refresh, exact: true }).click();
      await panel.getByText(copy.noFrames, { exact: true }).waitFor();
      // Without frames the frame views say so in place; the resource view still draws from the resource samples.
      await panel.getByRole("region", { name: copy.views.defaultNames.frameTime, exact: true }).first().getByRole("status").first().waitFor();
      const resourcesView = panel.getByRole("region", { name: copy.views.defaultNames.softwareResources, exact: true }).first();
      await resourcesView.getByRole("img", { name: copy.views.defaultNames.softwareResources, exact: true }).waitFor();
      assert(await resourcesView.locator(".targeted-report-view-issues").count() === 0, "Missing frames hid resources");
      await page.unroute(`**/api/targeted-recordings/${started.id}`, noFrames);
      // A failed list read must not be presented as an empty collection.
      const fail = (route: Route) => route.fulfill({ status: 503, body: "fixture" });
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
    // zh-CN deletes the one recording from its row; the other languages clear the history, which removes every completed
    // recording (docs/report_lifecycle_2026-10-28.md), confirmed with the count and what is kept, and reports the removed count.
    const confirmDelete = page.getByRole("dialog", { name: copy.confirmDelete, exact: true });
    const confirmClear = page.getByRole("dialog", { name: app.reportHistory.confirmTitle, exact: true });
    assert(await clearHistory.isEnabled(), `${language}: clear history was unavailable with a completed recording`);
    if (language === "zh-CN") {
      await clearHistory.click();
      await confirmClear.getByRole("button", { name: app.feedback.cancel, exact: true }).click();
      await confirmClear.waitFor({ state: "detached" });
      await row.getByRole("button", { name: copy.delete, exact: true }).click();
      await confirmDelete.getByRole("button", { name: app.feedback.cancel, exact: true }).click();
      await confirmDelete.waitFor({ state: "detached" });
      assert(await row.count() === 1, "Cancelling a confirmation removed the recording");
      await row.getByRole("button", { name: copy.delete, exact: true }).click();
      const confirmText = (await confirmDelete.textContent())!;
      assert(confirmText.includes("示例软件") && confirmText.includes(copy.deleteDetail),
        `${language}: delete confirmation does not name the recording and its consequence`);
      await confirmDelete.getByRole("button", { name: copy.delete, exact: true }).click();
    } else {
      await clearHistory.click();
      const confirmText = (await confirmClear.textContent())!;
      assert(confirmText.includes(app.reportHistory.confirmMessage.targetedRecordings("1"))
        && app.reportHistory.details.targetedRecordings.every((line) => confirmText.includes(line)),
        `${language}: clear confirmation does not give the count and its consequence: ${confirmText}`);
      await confirmClear.getByRole("button", { name: app.reportHistory.confirm, exact: true }).click();
      await page.locator(".toast.success:not([data-exit-ghost])").filter({ hasText: app.reportHistory.cleared("1") }).waitFor();
    }
    await panel.getByText(copy.empty, { exact: true }).waitFor();
    assert(await clearHistory.isDisabled(), `${language}: clear history stayed available with no completed recording`);
    assert(await panel.locator(".targeted-report-detail:not([data-exit-ghost])").count() === 0, `${language}: deleted report remained visible`);
    checked.push(language);
    previous = language;
  }
  assert(failures.length === 0, JSON.stringify(failures));
  return { languages: checked, startRunningStopReport: true, charts: true, views: true, exports: true, deletion: true, historyClear: true,
    listRecovery: true, profiles: true, targetNotRunning: true, reportInfo: true };
}

// Recording profiles (docs/targeted_recording_views_2026-10-30.md §3.2): the last profile cannot be deleted and says why;
// create a profile (empty until an item is added), rename it, change an item's interval and sync precision, copy it, and
// delete the copy through the app's confirm dialog. Every change is saved explicitly. Returns the created profile's id.
async function checkProfiles(page: Page, control: Locator, copy: ReportCopy, assert: JourneyAssert) {
  const profiles = copy.profiles;
  await control.getByRole("button", { name: copy.manageProfiles, exact: true }).click();
  const dialog = page.getByRole("dialog", { name: profiles.title, exact: true });
  await dialog.waitFor();
  const remove = dialog.getByRole("button", { name: profiles.delete, exact: true });
  await dialog.getByText(copy.errors.lastProfile, { exact: true }).waitFor();
  assert(await remove.isDisabled(), "The last profile could be deleted");

  await dialog.getByRole("button", { name: profiles.newProfile, exact: true }).click();
  const name = dialog.locator(".targeted-profiles-field input").first();
  await name.fill("旅程配置");
  const save = dialog.getByRole("button", { name: profiles.save, exact: true });
  await dialog.getByText(copy.errors.profileItemsEmpty, { exact: true }).waitFor();
  assert(await save.isDisabled(), "A profile without items could be saved");
  const picker = dialog.locator(".item-picker");
  await picker.getByText(copy.itemPicker.count(0, 256), { exact: true }).waitFor();
  await picker.getByRole("searchbox", { name: copy.itemPicker.search, exact: true }).fill("cpu.temperature");
  await picker.locator(".item-picker-catalog .item-picker-entry button").first().click();
  await picker.getByText(copy.itemPicker.count(1, 256), { exact: true }).waitFor();
  const created = page.waitForResponse(response => response.url().endsWith("/api/targeted-recordings/profiles")
    && response.request().method() === "POST");
  await save.click();
  const createdResponse = await created;
  const createdProfile = await createdResponse.json() as RecordingProfile;
  assert(createdResponse.status() === 201 && createdProfile.name === "旅程配置" && createdProfile.items.length === 1
    && createdProfile.items[0].source === "auto" && createdProfile.items[0].syncPrecision === "mid", "Profile creation sent the wrong profile");

  // Rename, interval and precision stay unsaved (and say so) until Save sends one PATCH with the changed fields.
  await name.fill("旅程配置（改）");
  await dialog.getByText(profiles.unsaved, { exact: true }).first().waitFor();
  const interval = picker.locator(".item-picker-row input[type='text']").first();
  await interval.fill("50");
  await picker.getByText(copy.itemPicker.intervalInvalid, { exact: true }).waitFor();
  assert(await save.isDisabled(), "An out-of-range interval could be saved");
  await interval.fill("500");
  const precision = picker.locator(".item-picker-row [role='combobox']").nth(1);
  await precision.click();
  await page.getByRole("option", { name: copy.itemPicker.precisions.high, exact: true }).click();
  const patched = page.waitForResponse(response => response.url().endsWith(`/api/targeted-recordings/profiles/${createdProfile.id}`)
    && response.request().method() === "PATCH");
  await save.click();
  const patchBody = JSON.parse((await patched).request().postData()!) as ProfilePatch;
  assert(patchBody.name === "旅程配置（改）" && patchBody.items![0].intervalMs === 500 && patchBody.items![0].syncPrecision === "high"
    && !("defaultIntervalMs" in patchBody), `Profile save sent the wrong patch: ${JSON.stringify(patchBody)}`);
  await dialog.getByText(profiles.unsaved, { exact: true }).first().waitFor({ state: "detached" });

  const copied = page.waitForResponse(response => response.url().endsWith("/api/targeted-recordings/profiles")
    && response.request().method() === "POST");
  await dialog.getByRole("button", { name: profiles.copyProfile, exact: true }).click();
  const copyResponse = await copied;
  assert(copyResponse.status() === 201 && (JSON.parse(copyResponse.request().postData()!) as { copyFromId: string }).copyFromId === createdProfile.id,
    "Copy did not copy the selected profile");
  const copyProfile = await copyResponse.json() as RecordingProfile;
  // The dialog selects the copy once the list is reread; deleting before that would remove the profile selected before.
  await dialog.locator(`button[aria-pressed="true"]`).filter({ hasText: copyProfile.name }).waitFor();

  // Deleting asks in the app's confirm dialog first.
  const confirm = page.getByRole("dialog", { name: profiles.confirmDelete, exact: true });
  await remove.click();
  await confirm.getByRole("button", { name: profiles.delete, exact: true }).waitFor();
  const deleted = page.waitForResponse(response => response.url().endsWith(`/api/targeted-recordings/profiles/${copyProfile.id}`)
    && response.request().method() === "DELETE");
  await confirm.getByRole("button", { name: profiles.delete, exact: true }).click();
  assert((await deleted).status() === 204, "Profile deletion failed");
  await confirm.waitFor({ state: "detached" });
  await dialog.getByRole("button", { name: copyProfile.name, exact: true }).waitFor({ state: "detached" });
  await dialog.getByRole("button", { name: profiles.close, exact: true }).click();
  await dialog.waitFor({ state: "detached" });
  return createdProfile.id;
}

// The report detail shows the profile, one row per recorded item, the processes and the storage used out of the cap.
async function checkReportInfo(panel: Locator, copy: ReportCopy, language: string, started: StartedRecording, assert: JourneyAssert) {
  const info = copy.reportInfo;
  const detail = panel.locator(".targeted-report-detail:not([data-exit-ghost])");
  await detail.getByText(started.profileName, { exact: true }).waitFor();
  const items = detail.getByRole("region", { name: info.items, exact: true });
  await items.getByRole("columnheader", { name: info.columns.gaps, exact: true }).waitFor();
  assert(await items.locator("tbody tr").count() === started.items.length, `${language}: the items table lost rows`);
  // The temperature item has a gap in the mock (no value), listed with its reason.
  await items.getByText(new RegExp(info.gapReasons.valueUnavailable)).first().waitFor();
  const processes = detail.getByRole("region", { name: info.processes, exact: true });
  await processes.getByRole("rowheader", { name: "example.exe", exact: true }).waitFor();
  await detail.getByText(info.storageUsed((2048 / 1024).toFixed(1), "4096"), { exact: true }).waitFor();
}

// Result views (docs/targeted_recording_views_2026-10-30.md). The three default views are ordinary views built from the
// report's series. The journey adds a view, changes its kind and both axes, drags a horizontal range on a time view and
// resets it, then saves every view as a template, applies it back (appending) and deletes the template through the app dialog.
async function checkReportViews(page: Page, panel: Locator, app: AppCopy, copy: ReportCopy, language: string, assert: JourneyAssert) {
  const views = copy.views;
  const cards = panel.locator("section.targeted-report-view");
  const card = (name: string) => panel.getByRole("region", { name, exact: true }).first();
  const drawn = (scope: Locator, name: string) => scope.getByRole("img", { name, exact: true }).waitFor();
  // The standard select's trigger is a combobox; a searchable one is a button (its search field is the combobox).
  const trigger = (scope: Locator, field: string) => scope.getByRole("combobox", { name: field, exact: true })
    .or(scope.getByRole("button", { name: field, exact: true })).first();
  const choose = async (scope: Locator, field: string, option: string) => {
    await trigger(scope, field).click();
    await page.getByRole("listbox", { name: field, exact: true }).getByRole("option", { name: option, exact: true }).click();
  };
  const enabled = async (button: Locator, value: boolean) => page.waitForFunction(([element, value]: readonly [HTMLButtonElement | null, boolean]) => element!.disabled !== value,
    [await button.elementHandle() as import("playwright-core").ElementHandle<HTMLButtonElement> | null, value] as const);
  const defaults = [views.defaultNames.frameTime, views.defaultNames.frameTimeDistribution, views.defaultNames.softwareResources];
  for (const name of defaults) {
    await drawn(card(name), name);
    assert(await card(name).locator(".targeted-report-view-issues").count() === 0, `${language}: default view ${name} reports issues`);
  }
  assert(await cards.count() === 3, `${language}: expected the three default views, found ${await cards.count()}`);
  const distributionLegend = (await card(views.defaultNames.frameTimeDistribution).locator(".u-legend").textContent())!;
  assert(distributionLegend.includes(views.cdfOverlay), `${language}: frame time distribution has no cumulative overlay`);
  const resources = card(views.defaultNames.softwareResources);
  const legend = resources.locator(".u-legend");
  for (const metric of ["cpu.usage", "memory.usage"] as const) {
    assert((await legend.textContent())!.includes(`${copy.software} · ${app.metricLabel[metric]}`),
      `${language}: real ${metric} metric label missing`);
  }
  await resources.locator(".u-over").hover();
  await legend.filter({ hasText: "MiB" }).waitFor();

  await panel.getByRole("button", { name: views.add, exact: true }).click();
  const addedName = views.newViewName(4);
  const added = card(addedName);
  await drawn(added, addedName);
  assert(await added.getByRole("button", { name: views.closeEditor, exact: true }).getAttribute("aria-expanded") === "true",
    `${language}: a new view did not open its editor`);
  await choose(added, views.fields.kind, views.kinds.scatter);
  await added.locator(".targeted-report-view-kind").filter({ hasText: views.kinds.scatter }).waitFor();
  await choose(added, views.fields.x, views.x.frameIndex);
  await drawn(added, addedName);
  assert(await added.locator(".targeted-report-view-issues").count() === 0, `${language}: frame index axis lost the frame time series`);
  await choose(added, views.fields.x, views.x.series(`${copy.software} · ${app.metricLabel["cpu.usage"]}`));
  await drawn(added, addedName);
  assert(await added.locator(".targeted-report-view-issues").count() === 0, `${language}: series axis lost the frame time series`);
  const axis = `${views.fields.series(1)} · ${views.fields.axis}`;
  await choose(added, axis, views.axes.right);
  assert((await trigger(added, axis).textContent())!.includes(views.axes.right),
    `${language}: the series did not move to the right axis`);
  await drawn(added, addedName);

  const frameView = card(views.defaultNames.frameTime);
  const reset = frameView.getByRole("button", { name: views.resetRange, exact: true });
  assert(await reset.isDisabled(), `${language}: reset range was available while the range was automatic`);
  // The mouse moves in viewport coordinates: bring the plot into view before reading its box.
  await frameView.locator(".u-over").scrollIntoViewIfNeeded();
  const box = (await frameView.locator(".u-over").boundingBox())!;
  await page.mouse.move(box.x + box.width * 0.2, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width * 0.5, box.y + box.height / 2, { steps: 5 });
  await page.mouse.move(box.x + box.width * 0.8, box.y + box.height / 2, { steps: 5 });
  await page.mouse.up();
  await enabled(reset, true);
  await drawn(frameView, views.defaultNames.frameTime);
  await reset.click();
  await enabled(reset, false);

  const templates = panel.locator(".targeted-report-views-templates");
  const templateName = `journey ${language}`;
  await templates.getByRole("textbox", { name: views.templates.name, exact: true }).fill(templateName);
  await templates.getByRole("button", { name: views.templates.saveAll, exact: true }).click();
  await choose(templates, views.templates.label, templateName);
  await templates.getByRole("button", { name: views.templates.apply, exact: true }).click();
  await cards.nth(7).waitFor();
  assert(await cards.count() === 8, `${language}: applying a four-view template did not append four views`);
  await panel.getByRole("img", { name: views.defaultNames.softwareResources, exact: true }).nth(1).waitFor();
  await templates.getByRole("button", { name: views.templates.remove, exact: true }).click();
  const confirmRemove = page.getByRole("dialog", { name: views.templates.confirmRemove, exact: true });
  await confirmRemove.getByRole("button", { name: views.templates.remove, exact: true }).click();
  await confirmRemove.waitFor({ state: "detached" });
  assert(await cards.count() === 8, `${language}: deleting a template changed the report's views`);
}

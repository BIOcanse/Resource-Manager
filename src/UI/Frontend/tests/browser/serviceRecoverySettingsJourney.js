import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";

export default async function serviceRecoverySettingsJourney(page, baseUrl) {
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const patches = [];
  const errors = [];
  page.on("pageerror", error => errors.push(String(error)));
  let observed = {
    state: "mismatch", configuredResetPeriodSeconds: 86400,
    actual: { resetPeriodSeconds: 172800, actions: [
      { type: 1, delayMilliseconds: 37000 }, { type: 1, delayMilliseconds: 37000 }, { type: 0, delayMilliseconds: 0 }
    ], failureActionsOnNonCrashFailures: false },
    skipReason: null, writeError: null, readError: null
  };
  await page.route("**/api/settings/app", async route => {
    if (route.request().method() === "PATCH") patches.push(route.request().postDataJSON());
    const response = await route.fetch();
    const json = await response.json();
    await route.fulfill({ response, json: { ...json, serviceRecovery: observed } });
  });
  await page.setViewportSize({ width: 800, height: 500 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  let previous = "zh-CN";
  const checked = [];
  for (const option of languageOptions.filter(item => item.id !== "system")) {
    const oldApp = await loadAppCopy(previous);
    const oldText = await loadSettingsText(previous);
    await page.getByRole("navigation").getByRole("button", { name: oldApp.page.settings, exact: true }).click();
    await page.getByRole("button", { name: oldText.sections.appearance, exact: true }).click();
    await page.getByRole("button", { name: oldText.appearance.languageSelectLabel, exact: true }).click();
    await page.getByRole("option", { name: `${option.nativeLabel} · ${option.label}`, exact: true }).click();
    if (option.id !== previous) await page.getByRole("button", { name: oldText.actions.save, exact: true }).click();
    await page.waitForFunction(language => document.documentElement.lang === language, option.id);
    const text = await loadSettingsText(option.id);
    const copy = text.systemIntegration;
    await page.getByRole("button", { name: text.sections.systemIntegration, exact: true }).click();
    await page.getByText(copy.crashRecoveryDescription, { exact: true }).waitFor();
    const toggle = page.getByRole("checkbox", { name: copy.crashRecoveryTitle, exact: true });
    const delay = page.getByRole("textbox", { name: copy.crashRestartDelayTitle, exact: true });
    const count = page.getByRole("textbox", { name: copy.crashRestartCountTitle, exact: true });
    assert(await toggle.isChecked(), option.id + ": default must be on");
    assert(await delay.inputValue() === "10" && await delay.getAttribute("inputmode") === "numeric", option.id + ": delay bounds");
    assert(await count.inputValue() === "3" && await count.getAttribute("inputmode") === "numeric", option.id + ": count bounds");
    await page.getByText(copy.crashCounterResetDescription.replace("{days}", "1"), { exact: true }).waitFor();
    const status = page.getByRole("status").filter({ has: page.getByText(copy.crashRecoveryActualTitle, { exact: true }) });
    await status.getByText(copy.crashRecoveryMismatch, { exact: true }).waitFor();
    await status.getByText(copy.crashRestartCountTitle + ": 2", { exact: true }).waitFor();
    await status.getByText(copy.crashRestartDelayTitle + ": 37, 37", { exact: true }).waitFor();
    checked.push(option.id);
    previous = option.id;
  }
  const text = await loadSettingsText(previous);
  const copy = text.systemIntegration;
  const delay = page.getByRole("textbox", { name: copy.crashRestartDelayTitle, exact: true });
  const count = page.getByRole("textbox", { name: copy.crashRestartCountTitle, exact: true });
  const toggle = page.getByRole("checkbox", { name: copy.crashRecoveryTitle, exact: true });
  const save = async () => {
    const saved = page.waitForResponse(response => response.request().method() === "PATCH"
      && new URL(response.url()).pathname === "/api/settings/app");
    await page.getByRole("button", { name: text.actions.save, exact: true }).click();
    assert((await saved).ok(), "Recovery save failed");
  };
  await delay.fill("25"); await delay.blur();
  await count.fill("6"); await count.blur();
  observed = { ...observed, state: "failed", writeError: "test write denied" };
  await save();
  assert(patches.at(-1).changes.systemIntegration.crashRestartDelaySeconds === 25, "Delay was not patched");
  assert(patches.at(-1).changes.systemIntegration.crashRestartCount === 6, "Count was not patched");
  await page.getByText(copy.crashRecoveryWriteFailed, { exact: true }).waitFor();
  await toggle.uncheck();
  assert(await delay.isDisabled() && await count.isDisabled(), "Disabled recovery must disable numeric controls");
  await save();
  assert(patches.at(-1).changes.systemIntegration.restartAfterCrashEnabled === false, "Disabled preference was not saved");
  await page.reload({ waitUntil: "domcontentloaded" });
  const app = await loadAppCopy(previous);
  await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
  await page.getByRole("button", { name: text.sections.systemIntegration, exact: true }).click();
  assert(!(await toggle.isChecked()) && await delay.inputValue() === "25" && await count.inputValue() === "6", "Recovery preferences did not survive reload");
  await page.getByText(copy.crashRecoveryFailed, { exact: true }).waitFor();
  await page.getByText(copy.crashRestartCountTitle + ": 2", { exact: true }).waitFor();
  assert(errors.length === 0, errors.join("\n"));
  return { checked, patches: patches.length, actualReadBackDistinctFromRequested: true };
}

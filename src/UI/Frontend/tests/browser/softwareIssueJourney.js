import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.js";
export default async function softwareIssueJourney(page, baseUrl) {
  const failures = [];
  page.on("pageerror", (error) => failures.push(`pageerror: ${String(error)}`));
  page.on("console", (message) => {
    if (message.type() === "error") {
      failures.push(`console: ${message.text()}`);
    }
  });
  // This journey doesn't exercise report-rule settings, but the shared shell reads them.
  await page.route("**/api/settings/report-rules", route => route.fulfill({ json: { rules: [] } }));

  const assert = (condition, message) => {
    if (!condition) {
      throw new Error(message);
    }
  };
  const postScenario = async (value) => {
    const response = await fetch(`${baseUrl}/__test/scenario`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(value)
    });
    assert(response.ok, `Unable to update software issue scenario: ${response.status}`);
  };

  await page.setViewportSize({ width: 900, height: 620 });
  await postScenario({
    capabilities: "ready",
    settings: "ready",
    softwareIssues: true
  });
  // 配色是前端偏好：从用户保存过的浅色起步。
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await writeFrontendPreferences(page, value => { value.appearance.theme = "light"; });
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.documentElement.dataset.theme === "light", null, { timeout: 5000 });
  await openSoftwareInventory(page);

  const card = page.locator(".software-item").filter({ hasText: "示例软件" }).first();
  await card.waitFor({ state: "visible", timeout: 5000 });
  const tags = card.locator(".software-issue-tag");
  assert(await tags.count() === 3, "Software card did not render all static and dynamic issue tags");
  for (const label of ["存在已公开安全漏洞", "内存占用异常", "长系统中断"]) {
    await card.getByText(label, { exact: true }).waitFor({ state: "visible", timeout: 3000 });
  }

  const search = page.getByRole("searchbox", { name: /一般应用搜索/ });
  await search.fill("长系统中断");
  await card.waitFor({ state: "visible", timeout: 3000 });
  assert(await page.locator(".software-item").count() === 1,
    "Issue label search did not retain only the matching software card");
  await search.fill("");

  await card.getByRole("button", { name: "软件设置" }).click();
  const dialog = page.getByRole("dialog", { name: "示例软件" });
  await dialog.waitFor({ state: "visible", timeout: 5000 });
  await dialog.getByRole("heading", { name: "问题提示" }).waitFor({ state: "visible" });
  assert(await dialog.locator(".software-issue-detail-item").count() === 3,
    "Software details did not render every issue entry");
  assert(await dialog.getByText("当前报告", { exact: true }).count() === 2,
    "Dynamic issue provenance was not rendered");
  const cveLink = dialog.getByRole("link", { name: "CVE-2020-14979" });
  assert(await cveLink.getAttribute("href")
    === "https://nvd.nist.gov/vuln/detail/CVE-2020-14979",
  "Static issue reference was not preserved");
  await dialog.locator('button.icon-button[aria-label="关闭"]').click();
  await dialog.waitFor({ state: "hidden", timeout: 3000 });

  await postScenario({ softwareIssues: false });
  await page.locator(".panel-header").getByRole("button", { name: "刷新", exact: true }).click();
  await card.locator(".software-issue-tag-strip").waitFor({ state: "detached", timeout: 5000 });
  assert(await card.isVisible(), "Clearing report issues removed the software record itself");

  await postScenario({ softwareIssues: true });
  await page.locator(".panel-header").getByRole("button", { name: "刷新", exact: true }).click();
  await card.getByText("内存占用异常", { exact: true }).waitFor({ state: "visible", timeout: 5000 });

  const themeSnapshots = [];
  themeSnapshots.push(await readTagThemeSnapshot(page));
  for (const theme of ["dark", "lowContrast"]) {
    await writeFrontendPreferences(page, value => { value.appearance.theme = theme; });
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.waitForFunction(
      (expected) => document.documentElement.dataset.theme === expected,
      theme,
      { timeout: 5000 });
    await openSoftwareInventory(page);
    themeSnapshots.push(await readTagThemeSnapshot(page));
  }

  for (const snapshot of themeSnapshots) {
    assert(snapshot.tagCount === 3, `Issue tags missing in ${snapshot.theme}: ${JSON.stringify(snapshot)}`);
    assert(snapshot.withinCard && !snapshot.horizontalOverflow,
      `Issue tags overflowed their card in ${snapshot.theme}: ${JSON.stringify(snapshot)}`);
    assert(snapshot.warningMatchesTokens
      && snapshot.criticalMatchesTokens
      && snapshot.infoMatchesTokens,
    `Issue tags did not use theme tokens in ${snapshot.theme}: ${JSON.stringify(snapshot)}`);
  }
  let previous = "zh-CN";
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
    await page.getByRole("navigation", { name: app.management.categoryNav }).getByRole("button", { name: new RegExp(app.softwareKind.other) }).click();
    const localizedCard = page.locator(".software-item").filter({ hasText: "示例软件" }).first();
    await localizedCard.getByText(app.management.issueText.abnormal_memory_usage, { exact: true }).waitFor();
    await localizedCard.getByRole("button", { name: app.management.softwareSettings, exact: true }).click();
    const detail = page.getByRole("dialog", { name: "示例软件", exact: true });
    const detailText = await detail.textContent();
    for (const phrase of [app.softwareFacts.processCount(2), app.softwareFacts.serviceCount(3), app.softwareFacts.endpointState.online,
      app.backendMessage.software.noUninstallUnknownRoot]) {
      assert(detailText.includes(phrase), `${option.id}: software fact text missing: ${phrase}`);
    }
    await detail.getByText(app.management.issueText.winringMessage, { exact: true }).waitFor();
    assert((await detail.textContent()).includes("example.sys: 12 ms"), `${option.id}: driver evidence missing`);
    assert((await detail.textContent()).includes(app.management.issueText.memoryAdvice), `${option.id}: localized memory advice missing`);
    await detail.locator("button.icon-button").first().click();
    previous = option.id;
  }
  assert(failures.length === 0, `Browser failures: ${JSON.stringify(failures)}`);

  return {
    multipleTags: true,
    issueSearch: true,
    reportRemovalClearsTags: true,
    detailsAndReference: true,
    themes: themeSnapshots.map((snapshot) => snapshot.theme),
    pageErrors: failures
  };
}

async function openSoftwareInventory(page) {
  await page.getByRole("button", { name: "组件与软件", exact: true }).click();
  const navigation = page.getByRole("navigation", { name: "组件和软件分类" });
  await navigation.waitFor({ state: "visible", timeout: 5000 });
  await navigation.getByRole("button", { name: /一般应用/ }).click();
  await page.getByText("示例软件", { exact: true }).first()
    .waitFor({ state: "visible", timeout: 5000 });
}

async function readTagThemeSnapshot(page) {
  return page.evaluate(() => {
    const card = Array.from(document.querySelectorAll(".software-item"))
      .find((candidate) => candidate.textContent?.includes("示例软件"));
    if (!(card instanceof HTMLElement)) {
      throw new Error("Software card is unavailable for issue theme inspection");
    }
    const tags = Array.from(card.querySelectorAll(".software-issue-tag"));
    const resolveToken = (name) => {
      const probe = document.createElement("span");
      probe.style.color = `var(${name})`;
      document.body.append(probe);
      const value = getComputedStyle(probe).color;
      probe.remove();
      return value;
    };
    const matches = (selector, colorToken, backgroundToken) => {
      const element = card.querySelector(selector);
      if (!(element instanceof HTMLElement)) {
        return false;
      }
      const style = getComputedStyle(element);
      return style.color === resolveToken(colorToken)
        && style.backgroundColor === resolveToken(backgroundToken);
    };
    const cardRect = card.getBoundingClientRect();
    const tagRects = tags.map((tag) => tag.getBoundingClientRect());
    return {
      theme: document.documentElement.dataset.theme ?? "",
      tagCount: tags.length,
      withinCard: tagRects.every((rect) => rect.left >= cardRect.left - 1
        && rect.right <= cardRect.right + 1),
      horizontalOverflow: card.scrollWidth > card.clientWidth + 1,
      warningMatchesTokens: matches(".software-issue-tag.warning", "--warn", "--warn-soft"),
      criticalMatchesTokens: matches(".software-issue-tag.critical", "--danger", "--danger-soft"),
      infoMatchesTokens: matches(".software-issue-tag.info", "--accent-strong", "--accent-soft")
    };
  });
}

import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { checkRecordedDisplayDialog } from "./displayEdidDialogProbe.js";
import { frontendPreferencesKey, writeFrontendPreferences } from "./frontendPreferencesFixture.js";
export default async function themeConsistencyJourney(page, baseUrl = "http://127.0.0.1:4177") {
  const failures = [];
  page.on("pageerror", (error) => failures.push(`pageerror: ${String(error)}`));
  page.on("console", (message) => {
    if (message.type() === "error") {
      failures.push(`console: ${message.text()}`);
    }
  });

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
    assert(response.ok, `Unable to update theme scenario: ${response.status}`);
  };

  await page.setViewportSize({ width: 1100, height: 760 });
  await postScenario({
    capabilities: "ready",
    settings: "ready",
    settingsConflict: false,
    settingsGetDelayMs: 0,
    settingsPatchDelayMs: 0,
    settingsRevision: "theme-r1",
    displayEdid: "ready"
  });

  // 配色是前端偏好：从用户保存过的浅色起步。
  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await writeFrontendPreferences(page, value => { value.appearance.theme = "light"; });
  await page.reload({ waitUntil: "domcontentloaded" });
  await openAppearanceSettings(page);

  const edidThemes = [];
  const checkEdid = async () => {
    const app = await loadAppCopy("zh-CN");
    await page.getByRole("navigation").getByRole("button", { name: app.page.details, exact: true }).click();
    const topology = page.locator(".device-topology-panel"); await topology.waitFor();
    await topology.locator(".device-port-item").first().click();
    edidThemes.push({ theme: await page.locator("html").getAttribute("data-theme"), ...await checkRecordedDisplayDialog(page, app) });
    await openAppearanceSettings(page);
  };
  const snapshots = [];
  snapshots.push(await assertThemeIsConsistent(page, "light", assert));
  await checkEdid();

  for (const transition of [
    { label: "深色", theme: "dark" },
    { label: "低对比", theme: "lowContrast" },
    { label: "浅色", theme: "light" }
  ]) {
    await page.getByRole("radio", { name: transition.label, exact: true }).click();
    await page.getByRole("button", { name: "保存", exact: true }).click();
    await page.waitForFunction(
      (theme) => document.documentElement.dataset.theme === theme,
      transition.theme,
      { timeout: 5000 });
    snapshots.push(await assertThemeIsConsistent(page, transition.theme, assert));
    await checkEdid();
  }

  // 界面风格：配色清单随风格变化；换风格时配色在新风格里有就保留，没有就换成该风格的默认配色。
  // 风格只改外观，设置页各区域的位置和尺寸必须和标准风格一致。
  const styleGroup = page.getByRole("radiogroup", { name: "界面风格", exact: true });
  const paletteGroup = page.getByRole("radiogroup", { name: "配色", exact: true });
  const standardLayout = await settingsLayout(page);
  const styleResults = [];
  for (const transition of [
    { label: "柔和", uiStyle: "soft", palettes: ["跟随系统", "浅色", "深色"], kept: "浅色", pick: "深色", theme: "dark", scheme: "dark" },
    { label: "终端", uiStyle: "terminal", palettes: ["磷绿", "琥珀", "冰蓝"], kept: "磷绿", pick: "琥珀", theme: "amber", scheme: "dark" },
    { label: "线框", uiStyle: "outline", palettes: ["跟随系统", "纸面", "石墨", "蓝图"], kept: "跟随系统", pick: "纸面", theme: "paper", scheme: "light" },
    { label: "标准", uiStyle: "standard", palettes: ["跟随系统", "浅色", "深色", "低对比"], kept: "跟随系统", pick: "浅色", theme: "light", scheme: "light" }
  ]) {
    await styleGroup.getByRole("radio", { name: transition.label, exact: true }).click();
    const palettes = await paletteGroup.getByRole("radio").allTextContents();
    assert(JSON.stringify(palettes.map((label) => label.trim())) === JSON.stringify(transition.palettes),
      `${transition.uiStyle} offers unexpected palettes: ${JSON.stringify(palettes)}`);
    const checked = await paletteGroup.getByRole("radio", { checked: true }).textContent();
    assert(checked?.trim() === transition.kept,
      `${transition.uiStyle} did not keep or default the palette: ${checked}`);
    await paletteGroup.getByRole("radio", { name: transition.pick, exact: true }).click();
    await page.getByRole("button", { name: "保存", exact: true }).click();
    await page.waitForFunction(
      ({ uiStyle, theme }) => document.documentElement.dataset.uiStyle === uiStyle
        && document.documentElement.dataset.theme === theme,
      transition,
      { timeout: 5000 });
    const applied = await page.evaluate((key) => {
      const resolve = (name) => {
        const probe = document.createElement("span");
        probe.style.color = `var(${name})`;
        document.body.append(probe);
        const color = getComputedStyle(probe).color;
        probe.remove();
        return color;
      };
      return {
        colorScheme: getComputedStyle(document.documentElement).colorScheme,
        background: resolve("--bg"),
        bodyBackground: getComputedStyle(document.body).backgroundColor,
        surface: resolve("--surface"),
        contentBackground: getComputedStyle(document.querySelector(".settings-content")).backgroundColor,
        cache: JSON.parse(localStorage.getItem(key) ?? "null")?.appearance
      };
    }, frontendPreferencesKey);
    assert(applied.colorScheme === transition.scheme,
      `${transition.uiStyle}/${transition.theme} uses the wrong color scheme: ${JSON.stringify(applied)}`);
    assert(applied.bodyBackground === applied.background && applied.contentBackground === applied.surface,
      `${transition.uiStyle}/${transition.theme} surfaces diverged from its tokens: ${JSON.stringify(applied)}`);
    assert(applied.cache?.uiStyle === transition.uiStyle && applied.cache?.theme === transition.theme,
      `Frontend preferences do not hold the style and palette: ${JSON.stringify(applied.cache)}`);
    const layout = await settingsLayout(page);
    const moved = Object.keys(standardLayout).filter((key) => {
      const before = standardLayout[key];
      const after = layout[key];
      return !after || ["x", "y", "width", "height"].some((side) => Math.abs(before[side] - after[side]) > 1);
    });
    assert(moved.length === 0,
      `${transition.uiStyle} moved settings layout: ${JSON.stringify(moved.map((key) => [key, standardLayout[key], layout[key]]))}`);
    styleResults.push({ uiStyle: transition.uiStyle, theme: transition.theme, background: applied.background });
  }
  assert(new Set(styleResults.map((result) => result.background)).size === styleResults.length,
    `Styles share a background and are not distinguishable: ${JSON.stringify(styleResults)}`);

  // 重启后启动外观和界面读同一份前端偏好：最后保存的标准风格、浅色配色保留。
  await page.reload({ waitUntil: "domcontentloaded" });
  await openAppearanceSettings(page);
  snapshots.push(await assertThemeIsConsistent(page, "light", assert));
  assert(await page.locator("html").getAttribute("data-ui-style") === "standard",
    "The saved interface style did not survive a reload");
  await checkEdid();

  assert(failures.length === 0, `Browser failures: ${JSON.stringify(failures)}`);
  return {
    edidThemes,
    modes: snapshots.map((snapshot) => snapshot.theme),
    styles: styleResults,
    savedAppearanceSurvivedReload: snapshots.at(-1)?.theme === "light",
    pageErrors: failures
  };
}

// 设置页主要区域的位置和尺寸，用来确认换风格不动布局。
async function settingsLayout(page) {
  return page.evaluate(() => {
    const entries = {};
    // 只比结构区域：换字体后控件宽度和说明文字换行可以不同。
    const selectors = [".settings-content", ".settings-content-header", ".settings-nav-item"];
    for (const selector of selectors) {
      document.querySelectorAll(selector).forEach((element, index) => {
        const rect = element.getBoundingClientRect();
        entries[`${selector}#${index}`] = { x: rect.x, y: rect.y, width: rect.width, height: rect.height };
      });
    }
    return entries;
  });
}

async function openAppearanceSettings(page) {
  await page.getByRole("button", { name: "设置", exact: true }).click();
  await page.locator(".settings-readonly-boundary").waitFor({
    state: "visible",
    timeout: 7000
  });
  await page.getByRole("button", { name: "外观", exact: true }).click();
}

async function assertThemeIsConsistent(page, expectedTheme, assert) {
  await page.waitForFunction(
    (theme) => document.documentElement.dataset.theme === theme,
    expectedTheme,
    { timeout: 5000 });
  await page.waitForFunction(() => {
    const probe = document.createElement("span");
    probe.style.color = "var(--divider-soft)";
    document.body.append(probe);
    const divider = getComputedStyle(probe).color;
    probe.remove();
    const row = Array.from(document.querySelectorAll(".settings-row"))
      .find((candidate) => {
        const style = getComputedStyle(candidate);
        return style.borderBottomStyle !== "none"
          && Number.parseFloat(style.borderBottomWidth) > 0;
      });
    return row !== undefined && getComputedStyle(row).borderBottomColor === divider;
  }, undefined, { timeout: 1500 });

  const snapshot = await page.evaluate(() => {
    const root = document.documentElement;
    const body = document.body;
    const appRoot = document.getElementById("root");
    const content = document.querySelector(".settings-content");
    const contentHeader = document.querySelector(".settings-content-header");
    const settingsRow = Array.from(document.querySelectorAll(".settings-row"))
      .find((candidate) => {
        const style = getComputedStyle(candidate);
        return style.borderBottomStyle !== "none"
          && Number.parseFloat(style.borderBottomWidth) > 0;
      });
    if (!appRoot || !content || !contentHeader || !settingsRow) {
      throw new Error("Theme probe surface is unavailable.");
    }

    const resolveToken = (name) => {
      const probe = document.createElement("span");
      probe.style.color = `var(${name})`;
      body.append(probe);
      const resolved = getComputedStyle(probe).color;
      probe.remove();
      return resolved;
    };
    const rootStyle = getComputedStyle(root);
    const bodyStyle = getComputedStyle(body);
    const tokenChannels = (name) => {
      const value = rootStyle.getPropertyValue(name).trim();
      const match = value.match(
        /color\(display-p3\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)/);
      if (!match) {
        return null;
      }
      return match.slice(1, 4).map(Number);
    };
    const tokenChannelSpread = (name) => {
      const channels = tokenChannels(name);
      return channels === null ? null : Math.max(...channels) - Math.min(...channels);
    };
    const tokenDistance = (leftName, rightName) => {
      const left = tokenChannels(leftName);
      const right = tokenChannels(rightName);
      if (left === null || right === null) {
        return null;
      }
      return left.reduce((sum, value, index) => sum + Math.abs(value - right[index]), 0)
        / left.length;
    };
    return {
      theme: root.dataset.theme ?? "",
      legacyPrebootTheme: root.getAttribute("data-preboot-theme"),
      bodyTheme: body.getAttribute("data-theme"),
      colorScheme: rootStyle.colorScheme,
      bodyColorScheme: bodyStyle.colorScheme,
      background: resolveToken("--bg"),
      surface: resolveToken("--surface"),
      divider: resolveToken("--divider"),
      dividerSoft: resolveToken("--divider-soft"),
      dividerChannelSpread: tokenChannelSpread("--divider"),
      dividerSoftChannelSpread: tokenChannelSpread("--divider-soft"),
      dividerSurfaceDistance: tokenDistance("--divider", "--surface"),
      dividerSoftSurfaceDistance: tokenDistance("--divider-soft", "--surface"),
      rootBackground: rootStyle.backgroundColor,
      bodyBackground: bodyStyle.backgroundColor,
      appBackground: getComputedStyle(appRoot).backgroundColor,
      contentBackground: getComputedStyle(content).backgroundColor,
      contentHeaderDivider: getComputedStyle(contentHeader).borderBottomColor,
      settingsRowDivider: getComputedStyle(settingsRow).borderBottomColor
    };
  });

  const expectedScheme = expectedTheme === "dark" ? "dark" : "light";
  assert(snapshot.theme === expectedTheme,
    `Unexpected root theme: ${JSON.stringify(snapshot)}`);
  assert(snapshot.legacyPrebootTheme === null && snapshot.bodyTheme === null,
    `A legacy theme owner remains active: ${JSON.stringify(snapshot)}`);
  assert(snapshot.colorScheme === expectedScheme && snapshot.bodyColorScheme === expectedScheme,
    `Native control color scheme diverged: ${JSON.stringify(snapshot)}`);
  assert(snapshot.rootBackground === snapshot.background
    && snapshot.bodyBackground === snapshot.background
    && snapshot.appBackground === snapshot.background,
  `Document backgrounds diverged from the theme token: ${JSON.stringify(snapshot)}`);
  assert(snapshot.contentBackground === snapshot.surface,
    `Primary surface diverged from the theme token: ${JSON.stringify(snapshot)}`);
  assert(snapshot.contentHeaderDivider === snapshot.divider,
    `Section divider diverged from the theme token: ${JSON.stringify(snapshot)}`);
  assert(snapshot.settingsRowDivider === snapshot.dividerSoft,
    `Row divider diverged from the soft theme token: ${JSON.stringify(snapshot)}`);
  assert(snapshot.dividerChannelSpread !== null && snapshot.dividerChannelSpread <= 0.02,
    `Divider has an unintended color cast: ${JSON.stringify(snapshot)}`);
  assert(snapshot.dividerSoftChannelSpread !== null && snapshot.dividerSoftChannelSpread <= 0.02,
    `Soft divider has an unintended color cast: ${JSON.stringify(snapshot)}`);
  assert(snapshot.dividerSurfaceDistance !== null && snapshot.dividerSurfaceDistance >= 0.06,
    `Divider is not distinguishable from its surface: ${JSON.stringify(snapshot)}`);
  assert(snapshot.dividerSoftSurfaceDistance !== null && snapshot.dividerSoftSurfaceDistance >= 0.06,
    `Soft divider is not distinguishable from its surface: ${JSON.stringify(snapshot)}`);
  return snapshot;
}

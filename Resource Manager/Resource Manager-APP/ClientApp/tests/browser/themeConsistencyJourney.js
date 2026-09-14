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
    settingsTheme: "light"
  });

  await page.goto(`${baseUrl}/`, { waitUntil: "domcontentloaded" });
  await page.evaluate(() => localStorage.setItem(
    "resource-manager:appearance",
    JSON.stringify({ theme: "dark" })));
  await page.reload({ waitUntil: "domcontentloaded" });
  await openAppearanceSettings(page);

  const snapshots = [];
  snapshots.push(await assertThemeIsConsistent(page, "light", assert));

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
  }

  await page.evaluate(() => localStorage.setItem(
    "resource-manager:appearance",
    JSON.stringify({ theme: "dark" })));
  await page.reload({ waitUntil: "domcontentloaded" });
  await openAppearanceSettings(page);
  snapshots.push(await assertThemeIsConsistent(page, "light", assert));

  assert(failures.length === 0, `Browser failures: ${JSON.stringify(failures)}`);
  return {
    modes: snapshots.map((snapshot) => snapshot.theme),
    stalePrebootConverged: snapshots[0].theme === "light"
      && snapshots.at(-1)?.theme === "light",
    pageErrors: failures
  };
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

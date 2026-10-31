import { checkDisclosureFrames, toggleDisclosure } from "./disclosureFrames.js";
import { readFileSync } from "node:fs";
import { waitForStableRendering } from "./journeyWaits.js";
import { creditSources } from "../../src/i18n/settingsCredits.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";

/**
 * 致谢页（docs/credits_sources.md），简体中文：
 * - 每个子项目显示官方原文和“来源”，没有官方简介的不显示说明；按钮只用固定词；
 * - 主列表和依赖表的按钮都在固定的列里：同一列的按钮 x 相同、等高，1280 和 760 宽都不溢出；
 * - 鼠标点开链接后不留焦点，键盘焦点才有焦点环；
 * - 完整依赖鸣谢是四列表，名单等于锁文件加解析的 .NET 包；展开和收起由磁石带：正常档有中间帧，无动画档直接到终态。
 */
export default async function creditsJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  const settings = await loadSettingsText("zh-CN");
  const credits = settings.credits;
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "设置", exact: true }).click();
  const openCredits = () => page.getByRole("button", { name: settings.sections.credits, exact: true }).click();
  await openCredits();
  // 换分区时旧分区留着淡出的幽灵（不可命中、去掉了身份），按类名找时排除它。
  const panel = page.locator(".settings-credits-panel:not([data-exit-ghost]):not([data-exit-ghost] *)");
  await panel.getByText(credits.heroTitle, { exact: true }).waitFor({ timeout: 7000 });
  // 外部链接不真的打开：捕获阶段拦掉默认动作，组件自己的点击处理照常运行。
  await page.evaluate(() => window.addEventListener("click", (event) => {
    if (event.target instanceof Element && event.target.closest(".settings-credits-panel a")) event.preventDefault();
  }, true));

  const result = await panel.evaluate((element) => ({
    groups: Array.from(element.querySelectorAll(".settings-credit-group"), (group) => ({
      title: group.querySelector("h3")?.textContent,
      names: Array.from(group.querySelectorAll(".settings-credit-title strong"), (name) => name.textContent)
    })),
    text: element.textContent,
    quotes: Array.from(element.querySelectorAll(".settings-credit-subject q"), (quote) => ({ text: quote.textContent, cite: quote.cite })),
    links: Array.from(element.querySelectorAll("a"), (link) => ({ href: link.href, target: link.target, rel: link.rel, text: link.textContent.trim() }))
  }));
  assert(result.groups.length === 6, "Credits did not render all six groups");
  const runtime = result.groups.find((group) => group.title === credits.groups.runtime);
  assert(runtime?.names.includes("Lucide / Feather") && runtime.names.includes("Microsoft WebView2"), "Runtime credits are missing");
  assert(result.groups.find((group) => group.title === credits.groups.build)?.names.includes("Zig"),
    "Build tools were not separated from runtime components");
  assert(!result.text.includes(atob("S2luZ3M=")), "Personal introduction must not be shown");

  // 官方原文：逐字、带来源；没有官方简介的子项目不显示说明。
  const expected = creditSources.flatMap((item) => item.subjects).filter((subject) => subject.description).map((subject) => subject.description);
  assert(result.quotes.length === expected.length, `Expected ${expected.length} official descriptions, saw ${result.quotes.length}`);
  for (const description of expected) {
    assert(result.quotes.some((quote) => quote.text === description.text && quote.cite === description.source),
      `Official description missing or changed: ${description.text}`);
  }
  for (const phrase of ["同样感谢 .NET 与 WebView2", "本项目采用 Apache-2.0", "用于读取"]) {
    assert(!result.text.includes(phrase), `A self-written description is still shown: ${phrase}`);
  }
  const sourceLinks = await panel.locator(".settings-credit-source").count();
  assert(sourceLinks === expected.length, "Every official description needs its source link");

  // 按钮：固定词、安全的外链属性、不是网址。
  const words = new Set(Object.values(credits.linkLabels).concat(credits.sourceLabel));
  assert(result.links.every((link) => words.has(link.text)), `Link labels outside the fixed words: ${[...new Set(result.links.map((link) => link.text))].filter((text) => !words.has(text))}`);
  assert(result.links.every((link) => link.href.startsWith("https://") && link.target === "_blank"
    && link.rel.includes("noopener") && link.rel.includes("noreferrer")), "Credits contain unsafe or broken link attributes");
  assert(await panel.getByRole("link", { name: /star|点星|點星/i }).count() === 0, "No Star buttons");

  /** 主列表：每种按钮一列，同一种按钮在所有分组、所有行里左边 x 相同、宽相同；列按固定的种类顺序排；所有按钮等高；整块不溢出。 */
  const kindOrder = ["official", "github", "docs", "nuget", "npm", "license"];
  const mainColumns = () => panel.evaluate((element) => {
    const kinds = new Map();
    const heights = new Set();
    for (const link of element.querySelectorAll(".settings-credit-table .settings-credit-link")) {
      const rect = link.getBoundingClientRect();
      const entry = kinds.get(link.dataset.kind) ?? { lefts: new Set(), widths: new Set(), count: 0 };
      entry.lefts.add(Math.round(rect.left));
      entry.widths.add(Math.round(rect.width));
      entry.count += 1;
      kinds.set(link.dataset.kind, entry);
      heights.add(Math.round(rect.height));
    }
    return {
      width: element.clientWidth,
      scrollWidth: element.scrollWidth,
      overflowingItems: Array.from(element.querySelectorAll(".settings-credit-item")).filter((item) => item.scrollWidth > item.clientWidth + 1).length,
      // 名称与分组的文字不能压到子项目文字上（按文字实际占的范围量，不按格子）。
      overlaps: Array.from(element.querySelectorAll(".settings-credit-item")).filter((item) => {
        const range = document.createRange();
        range.selectNodeContents(item.querySelector(".settings-credit-title"));
        const title = range.getBoundingClientRect();
        return Array.from(item.querySelectorAll(".settings-credit-subject")).some((subject) => {
          const box = subject.getBoundingClientRect();
          return title.right > box.left + 1 && title.left < box.right - 1 && title.bottom > box.top + 1 && title.top < box.bottom - 1;
        });
      }).map((item) => item.querySelector(".settings-credit-title strong").textContent),
      kinds: [...kinds].map(([kind, entry]) => ({ kind, count: entry.count, lefts: [...entry.lefts], widths: [...entry.widths] })),
      heights: [...heights]
    };
  });
  const layouts = [];
  for (const width of [1280, 760]) {
    await page.setViewportSize({ width, height: 800 });
    await waitForStableRendering(page, [".settings-credits-panel", ".settings-credit-item", ".settings-credit-link"], { timeoutMs: 3000 });
    const layout = await mainColumns();
    assert(layout.scrollWidth <= layout.width + 1 && layout.overflowingItems === 0, `Credits overflow at ${width}px: ${JSON.stringify(layout)}`);
    assert(layout.overlaps.length === 0, `Credit names run into the descriptions at ${width}px: ${layout.overlaps}`);
    assert(layout.kinds.length >= 3 && layout.kinds.every((kind) => kind.lefts.length === 1 && kind.widths.length === 1),
      `The same kind of credit button sits at different x at ${width}px: ${JSON.stringify(layout.kinds)}`);
    const byX = [...layout.kinds].sort((a, b) => a.lefts[0] - b.lefts[0]).map((kind) => kind.kind);
    assert(JSON.stringify(byX) === JSON.stringify(kindOrder.filter((kind) => byX.includes(kind))),
      `Credit button columns are not in the fixed kind order at ${width}px: ${byX}`);
    assert(new Set(layout.kinds.map((kind) => kind.lefts[0])).size === layout.kinds.length, `Two kinds share one column at ${width}px`);
    assert(layout.heights.length === 1, `Credit buttons differ in height at ${width}px: ${layout.heights}`);
    layouts.push({ viewportWidth: width, ...layout });
  }
  // 页面上没有任何一行用到的种类不出列。
  const usedKinds = new Set(creditSources.flatMap((item) => item.subjects.flatMap((subject) => subject.links.map((link) => link.kind))));
  assert(JSON.stringify(layouts[0].kinds.map((kind) => kind.kind).sort()) === JSON.stringify([...usedKinds].sort()),
    `Button columns must be exactly the kinds in use: ${layouts[0].kinds.map((kind) => kind.kind)}`);
  // 来源是文字链接，和原文隔开、颜色不同于原文。
  const source = await panel.evaluate((element) => {
    const link = element.querySelector(".settings-credit-source");
    const quote = link.parentElement.querySelector("q");
    return { linkColor: getComputedStyle(link).color,
      quoteColor: getComputedStyle(quote).color, underline: getComputedStyle(link).textDecorationLine };
  });
  assert(source.linkColor !== source.quoteColor && source.underline.includes("underline"), `The source link reads as part of the quote: ${JSON.stringify(source)}`);
  await page.setViewportSize({ width: 1280, height: 800 });

  // 焦点：鼠标点开后不留焦点；键盘焦点有焦点环。
  const firstLink = panel.locator(".settings-credit-list .settings-credit-link").first();
  await firstLink.click();
  assert(await firstLink.evaluate((link) => document.activeElement !== link), "A clicked credit link kept the focus");
  await firstLink.focus();
  await page.keyboard.press("Shift+Tab");
  await page.keyboard.press("Tab");
  const keyboardFocus = await page.evaluate(() => {
    const active = document.activeElement;
    return { link: active?.classList.contains("settings-credit-link") || active?.classList.contains("settings-credit-source"),
      visible: active?.matches(":focus-visible"), outline: active ? getComputedStyle(active).outlineStyle : "" };
  });
  assert(keyboardFocus.link && keyboardFocus.visible && keyboardFocus.outline !== "none", `Keyboard focus has no ring: ${JSON.stringify(keyboardFocus)}`);

  // 完整依赖鸣谢：折叠标题，收起时内容 inert。
  const header = panel.locator(".settings-credit-dependencies .ui-disclosure-header");
  const body = panel.locator(".settings-credit-dependencies .ui-disclosure-body");
  assert((await header.textContent()).includes(credits.dependencyListTitle), "The dependency disclosure header is missing");
  assert(await header.getAttribute("aria-expanded") === "false" && await body.evaluate((element) => element.inert), "Dependencies must start collapsed and inert");
  /** 点一下标题，按帧读内容体高度、内容和幽灵的不透明度（disclosureFrames.js）：布局当帧到位，画面是淡入、淡出。 */
  const toggleFrames = () => toggleDisclosure(page, ".settings-credit-dependencies .ui-disclosure-header");
  /** 设置 → 外观 → 动画，保存后回到致谢页（致谢组件重新挂上，依赖表回到收起）。 */
  const setAnimation = async (id) => {
    await page.getByRole("button", { name: settings.sections.appearance, exact: true }).click();
    const label = settings.animationStyleOptions.find((option) => option.id === id).label;
    await page.getByRole("radiogroup", { name: settings.appearance.animationStyleTitle, exact: true }).getByRole("radio", { name: label, exact: true }).click();
    await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
    await page.waitForFunction((mode) => document.body.dataset.animations === mode, id, { timeout: 5000 });
    await openCredits();
    await header.waitFor();
  };
  if (await page.evaluate(() => document.body.dataset.animations) !== "desktop") await setAnimation("desktop");
  const opened = await toggleFrames();
  const full = opened.frames.at(-1).height;
  assert(full > 100 && !opened.inert, `Dependencies did not open fully: ${JSON.stringify(opened.frames.at(-1))}`);
  const openProblems = checkDisclosureFrames(opened, { moving: true, opening: true });
  assert(openProblems.length === 0, `Normal motion, opening: ${openProblems.join("; ")}`);

  const dependencies = panel.locator(".settings-credit-dependency-table tbody tr");
  const lock = JSON.parse(readFileSync(new URL("../../package-lock.json", import.meta.url), "utf8"));
  const managed = JSON.parse(readFileSync(new URL("../../src/i18n/managedDependencyAcknowledgements.json", import.meta.url), "utf8"));
  const expectedDependencies = Object.entries(lock.packages).filter(([path]) => path !== "").map(([path, item]) => ({
    name: path.slice(path.lastIndexOf("node_modules/") + "node_modules/".length), version: item.version
  })).concat(managed);
  assert(await dependencies.count() === expectedDependencies.length, "The exact locked frontend and managed closure must be acknowledged");
  const table = await panel.evaluate((element) => {
    const scroll = element.querySelector(".settings-credit-dependency-scroll");
    const rows = Array.from(element.querySelectorAll(".settings-credit-dependency-table tbody tr"));
    return {
      headers: Array.from(element.querySelectorAll(".settings-credit-dependency-table thead th"), (cell) => cell.textContent.trim()),
      rows: rows.map((row) => Array.from(row.children, (cell) => cell.textContent.trim())),
      buttonLefts: [...new Set(rows.map((row) => Math.round(row.querySelector("a").getBoundingClientRect().left)))],
      buttonWidths: [...new Set(rows.map((row) => Math.round(row.querySelector("a").getBoundingClientRect().width)))],
      numeric: [...new Set(Array.from(element.querySelectorAll(".settings-credit-dependency-table .settings-credit-number"), (cell) => getComputedStyle(cell).fontVariantNumeric))],
      bullets: Array.from(element.querySelectorAll(".settings-credit-dependencies li")).length,
      scrollable: scroll.scrollHeight > scroll.clientHeight,
      stickyHeader: getComputedStyle(element.querySelector(".settings-credit-dependency-table thead th")).position
    };
  });
  const columns = credits.dependencyColumns;
  assert(JSON.stringify(table.headers) === JSON.stringify([columns.name, columns.version, columns.license, columns.source]), `Dependency headers: ${table.headers}`);
  assert(expectedDependencies.every((item) => table.rows.some((row) => row[0] === item.name && row[1] === item.version)), "Every name/version must render in its own column");
  assert(table.rows.every((row) => row[3] === credits.linkLabels.npm || row[3] === credits.linkLabels.nuget), "Every dependency needs its npm or NuGet button");
  // tslib left the resolved graph in the dependency upgrade; use a current permissive dependency.
  assert(table.rows.some((row) => row[0] === "picocolors" && row[2] === "ISC"), "Permissive terms are not a reason to omit acknowledgement");
  assert(readFileSync(new URL("../../../../Core/ThirdPartyNotices/Picocolors.LICENSE.txt", import.meta.url), "utf8")
    === readFileSync(new URL("../../node_modules/picocolors/LICENSE", import.meta.url), "utf8"), "Retain picocolors' original acknowledgement and license text");
  // The upgraded test platform no longer depends on Newtonsoft.Json; xunit still resolves this transitive package.
  assert(table.rows.some((row) => row[0] === "xunit.abstractions" && row[1] === "2.0.3"), "Transitive managed test dependencies are missing");
  assert(table.buttonLefts.length === 1, `Dependency buttons are not in one column: ${table.buttonLefts}`);
  assert(table.numeric.every((value) => value.includes("tabular-nums")), `Versions and licenses need tabular numbers: ${table.numeric}`);
  assert(table.bullets === 0, "The dependency list still has bullets");
  assert(table.scrollable && table.stickyHeader === "sticky", "The dependency table must scroll inside its own region under a sticky header");
  await panel.locator(".settings-credit-dependency-scroll").evaluate((element) => { element.scrollTop = element.scrollHeight; });
  assert(await panel.locator(".settings-credit-dependency-table tbody tr").last().isVisible(), "The last dependency cannot be scrolled into view");

  const closed = await toggleFrames();
  assert(closed.frames.at(-1).height === 0 && closed.inert, `Dependencies did not close: ${JSON.stringify(closed.frames.at(-1))}`);
  const closeProblems = checkDisclosureFrames(closed, { moving: true, opening: false });
  assert(closeProblems.length === 0, `Normal motion, closing: ${closeProblems.join("; ")}`);

  // 减弱版：无动画档直接到终态。
  await setAnimation("none");
  const instantOpen = await toggleFrames();
  const instantOpenProblems = checkDisclosureFrames(instantOpen, { moving: false, opening: true });
  assert(instantOpen.frames.at(-1).height > 100 && instantOpenProblems.length === 0, `No motion, opening: ${instantOpenProblems.join("; ")}`);
  const instantClose = await toggleFrames();
  const instantCloseProblems = checkDisclosureFrames(instantClose, { moving: false, opening: false });
  assert(instantCloseProblems.length === 0, `No motion, closing: ${instantCloseProblems.join("; ")}`);
  // 恢复正常档，后面的旅程从默认状态开始。
  await setAnimation("desktop");

  assert(errors.length === 0, `Page errors: ${errors.join("; ")}`);
  return {
    groupCount: result.groups.length, officialDescriptions: result.quotes.length, linkCount: result.links.length, layouts,
    dependencyRows: table.rows.length, normalOpenFrames: opened.frames.length, pageErrors: errors
  };
}

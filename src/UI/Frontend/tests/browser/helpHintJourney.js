import assert from "node:assert/strict";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { languageOptions } from "../../src/i18n/settingsLanguages.ts";
import { seedFrontendPreferences, writeFrontendPreferences } from "./frontendPreferencesFixture.js";

/*
 * 问号说明（src/ui/primitives/HelpHint.tsx）在资源列表计数旁边：
 * - 计数徽标不再带原生 title，计数后面紧跟一个问号按钮；说明画在我们自己的弹层上（role=tooltip，Portal 到 body），问号的 aria-describedby 指向它。
 * - 悬停问号打开；指针一像素一像素斜着从问号移进说明（先从问号侧边出去，经过两者之间的空隙），途中一直开着；从问号横着走开就关上；离开两者就关上，关上时由呈现层留一份只能看见的幽灵。
 * - 指针从问号或说明一步跳到远处停下（page.mouse.move 一步，之后不再移动）：当场关上，不等下一次移动。
 * - 常驻页被隐藏（离开监视页时只标 aria-hidden、content-visibility: hidden，不卸下）：说明当场关上，不管是悬停还是焦点打开的，指针和焦点没动也一样。
 * - 说明里的字能拖动选中，拖出说明外松开也不关，选区只停在说明里的字上（不会选到后面页面的内容）；Esc 关上并把焦点交还问号。
 * - 键盘：Shift+Tab 从搜索框回到问号就打开，Esc 关上、焦点留在问号，再点问号重新打开；焦点在别处、只靠悬停打开时，Esc 只关说明不抢焦点。
 * - 点问号打开后指针移开仍开着，在别处按下才关。
 * - 说明整个在窗口里，离问号 6 px，不盖住问号，水平上跨过问号。
 * - 无动画风格下当场出现、当场消失（没有退场幽灵）。
 * - 九种语言在最小窗口（760×520 的客户区）下：问号的名称和说明是这种语言的；问号不给标题行和页面添溢出（和去掉问号时比），不和右侧操作区重叠；悬停打开，说明在窗口里。
 */
const languages = languageOptions.filter((option) => option.id !== "system").map((option) => option.id);

const live = ".help-hint-popover:not([data-exit-ghost])";

async function boxes(page) {
  return page.evaluate((selector) => {
    const rect = (element) => {
      if (!element) return null;
      const r = element.getBoundingClientRect();
      return { left: r.left, right: r.right, top: r.top, bottom: r.bottom, width: r.width, height: r.height };
    };
    const button = document.querySelector(".resource-table-title .help-hint");
    const popover = document.querySelector(selector);
    return {
      button: rect(button),
      popover: rect(popover),
      placement: popover?.getAttribute("data-placement") ?? null,
      viewport: { width: document.documentElement.clientWidth, height: document.documentElement.clientHeight }
    };
  }, live);
}

const isOpen = (page) => page.evaluate((selector) => document.querySelector(selector) !== null, live);

async function waitClosed(page, message) {
  await page.waitForFunction((selector) => document.querySelector(selector) === null, live, { timeout: 2000 })
    .catch(() => assert.fail(message));
}

/** 说明整个在视口里，离问号 6 px（不盖住问号），水平上跨过问号。 */
function assertPlaced(layout, label) {
  const { button, popover, viewport } = layout;
  assert.ok(button && popover, `${label}: the question mark and its explanation are on the page`);
  assert.ok(popover.left >= 0 && popover.right <= viewport.width && popover.top >= 0 && popover.bottom <= viewport.height,
    `${label}: the explanation stays inside the window ${JSON.stringify(layout)}`);
  const gap = layout.placement === "above" ? button.top - popover.bottom : popover.top - button.bottom;
  assert.ok(Math.abs(gap - 6) <= 0.5, `${label}: the explanation is not 6 px from the question mark ${JSON.stringify(layout)}`);
  assert.ok(popover.left <= button.left && popover.right >= button.right, `${label}: the explanation does not span the question mark ${JSON.stringify(layout)}`);
}

export default async function helpHintJourney(page, baseUrl) {
  const failures = [];
  page.on("pageerror", (error) => failures.push(String(error)));
  await seedFrontendPreferences(page, (value) => {
    value.appearance.language = "zh-CN";
    value.appearance.animationStyle = "smooth";
    value.appearance.autoDisableAnimations = false;
  });
  await page.setViewportSize({ width: 1280, height: 900 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const copy = await loadAppCopy("zh-CN");
  const panel = page.locator(".resource-table-panel");
  await panel.scrollIntoViewIfNeeded();
  const badge = panel.locator("#resourceTableStatus");
  const hint = panel.locator(".resource-table-title .help-hint");
  await hint.waitFor();

  // 原生 title 不再出现在徽标、问号和标题区里；问号紧跟在计数后面。
  assert.equal(await badge.getAttribute("title"), null, "the count badge has no native title");
  assert.equal(await panel.locator(".resource-table-title [title]").count(), 0, "nothing in the resource table title falls back to a native tooltip");
  assert.equal(await badge.evaluate((element) => element.nextElementSibling?.classList.contains("help-hint")), true, "the question mark sits right after the count");
  assert.equal(await hint.getAttribute("aria-label"), copy.common.helpHint);
  assert.equal(await hint.evaluate((element) => element.tagName), "BUTTON", "the question mark is a real button");

  // 悬停打开。
  const start = await hint.boundingBox();
  await page.mouse.move(start.x + start.width / 2, start.y + start.height / 2);
  await page.locator(live).waitFor({ timeout: 2000 });
  const popover = page.locator(live);
  assert.equal(await popover.getAttribute("role"), "tooltip");
  assert.equal(await hint.getAttribute("aria-describedby"), await popover.getAttribute("id"), "the question mark is described by the explanation");
  assert.equal((await popover.textContent()).trim(), copy.resourceTable.accountingBasis);
  assert.equal(await popover.evaluate((element) => element.parentElement?.parentElement === document.body), true, "the explanation is portaled to the top layer, so scrolling panels cannot clip it");
  const opened = await boxes(page);
  assertPlaced(opened, "hover");

  // 一像素一像素从问号中心移到说明正文中间：途中每一步都开着。
  const from = { x: start.x + start.width / 2, y: start.y + start.height / 2 };
  const to = { x: opened.popover.left + Math.min(40, opened.popover.width / 2), y: (opened.popover.top + opened.popover.bottom) / 2 };
  const steps = Math.ceil(Math.max(Math.abs(to.x - from.x), Math.abs(to.y - from.y)));
  for (let step = 1; step <= steps; step += 1) {
    const x = from.x + ((to.x - from.x) * step) / steps;
    const y = from.y + ((to.y - from.y) * step) / steps;
    await page.mouse.move(x, y);
    assert.ok(await isOpen(page), `the explanation closed while the pointer crossed from the question mark at (${x.toFixed(1)}, ${y.toFixed(1)})`);
  }

  // 离开两者：关上，关上时留一份只能看见的幽灵（呈现层的退场）。
  // 离开时停在面板标题的字上：和问号同一行、不可聚焦，也不是会被点开的表格行。
  const heading = await panel.locator(".resource-table-title h2").boundingBox();
  const away = { x: heading.x + Math.min(12, heading.width / 2), y: heading.y + heading.height / 2 };
  await page.mouse.move(away.x, away.y, { steps: 4 });
  await waitClosed(page, "leaving both the question mark and the explanation closes it");
  const ghost = await page.waitForSelector(".help-hint-popover[data-exit-ghost]", { state: "attached", timeout: 1000 })
    .then((element) => element.evaluate((node) => ({ pointer: getComputedStyle(node).pointerEvents, hidden: node.getAttribute("aria-hidden"), inert: node.inert, id: node.id })))
    .catch(() => null);
  assert.ok(ghost, "closing plays the exit slot through the presentation layer");
  assert.deepEqual(ghost, { pointer: "none", hidden: "true", inert: true, id: "" }, "the exit ghost is visual only");
  assert.equal(await hint.evaluate((element) => element.classList.contains("open")), false);

  // 从问号横着走开（不是往说明去）：出了问号几像素就关上，旁边一行不会把说明一直留着。
  await page.mouse.move(from.x, from.y, { steps: 4 });
  await page.locator(live).waitFor({ timeout: 2000 });
  for (let x = from.x; x >= start.x - 6; x -= 1) await page.mouse.move(x, from.y);
  await waitClosed(page, "moving sideways off the question mark closes the explanation");

  // 一步跳到远处停下：离开事件的坐标就是那个远点，它已经不在问号和说明之间的走廊里，当场关上，之后不再移动指针。
  const size = page.viewportSize();
  const far = { x: size.width - 2, y: size.height - 2 };
  await page.mouse.move(from.x, from.y, { steps: 4 });
  await page.locator(live).waitFor({ timeout: 2000 });
  await page.mouse.move(far.x, far.y, { steps: 1 });
  await waitClosed(page, "a one-step jump from the question mark to far away closes the explanation without another move");
  // 从说明里一步跳走也一样。
  await page.mouse.move(from.x, from.y, { steps: 4 });
  await page.locator(live).waitFor({ timeout: 2000 });
  const inside = await boxes(page);
  await page.mouse.move(from.x, (inside.popover.top + inside.popover.bottom) / 2, { steps: 6 });
  assert.ok(await isOpen(page), "the pointer reached the explanation");
  await page.mouse.move(far.x, far.y, { steps: 1 });
  await waitClosed(page, "a one-step jump from the explanation to far away closes it without another move");

  // 拖动选字：从说明里按下，拖到说明外面松开，说明不关，选中的是说明里的字。
  await page.mouse.move(from.x, from.y);
  await page.locator(live).waitFor({ timeout: 2000 });
  const selecting = await boxes(page);
  const textStart = { x: selecting.popover.left + 11, y: selecting.popover.top + 14 };
  await page.mouse.move(from.x, textStart.y, { steps: 3 });
  await page.mouse.move(textStart.x, textStart.y, { steps: 6 });
  assert.equal(await page.evaluate(() => getComputedStyle(document.querySelector(".help-hint-popover:not([data-exit-ghost])")).userSelect), "text");
  await page.mouse.down();
  await page.mouse.move(selecting.popover.right + 80, selecting.popover.bottom + 60, { steps: 12 });
  assert.ok(await isOpen(page), "a selection drag that starts inside keeps the explanation open while the pointer is outside");
  await page.mouse.up();
  assert.ok(await isOpen(page), "releasing a selection drag outside keeps the explanation open");
  const selected = await page.evaluate((selector) => {
    const selection = window.getSelection();
    const popoverElement = document.querySelector(selector);
    return {
      text: selection?.toString() ?? "",
      inside: Boolean(selection?.anchorNode && popoverElement?.contains(selection.anchorNode) && selection.focusNode && popoverElement.contains(selection.focusNode))
    };
  }, live);
  assert.ok(selected.inside && selected.text.length > 4, `the explanation text is selectable by dragging: ${JSON.stringify(selected)}`);
  assert.ok(copy.resourceTable.accountingBasis.includes(selected.text), `the selection stays inside the explanation instead of running into the page: ${JSON.stringify(selected)}`);
  await page.keyboard.press("Escape");
  await waitClosed(page, "Escape closes the explanation after selecting");
  assert.equal(await hint.evaluate((element) => document.activeElement === element), true, "Escape returns focus to the question mark");

  // 键盘：Shift+Tab 从搜索框回到问号就打开；Esc 关上，焦点留在问号；再点问号重新打开。
  const search = panel.locator(".resource-table-search input");
  await search.focus();
  assert.equal(await isOpen(page), false);
  await page.keyboard.press("Shift+Tab");
  assert.equal(await hint.evaluate((element) => document.activeElement === element), true, "Shift+Tab from the search box lands on the question mark");
  await page.locator(live).waitFor({ timeout: 2000 });
  assertPlaced(await boxes(page), "focus");
  await page.keyboard.press("Escape");
  await waitClosed(page, "Escape closes the explanation opened by focus");
  assert.equal(await hint.evaluate((element) => document.activeElement === element), true, "focus stays on the question mark after Escape");
  await page.keyboard.press("Enter");
  await page.locator(live).waitFor({ timeout: 2000 });
  await page.keyboard.press("Escape");
  await waitClosed(page, "Escape closes the explanation reopened from the keyboard");
  await page.keyboard.press("Tab");
  assert.equal(await search.evaluate((element) => document.activeElement === element), true, "Tab moves on from the question mark");
  assert.equal(await isOpen(page), false, "moving focus away keeps it closed");

  // 焦点在别处、只靠悬停打开：Esc 只关说明，焦点留在搜索框。
  await page.mouse.move(from.x, from.y, { steps: 4 });
  await page.locator(live).waitFor({ timeout: 2000 });
  await page.keyboard.press("Escape");
  await waitClosed(page, "Escape closes a hover-opened explanation");
  assert.equal(await search.evaluate((element) => document.activeElement === element), true, "Escape on a hover-opened explanation does not take focus");

  // 点击（触摸同理）：点问号打开，指针移开仍开着；在别处按下才关。
  await page.mouse.move(away.x, away.y, { steps: 4 });
  await hint.click();
  await page.locator(live).waitFor({ timeout: 2000 });
  await page.mouse.move(away.x, away.y, { steps: 4 });
  assert.ok(await isOpen(page), "a clicked question mark keeps its explanation open after the pointer leaves");
  await page.mouse.click(away.x, away.y);
  await waitClosed(page, "pressing elsewhere closes a clicked explanation");

  // 常驻页被隐藏：换页只派发点击（不动指针、不动焦点），监视页标成 aria-hidden，问号自己的说明当场关上。
  const nav = (id) => page.getByRole("navigation").getByRole("button", { name: copy.page[id], exact: true });
  const own = `[id="${await hint.getAttribute("aria-describedby")}"]:not([data-exit-ghost])`;
  const hideMonitor = async (how) => {
    await page.locator(own).waitFor({ timeout: 2000 });
    await nav("settings").dispatchEvent("click");
    await page.locator('.page-host[data-page-id="monitor"][aria-hidden="true"]').waitFor({ state: "attached" });
    await page.waitForFunction((selector) => document.querySelector(selector) === null, own, { timeout: 2000 })
      .catch(() => assert.fail(`an explanation opened by ${how} stays open after its kept page was hidden`));
    await nav("monitor").dispatchEvent("click");
    await page.locator('.page-host[data-page-id="monitor"]:not([aria-hidden])').waitFor({ state: "attached" });
    // 换页的退场幽灵里有一份问号的拷贝（只能看见），等它播完再找问号。
    await page.waitForFunction(() => document.querySelector(".page-leaving-layer")?.childElementCount === 0, null, { timeout: 5000 });
  };
  await page.mouse.move(from.x, from.y, { steps: 4 });
  await hideMonitor("hover");
  await page.mouse.move(away.x, away.y, { steps: 4 });
  await waitClosed(page, "the explanation is closed after returning to the monitor page and moving away");
  await hint.focus();
  await hideMonitor("focus");
  await hint.evaluate((element) => element.blur());
  await waitClosed(page, "the explanation is closed after returning to the monitor page and blurring");

  // 无动画：当场出现、当场消失，没有退场幽灵。
  await writeFrontendPreferences(page, (value) => { value.appearance.animationStyle = "none"; });
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.body.dataset.animations === "none", null, { timeout: 5000 });
  await panel.scrollIntoViewIfNeeded();
  await hint.waitFor();
  const still = await hint.boundingBox();
  await page.mouse.move(still.x + still.width / 2, still.y + still.height / 2);
  await page.locator(live).waitFor({ timeout: 2000 });
  assert.equal(await popover.evaluate((element) => getComputedStyle(element).animationDuration), "0s", "no fade in the no-animation style");
  await page.mouse.move(away.x, away.y, { steps: 2 });
  await waitClosed(page, "leaving closes the explanation in the no-animation style");
  await page.waitForTimeout(150);
  assert.equal(await page.locator(".help-hint-popover").count(), 0, "the no-animation style leaves no exit ghost");

  // 九种语言，最小窗口：名称和说明是这种语言的；问号不给标题行和页面添一点溢出（和把问号去掉时比），不被挤小、不压到右侧操作区；
  // 悬停打开，说明整个在窗口里；离开就关。问号放到滚动区中间再操作：贴在顶上时会被外壳的标题栏盖住。
  await page.setViewportSize({ width: 744, height: 520 });
  const layouts = [];
  for (const language of languages) {
    const text = await loadAppCopy(language);
    await writeFrontendPreferences(page, (value) => { value.appearance.language = language; });
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.waitForFunction((label) => document.querySelector(".resource-table-title .help-hint")?.getAttribute("aria-label") === label, text.common.helpHint, { timeout: 8000 })
      .catch(() => assert.fail(`${language}: the question mark is not named in this language`));
    await hint.evaluate((element) => element.scrollIntoView({ block: "center" }));
    const measure = () => page.evaluate(() => {
      const rect = (element) => element?.getBoundingClientRect();
      const header = document.querySelector(".resource-table-panel .panel-header");
      const title = document.querySelector(".resource-table-title");
      const button = rect(document.querySelector(".resource-table-title .help-hint"));
      const badge = rect(document.querySelector("#resourceTableStatus"));
      const actions = rect(document.querySelector(".resource-table-panel .panel-header-actions"));
      const overlaps = (a, b) => a.left < b.right && b.left < a.right && a.top < b.bottom && b.top < a.bottom;
      return {
        pageOverflow: document.documentElement.scrollWidth - document.documentElement.clientWidth,
        headerOverflow: header.scrollWidth - header.clientWidth,
        titleOverflow: title.scrollWidth - title.clientWidth,
        buttonInsideHeader: button.width === 0 || (button.left >= rect(header).left - 0.5 && button.right <= rect(header).right + 0.5),
        buttonAfterBadge: button.width === 0 || button.left >= badge.right,
        buttonUnderActions: button.width > 0 && overlaps(button, actions),
        buttonWidth: button.width
      };
    });
    const layout = await measure();
    await hint.evaluate((element) => { element.style.display = "none"; });
    const withoutHint = await measure();
    await hint.evaluate((element) => { element.style.removeProperty("display"); });
    for (const key of ["pageOverflow", "headerOverflow", "titleOverflow"]) {
      assert.ok(layout[key] <= Math.max(0, withoutHint[key]), `${language}: the question mark adds ${key} ${JSON.stringify({ layout, withoutHint })}`);
    }
    assert.ok(layout.buttonInsideHeader && layout.buttonAfterBadge && !layout.buttonUnderActions && layout.buttonWidth >= 16, `${language}: the question mark is squeezed or covered ${JSON.stringify(layout)}`);
    const narrow = await hint.boundingBox();
    await page.mouse.move(narrow.x + narrow.width / 2, narrow.y + narrow.height / 2, { steps: 3 });
    await page.locator(live).waitFor({ timeout: 2000 }).catch(() => assert.fail(`${language}: hovering the question mark at the narrow window does not open it`));
    assert.equal((await page.locator(live).textContent()).trim(), text.resourceTable.accountingBasis, `${language}: the explanation is in this language`);
    assertPlaced(await boxes(page), `${language} narrow`);
    const label = await panel.locator(".resource-table-title h2").boundingBox();
    await page.mouse.move(label.x + Math.min(12, label.width / 2), label.y + label.height / 2, { steps: 3 });
    await waitClosed(page, `${language}: leaving closes the explanation`);
    layouts.push({ language, ...layout, baselineHeaderOverflow: withoutHint.headerOverflow });
  }

  assert.deepEqual(failures, [], "no page errors");
  return { hover: opened.placement, selected: selected.text.length, narrow: layouts };
}

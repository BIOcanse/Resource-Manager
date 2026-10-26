import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { animationStyle, TERMINAL_STEPS } from "../../src/presentation/animationStyle.ts";
import { frontendPreferencesKey, writeFrontendPreferences } from "./frontendPreferencesFixture.js";
import { waitForStableRendering } from "./journeyWaits.js";

/**
 * 动画风格（docs/animation_style_2026-10-26.md「验证」），简体中文，真实点击和保存。
 * - 设置 → 外观：一行「动画风格」五个选项，下面一行开关「按自调度自动关闭动画」；旧的两行没有了；默认流畅平滑、开关开。
 * - 每种风格：开关（键盘切换）、弹窗（任务中心）、换设置分区的实际过渡，逐帧在页面里读。
 *   流畅平滑带速度、能接住（动画中再按一次，滑块先顺着原来的方向走一点再回头）；桌面常规按固定时长、不带速度；
 *   终端顿挫按固定步数跳（拖着时连续）；无动画、超高性能在按键的同一个任务里就到终态。
 * - 刷新后保持；旧偏好（animations、motionFeel）按迁移读成新的两项。
 * 自动关闭在回到前台时的选择由 automatic-animation 旅程查。
 */
export default async function animationStyleJourney(page, baseUrl) {
  const failures = [];
  const fail = (message) => failures.push(message);
  page.on("pageerror", (error) => failures.push(`pageerror: ${String(error)}`));
  const app = await loadAppCopy("zh-CN");
  const settings = await loadSettingsText("zh-CN");
  const label = (id) => settings.animationStyleOptions.find((option) => option.id === id).label;
  const styleGroup = page.getByRole("radiogroup", { name: settings.appearance.animationStyleTitle, exact: true });
  const autoSwitch = page.getByRole("checkbox", { name: settings.appearance.autoDisableAnimationsTitle, exact: true });
  const openSection = (section) => page.getByRole("button", { name: settings.sections[section], exact: true }).click();
  const openSettings = async () => {
    await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
    await openSection("appearance");
    await styleGroup.waitFor();
  };
  const choose = async (style) => {
    await styleGroup.getByRole("radio", { name: label(style), exact: true }).click();
    await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
    await page.waitForFunction((id) => document.body.dataset.animations === id, style, { timeout: 5000 });
  };

  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await openSettings();

  // ---- 设置行 ----
  const rows = await page.locator(".settings-row strong").allTextContents();
  const styleRow = rows.indexOf(settings.appearance.animationStyleTitle);
  if (styleRow < 0 || rows[styleRow + 1] !== settings.appearance.autoDisableAnimationsTitle) fail(`the switch row is not right under the style row: ${JSON.stringify(rows)}`);
  for (const old of ["动画效果", "手感"]) if (rows.includes(old)) fail(`the old row ${old} is still there`);
  const optionLabels = (await styleGroup.getByRole("radio").allTextContents()).map((text) => text.trim());
  if (JSON.stringify(optionLabels) !== JSON.stringify(settings.animationStyleOptions.map((option) => option.label))) fail(`style options ${JSON.stringify(optionLabels)}`);
  const defaultChoice = (await styleGroup.getByRole("radio", { checked: true }).textContent())?.trim();
  if (defaultChoice !== label("smooth")) fail(`default style is ${defaultChoice}`);
  if (!(await autoSwitch.isChecked()) || await autoSwitch.isDisabled()) fail("the auto switch is not on and enabled by default");
  if (await page.evaluate(() => document.body.dataset.animations) !== "smooth") fail("the default effective style is not smooth");

  /** 页面里的读数：开关滑块的 translate、任务中心对话框的不透明度和变换、设置分区内容层的内联变换和不透明度。 */
  const installReaders = () => page.evaluate(() => {
    window.__styleReaders = {
      knob: () => {
        const knob = document.querySelector(".settings-switch-knob[data-style-probe]");
        const value = getComputedStyle(knob).translate;
        return value === "none" ? 0 : Math.round(Number.parseFloat(value) * 100) / 100;
      },
      dialog: () => {
        const dialog = document.querySelector("section.modal[role='dialog']");
        if (!dialog) return null;
        const style = getComputedStyle(dialog);
        return { opacity: Math.round(Number(style.opacity) * 1000) / 1000, transform: style.transform };
      },
      section: () => {
        const stage = document.querySelector(".settings-content > fieldset");
        return { transform: stage?.style.transform ?? null, opacity: stage?.style.opacity ?? null,
          leaving: document.querySelectorAll(".settings-content > .content-transition-leaving").length };
      }
    };
  });
  await installReaders();

  /**
   * 逐帧读一个值：从 startEvent（按键或点击）起，到连续 settleFrames 帧不变（或超时）。
   * sameTask 是 sameTaskEvent 冒泡到窗口那一刻（组件的处理之后）读到的值：无动画时这一刻就该是终态。
   */
  const sample = async ({ reader, startEvent, sameTaskEvent = startEvent, trigger, between, settleFrames = 6, timeoutMs = 2500 }) => {
    await page.evaluate(({ reader, startEvent, sameTaskEvent, settleFrames, timeoutMs }) => {
      const read = window.__styleReaders[reader];
      const stop = new AbortController();
      const probe = { t0: null, sameTask: undefined, events: [], frames: [], done: false };
      window.__styleProbe = probe;
      window.addEventListener(startEvent, () => {
        const now = performance.now();
        if (probe.t0 === null) probe.t0 = now;
        probe.events.push(now - probe.t0);
      }, { capture: true, signal: stop.signal });
      window.addEventListener(sameTaskEvent, () => { if (probe.sameTask === undefined) probe.sameTask = read(); }, { signal: stop.signal });
      let still = 0;
      const tick = (now) => {
        if (probe.t0 !== null) {
          const value = read();
          const last = probe.frames.at(-1);
          still = last && JSON.stringify(last.value) === JSON.stringify(value) ? still + 1 : 0;
          probe.frames.push({ t: now - probe.t0, value });
          if (still >= settleFrames || now - probe.t0 > timeoutMs) { probe.done = true; stop.abort(); return; }
        }
        requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    }, { reader, startEvent, sameTaskEvent, settleFrames, timeoutMs });
    await trigger();
    if (between) await between();
    await page.waitForFunction(() => window.__styleProbe.done, undefined, { timeout: timeoutMs + 3000 });
    const probe = await page.evaluate(() => window.__styleProbe);
    // 停下的时刻：最后一次变化的那一帧。
    const frames = probe.frames;
    let settledAt = 0;
    for (let index = 1; index < frames.length; index++) {
      if (JSON.stringify(frames[index].value) !== JSON.stringify(frames[index - 1].value)) settledAt = frames[index].t;
    }
    return { frames, settledAt, sameTask: probe.sameTask, events: probe.events, final: frames.at(-1)?.value };
  };

  // 开关：「更新」分区的“间隔检查”，空格切换（复选框在 keyup 时改值，change 之后磁石接手）。
  const switchBox = () => page.getByRole("checkbox", { name: settings.updates.periodicTitle, exact: true });
  const prepareSwitch = async () => {
    await openSection("updates");
    await switchBox().waitFor();
    await page.evaluate(() => document.querySelectorAll("[data-style-probe]").forEach((element) => element.removeAttribute("data-style-probe")));
    await switchBox().locator("xpath=..").locator(".settings-switch-knob").evaluate((element) => element.setAttribute("data-style-probe", ""));
    await switchBox().focus();
  };
  const waitSwitchRest = async () => {
    await page.waitForTimeout(50);
    await waitForStableRendering(page, [".settings-switch-knob", ".settings-switch-track"]);
  };
  const switchToggle = async () => {
    await prepareSwitch();
    const run = await sample({ reader: "knob", startEvent: "keydown", sameTaskEvent: "change", trigger: () => page.keyboard.press("Space") });
    await waitSwitchRest();
    return run;
  };
  /** 动画中再按一次（接住）：第一次按下后约 60 ms 再按。 */
  const switchCatch = async () => {
    await prepareSwitch();
    const run = await sample({ reader: "knob", startEvent: "keydown", sameTaskEvent: "change", trigger: () => page.keyboard.press("Space"),
      between: async () => { await page.waitForTimeout(60); await page.keyboard.press("Space"); } });
    await waitSwitchRest();
    return run;
  };
  /** 拖着滑块：鼠标按住滑块，水平移十步，每步之后读位置（终端顿挫也连续）；移回原处松手。 */
  const switchDrag = async () => {
    await prepareSwitch();
    const knob = switchBox().locator("xpath=..").locator(".settings-switch-knob");
    const box = await knob.boundingBox();
    const startX = box.x + box.width / 2;
    const y = box.y + box.height / 2;
    const direction = await switchBox().isChecked() ? -1 : 1;
    const positions = [];
    await page.mouse.move(startX, y);
    await page.mouse.down();
    for (let step = 1; step <= 10; step++) {
      await page.mouse.move(startX + direction * step * 1.8, y);
      await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
      positions.push(await page.evaluate(() => window.__styleReaders.knob()));
    }
    await page.mouse.move(startX, y);
    await page.mouse.up();
    await waitSwitchRest();
    return positions;
  };
  const dialogOpen = async () => {
    const run = await sample({ reader: "dialog", startEvent: "click", trigger: () => page.locator("#taskCenterButton").click() });
    await page.keyboard.press("Escape");
    await page.getByRole("dialog", { name: app.taskCenter.title, exact: true }).waitFor({ state: "hidden" });
    return run;
  };
  const sectionChange = async (section) => sample({ reader: "section", startEvent: "click", trigger: () => openSection(section) });

  const distinct = (values) => [...new Set(values.map((value) => JSON.stringify(value)))].map((value) => JSON.parse(value));
  const monotone = (values) => values.every((value, index) => index < 2 || Math.sign(value - values[index - 1]) * Math.sign(values[1] - values[0]) >= 0);
  const results = {};
  for (const style of ["desktop", "terminal", "smooth", "none", "ultra"]) {
    await openSettings();
    await choose(style);
    const definition = animationStyle(style);
    const medium = definition.durationsMs["--motion-medium"];
    const tokens = await page.evaluate(() => getComputedStyle(document.body).getPropertyValue("--motion-medium").trim());
    const tokenMs = tokens.endsWith("ms") ? Number.parseFloat(tokens) : Math.round(Number.parseFloat(tokens) * 1000);
    if (tokenMs !== medium) fail(`${style}: --motion-medium is ${tokens}, the catalog says ${medium} ms`);
    const toggle = [await switchToggle(), await switchToggle()];
    const caught = await switchCatch();
    const drag = await switchDrag();
    const dialog = await dialogOpen();
    await openSettings();
    const sections = [await sectionChange("updates"), await sectionChange("appearance")];
    const entry = { tokenMs, toggle: toggle.map(({ settledAt, sameTask, final }) => ({ settledAt, sameTask, final })),
      toggleFrames: toggle[0].frames.map((frame) => [Math.round(frame.t), frame.value]),
      caught: caught.frames.map((frame) => [Math.round(frame.t), frame.value]), caughtPresses: caught.events.map(Math.round), drag,
      dialog: { settledAt: dialog.settledAt, first: dialog.frames.find((frame) => frame.value)?.value, values: distinct(dialog.frames.filter((frame) => frame.value).map((frame) => frame.value.opacity)) },
      sections: sections.map((run) => ({ settledAt: run.settledAt, first: run.frames[0]?.value, sameTask: run.sameTask, values: distinct(run.frames.map((frame) => frame.value.transform)) })) };
    results[style] = entry;
    const xs = (run) => run.frames.map((frame) => frame.value);
    if (style === "none" || style === "ultra") {
      // 当帧到终态：按键派发完那一刻滑块已在另一端；弹窗、分区的第一帧就是终态。
      for (const run of toggle) if (run.sameTask !== run.final) fail(`${style}: the switch is not at its end in the same task: ${JSON.stringify(entry.toggle)}`);
      if (entry.dialog.first?.opacity !== 1 || entry.dialog.first?.transform !== "none") fail(`${style}: the dialog is not final in its first frame: ${JSON.stringify(entry.dialog)}`);
      for (const run of sections) {
        const first = run.frames[0]?.value;
        if (!first || first.transform !== "" || first.opacity !== "" || first.leaving !== 0) fail(`${style}: the section is not final in its first frame: ${JSON.stringify(first)}`);
      }
      continue;
    }
    for (const run of toggle) {
      if (run.sameTask === run.final) fail(`${style}: the switch jumps to its end in the same task`);
      if (!(Math.abs(run.final) < 0.5 || Math.abs(run.final - 20) < 0.5)) fail(`${style}: the switch did not reach an end: ${run.final}`);
    }
    const dragDistinct = new Set(drag).size;
    if (dragDistinct < 6) fail(`${style}: dragging the knob is not continuous: ${JSON.stringify(drag)}`);
    if (style === "desktop" || style === "terminal") {
      // 固定时长：开关、弹窗是 --motion-medium，分区是 --motion-page；不带速度：接住时立刻回头，不越过。
      const pageMs = definition.durationsMs["--motion-page"];
      for (const run of toggle) if (run.settledAt < medium * 0.75 || run.settledAt > medium + 90) fail(`${style}: the switch settles at ${run.settledAt} ms, not ${medium} ms`);
      if (dialog.settledAt < medium * 0.75 || dialog.settledAt > medium + 90) fail(`${style}: the dialog settles at ${dialog.settledAt} ms, not ${medium} ms`);
      for (const run of sections) if (run.settledAt < pageMs * 0.75 || run.settledAt > pageMs + 120) fail(`${style}: the section settles at ${run.settledAt} ms, not ${pageMs} ms`);
      for (const run of toggle) if (!monotone(xs(run))) fail(`${style}: the switch overshoots: ${JSON.stringify(entry.toggleFrames)}`);
      const second = caught.events[1];
      const atSecond = caught.frames.filter((frame) => frame.t <= second + 1).at(-1)?.value;
      const after = caught.frames.filter((frame) => frame.t > second + 1).map((frame) => frame.value);
      const firstDirection = Math.sign((atSecond ?? 0) - caught.frames[0].value);
      if (after.length && Math.sign(after[0] - atSecond) === firstDirection && Math.abs(after[0] - atSecond) > 0.05) {
        fail(`${style}: after the second press the knob keeps going (${atSecond} → ${after[0]}): it carries velocity`);
      }
      if (style === "terminal") {
        // 按步跳：画面最多 TERMINAL_STEPS + 1 个位置（含起点），弹窗的不透明度、分区的位移也一样。
        for (const run of toggle) if (distinct(xs(run)).length > TERMINAL_STEPS + 1) fail(`terminal: the switch takes ${distinct(xs(run)).length} positions: ${JSON.stringify(distinct(xs(run)))}`);
        if (entry.dialog.values.length > TERMINAL_STEPS + 1) fail(`terminal: the dialog takes ${entry.dialog.values.length} looks: ${JSON.stringify(entry.dialog.values)}`);
        for (const run of entry.sections) if (run.values.length > TERMINAL_STEPS + 1) fail(`terminal: the section takes ${run.values.length} positions: ${JSON.stringify(run.values)}`);
      } else {
        for (const run of toggle) if (distinct(xs(run)).length <= TERMINAL_STEPS + 1) fail(`desktop: the switch moves in too few frames to be eased: ${JSON.stringify(distinct(xs(run)))}`);
      }
    }
    if (style === "smooth") {
      // 弹簧：能接住、带速度——第二次按下后滑块先顺着原来的方向再走一点，然后回头，中间不跳。
      const second = caught.events[1];
      const atSecond = caught.frames.filter((frame) => frame.t <= second + 1).at(-1)?.value;
      const after = caught.frames.filter((frame) => frame.t > second + 1).map((frame) => frame.value);
      const firstDirection = Math.sign((atSecond ?? 0) - caught.frames[0].value);
      if (!(after.length && Math.sign(after[0] - atSecond) === firstDirection)) fail(`smooth: the caught knob does not carry its velocity: ${JSON.stringify(entry.caught)}`);
      const jumps = caught.frames.slice(1).map((frame, index) => Math.abs(frame.value - caught.frames[index].value));
      if (Math.max(...jumps) > 8) fail(`smooth: the caught knob jumps ${Math.max(...jumps)} px in a frame: ${JSON.stringify(entry.caught)}`);
      for (const run of toggle) if (run.settledAt < 120 || run.settledAt > 900) fail(`smooth: the switch spring settles at ${run.settledAt} ms`);
      if (dialog.settledAt < 120 || dialog.settledAt > 1200) fail(`smooth: the dialog settles at ${dialog.settledAt} ms`);
      for (const run of sections) if (run.settledAt < 200 || run.settledAt > 1200) fail(`smooth: the section settles at ${run.settledAt} ms`);
    }
  }

  // ---- 刷新后保持 ----
  await openSettings();
  await choose("terminal");
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.waitForFunction(() => document.body.dataset.animations === "terminal", undefined, { timeout: 5000 });
  await openSettings();
  const reloaded = (await styleGroup.getByRole("radio", { checked: true }).textContent())?.trim();
  if (reloaded !== label("terminal")) fail(`after reload the style shows ${reloaded}`);
  const cached = await page.evaluate((key) => JSON.parse(localStorage.getItem(key) ?? "null")?.appearance, frontendPreferencesKey);
  if (cached?.animationStyle !== "terminal" || "animations" in cached || "motionFeel" in cached) fail(`the cache holds ${JSON.stringify(cached)}`);

  // ---- 旧偏好迁移：normal + smooth → 流畅平滑、开关关；auto + crisp → 桌面常规、开关开 ----
  const migrations = [];
  for (const [animations, motionFeel, style, autoDisable] of [["normal", "smooth", "smooth", false], ["auto", "crisp", "desktop", true]]) {
    await writeFrontendPreferences(page, (value) => {
      delete value.appearance.animationStyle;
      delete value.appearance.autoDisableAnimations;
      Object.assign(value.appearance, { animations, motionFeel });
    });
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.waitForFunction((id) => document.body.dataset.animations === id, style, { timeout: 5000 });
    await openSettings();
    const shown = (await styleGroup.getByRole("radio", { checked: true }).textContent())?.trim();
    const switchOn = await autoSwitch.isChecked();
    migrations.push({ from: { animations, motionFeel }, shown, switchOn });
    if (shown !== label(style) || switchOn !== autoDisable) fail(`${animations}/${motionFeel} migrated to ${shown}, switch ${switchOn}`);
  }

  if (failures.length) throw new Error(`${failures.join("\n")}\n${JSON.stringify(results)}`);
  return { results, reloaded, migrations };
}

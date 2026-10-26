import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { animationStyle, TERMINAL_STEPS } from "../../src/presentation/animationStyle.ts";
import { frontendPreferencesKey, writeFrontendPreferences } from "./frontendPreferencesFixture.js";
import { waitForStableRendering } from "./journeyWaits.js";

/**
 * 动画风格（docs/animation_style_2026-10-26.md「验证」），简体中文，真实点击和保存。
 * - 设置 → 外观：一行「动画风格」五个选项，下面一行开关「按自调度自动关闭动画」；旧的两行没有了；默认流畅平滑、开关开。
 * - 每种风格：开关（键盘切换）、弹窗（任务中心）开和关、换设置分区（含很快换回去的反向）的实际过渡，逐帧在页面里读。
 *   所有风格交互先到（「总原则」）：点击处理完新分区已在终态几何上可点；关弹窗那一刻对话框已不在，原处不命中幽灵。
 *   流畅平滑带速度、能接住（动画中再按一次，滑块先顺着原来的方向走一点再回头）；桌面常规按固定时长、不带速度；
 *   终端顿挫按固定步数跳（拖着时连续）；无动画、超高性能在按键的同一个任务里就到终态。
 * - 滚动区边缘回弹：流畅平滑里滚到顶还往上滚，内容按橡皮筋偏出去再回到边缘，滚动区本身不动；其余风格不偏。
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
      band: () => {
        const element = document.querySelector("[data-edge-band]");
        return element ? Number.parseFloat(element.style.getPropertyValue("--edge-band")) : 0;
      },
      /** hit：内容层在视口里那一块的中心点命中的是真节点（幽灵不可命中，进场不挪命中区域）。 */
      section: () => {
        const stage = document.querySelector(".settings-content > fieldset:not([data-exit-ghost])");
        let hit = false;
        if (stage) {
          const box = stage.getBoundingClientRect();
          const top = Math.max(box.top, 0);
          const bottom = Math.min(box.bottom, innerHeight);
          const target = document.elementFromPoint(box.left + box.width / 2, (top + bottom) / 2);
          hit = !!target && stage.contains(target) && !target.closest("[data-exit-ghost]");
        }
        return { transform: stage?.style.transform ?? null, opacity: stage?.style.opacity ?? null, hit,
          leaving: document.querySelectorAll(".settings-content > [data-exit-ghost]").length };
      }
    };
  });
  await installReaders();

  /**
   * 逐帧读一个值：从 startEvent（按键或点击）起，到 settleMs 里都没变（或超时）。按时间判停，不按帧数：
   * 无头浏览器的帧可能很密，终端顿挫一步里有好几帧同样的值。settleMs 比最长的一步（--motion-bar 360 ms ÷ 4）长。
   * sameTask 是 sameTaskEvent 冒泡到窗口那一刻（组件的处理之后）读到的值：无动画时这一刻就该是终态。
   * changes 是每次 sameTaskEvent 的时刻：开关在 keyup 后的 change 里换终态，接住要按这一刻算，不按 keydown。
   */
  const sample = async ({ reader, startEvent, sameTaskEvent = startEvent, trigger, between, settleMs = 150, timeoutMs = 2500 }) => {
    await page.evaluate(({ reader, startEvent, sameTaskEvent, settleMs, timeoutMs }) => {
      const read = window.__styleReaders[reader];
      const stop = new AbortController();
      const probe = { t0: null, sameTask: undefined, events: [], changes: [], frames: [], done: false };
      window.__styleProbe = probe;
      window.addEventListener(startEvent, () => {
        const now = performance.now();
        if (probe.t0 === null) probe.t0 = now;
        probe.events.push(now - probe.t0);
      }, { capture: true, signal: stop.signal });
      window.addEventListener(sameTaskEvent, () => {
        if (probe.sameTask === undefined) probe.sameTask = read();
        if (probe.t0 !== null) probe.changes.push(performance.now() - probe.t0);
      }, { signal: stop.signal });
      let changedAt = null;
      const tick = (now) => {
        if (probe.t0 !== null) {
          const value = read();
          const last = probe.frames.at(-1);
          if (!last || JSON.stringify(last.value) !== JSON.stringify(value)) changedAt = now;
          probe.frames.push({ t: now - probe.t0, value });
          if (now - changedAt >= settleMs || now - probe.t0 > timeoutMs) { probe.done = true; stop.abort(); return; }
        }
        requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    }, { reader, startEvent, sameTaskEvent, settleMs, timeoutMs });
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
    return { frames, settledAt, sameTask: probe.sameTask, events: probe.events, changes: probe.changes, final: frames.at(-1)?.value };
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
  /**
   * 关弹窗（「总原则」：退场的幽灵只给眼睛看）：Esc 派发完那一刻对话框已经不在可访问树里，原处命中的是下面一层；
   * 动着的风格之后几帧有幽灵（不可命中、inert、aria-hidden），无动画、超高性能同一帧就没有幽灵。
   */
  const dialogClose = async () => {
    await page.locator("#taskCenterButton").click();
    const dialog = page.getByRole("dialog", { name: app.taskCenter.title, exact: true });
    await dialog.waitFor();
    await page.waitForFunction(() => !document.querySelector("[data-exit-ghost]") && !document.querySelector("[style*='will-change: opacity']"), undefined, { timeout: 5000 });
    const box = await dialog.boundingBox();
    await page.evaluate(({ x, y }) => {
      const read = () => {
        const target = document.elementFromPoint(x, y);
        const ghosts = [...document.querySelectorAll("[data-exit-ghost]")];
        return { dialogs: document.querySelectorAll("[role='dialog']").length, ghosts: ghosts.length,
          hitGhost: !!target?.closest("[data-exit-ghost]"), hitModal: !!target?.closest(".modal-backdrop:not([data-exit-ghost])"),
          ghostsHidden: ghosts.every((ghost) => ghost.inert && ghost.getAttribute("aria-hidden") === "true" && getComputedStyle(ghost).pointerEvents === "none") };
      };
      const probe = { pressed: false, sameTask: undefined, frames: [], done: false };
      window.__closeProbe = probe;
      // Esc 被对话框的焦点陷阱停在冒泡之前，窗口上收不到冒泡的 keydown：在捕获阶段开始观察，
      // 对话框从 DOM 里卸下的那批变动（同一个任务里的微任务）读一次。
      window.addEventListener("keydown", () => {
        probe.pressed = true;
        const observer = new MutationObserver(() => {
          if (probe.sameTask !== undefined || document.querySelector("[role='dialog']")) return;
          probe.sameTask = read();
          observer.disconnect();
        });
        observer.observe(document.body, { childList: true, subtree: true });
      }, { once: true, capture: true });
      const tick = () => {
        if (probe.pressed) probe.frames.push(read());
        if (probe.frames.length >= 4) { probe.done = true; return; }
        requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    }, { x: box.x + box.width / 2, y: box.y + box.height / 2 });
    await page.keyboard.press("Escape");
    await page.waitForFunction(() => window.__closeProbe.done, undefined, { timeout: 5000 });
    const probe = await page.evaluate(() => window.__closeProbe);
    await page.waitForFunction(() => !document.querySelector("[data-exit-ghost]"), undefined, { timeout: 5000 });
    return probe;
  };
  /** 换分区之前等上一段分区过渡走完（幽灵撤掉、内容层没有内联不透明度）：量的是从静止开始的一段。 */
  const sectionRest = () => page.waitForFunction(() => {
    const stage = document.querySelector(".settings-content > fieldset:not([data-exit-ghost])");
    return !document.querySelector(".settings-content > [data-exit-ghost]") && stage && !stage.style.opacity;
  }, undefined, { timeout: 5000 });
  const sectionChange = async (section) => {
    await sectionRest();
    return sample({ reader: "section", startEvent: "click", trigger: () => openSection(section) });
  };
  /**
   * 反向（docs/animation_style_2026-10-26.md「已裁定」：平滑反向）：换到「更新」约 60 ms 后又换回「外观」。
   * 量的是第二次点击起的一段：新内容当场进场，不等上一段的幽灵退完；新内容是另一个节点，从开度 0、静止开始，不接旧内容的位置和速度。
   */
  const sectionReverse = async () => {
    await sectionRest();
    await openSection("updates");
    await page.waitForTimeout(60);
    return sample({ reader: "section", startEvent: "click", trigger: () => openSection("appearance") });
  };
  /** 设置内容滚到顶，指针放在上面往上滚三格：读越过边缘的偏移。 */
  const edgeBand = async () => {
    const content = page.locator(".settings-content");
    await content.evaluate((element) => {
      for (let node = element; node; node = node.parentElement) node.scrollTop = 0;
    });
    const box = await content.boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + Math.min(box.height / 2, 200));
    await page.waitForTimeout(50);
    return sample({ reader: "band", startEvent: "wheel", settleMs: 250, timeoutMs: 3000, trigger: async () => {
      for (let notch = 0; notch < 3; notch++) { await page.mouse.wheel(0, -100); await page.waitForTimeout(30); }
    } });
  };

  /** 内容层的开度：进场完内联不透明度撤掉，读作 1。 */
  const opacityOf = (value) => (value.opacity === "" || value.opacity == null ? "1" : value.opacity);
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
    const closed = await dialogClose();
    await openSettings();
    const sections = [await sectionChange("updates"), await sectionChange("appearance")];
    const reverse = await sectionReverse();
    await page.waitForTimeout(400);
    const band = await edgeBand();
    const bandPeak = Math.max(...band.frames.map((frame) => Math.abs(frame.value)));
    const bandLeft = await page.evaluate(() => document.querySelectorAll("[data-edge-band]").length);
    const entry = { tokenMs, toggle: toggle.map(({ settledAt, sameTask, final }) => ({ settledAt, sameTask, final })),
      toggleFrames: toggle[0].frames.map((frame) => [Math.round(frame.t), frame.value]),
      caught: caught.frames.map((frame) => [Math.round(frame.t), frame.value]), caughtPresses: caught.events.map(Math.round), caughtChanges: caught.changes.map(Math.round), drag,
      dialog: { settledAt: dialog.settledAt, first: dialog.frames.find((frame) => frame.value)?.value, values: distinct(dialog.frames.filter((frame) => frame.value).map((frame) => frame.value.opacity)) },
      band: { peak: bandPeak, settledAt: band.settledAt, final: band.final, left: bandLeft },
      closed,
      sections: sections.map((run) => ({ settledAt: run.settledAt, first: run.frames[0]?.value, sameTask: run.sameTask, values: distinct(run.frames.map((frame) => opacityOf(frame.value))) })),
      reverse: { settledAt: reverse.settledAt, sameTask: reverse.sameTask, frames: reverse.frames.slice(0, 12).map((frame) => [Math.round(frame.t), opacityOf(frame.value), frame.value.leaving]) } };
    results[style] = entry;
    // 所有风格：交互先到。点击处理完那一刻新分区已在、在终态几何上可点；真节点从不带变换；关弹窗那一刻对话框已不在、原处不命中幽灵。
    for (const run of [...sections, reverse]) {
      if (!run.sameTask?.hit) fail(`${style}: the new section is not hit at its final place when the click is handled: ${JSON.stringify(run.sameTask)}`);
      if (run.frames.some((frame) => frame.value.transform || !frame.value.hit)) fail(`${style}: the section stage moves or is not hit during its entrance: ${JSON.stringify(run.frames.map((frame) => frame.value))}`);
    }
    if (closed.sameTask?.dialogs !== 0 || closed.sameTask?.hitGhost || closed.frames.some((frame) => frame.dialogs !== 0 || frame.hitGhost || frame.hitModal || !frame.ghostsHidden)) {
      fail(`${style}: the closed dialog is still in interaction or its ghost can be hit: ${JSON.stringify(closed)}`);
    }
    const xs = (run) => run.frames.map((frame) => frame.value);
    if (style === "smooth") {
      if (!(bandPeak > 5) || band.final !== 0 || bandLeft !== 0 || band.settledAt > 2000) fail(`smooth: the scroll edge does not rubber-band and return: ${JSON.stringify(entry.band)}`);
    } else if (bandPeak !== 0 || bandLeft !== 0) fail(`${style}: the scroll edge moves: ${JSON.stringify(entry.band)}`);
    if (style === "none" || style === "ultra") {
      // 当帧到终态：按键派发完那一刻滑块已在另一端；弹窗、分区的第一帧就是终态。
      for (const run of toggle) if (run.sameTask !== run.final) fail(`${style}: the switch is not at its end in the same task: ${JSON.stringify(entry.toggle)}`);
      if (entry.dialog.first?.opacity !== 1 || entry.dialog.first?.transform !== "none") fail(`${style}: the dialog is not final in its first frame: ${JSON.stringify(entry.dialog)}`);
      for (const run of [...sections, reverse]) {
        const first = run.frames[0]?.value;
        if (!first || first.transform !== "" || first.opacity !== "" || first.leaving !== 0) fail(`${style}: the section is not final in its first frame: ${JSON.stringify(first)}`);
        if (run.sameTask?.opacity !== "" || run.sameTask?.leaving !== 0) fail(`${style}: the section is not final when the click is handled: ${JSON.stringify(run.sameTask)}`);
      }
      if (closed.sameTask?.ghosts !== 0 || closed.frames.some((frame) => frame.ghosts !== 0)) fail(`${style}: closing the dialog leaves a ghost: ${JSON.stringify(closed)}`);
      continue;
    }
    for (const run of toggle) {
      if (run.sameTask === run.final) fail(`${style}: the switch jumps to its end in the same task`);
      if (!(Math.abs(run.final) < 0.5 || Math.abs(run.final - 20) < 0.5)) fail(`${style}: the switch did not reach an end: ${run.final}`);
    }
    if (!closed.frames.some((frame) => frame.ghosts > 0)) fail(`${style}: closing the dialog shows no exit: ${JSON.stringify(closed)}`);
    // 反向：第二次点击处理完那一刻新内容已经开始进场（开度 0 起、静止），旧内容的幽灵还在退；之后几帧里开度就往上走，不等幽灵退完。
    {
      const opacities = reverse.frames.map((frame) => Number(opacityOf(frame.value)));
      const start = Number(opacityOf(reverse.sameTask ?? {}));
      if (!(start < 0.3) || !(reverse.sameTask?.leaving >= 1)) fail(`${style}: the reversed section does not enter at once from rest: ${JSON.stringify(entry.reverse)}`);
      const risenAt = reverse.frames.find((frame) => Number(opacityOf(frame.value)) > start + 0.01)?.t;
      if (!(risenAt <= 120)) fail(`${style}: the reversed section waits ${risenAt} ms before it starts to show: ${JSON.stringify(entry.reverse)}`);
      if (opacities.some((value, index) => index > 0 && value < opacities[index - 1] - 0.001)) fail(`${style}: the reversed section's opacity goes back: ${JSON.stringify(entry.reverse)}`);
      const single = Math.max(...sections.map((run) => run.settledAt));
      if (reverse.settledAt > single + 120) fail(`${style}: the reversed section settles at ${reverse.settledAt} ms, a single change at ${single} ms: it waited for the previous one`);
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
      const second = caught.changes[1];
      const atSecond = caught.frames.filter((frame) => frame.t <= second + 1).at(-1)?.value;
      const after = caught.frames.filter((frame) => frame.t > second + 1).map((frame) => frame.value);
      const firstDirection = Math.sign((atSecond ?? 0) - caught.frames[0].value);
      // 第二次按下之后的第一帧可能还是按下前算的那一步；从这一帧起不再往原来的方向走：不带速度。
      // 终端顿挫画的是按步取整的位置，比真实位置落后：反向的新一段从真实位置起步，画面会先跳到真实位置一次（不是速度），之后不再往前。
      const reference = style === "terminal" && after.length > 1 ? Math.max(firstDirection * after[0], firstDirection * after[1]) * firstDirection : after[0];
      const checked = style === "terminal" ? after.slice(2) : after.slice(1);
      if (after.length > 1 && checked.some((value) => firstDirection * (value - reference) > 0.05)) {
        fail(`${style}: after the second press the knob keeps going (${atSecond} → ${JSON.stringify(after.slice(0, 4))}): it carries velocity`);
      }
      if (style === "terminal") {
        // 按步跳：画面最多 TERMINAL_STEPS + 1 个位置（含起点），弹窗的不透明度、分区的位移也一样。
        for (const run of toggle) if (distinct(xs(run)).length > TERMINAL_STEPS + 1) fail(`terminal: the switch takes ${distinct(xs(run)).length} positions: ${JSON.stringify(distinct(xs(run)))}`);
        if (entry.dialog.values.length > TERMINAL_STEPS + 1) fail(`terminal: the dialog takes ${entry.dialog.values.length} looks: ${JSON.stringify(entry.dialog.values)}`);
        for (const run of entry.sections) if (run.values.length > TERMINAL_STEPS + 1) fail(`terminal: the section takes ${run.values.length} looks: ${JSON.stringify(run.values)}`);
      } else {
        for (const run of toggle) if (distinct(xs(run)).length <= TERMINAL_STEPS + 1) fail(`desktop: the switch moves in too few frames to be eased: ${JSON.stringify(distinct(xs(run)))}`);
      }
    }
    if (style === "smooth") {
      // 弹簧：能接住、带速度——第二次按下后滑块先顺着原来的方向再走一点，然后回头，中间不跳。
      const second = caught.changes[1];
      const atSecond = caught.frames.filter((frame) => frame.t <= second + 1).at(-1)?.value;
      const after = caught.frames.filter((frame) => frame.t > second + 1).map((frame) => frame.value);
      const firstDirection = Math.sign((atSecond ?? 0) - caught.frames[0].value);
      if (!(after.length > 1 && after.slice(1).some((value) => firstDirection * (value - after[0]) > 0.05))) fail(`smooth: the caught knob does not carry its velocity: ${JSON.stringify(entry.caught)}`);
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

import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";
import { seedFrontendPreferences, writeFrontendPreferences } from "./frontendPreferencesFixture.js";

const STYLES = ["none", "smooth", "desktop", "terminal", "ultra"];
const EFFECT_TIMEOUT_MS = 500;
const GEOMETRY_DELAY_MS = 1500;
const RECT_TOLERANCE_PX = 0.5;

/**
 * 五种风格共用一段交互：呈现只能改像素，不能推迟挂载、焦点、命中和可访问性。
 * 测量从浏览器收到真实输入事件开始，不把 Playwright 的往返时间算成交互延迟。
 * 即时快照在任何等待之前保存；快速往返之间不等动画。稳定几何另行采样，不能用它放行操作。
 */
export default async function interactionParityJourney(page, baseUrl) {
  const app = await loadAppCopy("zh-CN");
  const settings = await loadSettingsText("zh-CN");
  const failures = [];
  const results = [];
  let activeStyle = "setup";
  const onPageError = error => failures.push(`${activeStyle}: pageerror: ${String(error)}`);
  page.on("pageerror", onPageError);
  const check = (style, step, actual, expected, reason) => {
    if (JSON.stringify(actual) !== JSON.stringify(expected)) {
      failures.push(`${style}: ${step}: actual=${JSON.stringify(actual)}, expected=${JSON.stringify(expected)}; ${reason}`);
    }
  };
  const primary = id => page.getByRole("navigation", { name: app.shell.pageNav, exact: true })
    .getByRole("button", { name: app.page[id], exact: true });
  const tab = id => page.locator(`.page-subnav-group[data-page-id="details"] [data-tab-value="${id}"]`);
  const point = locator => locator.evaluate(element => {
    const r = element.getBoundingClientRect();
    return { x: r.x + r.width / 2, y: r.y + r.height / 2 };
  });
  const mouse = xy => page.mouse.click(xy.x, xy.y);
  const scenario = { capabilities: "ready", settings: "ready", runtimeEffectOwners: true,
    debugMode: true, operations: "empty", operationCanceled: false,
    resourceTableVariant: "many", resourceTableRowCount: 3, resourceBreakdownGeneration: 0 };

  await page.setViewportSize({ width: 1280, height: 800 });
  await seedFrontendPreferences(page, value => {
    value.appearance.language = "zh-CN";
    value.appearance.animationStyle = "none";
    value.appearance.autoDisableAnimations = false;
  });
  try {
    for (const style of STYLES) {
      activeStyle = style;
      const result = { style, steps: [], settledGeometry: {}, targets: {} };
      results.push(result);
      try {
        const response = await page.request.post(`${baseUrl}/__test/scenario`, { data: scenario });
        if (!response.ok()) throw new Error(`scenario HTTP ${response.status()}`);
        // 每种风格重新载入相同场景；缓存只改前端自己的外观字段。
        if (style === "none") await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
        await writeFrontendPreferences(page, value => {
          value.appearance.language = "zh-CN";
          value.appearance.animationStyle = style;
          value.appearance.autoDisableAnimations = false;
        });
        await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
        await page.waitForFunction(id => document.body.dataset.animations === id
          && document.documentElement.lang === "zh-CN", style, { timeout: 7000 });
        await primary("monitor").waitFor();
        await page.locator('.resource-table-row.software-row[aria-hidden="false"]').first().waitFor({ timeout: 15000 });
        // 先载入分包与观察数据；预热只等真实控件出现，不以动画完成作为条件。
        for (const id of ["details", "monitor"]) {
          await mouse(await point(primary(id)));
          await page.waitForFunction(id => document.body.dataset.page === id
            && document.querySelector(`#${id}Page`), id, { timeout: 7000 });
          if (id === "details") {
            // mock 里 GPU 调度模型一直在读取中、没有可点的控件：只等它的页签选中；设备拓扑等到有可点的控件。
            for (const secondary of ["gpu", "device"]) {
              await mouse(await point(tab(secondary)));
              await page.waitForFunction(value => document.querySelector('.page-subnav-group:not([hidden]) [aria-selected="true"]')
                ?.getAttribute("data-tab-value") === value && (value === "gpu" || [...document.querySelectorAll("#pageStage button")]
                .some(element => !element.disabled && element.getClientRects().length > 0)), secondary, { timeout: 7000 });
            }
          }
        }
        await installProbe(page, app.misc.close);
        result.targets.primary = {};
        for (const id of ["monitor", "details"]) result.targets.primary[id] = await point(primary(id));
        // none 的实测框就是终态；会动的风格复用其坐标，不能跟着错误移动的真实控件追着点。
        const navPoints = results[0].targets.primary ?? result.targets.primary;
        let roleQuery = page.getByRole("dialog");
        for (const role of ["button", "tab", "row", "textbox", "searchbox", "combobox", "menuitem", "checkbox", "radio", "link"]) {
          roleQuery = roleQuery.or(page.getByRole(role));
        }
        const accessibleGhosts = roleQuery.and(page.locator("[data-exit-ghost], [data-exit-ghost] *"));
        const checkGhostRoles = async entry => {
          entry.roleLeaks = await accessibleGhosts.evaluateAll(elements => elements.map(element => ({
            tag: element.tagName, role: element.getAttribute("role"), label: element.getAttribute("aria-label")
          })));
          check(style, entry.step, entry.roleLeaks, [], "Playwright 角色查询不得包含退出幽灵");
        };

        const record = async (entry, probeRoles = true) => {
          const previous = result.steps.at(-1);
          entry.sincePreviousActionMs = previous && entry.startedAt !== null && previous.startedAt !== null
            ? entry.startedAt - previous.startedAt : null;
          result.steps.push(entry);
          check(style, entry.step, entry.immediate.effect, true, "输入后的即时快照必须已经达到交互终态");
          if (entry.eventEffect !== null) check(style, entry.step, entry.eventEffect, true, "事件处理结束时就应达到交互终态");
          check(style, entry.step, entry.effectMs !== null, true, `输入到副作用 ${EFFECT_TIMEOUT_MS} ms 内必须有读数，实际=${entry.effectMs}`);
          check(style, entry.step, entry.state.ghostHit, false, "elementFromPoint 不得命中幽灵");
          check(style, entry.step, entry.state.focusInGhost, false, "幽灵不得持有焦点");
          check(style, entry.step, entry.ghosts.violations, [], "幽灵必须 pointer-events:none、inert、aria-hidden，且整棵副本没有 id/role");
          if (style === "none" || style === "ultra") {
            check(style, entry.step, entry.ghosts.count, 0, "无动画风格不应生成退出幽灵");
          }
          if (entry.geometry) {
            check(style, entry.step, entry.geometry.hit, true, "真实控件在终态中心必须可命中");
            check(style, entry.step, entry.geometry.motion, [], "真实入场节点及其祖先只允许不透明度动画");
          }
          // 快速往返每步检查 DOM 隔离，整段结束才查一次角色，减少两次输入之间的协议往返。
          entry.roleLeaks = null;
          if (probeRoles) await checkGhostRoles(entry);
        };
        const run = async (step, expected, action, { event = "click", key, geometryKey, targetPoint, probeRoles = true } = {}) => {
          await page.evaluate(config => window.__parity.arm([config]), { step, expected, event, key, geometryKey, targetPoint });
          await action();
          // 这是动作之后的下一次 evaluate；不能先 waitFor、读角色或等一帧。
          let entry = await page.evaluate(() => {
            window.__parity.captureImmediate();
            return window.__parity.finish()[0];
          });
          if (entry.effectMs === null) {
            try {
              await page.waitForFunction(() => window.__parity.poll(), undefined, { timeout: EFFECT_TIMEOUT_MS, polling: 5 });
            } catch { /* 超时也保留即时状态和计时，后面与 none 一起报告。 */ }
            entry = await page.evaluate(() => window.__parity.finish()[0]);
          }
          await record(entry, probeRoles);
          return entry;
        };

        // 1. Enter 打开：第一份快照里焦点、可访问性和关闭按钮命中都已就绪。
        await page.locator("#taskCenterButton").focus();
        const opened = await run("task-center/Enter", { dialog: true }, () => page.keyboard.press("Enter"), { geometryKey: "dialog" });
        check(style, opened.step, await page.getByRole("dialog", { name: app.taskCenter.title, exact: true }).count(), 1,
          "任务中心必须立即出现在可访问性树中");
        // 2. Escape 后直接点背景导航：两次输入之间没有 evaluate、角色查询或等待。
        await page.evaluate(configs => window.__parity.arm(configs), [
          { step: "task-center/Escape", event: "keydown", key: "Escape", expected: { dialog: false, focusId: "taskCenterButton" }, targetPoint: navPoints.details },
          { step: "task-center/background-click", event: "click", expected: { page: "details", dialog: false },
            geometryKey: "details/device", targetPoint: navPoints.details }
        ]);
        await page.keyboard.press("Escape");
        await mouse(navPoints.details);
        await page.evaluate(() => window.__parity.captureImmediate());
        try {
          await page.waitForFunction(() => window.__parity.poll(), undefined,
            { timeout: EFFECT_TIMEOUT_MS, polling: 5 });
        } catch { /* 与普通步骤使用相同的短超时。 */ }
        const closed = await page.evaluate(() => window.__parity.finish());
        // 幽灵在下一帧画出来之前放好：再等一帧数任务中心的幽灵（它的退出槽位比这段等待长）。
        result.taskCenterGhosts = await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => resolve(
          [...document.querySelectorAll("[data-exit-ghost]")].filter(root => root.matches(".task-center-modal, .task-center-backdrop")
            || root.querySelector(".task-center-modal")).length))));
        for (const entry of closed) await record(entry);
        closed[0].roleLeaks = closed[1].roleLeaks;
        check(style, closed[0].step, closed[0].state.focus.id, "taskCenterButton", "Escape 必须立即还焦点");
        if (["smooth", "desktop", "terminal"].includes(style)) {
          check(style, closed[0].step, result.taskCenterGhosts > 0, true, "会动的风格在退出后应留下任务中心的纯视觉幽灵");
        }

        // 3. 四次连续一级切换；原生导航按钮用 Enter 激活，坐标从常驻导航的终态框取得。
        for (const [index, id] of ["monitor", "details", "monitor", "details"].entries()) {
          const keyboard = index % 2 === 0;
          if (keyboard) await primary(id).focus();
          await run(`primary/${index + 1}/${id}`, { page: id },
            () => keyboard ? page.keyboard.press("Enter") : mouse(navPoints[id]),
            { geometryKey: id === "details" ? "details/device" : id, targetPoint: navPoints[id], probeRoles: false });
        }
        await checkGhostRoles(result.steps.at(-1));

        // 4. 二级分页也快速来回；方向键沿用产品的 roving focus 与自动激活。
        result.targets.secondary = { device: await point(tab("device")), gpu: await point(tab("gpu")) };
        const tabPoints = results[0].targets.secondary ?? result.targets.secondary;
        for (const [index, id] of ["gpu", "device", "gpu", "device"].entries()) {
          const keyboard = index % 2 === 1;
          await run(`secondary/${index + 1}/${id}`, { page: "details", tab: id },
            () => keyboard ? page.keyboard.press("ArrowLeft") : mouse(tabPoints[id]),
            { event: keyboard ? "keydown" : "click", key: keyboard ? "ArrowLeft" : undefined,
              // GPU 调度模型在 mock 里一直在读取中，没有控件可量；只量设备拓扑的几何。
              geometryKey: id === "device" ? `details/${id}` : undefined, targetPoint: tabPoints[id], probeRoles: false });
        }
        await checkGhostRoles(result.steps.at(-1));
        await run("primary/return-monitor", { page: "monitor" }, () => mouse(navPoints.monitor),
          { geometryKey: "monitor", targetPoint: navPoints.monitor });

        // 5. 三个软件让整个小表都在虚拟行窗口里；不把不可见的预分配行算进结果。
        const panel = page.locator(".resource-table-panel:not([data-exit-ghost]):not([data-exit-ghost] *)");
        await panel.evaluate(element => element.scrollIntoView({ block: "start", behavior: "instant" }));
        const row = panel.locator('.software-row[aria-hidden="false"][aria-expanded="false"]').first();
        await row.focus();
        const rowId = await row.getAttribute("data-resource-row-id");
        if (!rowId) throw new Error("resource table has no expandable software row");
        const rowSelector = `.resource-table-row[data-resource-row-id=${JSON.stringify(rowId)}][aria-hidden="false"]:not([data-exit-ghost]):not([data-exit-ghost] *)`;
        result.targets.resourceRow = { id: rowId, point: await point(page.locator(rowSelector).locator(".resource-row-expander")) };
        const expanderPoint = (results[0].targets.resourceRow ?? result.targets.resourceRow).point;
        const baselineRows = await page.evaluate(() => window.__parity.state().rows);
        let expandedRows;
        for (const [index, expanded] of [true, false, true, false].entries()) {
          const keyboard = index < 2;
          const entry = await run(`resource-row/${index + 1}/${expanded ? "expand" : "collapse"}`,
            { row: rowId, expanded, focusedRow: rowId },
            () => keyboard ? page.keyboard.press(expanded ? "ArrowRight" : "ArrowLeft") : mouse(expanderPoint),
            { event: keyboard ? "keydown" : "click", key: keyboard ? expanded ? "ArrowRight" : "ArrowLeft" : undefined,
              targetPoint: expanderPoint, probeRoles: false });
          check(style, entry.step, entry.state.focusedRow, rowId, "展开折叠不能丢失当前行焦点");
          if (expanded) {
            if (!expandedRows) expandedRows = entry.state.rows;
            check(style, entry.step, entry.state.rows.length > baselineRows.length, true, "展开立即增加进程行");
            check(style, entry.step, entry.state.rows, expandedRows, "两种输入展开后的可见行集合一致");
          } else check(style, entry.step, entry.state.rows, baselineRows, "折叠立即恢复原行集合");
        }
        await checkGhostRoles(result.steps.at(-1));

        // 6. 从真实当前行开始，不重新强制焦点，观察 Tab 与 Shift+Tab 的自然结果。
        for (const [index, key] of ["Tab", "Tab", "Shift+Tab", "Shift+Tab"].entries()) {
          const entry = await run(`focus/${index + 1}/${key}`, { focusChanged: true }, () => page.keyboard.press(key),
            { event: "keydown", key: "Tab" });
          check(style, entry.step, entry.state.focus.inert, false, "Tab 不得进入 inert 区域");
        }

        // 几何审计与快速脚本分开：同一次进入里，点击处理完那一刻（第一帧之前）量一次，1.5 秒后再量同一个控件，两份框必须相同。
        // 不跨两次进入比较：监视控制台会恢复上一次的滚动位置。
        // 监视控制台在第 5 步滚到了资源表：先滚回顶上，再从另一页进来（每条路由都从别的页进入）。
        await page.evaluate(() => {
          for (const element of document.querySelectorAll("main, main *")) if (element.scrollTop > 0) element.scrollTop = 0;
        });
        for (const [route, from] of [["monitor", "details"], ["details/device", "monitor"]]) {
          const [id, secondary] = route.split("/");
          await mouse(navPoints[from]);
          await page.waitForFunction(value => document.body.dataset.page === value, from, { timeout: 7000 });
          await page.locator("#taskCenterButton").focus();
          const audit = await page.evaluate(async ({ buttons, key, delay }) => {
            for (const button of buttons) button.click();
            const first = window.__parity.geometry(key);
            await new Promise(resolve => setTimeout(resolve, delay));
            return { first, settled: window.__parity.geometry(key) };
          }, { buttons: [await primary(id).elementHandle(), ...(secondary ? [await tab(secondary).elementHandle()] : [])],
            key: route, delay: GEOMETRY_DELAY_MS });
          result.settledGeometry[route] = audit;
          check(style, `${route}/geometry-audit`, audit.first?.hit, true, `第一帧的控件必须在原处命中：${JSON.stringify(audit.first)}`);
          check(style, `${route}/geometry-audit`, sameRects(audit.first, audit.settled), true,
            `同一次进入的第一份框=${JSON.stringify(audit.first)}, 1.5s 框=${JSON.stringify(audit.settled)}; 容差 ${RECT_TOLERANCE_PX} px`);
        }
        // 快速脚本早已关掉初始弹窗；几何审计重新挂载同一个任务中心，在 1.5 秒后复测同一语义控件。
        // 所有样式都走这条固定审计路线，首份框始终来自最初 Enter 之后的即时快照。
        await page.locator("#taskCenterButton").focus();
        await page.keyboard.press("Enter");
        result.dialogGeometryAudit = await page.evaluate(async delay => {
          const node = [...document.querySelectorAll("[role='dialog']")].find(element => !element.closest("[data-exit-ghost]"));
          const first = window.__parity.geometry("dialog");
          const started = performance.now();
          await new Promise(resolve => setTimeout(resolve, delay));
          return { first, settled: window.__parity.geometry("dialog"), elapsedMs: performance.now() - started,
            sameNode: Boolean(node?.isConnected && !node.closest("[data-exit-ghost]")) };
        }, GEOMETRY_DELAY_MS);
        result.settledGeometry.dialog = result.dialogGeometryAudit.settled;
        check(style, "task-center/geometry-audit", result.dialogGeometryAudit.sameNode, true, "1.5 秒复测期间必须保持同一个真实弹窗节点");
        check(style, "task-center/geometry-audit", sameRects(result.dialogGeometryAudit.first, result.dialogGeometryAudit.settled), true,
          `同一次挂载的第一份框=${JSON.stringify(result.dialogGeometryAudit.first)}, 1.5s 框=${JSON.stringify(result.dialogGeometryAudit.settled)}`);
        await page.keyboard.press("Escape");
        result.labels = { taskCenter: app.taskCenter.title, close: app.misc.close,
          animationStyle: settings.appearance.animationStyleTitle };
      } catch (error) {
        result.error = String(error);
        failures.push(`${style}: script: actual=${String(error)}, expected=completed; 无法继续该风格，保留已有步骤`);
      }
    }

    const baseline = results.find(result => result.style === "none");
    for (const result of results.filter(item => item.style !== "none")) {
      check(result.style, "script/steps", result.steps.map(step => step.step), baseline.steps.map(step => step.step), "必须运行同一段脚本");
      for (const [index, entry] of result.steps.entries()) {
        const reference = baseline.steps[index];
        if (!reference || entry.step !== reference.step) continue;
        check(result.style, entry.step, entry.state, reference.state, "交互状态必须与 none 完全相同（幽灵数量仅是视觉证据，不参与状态比较）");
        check(result.style, entry.step, entry.eventState, reference.eventState, "事件处理后的交互状态必须与 none 完全相同");
        if (entry.geometryKey) {
          check(result.style, entry.step, sameRects(entry.geometry, reference.geometry), true,
            `命中几何必须与 none 一致（容差 ${RECT_TOLERANCE_PX} px）；style=${JSON.stringify(entry.geometry)}, none=${JSON.stringify(reference.geometry)}`);
        }
        entry.noneEffectMs = reference.effectMs;
        entry.effectDeltaMs = entry.effectMs === null || reference.effectMs === null ? null : entry.effectMs - reference.effectMs;
        if (entry.effectDeltaMs === null || Math.abs(entry.effectDeltaMs) > 50) {
          failures.push(`${result.style}: ${entry.step}: effectMs=${entry.effectMs}, none.effectMs=${reference.effectMs}, deltaMs=${entry.effectDeltaMs}; 可操作时刻相差不得超过 50 ms（null 表示 ${EFFECT_TIMEOUT_MS} ms 内没有交互副作用）`);
        }
      }
    }
    if (failures.length) throw new Error(`${failures.join("\n")}\n${JSON.stringify({ results })}`);
    return { results };
  } finally {
    await page.evaluate(() => window.__parity?.dispose()).catch(() => {});
    page.off("pageerror", onPageError);
  }
}

function sameRects(first, second) {
  return Boolean(first?.rect && second?.rect && first?.controlRect && second?.controlRect
    && ["x", "y", "width", "height"].every(key => Math.abs(first.rect[key] - second.rect[key]) <= RECT_TOLERANCE_PX
      && Math.abs(first.controlRect[key] - second.controlRect[key]) <= RECT_TOLERANCE_PX));
}

/** 页内探针只读 DOM；事件、MutationObserver 和 focusin 共享同一个副作用判定。没有逐帧轮询或递归。 */
async function installProbe(page, closeLabel) {
  await page.evaluate(closeLabel => {
    const ghostSelector = "[data-exit-ghost]";
    const focusSelector = "button, input, select, textarea, a[href], summary, [tabindex]";
    const clean = value => (value ?? "").replace(/\s+/g, " ").trim();
    const name = element => {
      if (!element || element === document.body || element === document.documentElement) return null;
      const labelledBy = element.getAttribute("aria-labelledby");
      return clean(element.getAttribute("aria-label") ?? (labelledBy
        ? labelledBy.split(/\s+/).map(id => document.getElementById(id)?.textContent ?? "").join(" ")
        : element.textContent));
    };
    const role = element => element?.getAttribute("role") ?? ({ BUTTON: "button", INPUT: element?.type === "search" ? "searchbox" : "textbox",
      SELECT: "combobox", TEXTAREA: "textbox", A: "link", SUMMARY: "button" }[element?.tagName] ?? element?.tagName?.toLowerCase() ?? null);
    const descriptor = element => ({ role: role(element), name: name(element),
      id: element?.id === "taskCenterButton" ? element.id : null,
      inert: Boolean(element?.closest("[inert]")) });
    const present = element => Boolean(element && !element.closest(`${ghostSelector}, [hidden], [aria-hidden='true']`)
      && getComputedStyle(element).display !== "none" && getComputedStyle(element).visibility !== "hidden"
      && element.getBoundingClientRect().width > 0 && element.getBoundingClientRect().height > 0);
    const rect = element => {
      if (!element) return null;
      const r = element.getBoundingClientRect();
      return { x: r.x, y: r.y, width: r.width, height: r.height };
    };
    const hit = element => {
      if (!element) return { matches: false, ghost: false };
      const r = element.getBoundingClientRect();
      const top = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2);
      return { matches: Boolean(top && element.contains(top)), ghost: Boolean(top?.closest(ghostSelector)) };
    };
    const state = () => {
      const focus = document.activeElement;
      const rows = [...document.querySelectorAll("[role='row']")].filter(present);
      const selected = [...document.querySelectorAll("[role='tab'][aria-selected='true']")].filter(present);
      const points = [...document.querySelectorAll(ghostSelector)].map(element => {
        const r = element.getBoundingClientRect();
        return { x: r.x + r.width / 2, y: r.y + r.height / 2 };
      });
      if (current?.targetPoint) points.push(current.targetPoint);
      return { page: document.body.dataset.page, focus: descriptor(focus),
        focusedRow: focus?.closest("[data-resource-row-id]")?.getAttribute("data-resource-row-id") ?? null,
        dialogs: [...document.querySelectorAll("[role='dialog']")].filter(present).map(descriptor),
        selectedTabs: selected.map(element => ({ value: element.getAttribute("data-tab-value"), name: name(element) })),
        rows: rows.map(element => ({ id: element.getAttribute("data-resource-row-id"), name: name(element), expanded: element.getAttribute("aria-expanded") })),
        visibleRowCount: rows.length,
        expandedRowIds: rows.filter(element => element.getAttribute("aria-expanded") === "true").map(element => element.getAttribute("data-resource-row-id")),
        focusInGhost: Boolean(focus?.closest(ghostSelector)),
        ghostHit: points.some(p => Boolean(document.elementFromPoint(p.x, p.y)?.closest(ghostSelector))) };
    };
    const ghosts = () => {
      const roots = [...document.querySelectorAll(ghostSelector)];
      const violations = [];
      for (const [index, root] of roots.entries()) {
        if (!root.inert || root.getAttribute("aria-hidden") !== "true" || getComputedStyle(root).pointerEvents !== "none") {
          violations.push({ index, inert: root.inert, ariaHidden: root.getAttribute("aria-hidden"), pointerEvents: getComputedStyle(root).pointerEvents });
        }
        for (const node of [root, ...root.querySelectorAll("*")]) {
          if (node.hasAttribute("id") || node.hasAttribute("role")) {
            violations.push({ index, tag: node.tagName, id: node.getAttribute("id"), role: node.getAttribute("role") });
          }
        }
      }
      return { count: roots.length, taskCenterCopies: roots.filter(root => root.matches(".task-center-modal, .task-center-backdrop")
        || root.querySelector(".task-center-modal")).length, violations };
    };
    const geometry = key => {
      const stage = key === "dialog" ? [...document.querySelectorAll("[role='dialog']")].find(present)
        : document.querySelector("#pageStage:not([data-exit-ghost]):not([data-exit-ghost] *)");
      const control = stage && (key === "dialog"
        ? [...stage.querySelectorAll("button")].find(element => name(element) === closeLabel)
        : [...stage.querySelectorAll(focusSelector)].find(element => present(element) && !element.disabled
          && !element.closest("[inert]") && element.tabIndex >= 0));
      const motion = [];
      let ancestor = control;
      for (let depth = 0; ancestor && depth < 64; depth += 1) {
        const style = getComputedStyle(ancestor);
        for (const property of ["transform", "translate", "scale", "rotate"]) {
          const value = style[property];
          const neutral = !value || value === "none" || (property === "transform" ? new DOMMatrixReadOnly(value).isIdentity
            : property === "rotate" ? Number.parseFloat(value) === 0
              : value.split(/\s+/).every(part => Number.parseFloat(part) === (property === "scale" ? 1 : 0)));
          if (!neutral) {
            motion.push({ tag: ancestor.tagName, property, value });
          }
        }
        for (const animation of ancestor.getAnimations()) {
          const frames = animation.effect?.getKeyframes?.() ?? [];
          for (const property of ["transform", "translate", "scale", "height", "maxHeight", "blockSize"]) {
            const values = [...new Set(frames.map(frame => frame[property]).filter(value => value !== undefined))];
            if (values.length > 1) motion.push({ tag: ancestor.tagName, property, values });
          }
        }
        if (ancestor === stage) break;
        ancestor = ancestor.parentElement;
      }
      return { rect: rect(stage), controlRect: rect(control), control: descriptor(control), hit: hit(control).matches, motion };
    };
    let current = null;
    let pending = [];
    let observer;
    const expectedNow = item => {
      const expected = item.expected;
      if (expected.page && document.body.dataset.page !== expected.page) return false;
      if (expected.dialog !== undefined) {
        const dialog = [...document.querySelectorAll("[role='dialog']")].find(present);
        if (Boolean(dialog) !== expected.dialog || (expected.dialog && (!dialog.contains(document.activeElement) || !hit(dialog.querySelector(".modal-header button")).matches))) return false;
        if (!expected.dialog && document.activeElement?.closest("[data-exit-ghost], [inert]")) return false;
      }
      if (expected.tab && document.querySelector('.page-subnav-group:not([hidden]) [aria-selected="true"]')?.getAttribute("data-tab-value") !== expected.tab) return false;
      if (expected.row) {
        const row = [...document.querySelectorAll("[data-resource-row-id]")].find(element => present(element) && element.getAttribute("data-resource-row-id") === expected.row);
        if (row?.getAttribute("aria-expanded") !== String(expected.expanded)
          || row?.querySelector(".resource-row-expander")?.getAttribute("aria-expanded") !== String(expected.expanded)) return false;
      }
      if (expected.focusedRow && document.activeElement?.closest("[data-resource-row-id]")?.getAttribute("data-resource-row-id") !== expected.focusedRow) return false;
      if (expected.focusId && document.activeElement?.id !== expected.focusId) return false;
      if (expected.focusChanged && document.activeElement === item.beforeFocus) return false;
      if (item.geometryKey && !geometry(item.geometryKey).hit) return false;
      return true;
    };
    const observeEffect = () => {
      if (current?.started !== undefined && current.effectMs === null && expectedNow(current)) current.effectMs = performance.now() - current.started;
    };
    const snapshot = item => ({ effect: expectedNow(item), at: performance.now(), state: state(), ghosts: ghosts(),
      geometry: item.geometryKey ? geometry(item.geometryKey) : null });
    const onInput = event => {
      const next = pending.find(item => item.started === undefined && item.event === event.type && (!item.key || item.key === event.key));
      if (!next) return;
      current = next;
      next.started = performance.now();
      next.beforeFocus = document.activeElement;
    };
    const afterInput = event => {
      if (!current || (event.type === "keyup" && current.key !== event.key)) return;
      observeEffect();
      current.eventSnapshot = snapshot(current);
    };
    window.addEventListener("click", onInput, true);
    window.addEventListener("keydown", onInput, true);
    // 点击在产品的 document 委托处理后读取。Escape 会被焦点陷阱停止传播，故在随后真实 keyup 读取关闭结果。
    // 不在 capture 监听器中排 microtask：浏览器可能在两个事件监听器之间清空微任务队列，那时产品尚未处理输入。
    window.addEventListener("click", afterInput);
    window.addEventListener("keyup", afterInput, true);
    document.addEventListener("focusin", observeEffect);
    window.__parity = {
      state, geometry,
      get pending() { return pending; },
      poll() { observeEffect(); return pending.every(item => item.effectMs !== null); },
      arm(configs) {
        observer?.disconnect();
        current = null;
        pending = configs.map(config => ({ ...config, effectMs: null }));
        observer = new MutationObserver(observeEffect);
        observer.observe(document.body, { subtree: true, childList: true, attributes: true,
          attributeFilter: ["data-page", "aria-selected", "aria-expanded", "aria-hidden", "inert"] });
      },
      captureImmediate() {
        observeEffect();
        if (current) current.nextEvaluation = snapshot(current);
      },
      finish() {
        return pending.map(item => {
          const immediate = item.nextEvaluation ?? item.eventSnapshot ?? snapshot(item);
          return { step: item.step, expected: item.expected, geometryKey: item.geometryKey ?? null,
            effectMs: item.effectMs, immediate: { effect: immediate.effect }, state: immediate.state,
            ghosts: immediate.ghosts, geometry: immediate.geometry,
            startedAt: item.started ?? null, snapshotAfterMs: item.started === undefined ? null : immediate.at - item.started,
            eventEffect: item.eventSnapshot?.effect ?? null,
            eventState: item.eventSnapshot?.state ?? null };
        });
      },
      dispose() {
        observer?.disconnect();
        window.removeEventListener("click", onInput, true);
        window.removeEventListener("keydown", onInput, true);
        window.removeEventListener("click", afterInput);
        window.removeEventListener("keyup", afterInput, true);
        document.removeEventListener("focusin", observeEffect);
      }
    };
  }, closeLabel);
}

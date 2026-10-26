/*
 * 设备拓扑的整机形态和三栏布局（用户 2026-10-25：「删掉底下四个没用信息，加上识别信息……上面那个切换内外的，
 * 要放到和底下内部接口，外部接口一样的位置，右边太空了，所以建议缩减大小给左边信息腾位置」）。
 * - 外观图下没有 CPU / GPU / MEM / IO 四格；
 * - 内外切换在左栏标题里，页头没有，标题不再另写一遍“外部接口／内部接口”，计数仍在；
 * - 形态由后端给：空事实是“未知（原因码）”；五种形态各有自己的示意和依据（mock 的 fixtureMachineIdentity 样例）；
 * - 宽屏三栏时右栏比左栏、中栏都窄，左栏比原来的 340 px 宽；窄屏时右栏移到下面，再窄成一列；
 * - 示意图入场：正常档有位移动画，无动画／极简档时长为 0。
 * publish(patch) 改 mock 场景并等到下一次推送。
 */
export async function checkMachineIdentity(page, topology, publish) {
  const view = topology.getByRole("group", { name: "电脑俯视图" });
  await view.waitFor({ state: "visible", timeout: 5_000 });

  const leftoverCells = await view.evaluate((element) => [...element.querySelectorAll("*")]
    .filter((node) => node.children.length === 0)
    .map((node) => node.textContent?.trim() ?? "")
    .filter((text) => ["CPU", "GPU", "MEM", "IO"].includes(text)));
  assert(leftoverCells.length === 0 && await topology.locator(".device-laptop-base").count() === 0,
    `The four empty CPU/GPU/MEM/IO cells are still shown: ${JSON.stringify(leftoverCells)}`);

  const placement = await topology.evaluate((panel) => {
    const title = panel.querySelector(".device-port-list-title");
    const rest = title?.cloneNode(true);
    rest?.querySelector(".device-scope-tabs")?.remove();
    return {
      groups: panel.querySelectorAll(".device-scope-tabs").length,
      inTitle: Boolean(title?.querySelector(".device-scope-tabs[role='radiogroup']")),
      inHeader: Boolean(panel.querySelector(".device-topology-header .device-scope-tabs")),
      titleRest: rest?.textContent?.trim() ?? null
    };
  });
  assert(placement.groups === 1 && placement.inTitle && !placement.inHeader
    && /^\d+ 本机 · \d+ 扩展$/.test(placement.titleRest ?? ""),
  `The scope switch is not the list title (or the title repeats the scope): ${JSON.stringify(placement)}`);

  // 原来在页头时切换总是看得见；放进会滚动的清单后，标题停在顶上。
  const sticky = await topology.locator(".device-port-list").evaluate((list) => {
    // 没滚动时标题不压住下面的搜索框（吸顶偏移曾把标题推下 10 px，盖住搜索框上边）。
    const restTitleBottom = list.querySelector(".device-port-list-title").getBoundingClientRect().bottom;
    const restSearchTop = list.querySelector(".device-port-search").getBoundingClientRect().top;
    const previous = list.style.height;
    list.style.height = "120px";
    list.scrollTop = list.scrollHeight;
    const tabs = list.querySelector(".device-scope-tabs").getBoundingClientRect();
    const box = list.getBoundingClientRect();
    const result = { restTitleBottom, restSearchTop, scrolled: list.scrollTop > 0, tabsTop: tabs.top, tabsBottom: tabs.bottom, listTop: box.top, listBottom: box.bottom };
    list.scrollTop = 0;
    list.style.height = previous;
    return result;
  });
  assert(sticky.restTitleBottom <= sticky.restSearchTop + 0.5
    && sticky.scrolled && sticky.tabsTop >= sticky.listTop - 1 && sticky.tabsBottom <= sticky.listBottom + 1,
    `The scope switch scrolls away with the list: ${JSON.stringify(sticky)}`);

  // 换形态时旧的整机图留着淡出的幽灵（不可命中、aria-hidden）：只读真实节点。
  const readMachine = () => view.evaluate((element) => {
    const all = (selector) => [...element.querySelectorAll(selector)].filter((node) => !node.closest("[data-exit-ghost]"));
    const one = (selector) => all(selector)[0] ?? null;
    return {
    figure: one(".device-machine-figure")?.getAttribute("data-form-factor") ?? null,
    name: one(".device-machine-form strong")?.textContent?.trim() ?? null,
    lines: [...all(".device-machine-evidence li")]
      .map((li) => `${li.querySelector("span")?.textContent?.trim() ?? ""}|${li.querySelector("strong")?.textContent?.trim() ?? ""}`),
    empty: one(".device-machine-evidence-empty")?.textContent?.trim() ?? null,
    laptopParts: all(".device-machine-hinge, .device-machine-deck").length,
    feet: all(".device-machine-feet").length,
    manufacturer: one(".device-machine-manufacturer")?.textContent?.trim() ?? null
    };
  });

  // 后端默认fixture明确报告desktop；缺字段是解码错误，不在呈现中补默认。
  const initial = await readMachine();
  assert(initial.figure === "desktop" && initial.name === "桌面主机"
    && JSON.stringify(initial.lines) === JSON.stringify(["机箱类型|桌面式", "无内置电池|"])
    && initial.manufacturer === "Frontend Harness",
    `Backend identity did not reach the machine view: ${JSON.stringify(initial)}`);

  const samples = [
    ["laptop", "laptop", "笔记本", ["机箱类型|笔记本", "有内置电池|"], 2, 0],
    ["mobile_workstation", "mobile_workstation", "移动工作站",
      ["机箱类型|笔记本", "有内置电池|", "专业 GPU|NVIDIA RTX PRO 5000 Blackwell Generation Laptop GPU"], 2, 0],
    ["workstation", "workstation", "工作站",
      ["机箱类型|塔式", "无内置电池|", "工作站级 CPU|AMD Ryzen Threadripper PRO 9955WX 16-Cores",
        "专业 GPU|NVIDIA RTX PRO 6000 Blackwell Workstation Edition"], 0, 1],
    ["desktop", "desktop", "桌面主机", ["机箱类型|桌面式", "无内置电池|"], 0, 1],
    ["unknown", "unknown", "未识别", ["机箱类型|未知 (source-unavailable)", "电池|未知 (source-unavailable)"], 0, 0]
  ];
  const forms = [];
  for (const [wire, form, name, lines, laptopParts, feet] of samples) {
    await publish({ deviceTopologyMachineIdentity: wire });
    await view.locator(`.device-machine-figure[data-form-factor="${form}"]`).waitFor({ state: "attached", timeout: 5_000 });
    const machine = await readMachine();
    assert(machine.figure === form && machine.name === name && machine.empty === null
      && JSON.stringify(machine.lines) === JSON.stringify(lines)
      && machine.laptopParts === laptopParts && machine.feet === feet,
    `Form factor ${wire} is not shown as the backend reported it: ${JSON.stringify(machine)}`);
    forms.push(machine);
  }

  const original = page.viewportSize();
  const measure = () => topology.evaluate((panel) => {
    const box = (selector) => {
      const rect = panel.querySelector(selector).getBoundingClientRect();
      return { left: rect.left, top: rect.top, width: rect.width, bottom: rect.bottom };
    };
    return { list: box(".device-port-list"), detail: box(".device-port-detail"), top: box(".device-top-view"), layout: box(".device-topology-layout") };
  });
  await page.setViewportSize({ width: 1440, height: 900 });
  const wide = await measure();
  assert(Math.abs(wide.list.top - wide.top.top) < 1 && Math.abs(wide.detail.top - wide.top.top) < 1
    && wide.top.left > wide.detail.left && wide.detail.left > wide.list.left
    && wide.top.width <= 340 && wide.list.width > 340
    && wide.top.width < wide.list.width && wide.top.width < wide.detail.width,
  `The right column is not narrower than the list and details at 1440 px: ${JSON.stringify(wide)}`);
  // 长标签（德语“Externe Schnittstellen”）在较窄的三栏里换行，不把清单撑出横向滚动、不裁掉条目。
  await page.setViewportSize({ width: 1100, height: 900 });
  const longLabels = await topology.locator(".device-port-list").evaluate((list) => {
    const extras = [...list.querySelectorAll(".device-scope-tab")].map((tab, index) => {
      const extra = document.createElement("span");
      extra.textContent = index === 0 ? " Externe Schnittstellen" : " Interne Schnittstellen";
      tab.append(extra);
      return extra;
    });
    const tabs = list.querySelector(".device-scope-tabs").getBoundingClientRect();
    const box = list.getBoundingClientRect();
    const result = { scrollWidth: list.scrollWidth, clientWidth: list.clientWidth, tabsRight: tabs.right, listRight: box.right };
    for (const extra of extras) extra.remove();
    return result;
  });
  assert(longLabels.scrollWidth <= longLabels.clientWidth + 1 && longLabels.tabsRight <= longLabels.listRight,
    `Long scope labels widen the list: ${JSON.stringify(longLabels)}`);
  await page.setViewportSize({ width: 800, height: 700 });
  const medium = await measure();
  assert(Math.abs(medium.list.top - medium.detail.top) < 1 && medium.top.top >= medium.list.bottom
    && Math.abs(medium.top.width - medium.layout.width) < 1,
  `At 800 px the machine view does not move below the list and details: ${JSON.stringify(medium)}`);
  await page.setViewportSize({ width: 700, height: 900 });
  const narrow = await measure();
  assert(narrow.detail.top >= narrow.list.bottom && narrow.top.top >= narrow.detail.bottom
    && Math.abs(narrow.list.width - narrow.layout.width) < 1,
  `At 700 px the layout is not a single column: ${JSON.stringify(narrow)}`);
  await page.setViewportSize(original);

  const motion = await page.evaluate(() => {
    const body = document.body;
    const before = body.dataset.animations;
    const figure = document.querySelector(".device-machine-figure");
    const read = (mode) => {
      body.dataset.animations = mode;
      const style = getComputedStyle(figure);
      return { name: style.animationName, duration: style.animationDuration };
    };
    const result = { desktop: read("desktop"), none: read("none"), ultra: read("ultra") };
    if (before === undefined) delete body.dataset.animations; else body.dataset.animations = before;
    return result;
  });
  assert(motion.desktop.name === "motion-fade-in" && motion.desktop.duration !== "0s"
    && motion.none.duration === "0s" && motion.ultra.duration === "0s",
  `The machine figure entrance does not have a normal and a reduced version: ${JSON.stringify(motion)}`);

  await publish({ deviceTopologyMachineIdentity: null });
  await view.locator('.device-machine-figure[data-form-factor="desktop"]').waitFor({ state: "attached", timeout: 5_000 });
  return { placement, sticky, initial, forms, wide, longLabels, medium, narrow, motion };
}

function assert(condition, message) {
  if (!condition) throw new Error(message);
}

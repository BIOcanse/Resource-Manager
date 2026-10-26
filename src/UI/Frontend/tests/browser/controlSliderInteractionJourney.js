import assert from "node:assert/strict";
import { mkdir } from "node:fs/promises";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import { fanModeFacts } from "./fanModeFacts.js";
import { seedFrontendPreferences, writeFrontendPreferences } from "./frontendPreferencesFixture.js";
import { waitForCondition, waitForNextPaint, waitForStableRendering } from "./journeyWaits.js";

/**
 * 控制面的数值滑条走交互协议的运行时（features/control/ControlNumberSlider.tsx），真实鼠标事件，简体中文。
 * 滑条还是原来的原生 range，只换了行为：
 * - 按在滑轨上：按下不改值，离开拖动框起滑块圆心在指针下；按在滑块上：拖动中保持按下时指针相对圆心的偏移，不跳；
 *   拖动中滑块和数字框跟着指针，期望状态不变（应用按钮一直不可用），松开才写一次；
 * - 单击和原生 range 相同：同一串单击，原生 range 每次得到的值和这里一样（点在滑块上不改，点在滑轨上到那里）；
 * - 拖动中按 Esc、按右键取消：什么都不写，回到拖动前的值；
 * - 方向键、PageUp / PageDown、Home / End 照原生 range 一步一写；
 * - 外观不变：四种界面风格下，可用和置灰两种状态，滑条的截图和原样的原生 range（同一处换上去的原生元素）逐像素相同
 *   （只容浏览器光栅化圆角抗锯齿时差 1 级的抖动），
 *   截图存在 RM_BROWSER_ARTIFACT_DIR。
 * 量程 0–100、步长 5，实际读数 40。
 */
export default async function controlSliderInteractionJourney(page, baseUrl) {
  const shots = process.env.RM_BROWSER_ARTIFACT_DIR
    ?? fileURLToPath(new URL("../../../../../.local/browser-gate/control-slider-interaction/", import.meta.url));
  await mkdir(shots, { recursive: true });
  await fetch(`${baseUrl}/__test/scenario`, { method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ controlActualValues: [{ objectId: "A", capabilityId: "fan.duty", number: 40, unit: "%" }] }) });
  let objects = [];
  const writes = [];
  const state = () => ({ desired: { objects }, lastApply: { outcomes: [], appliedAt: new Date().toISOString() } });
  await page.route("**/api/control/objects", route => route.fulfill({ json: {
    readAt: new Date().toISOString(), objects: [{
      id: "A", ...fanModeFacts(["fan.duty", "fan.duty-limit"], { firmware: false }), kind: "fan", reportedName: "Fan A", isControllable: true,
      platform: { operatingSystem: "windows", vendor: "test" }, terms: [],
      capabilities: [{ id: "fan.duty", valueKind: "number", supported: true, requiredAccessLevel: "normal",
        range: { minimum: 0, maximum: 100, step: 5, unit: "%", defaultValue: null } },
      // 不勾选的第二项：它的滑条置灰，用来比置灰时的外观（没有译名，界面上显示 id）。
      { id: "fan.duty-limit", valueKind: "number", supported: true, requiredAccessLevel: "normal",
        range: { minimum: 0, maximum: 100, step: 5, unit: "%", defaultValue: 30 } }]
    }]
  } }));
  await page.route("**/api/control/state**", route => {
    if (route.request().method() !== "PUT") return route.fulfill({ json: state() });
    const id = decodeURIComponent(new URL(route.request().url()).pathname.split("/").pop());
    const settings = route.request().postDataJSON();
    writes.push({ id, settings });
    objects = [...objects.filter(item => item.objectId !== id), ...(settings.length ? [{ objectId: id, settings }] : [])];
    return route.fulfill({ json: state() });
  });
  /*
   * 动画关掉（“无动画”）：滑块到终态、页面的颜色过渡都立即完成，每一步之后的状态是确定的，不靠等待时间。
   * 默认的“自动”按这台机器的 GPU 计划决定开不开，负载重时还在半路的运动会让下一步按在没停稳的滑块上
   * （按在滑块上是抓住，保持按下时相对圆心的偏移，滑块没停在 65 时拖到 30 处得到 35），
   * 截图之间也会落在过渡的不同时刻。手感和动效由 motion-feel、switch-interaction 等旅程覆盖。
   */
  await seedFrontendPreferences(page, value => { value.appearance.animations = "none"; });
  await page.setViewportSize({ width: 1400, height: 1000 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  const openControl = async () => {
    await page.getByRole("navigation", { name: "页面切换" }).getByRole("button", { name: "控制面", exact: true }).click();
    await card.waitFor();
  };
  const card = page.locator(".control-object").filter({ hasText: "Fan A" });
  await openControl();
  await card.getByRole("radio", { name: "软件管理", exact: true }).click();
  // 没设定时数字框显示的就是实际读数 40：只画滑轨刻度，旁边不再写第二遍。
  await card.locator('.control-editor-actual-mark[aria-label*="40"]').first().waitFor();
  assert.equal(await card.locator(".control-capability-actual").filter({ hasText: "40" }).count(), 0,
    "An unset number item must not show its actual value twice");
  await card.getByRole("checkbox", { name: "固定转速", exact: true }).check();

  const slider = card.getByRole("slider", { name: "固定转速", exact: true });
  const box = card.getByRole("spinbutton", { name: "固定转速", exact: true });
  const apply = card.getByRole("button", { name: "应用", exact: true });
  await slider.waitFor();
  await slider.scrollIntoViewIfNeeded();

  /** 每个绘制周期检查期望值，3 s 硬超时，并保留步骤诊断。 */
  const waitFor = async (step, read, expected) => {
    try {
      await waitForCondition(page, async () => await read() === expected, { timeoutMs: 3000, description: step });
    } catch {
      throw new Error(`${step}: ${await read()}, expected ${expected}`);
    }
  };
  const thumb = () => slider.inputValue();
  const shown = () => box.inputValue();
  // 有没改写下去的期望状态：应用按钮可用（放弃按钮在选过管理方式后一直可用，看不出来）。
  const pendingWrite = () => apply.isEnabled();
  // 读屏读到的值：可访问性树里这个滑条的 value（原生 range 按 input.value 给出 aria-valuenow）。
  const cdp = await page.context().newCDPSession(page);
  const announced = async () => {
    const { nodes } = await cdp.send("Accessibility.getFullAXTree");
    const node = nodes.find(item => item.role?.value === "slider" && item.name?.value === "固定转速" && !item.ignored);
    return node?.value?.value === undefined ? null : String(node.value.value);
  };
  const applyAndSettle = async (expected, step) => {
    const before = writes.length;
    await apply.click();
    await waitFor(`${step}: Apply`, async () => writes.length, before + 1);
    assert.equal(writes.at(-1).settings[0].number, expected, `${step}: Apply writes ${expected}`);
    await waitFor(`${step}: nothing pending after Apply`, pendingWrite, false);
  };
  /*
   * 每帧记下数字框和应用按钮：拖动中如果写了期望状态，应用按钮在那一帧就会变成可用。
   * 记录器跨帧运行，读的时候取出并清空。
   */
  await page.evaluate(() => {
    const log = [];
    window.__sliderFrames = log;
    const tick = () => {
      const card = [...document.querySelectorAll(".control-object")].find(element => element.textContent.includes("Fan A"));
      const button = [...(card?.querySelectorAll(".control-object-actions button") ?? [])].find(element => element.innerText.trim() === "应用");
      if (button) log.push({ pending: !button.disabled, box: card.querySelector("input[type=number]")?.value });
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  });
  const takeFrames = () => page.evaluate(() => window.__sliderFrames.splice(0));

  await waitFor("Initial value", thumb, "40");
  await applyAndSettle(40, "Baseline");

  const track = await slider.boundingBox();
  const y = track.y + track.height / 2;
  /** 值为 v 时滑块圆心的横坐标：两端各让出半个原生滑块（8 px）。 */
  const xOf = (v) => track.x + 8 + (v / 100) * (track.width - 16);
  const moveTo = async (fromX, toX) => {
    for (let i = 1; i <= 8; i += 1) await page.mouse.move(fromX + (toX - fromX) * i / 8, y);
  };
  const settleRendering = async () => {
    // Always cross the input/DOM commit before accepting an unchanged value.
    await waitForNextPaint(page);
    await waitForStableRendering(page, [".control-object input[type=range]", ".control-object"]);
  };

  // 单击和换算都和原生 range 相同：在同一处换上一个原样的原生 range，从同一个值出发，真实鼠标依次单击同一串位置，
  // 记下它每次的值；再在这里单击同一串位置比。有点在滑块上的（原生 range 不改值），也有按在相邻两档分界 ±1.5 px 处的
  // （看得出半个滑块的偏差）。
  const clicks = [xOf(40) + 5, xOf(10), xOf(10) - 6, xOf(37.5) - 1.5, xOf(37.5) + 1.5, xOf(62.5) - 1.5, xOf(62.5) + 1.5,
    track.x + track.width - 1, xOf(85) + 4, track.x + 1];
  const native = await swapInNative(await slider.elementHandle());
  const parity = [];
  for (const x of clicks) {
    await page.mouse.move(x, y);
    await page.mouse.down();
    await page.mouse.up();
    parity.push({ x: Number(x.toFixed(1)), native: await page.evaluate(element => element.value, native) });
  }
  await swapBack(native);
  await page.mouse.move(track.x + track.width / 2, y + 200);
  await settleRendering();
  for (const entry of parity) {
    await page.mouse.move(entry.x, y);
    await page.mouse.down();
    await page.mouse.up();
    await settleRendering();
    await waitFor(`Clicking at ${entry.x}: thumb`, thumb, entry.native);
    await waitFor(`Clicking at ${entry.x}: number box`, shown, entry.native);
    entry.ours = await thumb();
    assert.equal(entry.ours, entry.native, `Clicking at ${entry.x} like the native range: ${JSON.stringify(parity)}`);
    assert.equal(await shown(), entry.native, `Clicking at ${entry.x} writes the value`);
  }
  await applyAndSettle(Number(parity.at(-1).native), "Clicks");

  // 拖动：按下不改值；离开拖动框起滑块和数字框跟着指针，期望状态不变；松开写一次。
  const before = await thumb();
  takeFrames();
  await page.mouse.move(xOf(20), y);
  await page.mouse.down();
  // Keep the full 400 ms: pressing alone must not change either value, and the
  // per-frame recorder needs this observation window before the drag (>10 frames).
  await page.waitForTimeout(400);
  assert.equal(await thumb(), before, "Pressing alone does not move the thumb");
  assert.equal(await shown(), before, "Pressing alone does not change the number box");
  for (const target of [35, 50, 65]) {
    await moveTo(xOf(target - 15), xOf(target));
    await waitFor(`Thumb follows to ${target}`, thumb, String(target));
    await waitFor(`Number box follows to ${target}`, shown, String(target));
  }
  assert.equal(await announced(), "65", "The slider announces the value under the pointer");
  const dragging = await takeFrames();
  assert.ok(dragging.length > 10 && dragging.every(frame => !frame.pending), "Dragging never writes the desired state");
  await page.mouse.up();
  await waitFor("Release writes the value", pendingWrite, true);
  await settleRendering();
  assert.equal(await shown(), "65");
  assert.equal(await thumb(), "65");
  await applyAndSettle(65, "Drag");
  // 按在滑块上偏右 5 px 拖动：保持指针相对圆心的偏移，和原生 range 一样不跳。
  // 圆心若跳到指针下，第一次小移动就会到 70，拖到 80 + 5 px 处会是 85（量程 0–100 时一档约 5 px）。
  await page.mouse.move(xOf(65) + 5, y);
  await page.mouse.down();
  // 离开拖动框（半宽 2 px）但不到半档：抓住时还在 65，跳到指针下（偏右 7.5 px）就是 70。
  await page.mouse.move(xOf(65) + 7.5, y);
  // This is a committed pointer movement at the same quantized value, so cross
  // its paint and wait for the input/number rendering rather than the old value alone.
  await settleRendering();
  assert.equal(await thumb(), "65", "Grabbing the thumb off-centre does not jump it under the pointer");
  await moveTo(xOf(65) + 7.5, xOf(80) + 5);
  await waitFor("Grabbed thumb keeps the offset", thumb, "80");
  await page.mouse.up();
  await waitFor("Release after grabbing", shown, "80");
  await applyAndSettle(80, "Grab");
  // 按在滑块上不动就松开：不改值，不写。
  await page.mouse.move(xOf(80) - 6, y);
  await page.mouse.down();
  await page.mouse.up();
  // Keep 400 ms to observe that an on-thumb click never queues a desired write.
  await page.waitForTimeout(400);
  assert.equal(await thumb(), "80");
  assert.equal(await pendingWrite(), false, "Clicking the thumb writes nothing");
  await box.fill("65");
  await box.press("Enter");
  await applyAndSettle(65, "Back to 65");
  // 之后只记取消的两次拖动。
  await takeFrames();

  // Esc 取消：什么都不写，回到拖动前的值。
  await page.mouse.move(xOf(65), y);
  await page.mouse.down();
  await moveTo(xOf(65), xOf(30));
  await waitFor("Before Escape", thumb, "30");
  await page.keyboard.press("Escape");
  await waitFor("Thumb after Escape", thumb, "65");
  await waitFor("Number box after Escape", shown, "65");
  await moveTo(xOf(30), xOf(10));
  await page.mouse.up();
  // Keep 400 ms after the remaining move/up events: Escape must stop all later
  // pointer changes and desired writes, including the recorder's intervening frames.
  await page.waitForTimeout(400);
  assert.equal(await thumb(), "65", "After Escape the pointer no longer moves the thumb");
  assert.equal(await pendingWrite(), false, "Escape writes nothing");

  // 右键取消：同上。
  await page.mouse.move(xOf(65), y);
  await page.mouse.down();
  await moveTo(xOf(65), xOf(90));
  await waitFor("Before the right button", thumb, "90");
  await page.mouse.down({ button: "right" });
  await page.mouse.up({ button: "right" });
  await waitFor("Thumb after the right button", thumb, "65");
  await page.mouse.up();
  // Keep the complete 400 ms after right-button cancellation to observe no
  // delayed desired write; takeFrames below still checks every sampled frame.
  await page.waitForTimeout(400);
  assert.equal(await shown(), "65");
  assert.equal(await pendingWrite(), false, "The right button writes nothing");
  assert.ok((await takeFrames()).every(frame => !frame.pending), "Cancelled drags never wrote the desired state");

  // 键盘从 10 起。
  await page.mouse.move(xOf(10), y);
  await page.mouse.down();
  await page.mouse.up();
  await waitFor("Click writes the value", shown, "10");
  await applyAndSettle(10, "Click");

  // 键盘照原生 range：一步 5，一大步是一步和量程十分之一里大的（10），一步一写。
  for (const [key, expected] of [["ArrowRight", "15"], ["ArrowUp", "20"], ["ArrowLeft", "15"], ["ArrowDown", "10"],
    ["PageUp", "20"], ["PageDown", "10"], ["End", "100"], ["Home", "0"]]) {
    await slider.press(key);
    await waitFor(`${key} number box`, shown, expected);
    await waitFor(`${key} thumb`, thumb, expected);
  }
  await applyAndSettle(0, "Keyboard");
  // 在端点上再按 Home 不写。
  await slider.press("Home");
  // Keep 400 ms to observe that Home at the existing endpoint never queues a write.
  await page.waitForTimeout(400);
  assert.equal(await pendingWrite(), false, "Home at the start writes nothing");

  // 外观：四种界面风格、可用和置灰，滑条和原样的原生 range 逐字节相同。
  await box.fill("65");
  await box.press("Enter");
  await applyAndSettle(65, "Screenshot value");
  const looks = [];
  for (const uiStyle of ["standard", "soft", "outline", "terminal"]) {
    await writeFrontendPreferences(page, value => { value.appearance.uiStyle = uiStyle; });
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.waitForFunction(style => document.documentElement.dataset.uiStyle === style, uiStyle);
    await openControl();
    await slider.waitFor();
    await slider.scrollIntoViewIfNeeded();
    await waitFor(`${uiStyle} value`, thumb, "65");
    // 截图前等实际读数到：它在滑条上画一条实际值刻度（40 处）。重新加载后读数异步到达，
    // 没等它时，滑条和原生 range 两张截图之间刻度才出现，两张就差在这一条上（不是外观不同）。
    await card.locator(".control-capability-actual").filter({ hasText: "40" }).waitFor();
    await slider.locator("xpath=..").locator(".control-editor-actual-mark").waitFor();
    for (const state of ["enabled", "disabled"]) {
      const target = state === "enabled" ? slider : card.getByRole("slider", { name: "fan.duty-limit", exact: true });
      if (state === "disabled") assert.equal(await target.isDisabled(), true, "The unchecked capability's slider is greyed out");
      await page.mouse.move(0, 0);
      await page.evaluate(() => document.activeElement instanceof HTMLElement && document.activeElement.blur());
      // 截滑条所在的滑轨格（含实际值刻度）；换成原生 range 时滑条隐藏，所以先拿住元素。
      const editor = await target.evaluateHandle(element => element.parentElement);
      const path = join(shots, `slider-${uiStyle}-${state}.png`);
      const ours = await editor.screenshot({ path });
      const swapped = await swapInNative(await target.elementHandle());
      const original = await editor.screenshot({ path: join(shots, `slider-${uiStyle}-${state}-native.png`) });
      await swapBack(swapped);
      /*
       * 逐像素比，只容浏览器光栅化本身的抖动：Edge 把滑轨圆角的抗锯齿边缘画成相邻两级灰度之一，
       * 同一个原生 range、什么都没变，连截两次也会在圆角上差 1 级（batch-3 和本地 --jobs 2 下都见过，
       * 4 个像素 213 / 214、37 / 38）。所以“一样”是：尺寸相同，每个像素每个通道差不超过 1，这样的像素不超过 8 个。
       * 外观真的不同（颜色、尺寸、滑块、刻度）时差的是整片像素、几十级，照样报出来。
       */
      const diff = await pixelDiff(page, ours, original);
      assert.ok(diff.sameSize && diff.maxDelta <= 1 && diff.pixels <= 8,
        `${uiStyle} ${state}: the slider looks exactly like the native range (${path}): ${JSON.stringify(diff)}`);
      looks.push({ path, ...diff });
    }
  }
  return { parity, looks, nativeThumbPress: "on-thumb clicks keep the value in both" };
}

/** 两张 PNG 截图逐像素比：尺寸是否相同、有几个像素不同、任一通道最大差几级。在页面里用 canvas 解码。 */
async function pixelDiff(page, a, b) {
  return page.evaluate(async ([first, second]) => {
    const load = async (data) => {
      const image = new Image();
      image.src = `data:image/png;base64,${data}`;
      await image.decode();
      return image;
    };
    const read = (image) => {
      const canvas = document.createElement("canvas");
      canvas.width = image.width;
      canvas.height = image.height;
      const context = canvas.getContext("2d");
      context.drawImage(image, 0, 0);
      return context.getImageData(0, 0, canvas.width, canvas.height).data;
    };
    const [x, y] = await Promise.all([load(first), load(second)]);
    if (x.width !== y.width || x.height !== y.height) return { sameSize: false, pixels: -1, maxDelta: -1 };
    const p = read(x);
    const q = read(y);
    let pixels = 0;
    let maxDelta = 0;
    for (let i = 0; i < p.length; i += 4) {
      let delta = 0;
      for (let k = 0; k < 4; k += 1) delta = Math.max(delta, Math.abs(p[i + k] - q[i + k]));
      if (delta > 0) {
        pixels += 1;
        maxDelta = Math.max(maxDelta, delta);
      }
    }
    return { sameSize: true, pixels, maxDelta };
  }, [a.toString("base64"), b.toString("base64")]);
}

/**
 * 把滑条换成一个原样的原生 range（同样的属性、同样的值，不挂交互协议），放在同一处；返回换上去的元素。
 * 这就是迁移前的样子：同一份 control.css、同一个浏览器画法。
 */
async function swapInNative(slider) {
  return slider.evaluateHandle(element => {
    const plain = element.cloneNode(true);
    plain.value = element.value;
    element.style.display = "none";
    element.after(plain);
    return plain;
  });
}

/** 换回滑条：去掉原生 range，滑条照原样显示（它就在原生 range 前面）。 */
async function swapBack(plain) {
  await plain.evaluate(element => {
    element.previousElementSibling.style.removeProperty("display");
    element.remove();
  });
}

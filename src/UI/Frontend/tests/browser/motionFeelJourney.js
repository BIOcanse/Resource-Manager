import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { loadSettingsText } from "../../src/i18n/settingsLoader.ts";

/**
 * 手感（docs/motion_feel_2026-10-25.md）：设置 → 外观 → 手感，简体中文，真实点击和保存。
 * 换手感后根元素的 --motion-medium 跟着变（180 → 260 → 120），开关轨道的过渡和弹窗的入场时长读同一个令牌；
 * 磁石全局层一起换：开关滑块从切换到停在另一端的用时跟回点时间走。
 * 动画档位选“无动画”时照样不动（时长 0、滑块直接到端），手感仍记在根元素上；刷新后手感保持。
 */
export default async function motionFeelJourney(page, baseUrl) {
  const failures = [];
  page.on("pageerror", (error) => failures.push(`pageerror: ${String(error)}`));
  const app = await loadAppCopy("zh-CN");
  const settings = await loadSettingsText("zh-CN");
  const label = (id) => settings.motionFeelOptions.find((option) => option.id === id).label;
  const animationLabel = (id) => settings.animationOptions.find((option) => option.id === id).label;

  const openSection = async (section) => {
    await page.getByRole("button", { name: settings.sections[section], exact: true }).click();
  };
  const save = async () => {
    await page.getByRole("button", { name: settings.actions.save, exact: true }).click();
  };
  const choose = async (group, option) => {
    await page.getByRole("radiogroup", { name: group, exact: true }).getByRole("radio", { name: option, exact: true }).click();
  };

  /** 根元素的令牌、开关轨道的过渡、弹窗的入场动画（.modal 的样式规则，用一个探针元素读）。 */
  const readDurations = () => page.evaluate(() => {
    const track = document.querySelector(".settings-switch .settings-switch-track");
    const probe = document.createElement("div");
    probe.className = "modal";
    probe.style.position = "fixed";
    probe.style.visibility = "hidden";
    document.body.append(probe);
    const modal = getComputedStyle(probe).animationDuration;
    probe.remove();
    // 构建后的样式表把 180ms 压成 .18s：一律换成毫秒数比较。
    const ms = (value) => value.split(",").map((part) => {
      const text = part.trim();
      return text.endsWith("ms") ? Number.parseFloat(text) : Math.round(Number.parseFloat(text) * 1000);
    });
    return {
      feel: document.documentElement.dataset.motionFeel,
      medium: ms(getComputedStyle(document.documentElement).getPropertyValue("--motion-medium"))[0],
      track: track ? ms(getComputedStyle(track).transitionDuration) : null,
      modal: ms(modal)[0],
      animations: document.body.dataset.animations
    };
  });

  /** 在“更新”分区按空格切换“自动更新”，量从按下到滑块停在另一端（±0.5 px）的毫秒数；再切回来，草稿不变。 */
  const knobTravel = async () => {
    await openSection("updates");
    const box = page.getByRole("checkbox", { name: settings.updates.autoUpdateTitle, exact: true });
    await box.waitFor();
    const knob = box.locator("xpath=..").locator(".settings-switch-knob");
    const travels = [];
    for (let round = 0; round < 2; round += 1) {
      const before = await box.isChecked();
      await box.focus();
      // 先装好监听和逐帧读取，再按键：按键时刻由页面里的 keydown 记下。
      await knob.evaluate((element, target) => {
        const x = () => { const value = getComputedStyle(element).translate; return value === "none" ? 0 : Number.parseFloat(value); };
        const probe = { pressed: null, travel: undefined };
        window.__motionFeelProbe = probe;
        window.addEventListener("keydown", () => { probe.pressed = performance.now(); }, { once: true, capture: true });
        const deadline = performance.now() + 3000;
        const tick = (now) => {
          if (probe.pressed !== null && Math.abs(x() - target) <= 0.5) { probe.travel = now - probe.pressed; return; }
          if (now > deadline) { probe.travel = null; return; }
          requestAnimationFrame(tick);
        };
        requestAnimationFrame(tick);
      }, before ? 0 : 20);
      await page.keyboard.press("Space");
      await page.waitForFunction(() => window.__motionFeelProbe.travel !== undefined, undefined, { timeout: 5000 });
      travels.push(await page.evaluate(() => window.__motionFeelProbe.travel));
      if (await box.isChecked() === before) throw new Error("Space did not toggle the switch");
      await page.waitForTimeout(400);
    }
    await openSection("appearance");
    return travels;
  };

  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
  await openSection("appearance");
  const feelGroup = page.getByRole("radiogroup", { name: settings.appearance.motionFeelTitle, exact: true });
  await feelGroup.waitFor();
  // 这一行在“动画”下面，说明文字可见。
  await page.getByText(settings.appearance.motionFeelDescription, { exact: true }).waitFor();
  const rows = await page.locator(".settings-row strong").allTextContents();
  const animationRow = rows.indexOf(settings.appearance.animationTitle);
  if (animationRow < 0 || rows[animationRow + 1] !== settings.appearance.motionFeelTitle) {
    throw new Error(`The motion feel row is not right under the animation row: ${JSON.stringify(rows)}`);
  }
  const checked = (await feelGroup.getByRole("radio", { checked: true }).textContent())?.trim();
  if (checked !== label("standard")) throw new Error(`Default motion feel is ${checked}`);

  // 正常动画下逐个换手感：令牌、轨道过渡、弹窗入场、滑块用时都跟着变。
  await choose(settings.appearance.animationTitle, animationLabel("normal"));
  await save();
  const results = [];
  for (const [feel, ms] of [["standard", 180], ["smooth", 260], ["crisp", 120]]) {
    if (feel !== "standard") {
      await choose(settings.appearance.motionFeelTitle, label(feel));
      await save();
    }
    await page.waitForFunction((id) => document.documentElement.dataset.motionFeel === id, feel, { timeout: 5000 });
    await openSection("updates");
    await page.getByRole("checkbox", { name: settings.updates.autoUpdateTitle, exact: true }).waitFor();
    const durations = await readDurations();
    if (durations.animations !== "normal" || durations.medium !== ms || durations.modal !== ms
      || JSON.stringify(durations.track) !== JSON.stringify([ms, ms])) {
      throw new Error(`${feel}: durations do not follow the feel: ${JSON.stringify(durations)}`);
    }
    const travels = await knobTravel();
    for (const travel of travels) {
      if (travel === null || travel < ms * 0.75 || travel > ms + 90) {
        throw new Error(`${feel}: knob travel ${travel} ms does not follow the Lodestone spring time ${ms} ms (${JSON.stringify(travels)})`);
      }
    }
    results.push({ feel, ...durations, travels });
  }
  const mean = (feel) => { const { travels } = results.find((entry) => entry.feel === feel); return (travels[0] + travels[1]) / 2; };
  if (!(mean("crisp") < mean("standard") && mean("standard") < mean("smooth"))) {
    throw new Error(`Knob travel is not ordered crisp < standard < smooth: ${JSON.stringify(results)}`);
  }

  // 无动画：手感仍是利落，但什么都不动。
  await choose(settings.appearance.animationTitle, animationLabel("none"));
  await save();
  await page.waitForFunction(() => document.body.dataset.animations === "none", undefined, { timeout: 5000 });
  await openSection("updates");
  await page.getByRole("checkbox", { name: settings.updates.autoUpdateTitle, exact: true }).waitFor();
  const still = await readDurations();
  if (still.feel !== "crisp" || still.medium !== 120 || still.modal !== 0 || !still.track?.every((value) => value === 0)) {
    throw new Error(`No motion still animates or lost the feel: ${JSON.stringify(still)}`);
  }
  const instant = await knobTravel();
  if (instant.some((travel) => travel === null || travel > 50)) {
    throw new Error(`No motion: the knob still travels: ${JSON.stringify(instant)}`);
  }

  // 刷新后手感保持（前端偏好缓存），动画档位也照旧。
  await choose(settings.appearance.animationTitle, animationLabel("normal"));
  await save();
  await page.waitForFunction(() => document.body.dataset.animations === "normal", undefined, { timeout: 5000 });
  await page.reload({ waitUntil: "domcontentloaded" });
  await page.getByRole("navigation").getByRole("button", { name: app.page.settings, exact: true }).click();
  await openSection("appearance");
  await page.waitForFunction(() => document.documentElement.dataset.motionFeel === "crisp", undefined, { timeout: 5000 });
  const reloadedChoice = (await feelGroup.getByRole("radio", { checked: true }).textContent())?.trim();
  if (reloadedChoice !== label("crisp")) throw new Error(`After reload the motion feel shows ${reloadedChoice}`);
  const reloaded = await readDurations();
  if (reloaded.medium !== 120) throw new Error(`After reload --motion-medium is ${reloaded.medium}`);
  const reloadedTravel = await knobTravel();
  if (reloadedTravel.some((travel) => travel === null || travel < 90 || travel > 210)) {
    throw new Error(`After reload the Lodestone layer is not crisp: ${JSON.stringify(reloadedTravel)}`);
  }
  const cached = await page.evaluate(() => JSON.parse(localStorage.getItem("resource-manager:frontend-preferences:v1") ?? "null")?.appearance?.motionFeel);
  if (cached !== "crisp") throw new Error(`Frontend preference cache holds ${cached}`);

  if (failures.length) throw new Error(failures.join("\n"));
  return { results, noMotion: { ...still, travels: instant }, reloaded: { ...reloaded, travels: reloadedTravel } };
}

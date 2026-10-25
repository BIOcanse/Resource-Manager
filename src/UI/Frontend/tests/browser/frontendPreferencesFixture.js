import { pointPreferences } from "../../src/preferences/frontendPreferences.ts";

/*
 * 独立浏览器模式下的前端呈现偏好（docs/frontend_preferences_2026-10-24.md）。
 * 外观、语言、动效和刷新选择只存在前端自己的 localStorage 缓存里，后端设置不再携带，
 * mock 的 /__test/scenario 也就改不了它们。旅程要从某个偏好起步时，按用户在设置页保存后的结果
 * 写同一份缓存，再 reload 让前端读取。
 */
export const frontendPreferencesKey = "resource-manager:frontend-preferences:v1";

/** 当前缓存；没有缓存时为 null，界面使用本次构建的点值。 */
export async function readFrontendPreferences(page) {
  return page.evaluate(key => JSON.parse(localStorage.getItem(key) ?? "null"), frontendPreferencesKey);
}

/**
 * 从已有缓存（没有则从点值）出发，change 就地修改后整份写回。
 * 页面须已在前端源上（先 goto）；写入后调用方自己 reload。
 */
export async function writeFrontendPreferences(page, change) {
  const value = (await readFrontendPreferences(page)) ?? pointPreferences();
  change(value);
  await page.evaluate(([key, text]) => localStorage.setItem(key, text), [frontendPreferencesKey, JSON.stringify(value)]);
  return value;
}

/**
 * 首次加载前就要生效的偏好（例如要测首次加载本身的订阅）：在页面脚本之前写入缓存，
 * 仅当缓存还不存在时写，之后用户在设置页的保存、reload 都不会被覆盖。
 * target 是 Playwright 的 page 或 browser context。
 * 注意：界面上“重置”删掉缓存后，下一次加载会再写入这份起始偏好。
 */
export async function seedFrontendPreferences(target, change) {
  const value = pointPreferences();
  change(value);
  await target.addInitScript(([key, text]) => {
    // 上下文级脚本也在新页的初始 about:blank 上运行，那里没有可用的 localStorage（读取即抛 SecurityError）。
    if (location.origin === "null") return;
    if (localStorage.getItem(key) === null) localStorage.setItem(key, text);
  }, [frontendPreferencesKey, JSON.stringify(value)]);
  return value;
}

/** 刷新选择的自定义值：{mode:"custom"} 让前端按 customValue 提交观察需求。 */
export function customRefresh(customValue) {
  return { mode: "custom", preset: "responsive", customValue };
}

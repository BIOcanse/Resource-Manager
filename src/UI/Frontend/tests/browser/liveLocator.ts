/**
 * 退场幽灵（[data-exit-ghost]，docs/animation_style_2026-10-26.md「总原则」）只给眼睛看：它是 inert、aria-hidden、不可命中的视觉副本，
 * 保留类名好按原样子画。按角色找天然排除它；按类名或文字找时用这里排除幽灵和它里面的节点。
 */
import type { Page, Locator } from "./journeyTypes.ts";
export const NOT_GHOST = ":not([data-exit-ghost]):not([data-exit-ghost] *)";

/** 按文字找（精确匹配），排除幽灵里的副本。 */
export const liveText = (scope: Page | Locator, text: string | RegExp) => scope.getByText(text, { exact: true }).and("page" in scope ? scope.page().locator(NOT_GHOST) : scope.locator(NOT_GHOST));

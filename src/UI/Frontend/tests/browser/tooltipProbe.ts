import type { Locator } from "playwright-core";
/*
 * 读我们自己的提示（代替原生 title 之后各旅程共用）：
 * - helpHintText：问号说明（src/ui/primitives/HelpHint.tsx）。先把指针移到窗口左上角（指针停在问号或说明上时说明本来就不关），
 *   把焦点放到问号上打开说明，读问号 aria-describedby 指向的说明弹层，再把焦点移走让它关上。返回说明的文字（多段按换行连起来）。
 * - fullContent：完整内容提示和名称提示的文字来源（src/ui/primitives/TooltipLayer.tsx 读的属性）。
 */
export async function helpHintText(button: Locator) {
  await button.waitFor();
  await button.page().mouse.move(0, 0);
  await button.focus();
  const id = await button.getAttribute("aria-describedby");
  if (!id) throw new Error("the question mark has no aria-describedby");
  const popover = button.page().locator(`[id="${id}"]:not([data-exit-ghost])`);
  await popover.waitFor({ timeout: 2000 });
  const text = (await popover.innerText()).trim();
  await button.evaluate((element) => element.blur());
  await popover.waitFor({ state: "detached", timeout: 2000 });
  return text;
}

export const FULL_CONTENT_ATTRIBUTE = "data-full-content";

export function fullContent(locator: Locator) {
  return locator.getAttribute(FULL_CONTENT_ATTRIBUTE);
}

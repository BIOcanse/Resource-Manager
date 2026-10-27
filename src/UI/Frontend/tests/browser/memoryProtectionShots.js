import { mkdirSync } from "node:fs";
import { join } from "node:path";
import { waitForContentSettled } from "./contentSettled.js";

// 主机内存保护两处界面的页面截图（只截浏览器页面，不截桌面）。设了 RM_MEMORY_PROTECTION_SCREENSHOT_DIR 才截，
// 不设时旅程照常检查、不写文件。tall：临时把视口拉高到能放下整个设置分区，截完恢复原视口。
export async function memoryProtectionShot(page, name, { tall = false } = {}) {
  const directory = process.env.RM_MEMORY_PROTECTION_SCREENSHOT_DIR;
  if (!directory) return;
  mkdirSync(directory, { recursive: true });
  const viewport = page.viewportSize();
  if (tall && viewport) {
    // 页面本身不滚动，滚动的是分区或弹窗里的某个容器：找到截图目标上方实际可滚动的那一层，按它的溢出量加高视口。
    const height = await page.evaluate(() => {
      let element = document.querySelector(".memory-protection-settings, .software-memory-protection-group");
      let overflow = 0;
      while (element) {
        const style = getComputedStyle(element);
        if (/(auto|scroll)/.test(style.overflowY)) overflow += Math.max(0, element.scrollHeight - element.clientHeight);
        element = element.parentElement;
      }
      return Math.ceil(window.innerHeight + overflow);
    });
    await page.setViewportSize({ width: viewport.width, height: Math.min(Math.max(height, viewport.height), 6000) });
  }
  try {
    await waitForContentSettled(page);
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    await page.screenshot({ path: join(directory, `${name}.png`) });
  } finally {
    if (tall && viewport) await page.setViewportSize(viewport);
  }
}

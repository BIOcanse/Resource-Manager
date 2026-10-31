import type { Page } from "playwright-core";
import { loadAppCopy } from "../../src/i18n/copy/appCopyLoader.ts";
import { writeFrontendPreferences } from "./frontendPreferencesFixture.ts";

/*
 * 清单条目右侧的类别标签说明接口或设备是哪一类，必须完整可读（协调者 10-26 走查：英文里“On-device p…”“Expansion p…”被截断，
 * 德语更长）。英语、德语，1100 和 1440 两种宽度下：
 * - 清单里现有的每个标签都不截断（scrollWidth ≤ clientWidth），也不超出条目；
 * - 再把该语言里所有类别标签依次放进第一个条目，分别在顶层和三层嵌套（每层缩进 14 px）下量一遍，同样不截断、不超出；
 * - 标签放不下时换到标题下面，从标题的左边开始，而不是挤窄标题后面或被裁掉。
 * 结束时恢复界面语言为跟随系统。
 */
export async function checkBadgesNotTruncated(page: Page) {
  const results: { language: string; width: number; current: number; injected: number; wrapped: string[] }[] = [];
  for (const language of ["en-US", "de-DE"] as const) {
    const copy = await loadAppCopy(language);
    await writeFrontendPreferences(page, (value) => { value.appearance.language = language; });
    await page.reload({ waitUntil: "domcontentloaded" });
    const details = page.getByRole("navigation", { name: copy.shell.pageNav })
      .getByRole("button", { name: copy.page.details, exact: true });
    await details.waitFor({ state: "visible", timeout: 10_000 });
    if (await details.getAttribute("aria-current") !== "page") await details.click();
    const list = page.locator(".device-topology-panel .device-port-list");
    await list.locator(".device-port-item").first().waitFor({ state: "visible", timeout: 10_000 });
    const candidates = [...new Set([
      copy.deviceTree.hostInterface, copy.deviceTree.downstreamInterface, copy.deviceTree.attachedDevice,
      copy.deviceTree.internalInterface, copy.deviceTree.dockBadge, copy.deviceTree.monitorBadge,
      copy.deviceAdapters.usbAudioDevice, copy.deviceAdapters.externalStorageDevice, copy.deviceAdapters.bluetoothHandsFreeAudio,
      copy.deviceAdapters.bluetoothStereoAudio, copy.deviceAdapters.usbCompositeDevice, copy.deviceAdapters.internalCommunicationPort,
      copy.deviceAdapters.internalPanelBadge, copy.deviceAdapters.powerInputBadge, copy.deviceAdapters.videoFunctionBadge,
      copy.deviceAdapters.controllerBadge, "USB Hub"
    ])];
    for (const width of [1100, 1440]) {
      await page.setViewportSize({ width, height: 900 });
      const measured = await list.evaluate((element, texts) => {
        const measure = (item: HTMLElement) => {
          const badge = item.querySelector<HTMLElement>(".device-port-protocol")!;
          const title = item.querySelector<HTMLElement>(".device-port-main strong")!;
          const b = badge.getBoundingClientRect();
          const t = title.getBoundingClientRect();
          const box = item.getBoundingClientRect();
          return {
            text: badge.textContent?.trim() ?? "",
            truncated: badge.scrollWidth > badge.clientWidth + 1,
            outside: b.left < box.left - 0.5 || b.right > box.right + 0.5 || item.scrollWidth > item.clientWidth + 1,
            wrapped: b.top >= t.bottom - 0.5,
            wrappedFromTitleStart: Math.abs(b.left - t.left) <= 1
          };
        };
        const current = [...element.querySelectorAll<HTMLElement>(".device-port-item")].map(measure);
        const item = element.querySelector<HTMLElement>(".device-port-item")!;
        const badge = item.querySelector<HTMLElement>(".device-port-protocol")!;
        const saved = { text: badge.textContent!, style: item.getAttribute("style"), nested: item.classList.contains("nested") };
        const injected: (ReturnType<typeof measure> & { depth: number })[] = [];
        for (const depth of [0, 3]) {
          item.style.setProperty("--device-tree-depth", String(depth));
          item.classList.toggle("nested", depth > 0);
          for (const text of texts) {
            badge.textContent = text;
            injected.push({ depth, ...measure(item) });
          }
        }
        badge.textContent = saved.text;
        if (saved.style === null) item.removeAttribute("style"); else item.setAttribute("style", saved.style);
        item.classList.toggle("nested", saved.nested);
        return { current, injected };
      }, candidates);
      const failures = [...measured.current, ...measured.injected].filter((entry) =>
        entry.truncated || entry.outside || (entry.wrapped && !entry.wrappedFromTitleStart));
      if (measured.current.length === 0 || failures.length > 0) {
        throw new Error(`${language} ${width}px: list badges are truncated or misplaced: ${JSON.stringify(failures)}`);
      }
      results.push({
        language, width,
        current: measured.current.length,
        injected: measured.injected.length,
        wrapped: measured.injected.filter((entry) => entry.wrapped).map((entry) => `${entry.depth}:${entry.text}`)
      });
    }
  }
  await writeFrontendPreferences(page, (value) => { value.appearance.language = "system"; });
  return results;
}

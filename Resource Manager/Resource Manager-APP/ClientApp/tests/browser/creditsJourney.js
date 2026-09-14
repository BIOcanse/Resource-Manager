import { readFileSync } from "node:fs";

export default async function creditsJourney(page, baseUrl) {
  const errors = [];
  page.on("pageerror", (error) => errors.push(String(error)));
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" });
  await page.getByRole("button", { name: "设置", exact: true }).click();
  await page.getByRole("button", { name: "鸣谢", exact: true }).or(
    page.getByRole("button", { name: "致谢", exact: true })).click();
  const panel = page.locator(".settings-credits-panel");
  await panel.getByText("许可与鸣谢", { exact: true }).waitFor({ timeout: 7000 });
  const result = await panel.evaluate((element) => {
    const groups = Array.from(element.querySelectorAll(".settings-credit-group"), (group) => ({
      title: group.querySelector("h3")?.textContent,
      names: Array.from(group.querySelectorAll(".settings-credit-title strong"), (name) => name.textContent)
    }));
    return {
      groups,
      text: element.textContent,
      links: Array.from(element.querySelectorAll("a"), (link) => ({
        href: link.href, target: link.target, rel: link.rel
      }))
    };
  });
  const assert = (condition, message) => { if (!condition) throw new Error(message); };
  await panel.locator(".settings-credit-dependencies summary").click();
  const dependencies = panel.locator(".settings-credit-dependencies li");
  const lock = JSON.parse(readFileSync(new URL('../../package-lock.json', import.meta.url), 'utf8'));
  const managed = JSON.parse(readFileSync(new URL('../../src/i18n/managedDependencyAcknowledgements.json', import.meta.url), 'utf8'));
  const expected = Object.entries(lock.packages).filter(([path]) => path !== '').map(([path, item]) => ({
    name: path.slice(path.lastIndexOf('node_modules/') + 'node_modules/'.length), version: item.version
  })).concat(managed);
  assert(await dependencies.count() === expected.length, "The exact locked frontend and managed closure must be acknowledged");
  const rendered = await dependencies.allTextContents();
  assert(expected.every(item => rendered.some(text => text.includes(item.name) && text.includes(item.version))), "Every name/version must render");
  assert(await dependencies.locator('a').count() === expected.length, "Every dependency must have its real package link");
  assert(!result.text.includes("Kings"), "Personal introduction must not be shown");
  assert(await panel.getByRole('link', { name: /star|点星|點星/i }).count() === 0, "No Star buttons");
  assert((await panel.locator('a').allTextContents()).every(text => !text.includes('https://')), "Show short buttons, not raw URLs");
  assert((await dependencies.allTextContents()).some((line) => line.includes("tslib") && line.includes("0BSD")),
    "Permissive terms are not a reason to omit acknowledgement");
  assert((await dependencies.allTextContents()).some((line) => line.includes("Newtonsoft.Json")),
    "Transitive managed test dependencies are missing");
  assert(result.groups.length === 6, "Credits did not render all six groups");
  const runtime = result.groups.find((group) => group.title === "界面与运行组件");
  assert(runtime?.names.includes("Lucide / Feather"), "Lucide attribution is absent");
  assert(runtime?.names.includes("Microsoft WebView2"), "WebView2 attribution is absent");
  assert(result.groups.find((group) => group.title === "构建工具")?.names.includes("Zig"),
    "Build tools were not separated from runtime components");
  assert(result.text.includes("本项目采用 Apache-2.0") && !result.text.includes("PolyForm Noncommercial"),
    "The localized project license must be Apache-2.0");
  assert(result.links.every((link) => link.href.startsWith("https://")
    && link.target === "_blank" && link.rel.includes("noopener") && link.rel.includes("noreferrer")),
  "Credits contain unsafe or broken link attributes");
  const layouts = [];
  for (const width of [1280, 760]) {
    await page.setViewportSize({ width, height: 800 });
    const layout = await panel.evaluate((element) => ({
      width: element.clientWidth,
      scrollWidth: element.scrollWidth,
      overflowingItems: Array.from(element.querySelectorAll(".settings-credit-item"))
        .filter((item) => item.scrollWidth > item.clientWidth + 1).length
    }));
    assert(layout.scrollWidth <= layout.width + 1 && layout.overflowingItems === 0,
      `Credits overflow at ${width}px: ${JSON.stringify(layout)}`);
    layouts.push({ viewportWidth: width, ...layout });
  }
  assert(errors.length === 0, `Page errors: ${errors.join("; ")}`);
  return { groupCount: result.groups.length, linkCount: result.links.length, layouts, pageErrors: errors };
}

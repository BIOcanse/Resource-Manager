// 不重复当前所在的页（用户 10-25「不要重复一遍当前所在的分页」；docs/navigation_tree_2026-10-25.md“页名”）。
// - 顶栏的拖动区只有产品名和开机时长，不写当前页名：页签已经高亮着。
// - 页名的唯一来源是分页树：页标题（读屏、焦点落点）、窗口标题读它；页面里和选中的分页同名的区块标题用 CurrentPageHeading，只给读屏。
// 浏览器里看得见的结果由旅程 page-motion 查（顶栏文字里没有当前页名，页面上没有和选中分页同名的可见标题）。
import assert from "node:assert/strict";
import { readFileSync, readdirSync, statSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath } from "node:url";

const src = fileURLToPath(new URL("../src/", import.meta.url));
const read = (path: string) => readFileSync(join(src, path), "utf8");

const shell = read("app/AppShell.tsx");
const dragStart = shell.indexOf('class="window-drag-region"');
assert.ok(dragStart > 0, "AppShell has the drag region");
const dragEnd = shell.indexOf('<div class="shell-utility-actions">', dragStart);
const dragRegion = shell.slice(dragStart, dragEnd);
assert.match(dragRegion, /uiText\.shell\.productName/, "the drag region keeps the product name");
assert.match(dragRegion, /props\.systemUptime\(\)/, "the drag region keeps the uptime");
for (const forbidden of [/pageTitle/, /tree\.name/, /\.label\(\)/, /uiText\.page\./, /shellPageName/]) {
  assert.doesNotMatch(dragRegion, forbidden, `the top bar must not repeat the current page name (${forbidden})`);
}

// 页名只有一个来源。
const app = read("App.tsx");
assert.match(app, /const pageTitle = createMemo\(\(\) => navigation\.page\(activePage\(\)\)\.label\(\)\);/, "the page title comes from the navigation tree");
assert.match(shell, /<CurrentPageNameProvider name=\{\(\) => tree\.name\(props\.activePage\(\)\)\}>/, "pages read the current name from the tree");

/** src 下的全部源文件，显式栈遍历，带节点上限。 */
function sources(): string[] {
  const files: string[] = [];
  const stack = [src];
  let visited = 0;
  while (stack.length > 0) {
    const directory = stack.pop()!;
    visited += 1;
    assert.ok(visited < 5000, "source tree walk exceeded 5000 directories");
    for (const name of readdirSync(directory)) {
      const path = join(directory, name);
      if (statSync(path).isDirectory()) stack.push(path);
      else if (/\.(tsx?|css)$/.test(name)) files.push(path);
    }
  }
  return files;
}
for (const file of sources()) {
  assert.doesNotMatch(readFileSync(file, "utf8"), /shellPageName/, `${file} still refers to the removed top-bar page name`);
}

// 和选中的二级分页同名的区块标题：用树里的名字，不显示。
const sameNameSections = [
  "features/management/components/ManagementPage.tsx",
  "features/browserRuntimes/BrowserRuntimePage.tsx",
  "features/gpuScheduling/GpuSchedulingModel.tsx",
  "features/details/CpuTopologyDiagram.tsx",
  "features/details/HostManagerSmartCoordinatorDetailsReport.tsx",
  "features/targetedReport/TargetedReportsPage.tsx"
];
for (const path of sameNameSections) {
  const text = read(path);
  assert.match(text, /<CurrentPageHeading \/>/, `${path} titles its section with the tree name`);
  assert.doesNotMatch(text, /<h2>\{(activeKind\(\)\.label|uiText\.(browserRuntime\.title|gpuScheduling\.panel|cpuTopology\.panel|smartReport\.panel|targetedReport\.title))\}<\/h2>/,
    `${path} must not show its own copy of the tab name`);
}
const heading = read("app/navigation/currentPageName.tsx");
assert.match(heading, /<h2 id=\{props\.id\} class="sr-only">\{name\(\)\}<\/h2>/, "the same-name heading is for screen readers only");

console.log("shellPageNameContract: the top bar and the pages do not repeat the current page name");

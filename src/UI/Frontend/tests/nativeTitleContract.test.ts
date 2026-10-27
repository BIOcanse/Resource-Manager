// 原生 title 的静态合同：界面上的说明、名称和完整内容都用我们自己的提示（ui/primitives/HelpHint.tsx 的问号说明，
// ui/primitives/TooltipLayer.tsx 的名称提示和完整内容提示），不再让浏览器弹原生 title。
// 1. 源码里原生元素（小写开头的 JSX 标签）上没有 title 属性，没有例外；单选项（RadioGroupItem）不再透传 title。
//    代码里也不用 setAttribute 写 title。
// 2. 资源表的格子（每行每格）只用属性声明完整内容提示（data-full-content），由应用里唯一的 TooltipLayer 读：
//    表格里不挂提示组件、不挂提示监听，挂载预算不因提示变多。
// 组件的 title 属性（DialogHeader、UserDetailsDialog、DetailSection、ContentState……）画成看得见的标题，不是原生提示，不算。
import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const srcRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../src");

const files: string[] = [];
const stack = [srcRoot];
let visited = 0;
while (stack.length > 0) {
  assert.ok((visited += 1) < 10_000, "the source tree walk stays bounded");
  const directory = stack.pop()!;
  for (const name of readdirSync(directory)) {
    const path = join(directory, name);
    if (statSync(path).isDirectory()) stack.push(path);
    else if (/\.(ts|tsx)$/.test(name)) files.push(path);
  }
}

/** title= 所在的 JSX 标签名：往回找同一层（花括号配平）的第一个「<字母」。 */
function tagOf(text: string, position: number): string | null {
  let depth = 0;
  for (let index = position - 1; index >= 0; index -= 1) {
    const character = text[index];
    if (character === "}") depth += 1;
    else if (character === "{") depth -= 1;
    else if (character === "<" && depth === 0 && /[A-Za-z]/.test(text[index + 1] ?? "")) {
      return /^[A-Za-z][\w.]*/.exec(text.slice(index + 1))?.[0] ?? null;
    }
  }
  return null;
}

const found: string[] = [];
for (const path of files) {
  const file = relative(srcRoot, path).replaceAll("\\", "/");
  const text = readFileSync(path, "utf8").replace(/\/\*[\s\S]*?\*\//g, "").replace(/^\s*\/\/.*$/gm, "");
  assert.doesNotMatch(text, /setAttribute\(\s*["']title["']/, `${file} writes a native title with setAttribute`);
  for (const match of text.matchAll(/(?<![\w-])title=[{"]/g)) {
    const tag = tagOf(text, match.index!);
    if (tag !== null && (/^[a-z]/.test(tag) || tag === "RadioGroupItem")) found.push(`${file}: <${tag} title=…>`);
  }
}
assert.deepEqual(found, [], "native title tooltips: use HelpHint, data-name-tooltip or data-full-content");
const radioGroup = readFileSync(join(srcRoot, "ui/primitives/RadioGroup.tsx"), "utf8");
assert.doesNotMatch(radioGroup, /\btitle\??:/, "RadioGroupItem has no title prop to pass through");

const table = readFileSync(join(srcRoot, "features/resourceTable/ResourceTable.tsx"), "utf8");
assert.doesNotMatch(table, /<(TooltipLayer|AnchoredLayer)\b/, "the resource table mounts no tooltip layer of its own");
assert.doesNotMatch(table, /addEventListener\(\s*["'](pointerover|pointerout|pointerenter|pointerleave|mouseover|mouseout|mouseenter|mouseleave)["']/,
  "the resource table adds no hover listeners for tooltips");
for (const cell of ["resource-row-name", "status-cell", "text-cell", "value-cell"]) {
  assert.match(table, new RegExp(`class="[^"]*\\b${cell}\\b[^"]*"[^>]*?data-full-content=`, "s"),
    `the ${cell} declares its full content as a data attribute for the shared TooltipLayer`);
}

// 共用的提示层只有一个，挂在应用根上；名称提示、完整内容提示、问号说明和资源条提示都放在同一个跟随锚点的弹层上。
const app = readFileSync(join(srcRoot, "App.tsx"), "utf8");
assert.equal((app.match(/<TooltipLayer \/>/g) ?? []).length, 1, "the app mounts exactly one TooltipLayer");
for (const owner of ["ui/primitives/TooltipLayer.tsx", "ui/primitives/HelpHint.tsx", "features/resourceBreakdown/ResourceTooltipOverlay.tsx"]) {
  assert.match(readFileSync(join(srcRoot, owner), "utf8"), /<AnchoredLayer\b/, `${owner} uses the shared AnchoredLayer`);
}
// 提示层和问号说明不靠计时器：没有延迟显示，也没有自动隐藏；问号说明的离开只按几何判断（pointerGrace.ts）。
for (const owner of ["ui/primitives/TooltipLayer.tsx", "ui/primitives/HelpHint.tsx", "ui/primitives/AnchoredLayer.tsx"]) {
  assert.doesNotMatch(readFileSync(join(srcRoot, owner), "utf8"), /setTimeout|setInterval/, `${owner} has no timers`);
}

console.log(`nativeTitleContract: ok (${files.length} files, no native titles)`);

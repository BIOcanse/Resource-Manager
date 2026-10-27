// 原生 title 的静态合同：界面上的说明、名称和完整内容都用我们自己的提示（ui/primitives/HelpHint.tsx 的问号说明，
// ui/primitives/TooltipLayer.tsx 的名称提示和完整内容提示），不再让浏览器弹原生 title。
// 1. 资源表以外的源码里，原生元素（小写开头的 JSX 标签）上没有 title 属性；唯一的例外是单选项的 title 透传
//    （ui/primitives/RadioGroup.tsx），它只剩资源表在用。代码里也不用 setAttribute 写 title。
// 2. 资源表（features/resourceTable/）的挂载另有人在改，它剩下的原生 title 留到以后：数目钉在这里，只能往下减；
//    改掉几处就把这里的数字跟着改小，减到 0 时连同 RadioGroup 的透传一起删掉。
// 组件的 title 属性（DialogHeader、UserDetailsDialog、DetailSection、ContentState……）画成看得见的标题，不是原生提示，不算。
import assert from "node:assert/strict";
import { readdirSync, readFileSync, statSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const srcRoot = resolve(dirname(fileURLToPath(import.meta.url)), "../src");
const RESOURCE_TABLE = "features/resourceTable/";
/** 资源表里还剩的原生 title：原生元素上的，和经单选项透传的。 */
const RESOURCE_TABLE_REMAINING = { intrinsic: 8, radioPassthrough: 1 };

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

const outside: string[] = [];
const remaining = { intrinsic: 0, radioPassthrough: 0 };
for (const path of files) {
  const file = relative(srcRoot, path).replaceAll("\\", "/");
  const text = readFileSync(path, "utf8").replace(/\/\*[\s\S]*?\*\//g, "").replace(/^\s*\/\/.*$/gm, "");
  assert.doesNotMatch(text, /setAttribute\(\s*["']title["']/, `${file} writes a native title with setAttribute`);
  for (const match of text.matchAll(/(?<![\w-])title=[{"]/g)) {
    const tag = tagOf(text, match.index!);
    const intrinsic = tag !== null && /^[a-z]/.test(tag);
    if (file.startsWith(RESOURCE_TABLE)) {
      if (intrinsic) remaining.intrinsic += 1;
      if (tag === "RadioGroupItem") remaining.radioPassthrough += 1;
      continue;
    }
    if (tag === "RadioGroupItem") outside.push(`${file}: <RadioGroupItem title=…> (the passthrough is left for the resource table only)`);
    else if (intrinsic && file !== "ui/primitives/RadioGroup.tsx") outside.push(`${file}: <${tag} title=…>`);
  }
}
assert.deepEqual(outside, [], "native title tooltips outside the resource table: use HelpHint, data-name-tooltip or data-full-content");
assert.deepEqual(remaining, RESOURCE_TABLE_REMAINING,
  "the resource table's remaining native titles changed: lower RESOURCE_TABLE_REMAINING when they are replaced");

// 共用的提示层只有一个，挂在应用根上；名称提示、完整内容提示、问号说明和资源条提示都放在同一个跟随锚点的弹层上。
const app = readFileSync(join(srcRoot, "App.tsx"), "utf8");
assert.equal((app.match(/<TooltipLayer \/>/g) ?? []).length, 1, "the app mounts exactly one TooltipLayer");
for (const owner of ["ui/primitives/TooltipLayer.tsx", "ui/primitives/HelpHint.tsx", "features/resourceBreakdown/ResourceTooltipOverlay.tsx"]) {
  assert.match(readFileSync(join(srcRoot, owner), "utf8"), /<AnchoredLayer\b/, `${owner} uses the shared AnchoredLayer`);
}
// 提示层不靠计时器：没有延迟显示，也没有自动隐藏。
const layer = readFileSync(join(srcRoot, "ui/primitives/TooltipLayer.tsx"), "utf8");
assert.doesNotMatch(layer, /setTimeout|setInterval/, "the tooltip layer has no timers");

console.log(`nativeTitleContract: ok (${files.length} files; resource table keeps ${remaining.intrinsic} + ${remaining.radioPassthrough})`);

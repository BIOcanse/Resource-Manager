import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

// 消息码的唯一定义点在后端；这里保证前端的渲染表与它逐条对齐，
// 少一条会显示兜底文案，多一条说明后端已经删掉了它。
const backendCodes = readSource("../../Domain/Messages/BackendMessageCodes.cs");
const frontendRenderer = readSource("../src/presentation/backendMessage.ts");
const zhCopy = readSource("../src/i18n/copy/zh/backendMessages.ts");
const enCopy = readSource("../src/i18n/copy/en/backendMessages.ts");

const domains = collectDomains(backendCodes);
assert.ok(domains.size > 0, "The backend must define at least one message domain.");

for (const [domain, codes] of domains) {
  const rendererBlock = extractRendererBlock(frontendRenderer, domain);
  assert.ok(
    rendererBlock,
    `The frontend has no renderer table for the ${domain} domain.`);
  for (const [name, value] of codes) {
    assert.match(
      rendererBlock,
      new RegExp(`(^|\\n)\\s*${value}:\\s`),
      `Code ${value} (${domain}.${name}) has no renderer in the frontend.`);
  }
  const renderedCodes = [...rendererBlock.matchAll(/(^|\n)\s*(\d+):\s/g)]
    .map((match) => Number(match[2]));
  for (const rendered of renderedCodes) {
    assert.ok(
      [...codes.values()].includes(rendered),
      `The frontend renders code ${rendered} in ${domain}, but the backend does not define it.`);
  }
}

// 两个基底语言包必须提供同一组键，否则某种语言会掉回兜底。
assert.deepEqual(copyKeys(zhCopy), copyKeys(enCopy));

function readSource(relativePath: string) {
  return readFileSync(new URL(relativePath, import.meta.url), "utf8");
}

function collectDomains(source: string) {
  const domains = new Map<string, Map<string, number>>();
  const classPattern = /public static class (\w+)\s*\{([\s\S]*?)\n    \}/g;
  for (const match of source.matchAll(classPattern)) {
    const [, name, body] = match;
    if (name === "BackendMessageDomains") {
      continue;
    }
    const codes = new Map<string, number>();
    for (const entry of body.matchAll(/public const byte (\w+) = (\d+);/g)) {
      codes.set(entry[1], Number(entry[2]));
    }
    if (codes.size > 0) {
      domains.set(name.charAt(0).toLowerCase() + name.slice(1), codes);
    }
  }
  return domains;
}

function extractRendererBlock(source: string, domain: string) {
  const pattern = new RegExp(`function ${domain}Renderers\\(\\)[\\s\\S]*?\\n\\}`, "m");
  return source.match(pattern)?.[0] ?? null;
}

function copyKeys(source: string) {
  return [...source.matchAll(/^\s{4,}(\w+):/gm)].map((match) => match[1]).sort();
}

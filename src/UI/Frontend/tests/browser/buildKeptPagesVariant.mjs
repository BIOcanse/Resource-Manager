import { cpSync, existsSync, mkdirSync, readFileSync, symlinkSync, writeFileSync } from "node:fs";
import { resolve } from "node:path";
import { spawnSync } from "node:child_process";
import { fileURLToPath } from "node:url";

/*
 * 构建一份所有可选页都常驻的前端，给常驻页旅程测非默认的选项（自由点 refresh-kept_pages 是构建时的值）。
 * 用法：node tests/browser/buildKeptPagesVariant.mjs <新目录>
 * 在 <新目录>\frontend 放一份前端源码（node_modules 指向本工作树的），只改生成的 KeptPages 值为 preferences.points.json
 * 里这一点的全部 options，构建到 <新目录>\dist。之后用 RM_FRONTEND_STATIC_ROOT=<新目录>\dist、
 * RM_KEPT_PAGES_EXPECTED=<逗号分隔的同一份页面> 跑 kept-page 旅程。先跑过生成器（frontendPoints.values.ts 已生成）。
 * 目录必须是新的：脚本不删除任何东西。
 */
const projectRoot = fileURLToPath(new URL("../..", import.meta.url));
const target = process.argv[2];
if (!target) throw new Error("usage: buildKeptPagesVariant.mjs <new directory>");
const root = resolve(target);
if (existsSync(root)) throw new Error(`${root} already exists; give a new directory`);

const point = JSON.parse(readFileSync(resolve(projectRoot, "preferences.points.json"), "utf8")).points
  .find(item => item.name === "refresh-kept_pages");
if (!point) throw new Error("preferences.points.json has no refresh-kept_pages");
const pages = point.options;

const frontend = resolve(root, "frontend");
mkdirSync(frontend, { recursive: true });
for (const entry of ["src", "build", "index.html", "vite.config.ts", "tsconfig.json", "package.json", "package-lock.json", "preferences.points.json"]) {
  cpSync(resolve(projectRoot, entry), resolve(frontend, entry), { recursive: true });
}
symlinkSync(resolve(projectRoot, "node_modules"), resolve(frontend, "node_modules"), "junction");

const valuesPath = resolve(frontend, "src/preferences/generated/frontendPoints.values.ts");
const values = readFileSync(valuesPath, "utf8");
const pattern = /(export const KeptPages: [^=]+= __fpFreezeDeep\()\[[^\]]*\](\);)/;
if (!pattern.test(values)) throw new Error("the generated values have no KeptPages line; run the generator first");
writeFileSync(valuesPath, values.replace(pattern, `$1${JSON.stringify(pages)}$2`));

const vite = resolve(projectRoot, "node_modules/vite/bin/vite.js");
const build = spawnSync(process.execPath, [vite, "build", "--outDir", resolve(root, "dist"), "--emptyOutDir"], { cwd: frontend, stdio: "inherit" });
if (build.status !== 0) throw new Error(`vite build failed: ${build.status}`);
console.log(`RM_KEPT_PAGES_EXPECTED=${pages.join(",")}`);

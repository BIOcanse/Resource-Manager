import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";

const localeRoot = new URL("../src/i18n/settingsLocales/", import.meta.url);
const localeSources = readdirSync(localeRoot)
  .filter((name) => name.endsWith(".ts"))
  .map((name) => readFileSync(new URL(name, localeRoot), "utf8"));
const settingsFactory = readSource("../src/i18n/settingsLocaleFactory.ts");
const languageCopy = [settingsFactory, ...localeSources]
  .flatMap((source) => source.split(/\r?\n/))
  .filter((line) => /language(?:Title|Description|SelectLabel)/.test(line))
  .join("\n");

assert.match(settingsFactory, /languageTitle: "界面语言"/);
assert.match(settingsFactory, /languageTitle: "Interface language"/);
assert.doesNotMatch(languageCopy, /设置与致谢|設定與致謝|Settings and Credits/i);
assert.doesNotMatch(languageCopy, /页面|頁面|page/i);
assert.doesNotMatch(
  settingsFactory,
  /页面请求|頁面要求|页面轮询|頁面輪詢|账本|抓取|点击保存后|點擊儲存後|写入草稿|寫入草稿|written to the draft|after you save/i);

const directCopySources = [
  "../src/App.tsx",
  "../src/app/AppShell.tsx",
  "../src/app/useSoftwareActions.ts",
  "../src/features/browserRuntimes/BrowserRuntimePage.tsx",
  "../src/components/CpuTopologyDiagram.tsx",
  "../src/features/management/components/ManagementPage.tsx",
  "../src/features/monitor/MetricModal.tsx",
  "../src/features/management/components/MigrationPanel.tsx",
  "../src/components/SoftwareDetailModal.tsx",
  "../src/stores/managementStore.ts",
  "../src/stores/migrationStore.ts",
  "../src/utils.ts"
].map(readSource).join("\n");

assert.doesNotMatch(
  directCopySources,
  /组件\s*\/\s*软件管理|浏览器候选|可用候选|资源管理器受管目录|受管根目录|扫描候选|迁移候选|数据候选|文件级追踪|Windows 壳|线程活动采样中|精确 GPU 调度|运行时调度方式/);

function readSource(relativePath: string) {
  return readFileSync(new URL(relativePath, import.meta.url), "utf8");
}

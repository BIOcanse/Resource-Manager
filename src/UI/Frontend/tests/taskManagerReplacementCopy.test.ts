import assert from "node:assert/strict";
import { languageOptions } from "../src/i18n/settingsLanguages.ts";
import { loadSettingsText } from "../src/i18n/settingsLoader.ts";

/*
 * 任务管理器快捷键替换的说明（设置 → 系统集成）。原生壳开启后同时做两件事：
 * 运行期间接管 Ctrl+Shift+Esc；以管理员身份为 taskmgr.exe 登记 IFEO Debugger，登记期间任何方式启动任务管理器
 * （任务栏菜单、Ctrl+Alt+Del 界面里的“任务管理器”、直接运行 taskmgr.exe）都打开资源管理器；Ctrl+Alt+Del 界面本身不拦截；
 * 关闭时只删除自己写入的值（src/UI/Core/SystemIntegration/TaskManagerLaunchReplacementRegistry.cs）。
 * 每种开放语言的说明都要写到这些：提到 taskmgr.exe 和 Ctrl+Alt+Del，不再写“不接管 Ctrl+Alt+Del”。
 */
const ctrlAltDelete: Record<string, string> = {
  "es-ES": "Ctrl+Alt+Supr",
  "fr-FR": "Ctrl+Alt+Suppr"
};
const staleClaims = [/不接管 Ctrl\+Alt\+Del/, /不接管/, /is not changed\.$/, /Ctrl\+Alt\+Del bleibt unverändert/, /Ctrl\+Alt\+Supr no cambia\.$/,
  /n’est pas modifié\.$/, /Ctrl\+Alt\+Del は変更しません/, /Ctrl\+Alt\+Del은 변경하지 않습니다/, /Ctrl\+Alt\+Del не изменяется\.$/];
const checked: string[] = [];
for (const option of languageOptions.filter((option) => option.id !== "system")) {
  const text = (await loadSettingsText(option.id)).systemIntegration.taskManagerDescription;
  const key = ctrlAltDelete[option.id] ?? "Ctrl+Alt+Del";
  assert.ok(text.includes("taskmgr.exe"), `${option.id}: the description does not say that running taskmgr.exe is replaced`);
  assert.ok(text.split(key).length - 1 >= 2, `${option.id}: the description must name both Task Manager on the ${key} screen and the screen itself`);
  for (const claim of staleClaims) assert.doesNotMatch(text, claim, `${option.id}: the description still claims ${key} is not involved`);
  checked.push(option.id);
}
assert.equal(checked.length, 9);
console.log(`task manager replacement copy: ${checked.join(", ")}`);

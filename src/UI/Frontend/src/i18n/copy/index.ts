import type { ConcreteAppLanguageMode } from "../settingsTypes.ts";
import { enAppCopy } from "./en/index.ts";
import { zhAppCopy } from "./zh/index.ts";
import type { AppCopy } from "./zh/index.ts";

export type { AppCopy };

// 中文语言以中文基底合并，其余语言以英文基底合并各自补丁。
// 语言补丁在 S4 接入；补丁缺失时基底本身就是完整包。
export function createAppCopy(language: ConcreteAppLanguageMode): AppCopy {
  return language === "zh-CN" || language === "zh-TW" ? zhAppCopy : enAppCopy;
}

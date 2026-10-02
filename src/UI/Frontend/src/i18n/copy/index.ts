import type { ConcreteAppLanguageMode } from "../settingsTypes.ts";
import { enAppCopy } from "./en/index.ts";
import { zhAppCopy } from "./zh/index.ts";
import type { AppCopy } from "./zh/index.ts";

export type { AppCopy };

// 同步提供初始基底；其他语言（包括繁体中文）由 loadAppCopy 加载独立静态包。
export function createAppCopy(language: ConcreteAppLanguageMode): AppCopy {
  return language === "zh-CN" || language === "zh-TW" ? zhAppCopy : enAppCopy;
}

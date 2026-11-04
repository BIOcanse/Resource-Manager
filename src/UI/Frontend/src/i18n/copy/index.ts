import type { ConcreteAppLanguageMode } from "../settingsTypes.ts";
import { enAppCopy } from "./en/index.ts";
import { zhAppCopy } from "./zh/index.ts";
import type { AppCopy } from "./zh/index.ts";

export type { AppCopy };

// Provide an initial bundle synchronously; load other languages separately.
export function createAppCopy(language: ConcreteAppLanguageMode): AppCopy {
  return language === "zh-CN" || language === "zh-TW" ? zhAppCopy : enAppCopy;
}

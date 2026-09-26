import { createSignal } from "solid-js";
import { createStore, reconcile } from "solid-js/store";
import { createAppCopy } from "./copy/index.ts";
import { loadAppCopy } from "./copy/appCopyLoader.ts";
import type { AppCopy } from "./copy/index.ts";
import { isRightToLeftLanguage, resolveLanguageMode } from "./settingsLanguages.ts";
import { fallbackSettingsText, loadSettingsText } from "./settingsLoader.ts";
import { getHostMessageTransport } from "../host/hostMessageTransport.ts";
import type { ConcreteAppLanguageMode, SettingsTextBundle } from "./settingsTypes.ts";

// 界面语言的唯一运行时所有者：把设置里的语言选择解析为具体语言，
// 载入该语言的完整文案包，并原子替换当前文案。消费端只读取最终字符串。
// store 会就地改写传入的对象，所以每次都放入独立副本，避免基底文案包被写成另一种语言。
const [copy, setCopy] = createStore<AppCopy>(cloneCopy(createAppCopy("zh-CN")));
const [settingsText, setSettingsText] = createSignal<SettingsTextBundle>(fallbackSettingsText);
const [activeLanguage, setActiveLanguage] = createSignal<ConcreteAppLanguageMode>("zh-CN");

let appliedLanguage: ConcreteAppLanguageMode | null = null;
let pendingLanguage: ConcreteAppLanguageMode | null = null;

export const uiText = copy;

export function currentLanguage() {
  return activeLanguage();
}

export function currentSettingsText() {
  return settingsText();
}

export function applyLanguage(language?: string | null) {
  const resolved = resolveLanguageMode(language);
  if (resolved === appliedLanguage) {
    return;
  }

  pendingLanguage = resolved;
  // 语言包未到齐之前保留当前文案，避免先闪一遍基底语言再跳到目标语言。
  void loadAppCopy(resolved).then((nextCopy) => {
    if (pendingLanguage === resolved) {
      applyCopy(resolved, nextCopy);
    }
  });

  void loadSettingsText(resolved).then((nextText) => {
    if (pendingLanguage === resolved) {
      setSettingsText(nextText);
    }
  });
}

function applyCopy(language: ConcreteAppLanguageMode, nextCopy: AppCopy) {
  setCopy(reconcile(cloneCopy(nextCopy)));
  setActiveLanguage(language);
  applyDocumentLanguage(language);
  notifyShellLanguage(language);
  appliedLanguage = language;
}

// 外壳（NativeUi）的托盘、窗口标题和启动画面用同一种语言，这里把已解析的具体语言 id 告诉它。
// 外壳在前端可用之前自己读持久化设置，所以这条消息只负责运行中的切换。
function notifyShellLanguage(language: ConcreteAppLanguageMode) {
  getHostMessageTransport()?.postMessage({ type: "shell.language", language });
}

// 只复制纯对象层级，函数和字符串直接沿用，不需要结构化克隆。
// 文案包是固定深度的纯对象树，这里用显式栈遍历（项目禁止递归），
// 先建好同构的空壳，再逐层把叶子搬过去。
function cloneCopy<T>(value: T): T {
  if (!isPlainContainer(value)) {
    return value;
  }

  const root = createShell(value);
  const pending: Array<[unknown, unknown]> = [[value, root]];
  while (pending.length > 0) {
    const [source, target] = pending.pop() as [
      Record<string, unknown>,
      Record<string, unknown>
    ];
    for (const [key, item] of Object.entries(source)) {
      if (isPlainContainer(item)) {
        const shell = createShell(item);
        target[key] = shell;
        pending.push([item, shell]);
      } else {
        target[key] = item;
      }
    }
  }

  return root as T;
}

function isPlainContainer(value: unknown): value is Record<string, unknown> {
  return Array.isArray(value) || (!!value && typeof value === "object");
}

function createShell(value: Record<string, unknown>): Record<string, unknown> {
  return (Array.isArray(value) ? [] : {}) as Record<string, unknown>;
}

function applyDocumentLanguage(language: ConcreteAppLanguageMode) {
  if (typeof document === "undefined") {
    return;
  }

  document.documentElement.lang = language;
  document.documentElement.dir = isRightToLeftLanguage(language) ? "rtl" : "ltr";
}

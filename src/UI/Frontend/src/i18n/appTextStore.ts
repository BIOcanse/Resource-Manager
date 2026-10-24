import { batch, createSignal } from "solid-js";
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

export const uiText = copy;

export function currentLanguage() {
  return activeLanguage();
}

export function currentSettingsText() {
  return settingsText();
}

interface LanguageBundleLoaders {
  readonly appCopy: (language: ConcreteAppLanguageMode) => Promise<AppCopy>;
  readonly settingsText: (language: ConcreteAppLanguageMode) => Promise<SettingsTextBundle>;
}

export function createLanguageApplier(
  loaders: LanguageBundleLoaders,
  commit: (
    language: ConcreteAppLanguageMode,
    nextCopy: AppCopy,
    nextSettingsText: SettingsTextBundle
  ) => void
) {
  let appliedLanguage: ConcreteAppLanguageMode | null = null;
  let pendingLanguage: ConcreteAppLanguageMode | null = null;

  return (language?: string | null): Promise<void> => {
    const resolved = resolveLanguageMode(language);
    // 切回已生效的语言也要取消其他语言的待完成载入。
    pendingLanguage = resolved;
    if (resolved === appliedLanguage) {
      return Promise.resolve();
    }

    // 两种文案包都准备好后再提交，避免异步顺序留下混合语言。
    return Promise.all([
      loaders.appCopy(resolved),
      loaders.settingsText(resolved)
    ]).then(([nextCopy, nextSettingsText]) => {
      if (pendingLanguage !== resolved) {
        return;
      }
      commit(resolved, nextCopy, nextSettingsText);
      appliedLanguage = resolved;
    });
  };
}

function applyLanguageBundle(
  language: ConcreteAppLanguageMode,
  nextCopy: AppCopy,
  nextSettingsText: SettingsTextBundle
) {
  batch(() => {
    setCopy(reconcile(cloneCopy(nextCopy)));
    setSettingsText(nextSettingsText);
    setActiveLanguage(language);
  });
  applyDocumentLanguage(language);
  notifyShellLanguage(language);
}

const requestLanguage = createLanguageApplier(
  { appCopy: loadAppCopy, settingsText: loadSettingsText },
  applyLanguageBundle
);

export function applyLanguage(language?: string | null): Promise<void> {
  return requestLanguage(language);
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

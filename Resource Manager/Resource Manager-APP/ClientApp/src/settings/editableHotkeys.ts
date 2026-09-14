import type { AppEditableHotkeySettings } from "../types";

export const forceTerminateHotkeyActionId = "force-terminate-unresponsive-and-foreground";

const approvedDestructiveHotkeyModifiers = new Set([17, 18, 91, 92, 162, 163, 164, 165]);
const allHotkeyModifiers = new Set([16, 17, 18, 91, 92, 160, 161, 162, 163, 164, 165]);

export interface EditableHotkeyModel {
  keys: number[];
  relations: Array<0 | 1>;
}

export interface HotkeyKeyOption {
  code: number;
  label: string;
  group: "鼠标" | "字符" | "修饰键" | "功能键" | "导航" | "数字键盘" | "符号" | "系统键" | "媒体键" | "输入法";
}

const letterKeys = Array.from({ length: 26 }, (_, index) => ({
  code: 65 + index,
  label: String.fromCharCode(65 + index),
  group: "字符" as const
}));
const numberKeys = Array.from({ length: 10 }, (_, index) => ({
  code: 48 + index,
  label: String(index),
  group: "字符" as const
}));
const functionKeys = Array.from({ length: 24 }, (_, index) => ({
  code: 112 + index,
  label: `F${index + 1}`,
  group: "功能键" as const
}));
const numpadKeys = Array.from({ length: 10 }, (_, index) => ({
  code: 96 + index,
  label: `Num ${index}`,
  group: "数字键盘" as const
}));

export const hotkeyKeyOptions: HotkeyKeyOption[] = [
  { code: 1, label: "鼠标左键", group: "鼠标" },
  { code: 2, label: "鼠标右键", group: "鼠标" },
  { code: 4, label: "鼠标中键", group: "鼠标" },
  { code: 5, label: "鼠标侧键 1", group: "鼠标" },
  { code: 6, label: "鼠标侧键 2", group: "鼠标" },
  ...letterKeys,
  ...numberKeys,
  { code: 3, label: "Break", group: "系统键" },
  { code: 16, label: "Shift", group: "修饰键" },
  { code: 160, label: "左 Shift", group: "修饰键" },
  { code: 161, label: "右 Shift", group: "修饰键" },
  { code: 17, label: "Ctrl", group: "修饰键" },
  { code: 162, label: "左 Ctrl", group: "修饰键" },
  { code: 163, label: "右 Ctrl", group: "修饰键" },
  { code: 18, label: "Alt", group: "修饰键" },
  { code: 164, label: "左 Alt", group: "修饰键" },
  { code: 165, label: "右 Alt", group: "修饰键" },
  { code: 91, label: "左 Win", group: "修饰键" },
  { code: 92, label: "右 Win", group: "修饰键" },
  { code: 93, label: "菜单键", group: "系统键" },
  { code: 95, label: "Sleep", group: "系统键" },
  ...functionKeys,
  { code: 27, label: "Esc", group: "导航" },
  { code: 9, label: "Tab", group: "导航" },
  { code: 20, label: "Caps Lock", group: "导航" },
  { code: 32, label: "Space", group: "导航" },
  { code: 13, label: "Enter", group: "导航" },
  { code: 8, label: "Backspace", group: "导航" },
  { code: 45, label: "Insert", group: "导航" },
  { code: 46, label: "Delete", group: "导航" },
  { code: 36, label: "Home", group: "导航" },
  { code: 35, label: "End", group: "导航" },
  { code: 33, label: "Page Up", group: "导航" },
  { code: 34, label: "Page Down", group: "导航" },
  { code: 37, label: "左方向键", group: "导航" },
  { code: 38, label: "上方向键", group: "导航" },
  { code: 39, label: "右方向键", group: "导航" },
  { code: 40, label: "下方向键", group: "导航" },
  { code: 19, label: "Pause", group: "导航" },
  { code: 44, label: "Print Screen", group: "导航" },
  { code: 12, label: "Clear", group: "导航" },
  { code: 41, label: "Select", group: "系统键" },
  { code: 42, label: "Print", group: "系统键" },
  { code: 43, label: "Execute", group: "系统键" },
  { code: 47, label: "Help", group: "系统键" },
  { code: 145, label: "Scroll Lock", group: "导航" },
  { code: 144, label: "Num Lock", group: "数字键盘" },
  ...numpadKeys,
  { code: 106, label: "Num *", group: "数字键盘" },
  { code: 107, label: "Num +", group: "数字键盘" },
  { code: 108, label: "Num Separator", group: "数字键盘" },
  { code: 109, label: "Num -", group: "数字键盘" },
  { code: 110, label: "Num .", group: "数字键盘" },
  { code: 111, label: "Num /", group: "数字键盘" },
  { code: 186, label: ";", group: "符号" },
  { code: 187, label: "=", group: "符号" },
  { code: 188, label: ",", group: "符号" },
  { code: 189, label: "-", group: "符号" },
  { code: 190, label: ".", group: "符号" },
  { code: 191, label: "/", group: "符号" },
  { code: 192, label: "`", group: "符号" },
  { code: 219, label: "[", group: "符号" },
  { code: 220, label: "\\", group: "符号" },
  { code: 221, label: "]", group: "符号" },
  { code: 222, label: "'", group: "符号" },
  { code: 223, label: "OEM 8", group: "符号" },
  { code: 226, label: "OEM < >", group: "符号" },
  { code: 166, label: "浏览器后退", group: "媒体键" },
  { code: 167, label: "浏览器前进", group: "媒体键" },
  { code: 168, label: "浏览器刷新", group: "媒体键" },
  { code: 169, label: "浏览器停止", group: "媒体键" },
  { code: 170, label: "浏览器搜索", group: "媒体键" },
  { code: 171, label: "浏览器收藏", group: "媒体键" },
  { code: 172, label: "浏览器主页", group: "媒体键" },
  { code: 173, label: "静音", group: "媒体键" },
  { code: 174, label: "音量减", group: "媒体键" },
  { code: 175, label: "音量加", group: "媒体键" },
  { code: 176, label: "下一曲", group: "媒体键" },
  { code: 177, label: "上一曲", group: "媒体键" },
  { code: 178, label: "停止播放", group: "媒体键" },
  { code: 179, label: "播放 / 暂停", group: "媒体键" },
  { code: 180, label: "邮件", group: "媒体键" },
  { code: 181, label: "媒体选择", group: "媒体键" },
  { code: 182, label: "应用 1", group: "媒体键" },
  { code: 183, label: "应用 2", group: "媒体键" },
  { code: 21, label: "Kana / Hangul", group: "输入法" },
  { code: 22, label: "IME On", group: "输入法" },
  { code: 23, label: "Junja", group: "输入法" },
  { code: 24, label: "Final", group: "输入法" },
  { code: 25, label: "Hanja / Kanji", group: "输入法" },
  { code: 26, label: "IME Off", group: "输入法" },
  { code: 28, label: "IME Convert", group: "输入法" },
  { code: 29, label: "IME NonConvert", group: "输入法" },
  { code: 30, label: "IME Accept", group: "输入法" },
  { code: 31, label: "IME Mode", group: "输入法" },
  { code: 146, label: "Num =", group: "数字键盘" },
  { code: 193, label: "ABNT C1", group: "符号" },
  { code: 194, label: "ABNT C2", group: "符号" },
  { code: 225, label: "OEM AX", group: "输入法" },
  { code: 227, label: "ICO Help", group: "系统键" },
  { code: 228, label: "ICO 00", group: "系统键" },
  { code: 229, label: "IME Process", group: "输入法" },
  { code: 230, label: "ICO Clear", group: "系统键" },
  { code: 231, label: "Packet", group: "输入法" },
  { code: 242, label: "OEM Copy", group: "系统键" },
  { code: 243, label: "OEM Auto", group: "系统键" },
  { code: 244, label: "OEM Enlarge Window", group: "系统键" },
  { code: 245, label: "OEM Back Tab", group: "系统键" },
  { code: 246, label: "Attn", group: "系统键" },
  { code: 247, label: "CrSel", group: "系统键" },
  { code: 248, label: "ExSel", group: "系统键" },
  { code: 249, label: "Erase EOF", group: "系统键" },
  { code: 250, label: "Play", group: "媒体键" },
  { code: 251, label: "Zoom", group: "系统键" },
  { code: 253, label: "PA1", group: "系统键" },
  { code: 254, label: "OEM Clear", group: "系统键" }
];

export function decodeEditableHotkey(encoding?: readonly number[] | null): EditableHotkeyModel {
  const normalized = normalizeHotkeyEncoding(encoding);
  const keys: number[] = [];
  const relations: Array<0 | 1> = [];
  for (let index = 0; index < 4; index += 1) {
    const key = normalized[index * 2];
    if (!key) break;
    keys.push(key);
    if (index > 0) relations.push(normalized[(index * 2) - 1] === 1 ? 1 : 0);
  }
  return { keys, relations };
}

export function encodeEditableHotkey(model: EditableHotkeyModel): number[] {
  const encoding = Array<number>(7).fill(0);
  model.keys.slice(0, 4).forEach((key, index) => {
    encoding[index * 2] = key;
    if (index > 0) encoding[(index * 2) - 1] = model.relations[index - 1] === 1 ? 1 : 0;
  });
  return normalizeHotkeyEncoding(encoding);
}

export function normalizeHotkeyEncoding(encoding?: readonly number[] | null): number[] {
  const source = Array.from(encoding ?? []).slice(0, 7);
  const keys: number[] = [];
  const relations: Array<0 | 1> = [];
  for (let keyIndex = 0; keyIndex < 4; keyIndex += 1) {
    const slot = keyIndex * 2;
    const key = Number(source[slot] ?? 0);
    if (!Number.isInteger(key) || key <= 0 || key > 255 || keys.includes(key)) continue;
    if (keys.length > 0) relations.push(source[slot - 1] === 1 ? 1 : 0);
    keys.push(key);
  }
  return encodeNormalized(keys, relations);
}

export function normalizeEditableHotkeys(hotkeys?: readonly AppEditableHotkeySettings[] | null): AppEditableHotkeySettings[] {
  const normalized = (hotkeys ?? [])
    .filter((hotkey) => typeof hotkey?.actionId === "string" && hotkey.actionId.trim().length > 0)
    .map((hotkey) => {
      const encoding = normalizeHotkeyEncoding(hotkey.encoding);
      const enabled = hotkey.enabled === true
        && encoding[0] !== 0
        && (hotkey.actionId.trim() !== forceTerminateHotkeyActionId
          || isSafeDestructiveHotkeyEncoding(encoding));
      return {
        actionId: hotkey.actionId.trim(),
        enabled,
        encoding
      };
    })
    .filter((hotkey, index, all) => all.findIndex((item) => item.actionId === hotkey.actionId) === index)
    .slice(0, 32);
  if (!normalized.some((hotkey) => hotkey.actionId === forceTerminateHotkeyActionId)) {
    normalized.push(defaultForceTerminateHotkey());
  }
  return normalized;
}

export function isSafeDestructiveHotkeyEncoding(encoding?: readonly number[] | null): boolean {
  const normalized = normalizeHotkeyEncoding(encoding);
  const keys = normalized.filter((value, index) => index % 2 === 0 && value !== 0);
  return keys.length >= 2
    && keys.some((key) => approvedDestructiveHotkeyModifiers.has(key))
    && keys.some((key) => !allHotkeyModifiers.has(key));
}

export function defaultForceTerminateHotkey(): AppEditableHotkeySettings {
  return {
    actionId: forceTerminateHotkeyActionId,
    enabled: false,
    encoding: [0, 0, 0, 0, 0, 0, 0]
  };
}

function encodeNormalized(keys: number[], relations: Array<0 | 1>): number[] {
  const result = Array<number>(7).fill(0);
  keys.slice(0, 4).forEach((key, index) => {
    result[index * 2] = key;
    if (index > 0) result[(index * 2) - 1] = relations[index - 1] ?? 0;
  });
  return result;
}

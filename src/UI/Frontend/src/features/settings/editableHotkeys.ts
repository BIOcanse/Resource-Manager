import { uiText } from "../../text.ts";
import type { AppEditableHotkeySettings } from "../../types";

export const forceTerminateHotkeyActionId = "force-terminate-unresponsive-and-foreground";

const approvedDestructiveHotkeyModifiers = new Set([17, 18, 91, 92, 162, 163, 164, 165]);
const allHotkeyModifiers = new Set([16, 17, 18, 91, 92, 160, 161, 162, 163, 164, 165]);

export interface EditableHotkeyModel {
  keys: number[];
  relations: Array<0 | 1>;
}

export type HotkeyKeyGroup =
  | "mouse"
  | "character"
  | "modifier"
  | "functionKey"
  | "navigation"
  | "numpad"
  | "symbol"
  | "system"
  | "media"
  | "ime";

export interface HotkeyKeyOption {
  code: number;
  label: string;
  group: HotkeyKeyGroup;
}

const letterKeys = Array.from({ length: 26 }, (_, index) => ({
  code: 65 + index,
  label: String.fromCharCode(65 + index),
  group: "character" as const
}));
const numberKeys = Array.from({ length: 10 }, (_, index) => ({
  code: 48 + index,
  label: String(index),
  group: "character" as const
}));
const functionKeys = Array.from({ length: 24 }, (_, index) => ({
  code: 112 + index,
  label: `F${index + 1}`,
  group: "functionKey" as const
}));
const numpadKeys = Array.from({ length: 10 }, (_, index) => ({
  code: 96 + index,
  label: `Num ${index}`,
  group: "numpad" as const
}));

// 标签按当前语言求值，不能在模块顶层固化。
export function hotkeyKeyOptions(): HotkeyKeyOption[] {
  return [
  { code: 1, label: uiText.hotkeyKey.mouseLeft, group: "mouse" },
  { code: 2, label: uiText.hotkeyKey.mouseRight, group: "mouse" },
  { code: 4, label: uiText.hotkeyKey.mouseMiddle, group: "mouse" },
  { code: 5, label: uiText.hotkeyKey.mouseX1, group: "mouse" },
  { code: 6, label: uiText.hotkeyKey.mouseX2, group: "mouse" },
  ...letterKeys,
  ...numberKeys,
  { code: 3, label: "Break", group: "system" },
  { code: 16, label: "Shift", group: "modifier" },
  { code: 160, label: uiText.hotkeyKey.leftShift, group: "modifier" },
  { code: 161, label: uiText.hotkeyKey.rightShift, group: "modifier" },
  { code: 17, label: "Ctrl", group: "modifier" },
  { code: 162, label: uiText.hotkeyKey.leftCtrl, group: "modifier" },
  { code: 163, label: uiText.hotkeyKey.rightCtrl, group: "modifier" },
  { code: 18, label: "Alt", group: "modifier" },
  { code: 164, label: uiText.hotkeyKey.leftAlt, group: "modifier" },
  { code: 165, label: uiText.hotkeyKey.rightAlt, group: "modifier" },
  { code: 91, label: uiText.hotkeyKey.leftWin, group: "modifier" },
  { code: 92, label: uiText.hotkeyKey.rightWin, group: "modifier" },
  { code: 93, label: uiText.hotkeyKey.menu, group: "system" },
  { code: 95, label: "Sleep", group: "system" },
  ...functionKeys,
  { code: 27, label: "Esc", group: "navigation" },
  { code: 9, label: "Tab", group: "navigation" },
  { code: 20, label: "Caps Lock", group: "navigation" },
  { code: 32, label: "Space", group: "navigation" },
  { code: 13, label: "Enter", group: "navigation" },
  { code: 8, label: "Backspace", group: "navigation" },
  { code: 45, label: "Insert", group: "navigation" },
  { code: 46, label: "Delete", group: "navigation" },
  { code: 36, label: "Home", group: "navigation" },
  { code: 35, label: "End", group: "navigation" },
  { code: 33, label: "Page Up", group: "navigation" },
  { code: 34, label: "Page Down", group: "navigation" },
  { code: 37, label: uiText.hotkeyKey.arrowLeft, group: "navigation" },
  { code: 38, label: uiText.hotkeyKey.arrowUp, group: "navigation" },
  { code: 39, label: uiText.hotkeyKey.arrowRight, group: "navigation" },
  { code: 40, label: uiText.hotkeyKey.arrowDown, group: "navigation" },
  { code: 19, label: "Pause", group: "navigation" },
  { code: 44, label: "Print Screen", group: "navigation" },
  { code: 12, label: "Clear", group: "navigation" },
  { code: 41, label: "Select", group: "system" },
  { code: 42, label: "Print", group: "system" },
  { code: 43, label: "Execute", group: "system" },
  { code: 47, label: "Help", group: "system" },
  { code: 145, label: "Scroll Lock", group: "navigation" },
  { code: 144, label: "Num Lock", group: "numpad" },
  ...numpadKeys,
  { code: 106, label: "Num *", group: "numpad" },
  { code: 107, label: "Num +", group: "numpad" },
  { code: 108, label: "Num Separator", group: "numpad" },
  { code: 109, label: "Num -", group: "numpad" },
  { code: 110, label: "Num .", group: "numpad" },
  { code: 111, label: "Num /", group: "numpad" },
  { code: 186, label: ";", group: "symbol" },
  { code: 187, label: "=", group: "symbol" },
  { code: 188, label: ",", group: "symbol" },
  { code: 189, label: "-", group: "symbol" },
  { code: 190, label: ".", group: "symbol" },
  { code: 191, label: "/", group: "symbol" },
  { code: 192, label: "`", group: "symbol" },
  { code: 219, label: "[", group: "symbol" },
  { code: 220, label: "\\", group: "symbol" },
  { code: 221, label: "]", group: "symbol" },
  { code: 222, label: "'", group: "symbol" },
  { code: 223, label: "OEM 8", group: "symbol" },
  { code: 226, label: "OEM < >", group: "symbol" },
  { code: 166, label: uiText.hotkeyKey.browserBack, group: "media" },
  { code: 167, label: uiText.hotkeyKey.browserForward, group: "media" },
  { code: 168, label: uiText.hotkeyKey.browserRefresh, group: "media" },
  { code: 169, label: uiText.hotkeyKey.browserStop, group: "media" },
  { code: 170, label: uiText.hotkeyKey.browserSearch, group: "media" },
  { code: 171, label: uiText.hotkeyKey.browserFavorites, group: "media" },
  { code: 172, label: uiText.hotkeyKey.browserHome, group: "media" },
  { code: 173, label: uiText.hotkeyKey.mute, group: "media" },
  { code: 174, label: uiText.hotkeyKey.volumeDown, group: "media" },
  { code: 175, label: uiText.hotkeyKey.volumeUp, group: "media" },
  { code: 176, label: uiText.hotkeyKey.nextTrack, group: "media" },
  { code: 177, label: uiText.hotkeyKey.previousTrack, group: "media" },
  { code: 178, label: uiText.hotkeyKey.stopPlayback, group: "media" },
  { code: 179, label: uiText.hotkeyKey.playPause, group: "media" },
  { code: 180, label: uiText.hotkeyKey.mail, group: "media" },
  { code: 181, label: uiText.hotkeyKey.mediaSelect, group: "media" },
  { code: 182, label: uiText.hotkeyKey.launchApp1, group: "media" },
  { code: 183, label: uiText.hotkeyKey.launchApp2, group: "media" },
  { code: 21, label: "Kana / Hangul", group: "ime" },
  { code: 22, label: "IME On", group: "ime" },
  { code: 23, label: "Junja", group: "ime" },
  { code: 24, label: "Final", group: "ime" },
  { code: 25, label: "Hanja / Kanji", group: "ime" },
  { code: 26, label: "IME Off", group: "ime" },
  { code: 28, label: "IME Convert", group: "ime" },
  { code: 29, label: "IME NonConvert", group: "ime" },
  { code: 30, label: "IME Accept", group: "ime" },
  { code: 31, label: "IME Mode", group: "ime" },
  { code: 146, label: "Num =", group: "numpad" },
  { code: 193, label: "ABNT C1", group: "symbol" },
  { code: 194, label: "ABNT C2", group: "symbol" },
  { code: 225, label: "OEM AX", group: "ime" },
  { code: 227, label: "ICO Help", group: "system" },
  { code: 228, label: "ICO 00", group: "system" },
  { code: 229, label: "IME Process", group: "ime" },
  { code: 230, label: "ICO Clear", group: "system" },
  { code: 231, label: "Packet", group: "ime" },
  { code: 242, label: "OEM Copy", group: "system" },
  { code: 243, label: "OEM Auto", group: "system" },
  { code: 244, label: "OEM Enlarge Window", group: "system" },
  { code: 245, label: "OEM Back Tab", group: "system" },
  { code: 246, label: "Attn", group: "system" },
  { code: 247, label: "CrSel", group: "system" },
  { code: 248, label: "ExSel", group: "system" },
  { code: 249, label: "Erase EOF", group: "system" },
  { code: 250, label: "Play", group: "media" },
  { code: 251, label: "Zoom", group: "system" },
  { code: 253, label: "PA1", group: "system" },
    { code: 254, label: "OEM Clear", group: "system" }
  ];
}

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

import type { AppCopy } from "../zh/index.ts";

export const enHotkeysCopy: Pick<AppCopy, "hotkeyKey" | "hotkeyGroup" | "hotkeyEditor"> = {
  hotkeyKey: {
    mouseLeft: "Left mouse button",
    mouseRight: "Right mouse button",
    mouseMiddle: "Middle mouse button",
    mouseX1: "Mouse side button 1",
    mouseX2: "Mouse side button 2",
    leftShift: "Left Shift",
    rightShift: "Right Shift",
    leftCtrl: "Left Ctrl",
    rightCtrl: "Right Ctrl",
    leftAlt: "Left Alt",
    rightAlt: "Right Alt",
    leftWin: "Left Win",
    rightWin: "Right Win",
    menu: "Menu key",
    arrowLeft: "Left arrow",
    arrowUp: "Up arrow",
    arrowRight: "Right arrow",
    arrowDown: "Down arrow",
    browserBack: "Browser back",
    browserForward: "Browser forward",
    browserRefresh: "Browser refresh",
    browserStop: "Browser stop",
    browserSearch: "Browser search",
    browserFavorites: "Browser favorites",
    browserHome: "Browser home",
    mute: "Mute",
    volumeDown: "Volume down",
    volumeUp: "Volume up",
    nextTrack: "Next track",
    previousTrack: "Previous track",
    stopPlayback: "Stop playback",
    playPause: "Play / pause",
    mail: "Mail",
    mediaSelect: "Media select",
    launchApp1: "App 1",
    launchApp2: "App 2"
  },
  hotkeyGroup: {
    mouse: "Mouse",
    system: "System keys"
  },
  hotkeyEditor: {
    searchKey: "Search keys",
    destructiveModifierRequired: "The shortcut must include Ctrl, Alt, or Win plus one non-modifier key."
  }
};
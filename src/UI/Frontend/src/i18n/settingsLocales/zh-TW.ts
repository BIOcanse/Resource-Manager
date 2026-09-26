import { createSettingsLocale } from "../settingsLocaleFactory.ts";

export default createSettingsLocale("zh-TW", {
  sections: { performance: "效能", appearance: "外觀", systemIntegration: "系統整合", debug: "偵錯", credits: "致謝" },
  saveState: { saving: "儲存中", saved: "已儲存", error: "儲存失敗" },
  performance: {
    smartMonitoringTitle: "按需監控",
    smartMonitoringDescription: (seconds) => `只在需要顯示資料時保持對應監控；連續 ${seconds} 秒未使用後停止。`,
    pauseHiddenTitle: "視窗隱藏時重新整理",
    pauseHiddenDescription: "普通模式下可以暫停隱藏視窗的介面更新；重新顯示後立即更新。"
  },
  appearance: {
    themeTitle: "色彩模式",
    themeDescription: "選擇跟隨系統、淺色、深色或低對比外觀。",
    animationTitle: "動畫效果",
    animationDescription: "選擇完整動畫、關閉動畫，或進一步簡化視覺以降低介面開銷。",
    languageTitle: "介面語言",
    languageDescription: "選擇 Resource Manager 介面使用的語言。跟隨系統會使用 Windows 顯示語言。",
    languageSelectLabel: "語言",
    themeOptions: { system: "跟隨系統", light: "淺色", dark: "深色", lowContrast: "低對比" },
    animationOptions: {
      auto: { label: "自動調度", description: "前景使用正常動畫，低功耗背景自動切換為超高效能外觀。" },
      none: { label: "無動畫", description: "關閉全部過渡與動畫，追求極致效能" },
      normal: { label: "正常動畫", description: "啟用流暢的全域過渡動畫" },
      ultra: { label: "超高性能", description: "關閉動畫並簡化視覺（去除陰影 / 高光 / 模糊），開銷最低" }
    }
  },
  systemIntegration: {
    taskManagerTitle: "工作管理員快捷鍵替換",
    taskManagerDescription: "接管 Ctrl+Shift+Esc：註冊成功時未執行也會啟動 Resource Manager，已執行時顯示或置頂主視窗；不接管 Ctrl+Alt+Del。",
    forceTerminateTitle: "強制結束快捷鍵",
    forceTerminateDescription: "結束目前前景焦點程式，以及所有具有可見無回應視窗的程式。預設關閉。",
    forceTerminateWarning: "觸發後會立即強制結束處理程序，未儲存的內容將遺失。啟用時必須同時包含 Ctrl、Alt 或 Win 修飾鍵以及一個非修飾鍵。",
    hotkeyEmpty: "尚未設定按鍵",
    hotkeyAddKey: "新增按鍵",
    hotkeyKeyLabel: "按鍵",
    hotkeyRemoveKey: "刪除按鍵",
    hotkeyUnordered: "同時按下",
    hotkeyOrdered: "前鍵先按"
  },
  debug: {
    debugModeTitle: "偵錯模式",
    debugModeDescription: "啟用本機偵錯功能。只在排查問題時開啟。",
    debugLogTitle: "儲存偵錯日誌",
    debugLogDescription: "記錄排查問題所需資訊，可能增加少量磁碟與處理器占用。",
    hostManagerSmartCoordinatorScoreOnlyTitle: "僅分析，不執行最佳化",
    hostManagerSmartCoordinatorScoreOnlyDescription: "繼續監控並產生調度結果，但不實際變更軟體或硬體狀態。",
    hostManagerSmartCoordinatorPerformanceLogTitle: "記錄調度效能",
    hostManagerSmartCoordinatorPerformanceLogDescription: "記錄調度耗時與負載，用於排查 Resource Manager 自身開銷。"
  },
  credits: {
    heroTitle: "授權與鳴謝",
    heroBody: "Resource Manager 使用的開源專案、執行環境、硬體支援和主要貢獻。",
    groups: { project: "專案與貢獻", runtime: "應用程式執行階段與建置工具", windows: "Windows 與診斷介面", hardware: "硬體監控與可選支援" },
    linkLabels: { official: "官網", github: "GitHub", docs: "文件", license: "License", nuget: "NuGet", gpuOpen: "GPUOpen", eula: "EULA", runtime: "Runtime", aspnet: "ASP.NET Core", vite: "Vite", solidPlugin: "Solid 外掛" }
  }
});

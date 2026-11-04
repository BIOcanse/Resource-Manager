import { operationsFeedback } from "../operationsFeedback.ts";
export const managementFeedback = {
  ...operationsFeedback["zh-TW"],
  "resultCode": "結果代碼",
  "removeManual": "移除手動登記",
  "confirmManual": "僅移除此手動登記及分類，不會解除安裝或刪除軟體檔案。繼續？",
  "manualRemoved": "已移除手動登記。",
  "manualRemoveFailed": "移除手動登記失敗。",
  "metadataFailed": "軟體資料載入失敗。",
  "metadataStale": "軟體資料更新失敗，目前內容可能已過時或使用上一種語言。",
  "metadataLoading": "正在載入軟體資料…",
  "retry": "重試",
  "networkFailure": "下載要求失敗，請檢查網路後重試。",
  "fileUnavailable": "所需檔案無法使用。",
  "accessDenied": "存取遭拒，請檢查權限。",
  "requestRejected": "操作要求遭拒，請檢查操作詳細資料。",
  "ioFailure": "讀寫檔案失敗，請檢查路徑及可用空間。",
  "unexpectedFailure": "操作發生非預期錯誤。",
  "noExecutionEvidence": "沒有動作執行證據，可以重試。",
  "outcomeUncertain": "動作可能已經執行，請重新整理並核對後再操作。",
  "managedRootRemoved": "已清理此相依元件的受管安裝目錄。驅動程式及系統全域執行階段不會被移除。",
  "uninstallerLaunchRequested": "已要求啟動 Windows 解除安裝程式。完成後請重新整理軟體清單。"
};

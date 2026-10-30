import { cjkText } from "../../cjkSpacing.ts";
import type { AppCopy } from "../zh/index.ts";

export const zhTwMemoryCpuTierCopy: Pick<AppCopy, "memoryCpuTier"> = {
  memoryCpuTier: {
    intro: "一批在記憶體和 CPU 之間取捨的選項分成五檔：第 1 檔最省記憶體、CPU 壓力最高，第 5 檔最省 CPU、記憶體壓力最高，預設第 3 檔。第 2 到第 4 檔開著對另一方影響不顯著的比例項，只是比例不同；第 1 檔和第 5 檔另外開啟代價顯著的激進項。每個選項都可以單獨設定。",
    tierTitle: "檔位",
    tierDescription: "選定檔位後，下方列出這一檔每個選項的值。",
    tierAriaLabel: "記憶體與 CPU 取捨檔位",
    tierLabel: (tier: number) => cjkText`第${tier}檔`,
    tierLowEnd: "省記憶體",
    tierHighEnd: "省 CPU",
    savedTier: (tier: number) => cjkText`已儲存：第${tier}檔`,
    defaultTier: (tier: number) => cjkText`預設：第${tier}檔`,
    measuredTitle: "實測差別",
    measuredDescription: "每檔大約多用或少用多少記憶體和 CPU。",
    notMeasured: "尚未實測",
    optionsTitle: (tier: number) => cjkText`第${tier}檔的選項`,
    previewNote: "這是尚未儲存的預覽：值依所選檔位和您的單獨設定算出。儲存後顯示後端提供的值。",
    loading: "正在讀取檔位和選項…",
    loadFailed: "無法讀取記憶體與 CPU 取捨的檔位和選項。",
    retry: "重試",
    noOptions: "後端沒有提供任何選項。",
    unknownOption: "未知選項",
    unset: "不設",
    valueLabel: "設定值",
    choiceLabel: "取值",
    categories: { proportional: "比例項", aggressive: "激進項" },
    applyModes: { immediate: "立即生效", restart: "重新啟動後生效" },
    sources: { tier: "來自檔位", user: "您的設定" },
    currentlyRunning: (value: string) => cjkText`目前使用中：${value}`,
    afterRestart: (value: string) => cjkText`重新啟動後：${value}`,
    restartNote: "重新啟動服務後生效；本頁不會重新啟動任何程式。",
    clear: "清除",
    seconds: (value: string) => cjkText`${value}秒`,
    milliseconds: (value: string) => cjkText`${value}毫秒`,
    choices: {
      optimize: "至少最佳化（每次檢查都歸還）",
      autoLean: "自動，偏向少佔記憶體",
      auto: "自動",
      autoRetain: "自動，偏向多留記憶體",
      unrestricted: "不限制（不主動歸還）"
    },
    options: {
      gcGen0MaxBudget: { title: "GC 第 0 代預算上限", description: "執行階段兩次回收之間允許新配置的記憶體上限；越小回收越頻繁、越省記憶體。不設時由執行階段依處理器快取決定。" },
      gcConserveMemory: { title: "GC 壓縮傾向", description: "執行階段為減少記憶體而整理堆積的積極程度，0 到 9，越高越省記憶體、越耗 CPU。" },
      ledgerMemoryModeFloor: { title: "帳本記憶體模式下限", description: "程式本身的記憶體帳本歸還未用記憶體的最低積極程度。" },
      bufferReturn: { title: "緩衝區歸還", description: "處理程序列舉緩衝、計數器剖析區等暫存區用完後多快歸還記憶體。" },
      nativeWorkareaGrowth: { title: "原生工作區成長", description: "原生模組的工作區依實際用量成長並縮回，還是依上限一次保留。" },
      singleRequestStopBufferMs: { title: "單次讀取後的取樣來源保留時間", description: "一次性讀取結束後共用取樣來源繼續保留的時間，緊接著再讀時不必重新啟動；0 表示讀取結束即停止。保留期間不額外取樣。" },
      serviceIdentityCacheMs: { title: "服務快照有效期", description: "服務與處理程序對應關係的快照直接重複使用的時間，0 表示每次重新讀取。" },
      browserRuntimeSnapshotSeconds: { title: "瀏覽器執行階段目錄快照有效期", description: "瀏覽器執行階段目錄的快照直接重複使用的時間，0 表示每次重新讀取。" },
      platformSensorSnapshotSeconds: { title: "平台感測器快照有效期", description: "平台感測器讀數的快照直接重複使用的時間，0 表示每次重新讀取。" },
      storageSnapshotSeconds: { title: "儲存裝置快照有效期", description: "儲存裝置資訊的快照直接重複使用的時間，0 表示每次重新讀取。" },
      rendererAbsenceEntries: { title: "轉譯器缺席快取條數", description: "記住哪些處理程序沒有轉譯器的條目上限；越多重複檢查越少、佔用記憶體越多。" },
      fileQuerySessionsMax: { title: "檔案查詢工作階段上限", description: "同時保留的檔案查詢工作階段最多幾個。" },
      fileQuerySessionsPrebuilt: { title: "預建檔案查詢工作階段", description: "預先建立、隨時可用的檔案查詢工作階段個數，0 表示依需要建立。" }
    }
  }
};

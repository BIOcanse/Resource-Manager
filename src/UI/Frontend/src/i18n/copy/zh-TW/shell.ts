import { zhControlPresentationCopy } from "./controlPresentation.ts";
export const zhShellCopy = {
  common: {
    saving: "儲存中",
    saveFailed: "儲存失敗"
  },
  feedback: {
    confirm: "確認",
    cancel: "取消",
    close: "關閉",
    success: "操作完成",
    error: "操作失敗",
    warning: "需要處理",
    info: "提示"
  },
  page: {
    monitor: "監視控制檯",
    components: "元件與軟體",
    optimization: "效能最佳化",
    details: "詳細資訊",
    settings: "設定",
    diskUsage: "磁碟佔用",
    control: "控制面"
  },
  control: {
    presentation: zhControlPresentationCopy,
    title: "控制面",
    intro: "風扇、顯示卡、處理器的調節都在這一頁。控不了的也列出來，並說明差什麼。",
    loadFailed: "讀不到可控物件。",
    empty: "這臺機器上還沒認出可以調節的物件。",
    ready: "可調節",
    needsComponent: (name: string) => `需要 ${name}`,
    forgetFailed: "禁止刪除",
    instances: {
      title: "識別到過的裝置",
      present: "在場",
      absent: "不在場",
      refresh: "重新檢測",
      forget: "刪除記錄",
      firstSeen: (at: string) => `首次見到 ${at}`,
      open: "例項管理"
    },
    actual: "當前 ",
    presets: {
      save: "存為配置",
      namePlaceholder: "配置名",
      remove: "刪除配置"
    },
    overclock: {
      title: "Intel 核顯調節授權",
      body: "Intel 的核顯控制庫要求先取得你的同意才允許改動頻率，"
        + "負向偏移也不例外。這是它那一側的硬性要求，和其他顯示卡無關。",
      accept: "同意",
      revoke: "收回同意",
      accepted: "已同意"
    },
    ownership: {
      label: "控制歸屬",
      firmware: "韌體自動管理",
      app: "軟體管理",
      firmwareNote: "歸韌體管，下面的設定不生效。"
    },
    /*
     * 第一次进控制页说的两屏。
     *
     * 先说保修，再说风险。**不叫"超频免责声明"** —— 这一页大部分事情不是超频：
     * 降压、降频、往下收功耗墙都在厂商设定的范围之内。把它们和超频写成一句话，
     * 用户就无法判断自己正在做的到底是哪一种。
     */
    notice: {
      warrantyTitle: "關於保修",
      warrantyBody: [
        "廠商條款通常把任何超出規格的執行都算作改動，不分方向 —— 按字面說，降壓也在內。",
        "實際差別在留不留痕跡：降壓、降頻、往下收功耗牆斷電即失效，處理器裡不留記錄；"
          + "正向超頻和加電壓會置位處理器裡的標記，送修可查。",
        "筆記本上不少限制是整機廠商在 BIOS 裡設的，不是晶片廠商的預設值，"
          + "所以這一頁能調到多少也由整機廠商決定。"
      ],
      riskTitle: "開始之前",
      next: "下一步",
      accept: "我已知悉"
    },
    accessLevel: {
      title: "調節許可權",
      intro: "決定控制頁裡哪些項可以調。切到更高一檔不會改動任何已有設定，"
        + "只是把更多項放出來。",
      name: { normal: "普通", root: "root" },
      summary: {
        normal: "功耗、電流、風扇及常規頻率調節。設定不當可能造成執行不穩定。",
        root: "放開沒有兜底的項：直接寫電壓、改基準時鐘。平時用不到。"
      },
      hint: {
        normal: "調錯了機器會不穩定，重啟能恢復，硬體不受損。要先在設定裡切到普通檔。",
        root: "這一項沒有硬體保護兜底，寫錯可能開不了機。要先在設定裡切到 root 檔。"
      },
      consequences: {
        normal: [
          "電壓調得過低會突然關機，頻率偏移過頭會破圖或算錯 —— 都是重啟就好，不傷硬體。",
          "溫度牆調高是把過熱保護往外挪，長期這麼跑會加速老化。"
        ],
        root: [
          "直接寫電壓和改基準時鐘沒有任何軟體保護，寫錯一個數可能開不了機。",
          "這一檔的項平時用不到；要調的話先確認你知道那個數字的含義。"
        ]
      },
      /*
       * 免责声明。**两档各一句，用户给的原话，不改写。**
       * 普通档那句在第一次进控制页时就要说 —— 默认这一档也是在动硬件配置。
       */
      disclaimer: {
        normal: "某些設定可能會導致電腦不穩定，對於本就有設計問題的電腦可能會有一定損傷風險，"
          + "調整硬體配置造成的一切後果本軟體概不負責。",
        root: "root 模式下某些設定調整極度危險，極大可能造成系統不穩定或者永久性硬體損傷，"
          + "調整硬體配置造成的一切後果本軟體概不負責。"
      },
      confirmTitle: (name: string) => `切到${name}檔？`,
      confirmAction: "切過去",
      cancel: "取消",
      saveFailed: "切換失敗，還在原來那一檔。"
    },
    channel: {
      nvapi: "NVAPI",
      "nvapi-drs": "驅動 Profile",
      nvml: "NVML",
      "oem-ec": "整機韌體",
      "amd-smu": "AMD SMU",
      "fan-core": "風扇核心",
      igcl: "Intel 顯示卡庫",
      adlx: "AMD ADLX"
    } as Record<string, string>,
    channelHint: "這一項實際走哪條鏈路寫下去。",
    detect: "重新檢測硬體",
    detecting: "檢測中",
    readings: "只讀數值",
    curveExecutionLabel: "曲線交給誰執行",
    takeoverCoversOthers: (others: string) =>
      `這臺機器上「交給軟體管」是整機一個開關：選了軟體接管，${others} 也會一起脫離韌體自動控制，停在當時的轉速上，除非也給它設一條曲線。`,
    curveExecutionFirmware: "寫入韌體",
    curveExecutionFirmwareHint: "韌體自己按表調速。關掉本程式、重啟之後依然有效。",
    curveExecutionSoftware: "軟體接管",
    curveExecutionSoftwareHint: "本程式每 0.1 秒算一次並寫下去。程式不在時風扇交還韌體。",
    createCurve: "新建曲線",
    readingFirmwareCurve: "正在讀取韌體當前曲線…",
    fixedStepsCurveNote: "這臺機器的韌體表只讓改每一檔的轉速，溫度斷點是韌體定死的，改不了。",
    curvePreview: "曲線預覽",
    saveFailed: "儲存失敗",
    apply: "應用",
    discard: "撤銷改動",
    term: {
      portable: "膝上型電腦",
      fixed: "桌面主機",
      cpu: "CPU風扇",
      "curve-firmware": "韌體跑曲線",
      "curve-software": "軟體跑曲線",
      "curve-fixed-steps": "韌體檔位表",
      gpu: "顯示卡風扇",
      intake: "內吹風扇",
      case: "機箱風扇"
    } as Record<string, string>,
    attachment: {
      integrated: "核顯",
      discrete: "獨顯",
      unknown: "接法未知"
    },
    status: {
      unset: "未設定",
      edited: "已改動，未應用",
      applying: "正在應用",
      applied: "已應用",
      unsupported: "這臺機器上控不了",
      failed: "沒應用上"
    },
    kind: {
      gpu: "顯示卡",
      cpu: "處理器",
      fan: "風扇"
    }
  },
  diskUsage: {
    title: "磁碟佔用",
    intro: "掃描後按真實大小把檔案擺成方格圖，一眼看出空間去哪了。",
    scopeLabel: "掃描範圍",
    scopeAllVolumes: "全部磁碟",
    scopeVolume: "指定磁碟",
    scopeFolder: "指定資料夾",
    modeLabel: "掃描方式",
    modeFast: "快速掃描",
    modeFastHint: "只適用於 NTFS，需要管理員許可權。",
    modeFull: "全掃描",
    modeFullHint: "逐級遍歷目錄。慢，但任何磁碟都能掃。",
    chooseFolder: "選擇資料夾",
    folderNotChosen: "還沒選資料夾",
    scan: "開始掃描",
    rescan: "重新掃描",
    cancel: "取消掃描",
    scanning: "正在掃描",
    volumeColumnLabel: "磁碟",
    noVolumes: "沒有找到可以掃描的磁碟。",
    notReady: "未就緒",
    freeOfTotal: (free: string, total: string) => `可用 ${free} / 共 ${total}`,
    volumeKind: {
      physical: "物理磁碟",
      virtual: "虛擬磁碟",
      removable: "可移動",
      network: "網路位置",
      optical: "光碟機",
      unknown: "來源未知"
    },
    fastUnsupported: "這個磁碟沒有可讀的檔案系統索引，快速掃描掃不到它；用全掃描。",
    skipped: "本次沒掃到的目標",
    skipReason: {
      noFileSystemIndex: "檔案系統沒有可讀的索引",
      needsElevation: "讀索引需要管理員許可權",
      volumeNotReady: "磁碟未就緒",
      targetUnavailable: "路徑不存在或打不開"
    },
    scanKind: {
      masterFileTable: "讀檔案系統索引",
      directoryWalk: "逐級遍歷目錄"
    },
    navigateUp: "上一層",
    resetView: "復位檢視",
    collapseSetup: "收起設定",
    expandSetup: "重新設定",
    scanTotals: (size: string, files: number, folders: number) =>
      `${size} · ${files} 個檔案 · ${folders} 個資料夾`,
    fileCount: (count: number) => `${count} 個檔案`,
    omitted: (count: number) =>
      `還有 ${count} 個方格這會兒太小或者不在視野裡，放大就會出現。`,
    copied: "已複製",
    menuOpenLocation: "開啟所在位置",
    menuProperties: "屬性",
    menuCopyPath: "複製完整路徑",
    menuDrillDown: "只看這一層",
    menuSize: "大小",
    menuPath: "路徑",
    menuKindFile: "檔案",
    menuKindDirectory: "資料夾",
    emptyTitle: "還沒有掃描結果",
    emptyDetail: "選好範圍和方式，點「開始掃描」。"
  },
  shell: {
    productName: "資源管理器",
    documentTitle: (page: string, product: string) => `${page} · ${product}`,
    pageNav: "頁面切換",
    currentPage: "當前頁面",
    runtimeCapability: "執行能力",
    taskCenter: "任務中心",
    taskCenterActive: (count: number) => `任務中心，${count} 項正在進行`,
    taskCenterSyncing: "任務中心 · 後端任務正在同步",
    taskCenterDisconnected: "任務中心 · 本機服務正在重新同步",
    taskCenterUnavailable: "任務中心 · 後端任務狀態不可用",
    minimize: "最小化",
    maximize: "最大化",
    closeWindow: "關閉"
  }
};

import { cjkText } from "../../cjkSpacing.ts";
import type { AppCopy } from "../zh/index.ts";

export const zhTwFreezeReportsCopy: Pick<AppCopy, "freezeReports"> = {
  freezeReports: {
    systemEvents: {
      tab: "系統事件",
      intro: "本機服務從系統記錄檔中收集儲存重試與重設、顯示卡驅動程式重設、記憶體耗盡、桌面處理程序當機和硬體錯誤等事件，依類別和目標合併為項目。這些問題無法由本軟體處理，此處僅偵測和報告；長時間沒有新事件的項目會轉為歷史。",
      loading: "正在讀取系統事件報告…",
      readFailed: "系統事件報告讀取失敗",
      notObservedTitle: "未在記錄系統事件",
      notObservedDetail: "系統事件記錄目前沒有執行：可能已在設定中關閉，或尚未啟動。執行之前不會產生項目。",
      unavailableTitle: "系統事件記錄失敗",
      unavailableDetail: "本機服務無法讀取系統記錄檔。失敗原因請見上方說明。",
      emptyTitle: "目前沒有系統事件",
      emptyDetail: "系統記錄檔中尚未出現需要報告的事件。",
      status: {
        label: "記錄狀態",
        observing: "正在記錄",
        "not-observed": "未在記錄",
        unavailable: "記錄失敗"
      },
      activeCount: "作用中的項目",
      historyCount: "歷史項目",
      machineTarget: "整部電腦",
      count: "次數",
      times: (count: number) => cjkText`${count}次`,
      firstObserved: "首次出現",
      lastObserved: "最近出現",
      overview: "概要",
      categoryLabel: "類別",
      target: "目標",
      state: "狀態",
      activeEntry: "作用中",
      historyEntry: "歷史",
      category: {
        storage_retry_reset: "儲存重試與重設",
        gpu_reset: "顯示卡驅動程式重設",
        memory_exhausted: "記憶體耗盡",
        desktop_crash: "桌面處理程序當機",
        hardware_error: "硬體錯誤",
        unexpected_shutdown: "意外關機與藍色畫面"
      },
      targetKindLabel: "目標類型",
      targetKind: {
        machine: "整部電腦",
        physical_disk: "實體磁碟",
        storage_port: "儲存控制器連接埠",
        volume: "磁碟區",
        gpu_device: "顯示卡裝置",
        desktop_process: "桌面處理程序",
        hardware: "硬體"
      },
      time: "時間",
      channel: "記錄檔",
      channelName: {
        system: "系統",
        application: "應用程式"
      },
      provider: "來源",
      eventId: "事件識別碼",
      recordId: "記錄編號",
      bugcheckCode: "藍色畫面代碼",
      dumpPath: "傾印檔案",
      failure: (name: string) => cjkText`系統記錄檔讀取失敗（${name}）。現有項目保留，恢復後繼續記錄。`,
      liveKernelFailure: (name: string) => cjkText`LiveKernelReports 資料夾掃描失敗（${name}）。系統記錄檔中的事件不受影響。`,
      unrecorded: (count: number) => cjkText`另有${count}個項目因達到項目上限而未記錄。`,
      items: "系統事件項目",
      detailsButton: "詳細資訊",
      back: "返回清單",
      eventsTitle: (count: number) => cjkText`最近的原始事件（${count}筆）`,
      xml: "事件 XML 原文"
    },
    recorder: {
      tab: "當機記錄",
      intro: "本機服務持續將記憶體、核心集區、磁碟和中斷的少量資料寫入固定大小的記錄檔案。電腦當機後被強制關機或出現藍色畫面時，下次開機會對照系統記錄檔產生一筆開機復盤，列出當機前的記錄、同一時段的系統事件和判定標籤。此處僅偵測和報告。",
      loading: "正在讀取當機記錄…",
      readFailed: "當機記錄讀取失敗",
      emptyTitle: "目前沒有開機復盤",
      emptyDetail: "上次開機以來沒有發現意外關機或藍色畫面。",
      recorder: "記錄器",
      running: "正在記錄",
      stopped: "未在記錄",
      lastRecordAt: "最後一筆記錄",
      reviewCount: "開機復盤",
      failure: (name: string) => cjkText`記錄器發生故障（${name}），目前沒有寫入記錄。`,
      stoppedNote: "記錄器目前沒有執行，可能已在設定中關閉。關閉期間發生當機時無法產生完整的復盤。",
      gapsTitle: "目前記錄中缺少的資料",
      gap: {
        "no-sample": "尚未寫入任何記錄",
        "interrupt-cpu-share": "中斷佔用的處理器時間",
        "longest-interrupt": "最長的單次中斷",
        "active-kernel-leak-system-item": "是否有作用中的系統層級核心洩漏項目",
        "process-private-commit": "處理程序的私人認可量",
        "physical-disk-inventory": "實體磁碟清單",
        "physical-disk-completed-operations": "實體磁碟完成的作業數",
        commit: "認可量和認可限制",
        "physical-memory": "實體記憶體",
        "kernel-pools": "分頁集區和非分頁集區"
      },
      reviews: "開機復盤",
      reviewTitle: (time: string) => cjkText`意外關機 · ${time}`,
      shutdownAt: "關機或藍色畫面時間",
      gapDuration: "間隔",
      bugcheckCode: "藍色畫面代碼",
      tagsTitle: "判定標籤",
      noTags: "沒有判定標籤。",
      inputsIncomplete: "復盤時部分記錄或系統記錄檔無法完整讀取，結果可能不完整。",
      tag: {
        bugcheck: {
          name: "藍色畫面",
          description: "系統記錄檔記錄了藍色畫面，藍色畫面代碼請見概要。"
        },
        "commit-exhausted": {
          name: "認可量耗盡",
          description: "記錄中的認可量達到記憶體保護的立即線，或同一時段內系統報告了記憶體耗盡。"
        },
        "physical-exhausted": {
          name: "實體記憶體耗盡",
          description: "記錄中的可用實體記憶體低於設定中的比例。"
        },
        "pool-high": {
          name: "核心集區過高",
          description: "非分頁集區超過實體記憶體的設定比例，或存在作用中的系統層級核心洩漏項目。"
        },
        "storage-unresponsive": {
          name: "儲存裝置無回應",
          description: "同一時段內出現了儲存重試或重設，或某個磁碟連續多次符合無回應條件。"
        },
        "gpu-reset": {
          name: "顯示卡驅動程式重設",
          description: "同一時段內出現了顯示卡驅動程式重設。"
        },
        "hardware-error": {
          name: "硬體錯誤",
          description: "同一時段內系統報告了硬體錯誤。"
        },
        "recording-stopped-normal": {
          name: "記錄正常中斷",
          description: "以上情況都沒有出現，最後的記錄全部正常。可能是儲存或篩選驅動程式卡住、突然斷電，或長按電源按鈕關機。"
        }
      },
      details: {
        button: "詳細資訊",
        back: "返回清單",
        loading: "正在讀取復盤內容…",
        readFailed: "復盤內容讀取失敗",
        export: "匯出",
        exporting: "正在匯出",
        exportFailed: "復盤匯出失敗",
        overview: "概要",
        reviewWindow: "復盤時段",
        timeRange: (from: string, to: string) => `${from} ～ ${to}`,
        samplesSeen: "時段內的記錄數",
        inputsComplete: "資料完整",
        yes: "是",
        no: "否",
        dumpPath: "傾印檔案",
        thresholdsTitle: "復盤時使用的閾值",
        commitImmediatePercent: "認可量立即線",
        physicalAvailablePercent: "可用實體記憶體下限",
        nonpagedPoolPercent: "非分頁集區上限（佔實體記憶體）",
        storageConsecutive: "磁碟無回應的連續次數",
        sampleInterval: "記錄間隔",
        reviewWindowLength: "復盤時段長度",
        curvesTitle: "記錄曲線",
        curve: {
          commit: "認可量（佔認可限制）",
          physicalAvailable: "可用實體記憶體（佔總量）",
          nonpagedPool: "非分頁集區",
          pagedPool: "分頁集區",
          diskQueue: "磁碟佇列深度（最大）",
          diskLatency: "磁碟延遲（最大）",
          interrupt: "中斷佔用的處理器時間"
        },
        curveRange: (min: string, max: string) => `${min} ～ ${max}`,
        noCurveData: "無資料",
        noRecords: "復盤時段內沒有記錄。",
        lastRecordTitle: "最後一筆記錄",
        time: "時間",
        commit: "認可量",
        physicalAvailable: "可用實體記憶體",
        ofLimit: (formattedValue: string, formattedLimit: string) => `${formattedValue} / ${formattedLimit}`,
        longestInterrupt: "最長的單次中斷",
        kernelLeakActive: "有作用中的系統層級核心洩漏項目",
        processesTitle: "私人認可量最多的處理程序",
        processName: (name: string, pid: string) => `${name}（PID ${pid}）`,
        diskName: (id: string) => cjkText`實體磁碟${id}`,
        queueDepth: "佇列深度",
        latency: "延遲",
        completedOperations: "完成的作業數",
        eventsTitle: (count: number) => cjkText`時段內的系統事件（${count}筆）`,
        noEvents: "復盤時段內沒有收到系統事件。",
        actualShutdownAt: "實際關機時間"
      }
    },
    settings: {
      open: "設定",
      back: "返回報告",
      systemEventsTitle: "系統事件設定",
      recorderTitle: "當機記錄設定",
      readFailed: "設定讀取失敗",
      save: "儲存",
      saving: "正在儲存",
      discard: "捨棄變更",
      saved: "設定已儲存。",
      saveFailed: "設定儲存失敗。",
      invalid: "設定未通過檢查，未儲存。請檢查各項數值。",
      conflict: "設定未儲存：執行中不能修改規則表、記錄筆數或記錄檔案位置。請先關閉對應功能並儲存，再修改這些項目；已有記錄檔案時，修改記錄筆數還需要同時更換記錄檔案位置。",
      resourceBound: "設定未儲存：所需資源超過上限，請減少容量或筆數。",
      draftInvalid: "目前的變更不完整（例如規則缺少來源名稱），無法儲存。",
      rulesLocked: "系統事件記錄開啟時不能修改規則表。請先關閉並儲存。",
      recorderLocked: "記錄器開啟時不能修改此項目。請先關閉並儲存。",
      units: {
        minutes: "分鐘",
        seconds: "秒",
        milliseconds: "毫秒",
        percent: "百分比",
        count: "數量"
      },
      systemEvents: {
        enabled: {
          title: "記錄系統事件",
          description: "從系統記錄檔接收事件並合併為項目。關閉後不再接收新事件，現有項目保留。"
        },
        historyAfter: {
          title: "轉為歷史",
          description: "項目在最後一次事件之後經過這段時間仍沒有新事件時轉為歷史。"
        },
        eventTailCapacity: {
          title: "每個項目保留的原始事件",
          description: "每個項目保留最近多少筆原始事件，供詳細資訊頁查看。"
        },
        entryCapacity: {
          title: "項目上限",
          description: "最多保留的項目數量；達到上限後新的項目只計數，不記錄。"
        },
        liveKernelRoot: {
          title: "LiveKernelReports 位置",
          description: "系統存放核心即時傾印報告的資料夾，用於發現顯示卡驅動程式逾時等報告。"
        },
        liveKernelDirectories: {
          title: "掃描的子資料夾",
          description: "在上述位置下掃描的子資料夾，每行一個。"
        },
        liveKernelEntryCapacity: {
          title: "傾印報告上限",
          description: "每次掃描最多記錄的傾印報告數量。"
        },
        rules: {
          title: "事件規則",
          description: "哪些系統記錄檔事件歸入哪個類別，以及事件指向的目標類型。"
        }
      },
      recorder: {
        enabled: {
          title: "記錄當機資料",
          description: "持續寫入記錄檔案，並在意外關機後的下次開機時產生復盤。關閉後停止記錄，記錄檔案保留。"
        },
        sampleInterval: {
          title: "記錄間隔",
          description: "兩次記錄之間的時間。間隔越短，當機前的記錄越接近當機時刻。"
        },
        reviewWindow: {
          title: "復盤時段",
          description: "復盤顯示最後一筆記錄之前多長時間的記錄和系統事件。"
        },
        commitImmediate: {
          title: "認可量立即線",
          description: "判定認可量耗盡的界線，跟隨記憶體保護的立即線，請在記憶體保護設定中修改。"
        },
        commitImmediateNone: "未設定",
        physicalAvailable: {
          title: "可用實體記憶體下限",
          description: "可用實體記憶體低於總量的這個比例時，判定為實體記憶體耗盡。"
        },
        nonpagedPool: {
          title: "非分頁集區上限",
          description: "非分頁集區超過實體記憶體的這個比例時，判定為核心集區過高。"
        },
        storageConsecutive: {
          title: "磁碟無回應的連續次數",
          description: "某個磁碟連續多少筆記錄符合無回應條件時，判定為儲存裝置無回應。"
        },
        slots: {
          title: "記錄筆數",
          description: "記錄檔案能容納的記錄筆數，決定能回看多長時間。已有記錄檔案時，修改筆數需要同時更換記錄檔案位置。"
        },
        ringPath: {
          title: "記錄檔案位置",
          description: "記錄檔案的完整路徑。留空則使用預設位置。"
        },
        ringPathDefault: "預設位置"
      },
      ruleTable: {
        channel: "記錄檔",
        provider: "來源名稱",
        ids: "事件識別碼",
        idsHint: "以逗號分隔",
        category: "類別",
        targetKind: "目標類型",
        remove: "刪除",
        add: "新增規則"
      }
    }
  }
};

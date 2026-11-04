import { cjkText } from "../../cjkSpacing.ts";

// 详细信息 → 系统事件、假死记录（docs/system_freeze_protection_2026-11-01.md F3、F4）及两者的设置子页。
// 阈值和容量的数值都来自后端设置文档，这里的说明只写含义，不写默认值。
export const zhFreezeReportsCopy = {
  freezeReports: {
    systemEvents: {
      tab: "系统事件",
      intro: "本地服务从系统日志中收集存储重试与重置、显卡重置、内存耗尽、桌面进程崩溃和硬件错误等事件，按类别和目标合并为条目。这些问题无法由本软件处理，此处仅检测和报告；长时间没有新事件的条目转为历史。",
      loading: "正在读取系统事件报告…",
      readFailed: "系统事件报告读取失败",
      notObservedTitle: "未在记录系统事件",
      notObservedDetail: "系统事件记录目前没有运行：可能已在设置中关闭，或尚未启动。运行之前不会生成条目。",
      unavailableTitle: "系统事件记录失败",
      unavailableDetail: "本地服务无法读取系统日志。失败原因见上方说明。",
      emptyTitle: "暂无系统事件",
      emptyDetail: "系统日志中尚未出现需要报告的事件。",
      status: {
        label: "记录状态",
        observing: "正在记录",
        "not-observed": "未在记录",
        unavailable: "记录失败"
      },
      activeCount: "活动条目",
      historyCount: "历史条目",
      machineTarget: "整机",
      count: "次数",
      times: (count: number) => cjkText`${count}次`,
      firstObserved: "首次出现",
      lastObserved: "最近出现",
      overview: "概要",
      categoryLabel: "类别",
      target: "目标",
      state: "状态",
      activeEntry: "活动",
      historyEntry: "历史",
      category: {
        storage_retry_reset: "存储重试与重置",
        gpu_reset: "显卡驱动重置",
        memory_exhausted: "内存耗尽",
        desktop_crash: "桌面进程崩溃",
        hardware_error: "硬件错误",
        unexpected_shutdown: "意外关机与蓝屏"
      },
      targetKindLabel: "目标类型",
      targetKind: {
        machine: "整机",
        physical_disk: "物理磁盘",
        storage_port: "存储控制器端口",
        volume: "卷",
        gpu_device: "显卡设备",
        desktop_process: "桌面进程",
        hardware: "硬件"
      },
      time: "时间",
      channel: "日志",
      channelName: {
        system: "系统",
        application: "应用程序"
      },
      provider: "来源",
      eventId: "事件编号",
      recordId: "记录编号",
      bugcheckCode: "蓝屏代码",
      dumpPath: "转储文件",
      failure: (name: string) => cjkText`系统日志读取失败（${name}）。已有条目保留，恢复后继续记录。`,
      liveKernelFailure: (name: string) => cjkText`LiveKernelReports 目录扫描失败（${name}）。系统日志中的事件不受影响。`,
      unrecorded: (count: number) => cjkText`另有${count}个条目因达到条目上限未记录。`,
      items: "系统事件条目",
      detailsButton: "详细信息",
      back: "返回列表",
      eventsTitle: (count: number) => cjkText`最近的原始事件（${count}条）`,
      xml: "事件 XML 原文"
    },
    recorder: {
      tab: "假死记录",
      intro: "本地服务持续把内存、内核池、磁盘和中断的少量数据写入固定大小的记录文件。电脑卡死后被强制关机或蓝屏，下次开机时会对照系统日志生成一条开机复盘，列出卡死前的记录、同一时间段的系统事件和判定标签。此处仅检测和报告。",
      loading: "正在读取假死记录…",
      readFailed: "假死记录读取失败",
      emptyTitle: "暂无开机复盘",
      emptyDetail: "上次开机以来没有发现意外关机或蓝屏。",
      recorder: "记录器",
      running: "正在记录",
      stopped: "未在记录",
      lastRecordAt: "最后一条记录",
      reviewCount: "开机复盘",
      failure: (name: string) => cjkText`记录器出现故障（${name}），目前没有在写入记录。`,
      stoppedNote: "记录器目前没有运行，可能已在设置中关闭。关闭期间发生卡死时无法生成完整的复盘。",
      gapsTitle: "当前记录中缺少的数据",
      gap: {
        "no-sample": "尚未写入任何记录",
        "interrupt-cpu-share": "中断占用的处理器时间",
        "longest-interrupt": "最长的单次中断",
        "active-kernel-leak-system-item": "是否有活动的系统级内核泄漏条目",
        "process-private-commit": "进程的私有提交量",
        "physical-disk-inventory": "物理磁盘列表",
        "physical-disk-completed-operations": "物理磁盘完成的操作数",
        commit: "提交量和提交限制",
        "physical-memory": "物理内存",
        "kernel-pools": "分页池和非分页池"
      },
      reviews: "开机复盘",
      reviewTitle: (time: string) => cjkText`意外关机 · ${time}`,
      shutdownAt: "关机或蓝屏时间",
      gapDuration: "间隔",
      bugcheckCode: "蓝屏代码",
      tagsTitle: "判定标签",
      noTags: "没有判定标签。",
      inputsIncomplete: "复盘时部分记录或系统日志无法完整读取，结果可能不完整。",
      tag: {
        bugcheck: {
          name: "蓝屏",
          description: "系统日志记录了蓝屏，蓝屏代码见概要。"
        },
        "commit-exhausted": {
          name: "提交量耗尽",
          description: "记录中的提交量达到内存保护的立即线，或同一时间段内系统报告了内存耗尽。"
        },
        "physical-exhausted": {
          name: "物理内存耗尽",
          description: "记录中的可用物理内存低于设置中的比例。"
        },
        "pool-high": {
          name: "内核池过高",
          description: "非分页池超过物理内存的设置比例，或存在活动的系统级内核泄漏条目。"
        },
        "storage-unresponsive": {
          name: "存储无响应",
          description: "同一时间段内出现了存储重试或重置，或某块磁盘连续多次满足无响应条件。"
        },
        "gpu-reset": {
          name: "显卡驱动重置",
          description: "同一时间段内出现了显卡驱动重置。"
        },
        "hardware-error": {
          name: "硬件错误",
          description: "同一时间段内系统报告了硬件错误。"
        },
        "recording-stopped-normal": {
          name: "记录正常中断",
          description: "以上情况都没有出现，最后的记录全部正常。可能是存储或过滤驱动卡住、突然断电，或长按电源键关机。"
        }
      },
      details: {
        button: "详细信息",
        back: "返回列表",
        loading: "正在读取复盘内容…",
        readFailed: "复盘内容读取失败",
        export: "导出",
        exporting: "正在导出",
        exportFailed: "复盘导出失败",
        overview: "概要",
        reviewWindow: "复盘时间段",
        timeRange: (from: string, to: string) => `${from} ～ ${to}`,
        samplesSeen: "时间段内的记录数",
        inputsComplete: "数据完整",
        yes: "是",
        no: "否",
        dumpPath: "转储文件",
        thresholdsTitle: "复盘时使用的阈值",
        commitImmediatePercent: "提交量立即线",
        physicalAvailablePercent: "可用物理内存下限",
        nonpagedPoolPercent: "非分页池上限（占物理内存）",
        storageConsecutive: "磁盘无响应的连续次数",
        sampleInterval: "记录间隔",
        reviewWindowLength: "复盘时间段长度",
        curvesTitle: "记录曲线",
        curve: {
          commit: "提交量（占提交限制）",
          physicalAvailable: "可用物理内存（占总量）",
          nonpagedPool: "非分页池",
          pagedPool: "分页池",
          diskQueue: "磁盘队列深度（最大）",
          diskLatency: "磁盘延迟（最大）",
          interrupt: "中断占用的处理器时间"
        },
        curveRange: (min: string, max: string) => `${min} ～ ${max}`,
        noCurveData: "无数据",
        noRecords: "复盘时间段内没有记录。",
        lastRecordTitle: "最后一条记录",
        time: "时间",
        commit: "提交量",
        physicalAvailable: "可用物理内存",
        ofLimit: (formattedValue: string, formattedLimit: string) => `${formattedValue} / ${formattedLimit}`,
        longestInterrupt: "最长的单次中断",
        kernelLeakActive: "有活动的系统级内核泄漏条目",
        processesTitle: "私有提交量最多的进程",
        processName: (name: string, pid: string) => `${name}（PID ${pid}）`,
        diskName: (id: string) => cjkText`物理磁盘${id}`,
        queueDepth: "队列深度",
        latency: "延迟",
        completedOperations: "完成的操作数",
        eventsTitle: (count: number) => cjkText`时间段内的系统事件（${count}条）`,
        noEvents: "复盘时间段内没有收到系统事件。",
        actualShutdownAt: "实际关机时间"
      }
    },
    settings: {
      open: "设置",
      back: "返回报告",
      systemEventsTitle: "系统事件设置",
      recorderTitle: "假死记录设置",
      readFailed: "设置读取失败",
      save: "保存",
      saving: "正在保存",
      discard: "放弃改动",
      saved: "设置已保存。",
      saveFailed: "设置保存失败。",
      invalid: "设置未通过检查，未保存。请检查各项数值。",
      conflict: "设置未保存：运行中不能修改规则表、记录条数或记录文件位置。请先关闭对应功能并保存，再修改这些项目；已有记录文件时修改记录条数还需要同时更换记录文件位置。",
      resourceBound: "设置未保存：所需资源超过上限，请减小容量或条数。",
      draftInvalid: "当前改动不完整（例如规则缺少来源名称），无法保存。",
      rulesLocked: "系统事件记录开启时不能修改规则表。请先关闭并保存。",
      recorderLocked: "记录器开启时不能修改此项。请先关闭并保存。",
      units: {
        minutes: "分钟",
        seconds: "秒",
        milliseconds: "毫秒",
        percent: "百分比",
        count: "数量"
      },
      systemEvents: {
        enabled: {
          title: "记录系统事件",
          description: "从系统日志接收事件并合并为条目。关闭后不再接收新事件，已有条目保留。"
        },
        historyAfter: {
          title: "转为历史",
          description: "条目在最后一次事件之后经过这段时间仍没有新事件时转为历史。"
        },
        eventTailCapacity: {
          title: "每个条目保留的原始事件",
          description: "每个条目保留最近多少条原始事件，供详细信息页查看。"
        },
        entryCapacity: {
          title: "条目上限",
          description: "最多保留的条目数量；达到上限后新的条目只计数，不记录。"
        },
        liveKernelRoot: {
          title: "LiveKernelReports 位置",
          description: "系统存放内核实时转储报告的目录，用于发现显卡驱动超时等报告。"
        },
        liveKernelDirectories: {
          title: "扫描的子目录",
          description: "在上述位置下扫描的子目录，每行一个。"
        },
        liveKernelEntryCapacity: {
          title: "转储报告上限",
          description: "每次扫描最多记录的转储报告数量。"
        },
        rules: {
          title: "事件规则",
          description: "哪些系统日志事件归入哪个类别，以及事件指向的目标类型。"
        }
      },
      recorder: {
        enabled: {
          title: "记录假死数据",
          description: "持续写入记录文件，并在意外关机后的下次开机时生成复盘。关闭后停止记录，记录文件保留。"
        },
        sampleInterval: {
          title: "记录间隔",
          description: "两次记录之间的时间。间隔越短，卡死前的记录越接近卡死时刻。"
        },
        reviewWindow: {
          title: "复盘时间段",
          description: "复盘显示最后一条记录之前多长时间的记录和系统事件。"
        },
        commitImmediate: {
          title: "提交量立即线",
          description: "判定提交量耗尽的界线，跟随内存保护的立即线，在内存保护设置中修改。"
        },
        commitImmediateNone: "未设置",
        physicalAvailable: {
          title: "可用物理内存下限",
          description: "可用物理内存低于总量的这个比例时，判定为物理内存耗尽。"
        },
        nonpagedPool: {
          title: "非分页池上限",
          description: "非分页池超过物理内存的这个比例时，判定为内核池过高。"
        },
        storageConsecutive: {
          title: "磁盘无响应的连续次数",
          description: "某块磁盘连续多少次记录满足无响应条件时，判定为存储无响应。"
        },
        slots: {
          title: "记录条数",
          description: "记录文件能容纳的记录条数，决定能回看多长时间。已有记录文件时，修改条数需要同时更换记录文件位置。"
        },
        ringPath: {
          title: "记录文件位置",
          description: "记录文件的完整路径。留空使用默认位置。"
        },
        ringPathDefault: "默认位置"
      },
      ruleTable: {
        channel: "日志",
        provider: "来源名称",
        ids: "事件编号",
        idsHint: "以逗号分隔",
        category: "类别",
        targetKind: "目标类型",
        remove: "删除",
        add: "添加规则"
      }
    }
  }
};

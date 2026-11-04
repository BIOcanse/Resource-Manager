import { cjkText } from "../../cjkSpacing.ts";

// 假死防护的三项动作（docs/system_freeze_protection_2026-11-01.md F5、F6、F7）：报告上的“重启设备”和它的记录、
// 系统集成里的蓝屏转储快捷键、CPU 独占保护的设置与记录。数值、默认值和容量都来自后端，这里只写含义。
export const zhFreezeActionsCopy = {
  deviceActions: {
    restart: {
      label: "重启设备",
      busy: "正在重启",
      hint: "停用并重新启用这条报告指向的设备，可以让卡住的驱动重新开始工作。每次都需要你确认，软件不会自动执行。",
      confirmTitle: "重启这个设备？",
      confirmMessage: (target: string) => cjkText`将停用并重新启用“${target}”对应的设备。`,
      confirmDetails: [
        "只在你确认后执行这一次，不会自动重复。",
        "需要管理员权限；结果写入详细信息里的设备动作记录。"
      ],
      risks: {
        DeviceRestartNetworkInterrupted: "如果是网络适配器，网络连接会暂时中断。",
        DeviceRestartDisplayStateLost: "如果是显卡，屏幕会短暂黑屏，窗口位置和显示设置可能丢失。",
        DeviceRestartMayRequireReboot: "部分设备要重新启动电脑才能恢复。"
      },
      unknownRisk: (code: string) => cjkText`其他风险（${code}）。`,
      confirm: "重启设备",
      doneTitle: "设备已重启",
      refusedTitle: "设备没有重启",
      failed: "重启设备失败。",
      reason: (text: string) => cjkText`结论：${text}`,
      deviceResult: (text: string) => cjkText`设备结果：${text}`,
      win32Detail: (code: string) => cjkText`系统错误码：${code}`,
      recorded: (sequence: string) => cjkText`已写入设备动作记录（序号 ${sequence}）。`,
      notRecorded: "这次请求没有写入记录。"
    },
    reasons: {
      completed: "已完成",
      "confirmation-required": "需要确认",
      "report-not-found": "找不到这条报告",
      "unsupported-report": "这类报告不支持重启设备",
      "device-evidence-unavailable": "报告里没有能确定设备的证据",
      "invalid-settings": "设置无效",
      "record-capacity-reached": "设备动作记录已满，没有执行",
      "persistence-failed": "记录写入失败，没有执行",
      "device-resolution-failed": "无法确定要重启的设备",
      "device-restart-failed": "重启设备失败",
      "notification-failed": "设备已处理，但通知发送失败"
    },
    results: {
      success: "成功",
      "confirmation-required": "需要确认",
      "invalid-instance-id": "设备实例 ID 无效",
      "administrator-required": "需要管理员权限",
      "device-not-found": "找不到设备",
      "device-unavailable": "设备当前不可用",
      "disable-failed": "停用设备失败",
      "enable-failed": "重新启用设备失败",
      "reboot-required": "需要重新启动电脑",
      "verification-failed": "重启后检查未通过",
      "driver-not-found": "找不到驱动程序",
      "ambiguous-device": "匹配到多个设备，没有执行",
      "invalid-module": "驱动模块无效",
      "enumeration-failed": "列举设备失败",
      "buffer-too-small": "系统缓冲区不足",
      "unsupported-platform": "当前系统不支持",
      interrupted: "进行中被中断，结果无法确定"
    },
    unknownCode: (code: string) => cjkText`代码 ${code}`,
    records: {
      tab: "设备动作记录",
      intro: "在报告上确认“重启设备”后，每次操作的结果记在这里。记录持久保存，不自动删除；进行中的记录在操作结束后转为历史。",
      loading: "正在读取设备动作记录…",
      readFailed: "设备动作记录读取失败",
      empty: "还没有设备动作记录。",
      loadMore: "加载更早的记录",
      loadingMore: "正在加载",
      refresh: "刷新",
      shown: (count: string) => cjkText`已显示${count}条记录。`,
      states: { active: "进行中", history: "历史" },
      device: "设备实例",
      report: "报告条目",
      time: "时间",
      result: "结果",
      detail: "系统错误码",
      sequence: "序号"
    }
  },
  crashDumpKeys: {
    title: "蓝屏转储快捷键",
    description: "开启后，按住右 Ctrl 键并连按两次 Scroll Lock 键，系统会立即蓝屏并写下内存转储。电脑卡死、无法正常操作时，可以用它留下现场供事后分析。默认关闭。",
    details: [
      "只有你按下这组按键时才会触发，平时没有任何作用。",
      "触发后电脑会立即重新启动，未保存的工作会丢失。",
      "更改在重新启动电脑后生效，写入时需要管理员权限；关闭后恢复开启前的系统设置。"
    ],
    notProvided: "后端没有提供这项设置。",
    statusTitle: "系统中的状态",
    states: {
      takesEffectAfterRestart: "已写入系统，重新启动电脑后生效。",
      off: "未开启。",
      restorePending: "正在恢复开启前的系统设置，重新启动电脑后完成。",
      configurationMismatch: "系统里的按键设置和这里的选择不一致。",
      failed: "写入系统设置失败。"
    },
    combinations: {
      rightCtrlScrollLockTwice: "按键：右 Ctrl + 两次 Scroll Lock",
      custom: "按键：其他程序或手动设置的自定义按键"
    },
    kernelDumpMissing: "系统没有配置内核内存转储，触发后可能不会写下转储。",
    reason: (text: string) => cjkText`原因：${text}`,
    win32Detail: (code: string) => cjkText`系统错误码：${code}`,
    reasons: {
      "invalid-crash-dump-keys": "按键设置无效",
      "crash-dump-keys-read-failed": "读取系统设置失败",
      "crash-dump-keys-write-failed": "写入系统设置失败",
      "crash-dump-keys-verify-failed": "写入后检查未通过",
      "crash-dump-keys-restore-pending": "开启前的系统设置还没有恢复完成",
      "invalid-crash-dump-key-snapshot": "保存的原始设置无效",
      "crash-dump-keys-access-denied": "需要管理员权限",
      "crash-dump-key-value-too-large": "系统中的原始值过大，无法保存",
      "crash-dump-keys-configuration-mismatch": "系统里的设置被其他程序改动过",
      "crash-dump-keys-bridge-failed": "本地服务无法处理这项设置"
    }
  },
  cpuMonopoly: {
    intro: "一个进程以高或实时优先级持续占满 CPU 时，系统可能卡住、无法响应。开启后，这样的进程会被调回正常优先级：只调整优先级，不结束、不挂起，也不会再改回去。主机内存保护的进程白名单同样适用。默认关闭。",
    notProvided: "后端没有提供 CPU 独占保护的设置。",
    unsavedNote: "有未保存的更改，保存后生效。",
    rejected: (reason: string) => cjkText`设置未被接受：${reason}`,
    enabledTitle: "启用 CPU 独占保护",
    enabledDescription: "开启后在后台检查各进程的 CPU 占用和优先级；关闭时不检查。",
    thresholdTitle: "占用阈值",
    thresholdDescription: "以高或实时优先级运行的进程，CPU 占用（占全部处理器的百分比）达到这个值才开始计时。",
    thresholdLabel: "百分比",
    durationTitle: "持续时间",
    durationDescription: "占用连续达到阈值这么久，才把它调回正常优先级。",
    durationLabel: "秒",
    intervalTitle: "检查间隔",
    intervalDescription: "多久读取一次各进程的 CPU 占用。",
    intervalLabel: "秒",
    whitelistNote: "白名单在“主机内存保护”分区中编辑。",
    statusTitle: "当前状态",
    statusLoading: "正在读取 CPU 独占保护状态…",
    statusUnavailable: "CPU 独占保护状态读取失败",
    statusUnavailableDetail: "设置仍可编辑和保存；状态恢复后在这里显示。",
    stateActive: "正在检查",
    stateInactive: (reason: string) => cjkText`未在检查：${reason}`,
    inactiveReasons: {
      disabled: "未开启",
      "not-running": "保护还没有启动",
      "process-data-unavailable": "读不到进程数据",
      "process-exit-proof-unavailable": "无法确认进程是否已退出",
      "process-capacity": "进程数超过可跟踪的上限",
      stopped: "已停止"
    },
    currentAction: "正在生效的处理",
    exclusionsTitle: "排除的进程",
    exclusionsEmpty: "当前没有排除的进程。",
    exclusionReasons: {
      "resource-manager": "本软件自己的进程",
      "resource-manager-process": "本软件自己的进程",
      "protected-process": "在白名单中",
      "critical-process": "系统关键进程",
      "unsupported-platform": "当前系统不支持",
      "identity-changed": "进程已变化",
      "cannot-open": "无法访问这个进程",
      "criticality-unavailable": "无法确认是否为系统关键进程"
    },
    process: (name: string, processId: number) => cjkText`${name}（PID ${processId}）`,
    percent: (value: string) => `${value}%`,
    seconds: (value: string) => cjkText`${value} 秒`,
    priorities: { High: "高", RealTime: "实时" },
    records: {
      tab: "CPU 独占保护记录",
      intro: "CPU 独占保护每处理一个进程记一条。调回正常优先级后，进程退出或你另行设置了它的优先级时，记录转为历史；处理失败的直接是历史。记录持久保存，不自动删除。",
      loading: "正在读取 CPU 独占保护记录…",
      unavailable: "CPU 独占保护记录读取失败",
      empty: "还没有处理记录。",
      count: (count: string, capacity: string) => cjkText`共${count}条记录，最多保存${capacity}条。`,
      unrecorded: (count: number) => cjkText`记录已满，之后有${count}次处理没有记下；清空历史后恢复记录。`,
      outcomes: { normalized: "已调回正常优先级", failed: "处理失败" },
      states: { active: "仍在生效", history: "历史" },
      time: "时间",
      cpu: "CPU 占用",
      previousPriority: "原优先级",
      threshold: "当时的阈值",
      duration: "当时的持续时间",
      reason: "原因",
      detail: "详细信息",
      endedAt: "结束时间",
      endReason: "结束原因"
    },
    actionReasons: {
      "process-exited": "进程已退出",
      "user-priority-set": "优先级已被另行设置",
      "write-failed": "写入优先级失败",
      "identity-changed": "进程已变化",
      "priority-changed": "优先级已被其他程序改变",
      "priority-unavailable": "读不到进程的优先级",
      "priority-write-failed": "写入优先级失败",
      "priority-readback-unavailable": "写入后无法读回优先级",
      "priority-readback-mismatch": "写入后读回的优先级不一致"
    },
    notice: {
      title: "CPU 独占保护",
      normalized: (target: string) => cjkText`已把${target}调回正常优先级。`,
      failed: (target: string, reason: string) => cjkText`未能调整${target}：${reason}`,
      openRecords: "查看记录"
    }
  }
};

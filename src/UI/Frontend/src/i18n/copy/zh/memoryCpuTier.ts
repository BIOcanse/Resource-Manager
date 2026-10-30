import { cjkText } from "../../cjkSpacing.ts";
import type { MemoryCpuTierOptionName } from "../../../data/memoryCpuTier/memoryCpuTierOptionNames.ts";

// 内存与 CPU 取舍五档（docs/memory_cpu_tiers_2026-10-30.md）。选项的名称和说明按后端回复里的 name 取，
// 键是 data/memoryCpuTier/memoryCpuTierOptionNames.ts 里的暂定名；回复里认不出的名字由界面原样显示并标“未知选项”。
// choices 是选择型选项的取值名称，认不出的取值原样显示。
export const zhMemoryCpuTierCopy = {
  memoryCpuTier: {
    intro: "一批在内存和 CPU 之间取舍的选项分成五档：第 1 档最省内存、CPU 压力最高，第 5 档最省 CPU、内存压力最高，默认第 3 档。第 2 到第 4 档开着对另一方影响不显著的比例项，只是比例不同；第 1 档和第 5 档另外开启代价显著的激进项。每个选项都可以单独设置。",
    tierTitle: "档位",
    tierDescription: "选定档位后，下方列出这一档每个选项的值。",
    tierAriaLabel: "内存与 CPU 取舍档位",
    tierLabel: (tier: number) => cjkText`第${tier}档`,
    tierLowEnd: "省内存",
    tierHighEnd: "省 CPU",
    savedTier: (tier: number) => cjkText`已保存：第${tier}档`,
    defaultTier: (tier: number) => cjkText`默认：第${tier}档`,
    measuredTitle: "实测差别",
    measuredDescription: "每档大约多用或少用多少内存和 CPU。",
    notMeasured: "尚未实测",
    optionsTitle: (tier: number) => cjkText`第${tier}档的选项`,
    previewNote: "这是未保存的预览：值按所选档位和你的单独设置算出。保存后显示后端给出的值。",
    loading: "正在读取档位和选项…",
    loadFailed: "无法读取内存与 CPU 取舍的档位和选项。",
    retry: "重试",
    noOptions: "后端没有给出任何选项。",
    unknownOption: "未知选项",
    unset: "不设",
    valueLabel: "设定值",
    choiceLabel: "取值",
    categories: { proportional: "比例项", aggressive: "激进项" },
    applyModes: { immediate: "立即生效", restart: "重启后生效" },
    sources: { tier: "来自档位", user: "你的设置" },
    currentlyRunning: (value: string) => cjkText`现在在用：${value}`,
    afterRestart: (value: string) => cjkText`重启后：${value}`,
    restartNote: "重启服务后生效；本页不会重启任何程序。",
    clear: "清除",
    seconds: (value: string) => cjkText`${value}秒`,
    milliseconds: (value: string) => cjkText`${value}毫秒`,
    choices: {
      optimize: "至少优化（每次检查都归还）",
      autoLean: "自动，偏向少占内存",
      auto: "自动",
      autoRetain: "自动，偏向多留内存",
      unrestricted: "不限制（不主动归还）"
    } as Record<string, string>,
    options: {
      gcGen0MaxBudget: { title: "GC 第 0 代预算上限", description: "运行时两次回收之间允许新分配的内存上限；越小回收越频繁、越省内存。不设时由运行时按处理器缓存决定。" },
      gcConserveMemory: { title: "GC 压缩倾向", description: "运行时为减少内存而整理堆的积极程度，0 到 9，越高越省内存、越费 CPU。" },
      ledgerMemoryModeFloor: { title: "账本内存模式下限", description: "程序自身的内存账本归还未用内存的最低积极程度。" },
      bufferReturn: { title: "缓冲区归还", description: "进程枚举缓冲、计数器解析区等临时区用完后多快归还内存。" },
      nativeWorkareaGrowth: { title: "原生工作区增长", description: "原生模块的工作区按实际用量增长并缩回，还是按上限一次预留。" },
      singleRequestStopBufferMs: { title: "单次读取后的采样源保留时间", description: "一次性读取结束后共享采样源继续保留的时间，紧接着再读时不必重新启动；0 表示读取结束即停止。保留期间不额外采样。" },
      serviceIdentityCacheMs: { title: "服务快照有效期", description: "服务与进程对应关系的快照直接复用的时间，0 表示每次重读。" },
      browserRuntimeSnapshotSeconds: { title: "浏览器运行时目录快照有效期", description: "浏览器运行时目录的快照直接复用的时间，0 表示每次重读。" },
      platformSensorSnapshotSeconds: { title: "平台传感器快照有效期", description: "平台传感器读数的快照直接复用的时间，0 表示每次重读。" },
      storageSnapshotSeconds: { title: "存储快照有效期", description: "存储设备信息的快照直接复用的时间，0 表示每次重读。" },
      rendererAbsenceEntries: { title: "渲染器缺席缓存条数", description: "记住哪些进程没有渲染器的条目上限；越多重复检查越少、占用内存越多。" },
      fileQuerySessionsMax: { title: "文件查询会话上限", description: "同时保留的文件查询会话最多几个。" },
      fileQuerySessionsPrebuilt: { title: "预建文件查询会话", description: "提前建好、随时可用的文件查询会话个数，0 表示按需建立。" }
    } satisfies Record<MemoryCpuTierOptionName, { title: string; description: string }>
  }
};

// 内核泄漏报告的线上帧（/api/kernel-leaks/subscribe 的 value），模拟后端和解码器测试共用。合同 v3：v2（codex/kernel-leak-report-v2 db67b1ca）
// 加条目生命周期（docs/report_lifecycle_2026-10-28.md：条目带 state、firstObservedAt、lastObservedAt，任何 status 下都可以有，报告带 unrecordedItemCount）。
// kernelLeakRecordedFrames 是后端交接里记录的线上例子（final-message.md「Recorded wire examples」），生命周期字段按合同补上，解码器测试逐个解码；
// sourceBeforeFirstFrame 取自 codex/kernel-leak-contract 的交接（kernel-leak-contract.md「Wire shape before and after」）。
// kernelLeakReportFrames 是 mock 场景：观察要求取生产默认值（600 秒、8 个样本，采样间隔 30 秒）；
// 持有者取自本机 10-27 的真实例子（GCUBridge 握着 1,464 个句柄、NahimicService 1,071 个），
// 其余几项摆出后端会发的其他形状：多个服务名、进程已结束（映像和服务都是 ProcessEnded）、映像信息读不到、文件没有产品名。

const recordedActions = [
  {
    action: "RestartHolder",
    enabled: false,
    disabledReason: { domain: 7, code: 1, args: [] },
    risk: { domain: 7, code: 2, args: [] },
    riskLevel: "Warning"
  },
  {
    action: "ForceReleaseHandles",
    enabled: false,
    disabledReason: { domain: 7, code: 1, args: [] },
    risk: { domain: 7, code: 3, args: [] },
    riskLevel: "High"
  }
];

export const kernelLeakRecordedFrames = Object.freeze({
  observing: {
    status: "Observing",
    capturedAt: "1970-01-01T00:00:01+00:00",
    items: [],
    failure: "None",
    observation: {
      startedAt: "1970-01-01T00:00:00+00:00",
      elapsedSeconds: 1,
      sampleCount: 2,
      requiredSeconds: 2,
      requiredSampleCount: 3,
      previousRunFailure: null,
      restartPolicy: "NextInterval"
    },
    unavailableHolderCount: 0,
    unrecordedItemCount: 0
  },
  suspected: {
    status: "Suspected",
    capturedAt: "1970-01-01T00:00:02+00:00",
    items: [
      {
        state: "active",
        firstObservedAt: "1970-01-01T00:00:02+00:00",
        lastObservedAt: "1970-01-01T00:00:02+00:00",
        resourceKind: "ProcessHandles",
        currentValue: 1200,
        growthPerSecond: 100,
        observationSeconds: 2,
        sampleCount: 3,
        growth: 200,
        holder: {
          processId: 7,
          startKey: "42",
          image: { status: "Current", name: "recorded-holder", path: "C:\\recorded\\holder.exe", productName: "recorded-product" },
          services: { status: "Current", names: ["recorded-service"] }
        },
        unit: "count",
        actions: recordedActions
      }
    ],
    failure: "None",
    observation: {
      startedAt: "1970-01-01T00:00:00+00:00",
      elapsedSeconds: 2,
      sampleCount: 3,
      requiredSeconds: 2,
      requiredSampleCount: 3,
      previousRunFailure: null,
      restartPolicy: "NextInterval"
    },
    unavailableHolderCount: 0,
    unrecordedItemCount: 0
  },
  failedRun: {
    status: "Unavailable",
    capturedAt: "1970-01-01T00:00:01+00:00",
    items: [],
    failure: "CapacityExceeded",
    observation: {
      startedAt: "1970-01-01T00:00:00+00:00",
      elapsedSeconds: 1,
      sampleCount: 1,
      requiredSeconds: 2,
      requiredSampleCount: 3,
      previousRunFailure: null,
      restartPolicy: "NextInterval"
    },
    unavailableHolderCount: 0,
    unrecordedItemCount: 0
  },
  nextRun: {
    status: "Observing",
    capturedAt: "1970-01-01T00:00:02+00:00",
    items: [],
    failure: "None",
    observation: {
      startedAt: "1970-01-01T00:00:02+00:00",
      elapsedSeconds: 0,
      sampleCount: 1,
      requiredSeconds: 2,
      requiredSampleCount: 3,
      previousRunFailure: {
        startedAt: "1970-01-01T00:00:00+00:00",
        failedAt: "1970-01-01T00:00:01+00:00",
        failure: "CapacityExceeded"
      },
      restartPolicy: "NextInterval"
    },
    unavailableHolderCount: 0,
    unrecordedItemCount: 0
  },
  notObserved: {
    status: "NotObserved",
    capturedAt: null,
    items: [],
    failure: "None",
    observation: {
      startedAt: null,
      elapsedSeconds: 0,
      sampleCount: 0,
      requiredSeconds: 2,
      requiredSampleCount: 3,
      previousRunFailure: null,
      restartPolicy: "NextInterval"
    },
    unavailableHolderCount: 0,
    unrecordedItemCount: 0
  },
  sourceBeforeFirstFrame: {
    status: "Unavailable",
    capturedAt: "2026-10-27T19:00:01+00:00",
    items: [],
    failure: "SourceUnavailable",
    observation: {
      startedAt: null,
      elapsedSeconds: 0,
      sampleCount: 0,
      requiredSeconds: 600,
      requiredSampleCount: 8,
      previousRunFailure: null,
      restartPolicy: "NextInterval"
    },
    unavailableHolderCount: 0,
    unrecordedItemCount: 0
  }
});

const capturedAt = "2026-10-27T10:42:30+00:00";
const current = (name, path, productName) => ({ status: "Current", name, path, ...(productName === null ? {} : { productName }) });
const services = (...names) => ({ status: "Current", names });

// 生命周期默认是活动：本轮仍判出疑似，首次判出在本轮开始后不久，最近一次就是本帧。
const item = (holder, evidence, lifecycle = {}) => ({
  state: "active",
  firstObservedAt: "2026-10-27T09:55:00+00:00",
  lastObservedAt: capturedAt,
  ...lifecycle,
  resourceKind: "ProcessHandles",
  unit: "count",
  ...evidence,
  holder,
  actions: recordedActions
});

const gcuBridge = item({
  processId: 5124,
  startKey: "134058917412345678",
  image: current("GCUBridge", "C:\\Program Files\\OEM\\机械革命控制中心\\AiStoneService\\GCUBridge.exe", "GCUBridge"),
  services: services("GCUBridge")
}, { currentValue: 1464, growthPerSecond: 0.4183, observationSeconds: 3480, sampleCount: 117, growth: 1458 });

const nahimic = item({
  processId: 4388,
  startKey: "134058917398765432",
  image: current("NahimicService", "C:\\WINDOWS\\System32\\NahimicService.exe", "NahimicService"),
  services: services("NahimicService")
}, { currentValue: 1071, growthPerSecond: 0.3076, observationSeconds: 3480, sampleCount: 117, growth: 1069 });

// 一个 svchost 承载多个服务。
const sharedServiceHost = item({
  processId: 1876,
  startKey: "134058916901234567",
  image: current("svchost", "C:\\WINDOWS\\System32\\svchost.exe", "Microsoft® Windows® Operating System"),
  services: services("Appinfo", "ProfSvc", "Schedule", "Winmgmt")
}, { currentValue: 2310, growthPerSecond: 0.1517, observationSeconds: 1890, sampleCount: 64, growth: 287 }, { firstObservedAt: "2026-10-27T10:15:00+00:00" });

// 归因时进程已退出（或 PID 已被复用）：映像和服务都是 ProcessEnded，不带值。
const endedHolder = item({
  processId: 9140,
  startKey: "134058919987654321",
  image: { status: "ProcessEnded" },
  services: { status: "ProcessEnded", names: [] }
}, { currentValue: 1302, growthPerSecond: 0.2214, observationSeconds: 1200, sampleCount: 41, growth: 266 });

// 映像信息读不到（受保护进程或产品名读取失败）：服务表照常读到，没有对应服务。
const protectedHolder = item({
  processId: 3312,
  startKey: "134058917005550001",
  image: { status: "Unavailable" },
  services: services()
}, { currentValue: 1988, growthPerSecond: 0.1032, observationSeconds: 2610, sampleCount: 88, growth: 269 }, { firstObservedAt: "2026-10-27T10:25:00+00:00" });

// 文件不带产品名（后端省略 productName），也没有对应服务。
const noProductHolder = item({
  processId: 11872,
  startKey: "134058921100000042",
  image: current("tray-helper", "D:\\Tools\\tray-helper\\tray-helper.exe", null),
  services: services()
}, { currentValue: 1136, growthPerSecond: 0.0911, observationSeconds: 1500, sampleCount: 51, growth: 137 }, { firstObservedAt: "2026-10-27T10:30:00+00:00" });

// 进程已退出后转为历史：最近一次判出停在退出前。
const endedHistory = { ...endedHolder, state: "history", firstObservedAt: "2026-10-27T10:00:00+00:00", lastObservedAt: "2026-10-27T10:12:00+00:00" };
// 判定不再成立后转为历史（进程还在）。
const nahimicHistory = { ...nahimic, state: "history", firstObservedAt: "2026-10-27T10:05:00+00:00", lastObservedAt: "2026-10-27T10:20:00+00:00" };

const observation = (fields) => ({
  startedAt: "2026-10-27T09:44:30+00:00",
  elapsedSeconds: 3480,
  sampleCount: 117,
  requiredSeconds: 600,
  requiredSampleCount: 8,
  previousRunFailure: null,
  restartPolicy: "NextInterval",
  ...fields
});

const report = (status, fields) => ({
  status, capturedAt, items: [], failure: "None", observation: observation({}), unavailableHolderCount: 0, unrecordedItemCount: 0, ...fields
});

// 上一轮：10:20 开始，10:31:30 观察的进程数超过容量上限而失败；下一个采样间隔（10:32）开始新一轮。
const capacityFailure = { startedAt: "2026-10-27T10:20:00+00:00", failedAt: "2026-10-27T10:31:30+00:00", failure: "CapacityExceeded" };

/** 场景名 → 后端帧。名字即 mock 场景 kernelLeakReport 的取值。 */
export const kernelLeakReportFrames = Object.freeze({
  // 观察块没有运行（关闭、已停止）：没有采集时间，进度为零，要求取计划值。
  notObserved: report("NotObserved", {
    capturedAt: null,
    observation: observation({ startedAt: null, elapsedSeconds: 0, sampleCount: 0 })
  }),
  // 观察计划还没发布（服务刚启动、块还没运行）：要求也是 0。
  notObservedBeforePlan: report("NotObserved", {
    capturedAt: null,
    observation: observation({ startedAt: null, elapsedSeconds: 0, sampleCount: 0, requiredSeconds: 0, requiredSampleCount: 0 })
  }),
  // 本轮观察 2 分 30 秒、6 个样本，还不够判定。
  observing: report("Observing", {
    observation: observation({ startedAt: "2026-10-27T10:40:00+00:00", elapsedSeconds: 150, sampleCount: 6 })
  }),
  // 刚启动，还没采样。
  "observing-not-started": report("Observing", {
    capturedAt: null,
    observation: observation({ startedAt: null, elapsedSeconds: 0, sampleCount: 0 })
  }),
  // 观察已足够，没有疑似项。
  empty: report("Empty", {}),
  // 首次判出时间升序；已退出的进程留下来是历史条目，排在第三位，显示时排到活动条目后面。
  suspected: report("Suspected", { items: [gcuBridge, nahimic, endedHistory, sharedServiceHost, protectedHolder, noProductHolder] }),
  // 本轮没有活动条目，但以前判出过的留在历史里。
  "empty-with-history": report("Empty", { items: [endedHistory, nahimicHistory] }),
  // 观察没有运行、本轮失败时条目照常保留，状态不变。
  "notObserved-with-history": report("NotObserved", {
    capturedAt: null,
    observation: observation({ startedAt: null, elapsedSeconds: 0, sampleCount: 0 }),
    items: [gcuBridge, endedHistory]
  }),
  "unavailable-with-history": report("Unavailable", {
    failure: "SourceUnavailable",
    observation: observation({ startedAt: "2026-10-27T10:41:00+00:00", elapsedSeconds: 60, sampleCount: 3 }),
    items: [gcuBridge, endedHistory]
  }),
  // 条目容量已满：新判出的 2 项没有记录。
  "suspected-unrecorded": report("Suspected", { items: [gcuBridge, nahimic], unrecordedItemCount: 2 }),
  "suspected-unreadable": report("Suspected", { items: [gcuBridge, nahimic], unavailableHolderCount: 3 }),
  // 数据源在第一帧之前就不可用：没有开始过的一轮，也就没有失败的一轮；失败只在报告的 failure 上。
  "unavailable-before-first-frame": report("Unavailable", {
    capturedAt: "2026-10-27T10:40:01+00:00",
    failure: "SourceUnavailable",
    observation: observation({ startedAt: null, elapsedSeconds: 0, sampleCount: 0 })
  }),
  "unavailable-source": report("Unavailable", {
    failure: "SourceUnavailable",
    observation: observation({ startedAt: "2026-10-27T10:41:00+00:00", elapsedSeconds: 60, sampleCount: 3 })
  }),
  // 本轮失败（溢出）：当前报告就是这次失败，进度停在失败那一轮。
  "unavailable-capacity": report("Unavailable", {
    capturedAt: "2026-10-27T10:31:30+00:00",
    failure: "CapacityExceeded",
    observation: observation({ startedAt: capacityFailure.startedAt, elapsedSeconds: 690, sampleCount: 24 })
  }),
  "unavailable-invalid": report("Unavailable", {
    failure: "InvalidObservation",
    observation: observation({ startedAt: "2026-10-27T10:30:00+00:00", elapsedSeconds: 750, sampleCount: 26 })
  }),
  // 溢出后的下一轮：新一轮的进度，带着上一轮的失败和它的起止时间。
  "observing-after-failure": report("Observing", {
    capturedAt: "2026-10-27T10:35:30+00:00",
    observation: observation({
      startedAt: "2026-10-27T10:32:00+00:00", elapsedSeconds: 210, sampleCount: 8, previousRunFailure: capacityFailure
    })
  }),
  // 下一轮的第一帧就是不可用帧：这一帧开始了新一轮（开始时间是它的采集时间），显示这次失败，同时保留上一轮的失败。
  "unavailable-after-failure": report("Unavailable", {
    capturedAt: "2026-10-27T10:32:00+00:00",
    failure: "SourceUnavailable",
    observation: observation({
      startedAt: "2026-10-27T10:32:00+00:00", elapsedSeconds: 0, sampleCount: 0, previousRunFailure: capacityFailure
    })
  }),
  // 不合合同的帧（内部样本种类 FileCachePoolBytes 不对外发布）：解码失败，按读取失败显示。
  invalid: report("Suspected", { items: [{ ...gcuBridge, resourceKind: "FileCachePoolBytes" }] })
});

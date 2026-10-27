// 内核泄漏报告的线上帧（/api/kernel-leaks/subscribe 的 value），模拟后端和解码器测试共用。合同 v2（codex/kernel-leak-report-v2 db67b1ca）。
// kernelLeakRecordedFrames 是后端交接里记录的线上例子原样照抄（final-message.md「Recorded wire examples」），解码器测试逐个解码。
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
    unavailableHolderCount: 0
  },
  suspected: {
    status: "Suspected",
    capturedAt: "1970-01-01T00:00:02+00:00",
    items: [
      {
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
    unavailableHolderCount: 0
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
    unavailableHolderCount: 0
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
    unavailableHolderCount: 0
  },
  disabled: {
    status: "Disabled",
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
    unavailableHolderCount: 0
  }
});

const capturedAt = "2026-10-27T10:42:30+00:00";
const current = (name, path, productName) => ({ status: "Current", name, path, ...(productName === null ? {} : { productName }) });
const services = (...names) => ({ status: "Current", names });

const item = (holder, evidence) => ({
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
}, { currentValue: 2310, growthPerSecond: 0.1517, observationSeconds: 1890, sampleCount: 64, growth: 287 });

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
}, { currentValue: 1988, growthPerSecond: 0.1032, observationSeconds: 2610, sampleCount: 88, growth: 269 });

// 文件不带产品名（后端省略 productName），也没有对应服务。
const noProductHolder = item({
  processId: 11872,
  startKey: "134058921100000042",
  image: current("tray-helper", "D:\\Tools\\tray-helper\\tray-helper.exe", null),
  services: services()
}, { currentValue: 1136, growthPerSecond: 0.0911, observationSeconds: 1500, sampleCount: 51, growth: 137 });

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
  status, capturedAt, items: [], failure: "None", observation: observation({}), unavailableHolderCount: 0, ...fields
});

// 上一轮：10:20 开始，10:31:30 观察的进程数超过容量上限而失败；下一个采样间隔（10:32）开始新一轮。
const capacityFailure = { startedAt: "2026-10-27T10:20:00+00:00", failedAt: "2026-10-27T10:31:30+00:00", failure: "CapacityExceeded" };

/** 场景名 → 后端帧。名字即 mock 场景 kernelLeakReport 的取值。 */
export const kernelLeakReportFrames = Object.freeze({
  // 配置关闭了观察：没有采集时间，进度为零。
  disabled: report("Disabled", {
    capturedAt: null,
    observation: observation({ startedAt: null, elapsedSeconds: 0, sampleCount: 0 })
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
  suspected: report("Suspected", { items: [gcuBridge, nahimic, sharedServiceHost, endedHolder, protectedHolder, noProductHolder] }),
  "suspected-unreadable": report("Suspected", { items: [gcuBridge, nahimic], unavailableHolderCount: 3 }),
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
  // 下一轮的第一次采样也失败了：显示这次失败，同时保留上一轮的失败。
  "unavailable-after-failure": report("Unavailable", {
    capturedAt: null,
    failure: "SourceUnavailable",
    observation: observation({
      startedAt: "2026-10-27T10:32:00+00:00", elapsedSeconds: 0, sampleCount: 0, previousRunFailure: capacityFailure
    })
  }),
  // 不合合同的帧（内部样本种类 FileCachePoolBytes 不对外发布）：解码失败，按读取失败显示。
  invalid: report("Suspected", { items: [{ ...gcuBridge, resourceKind: "FileCachePoolBytes" }] })
});

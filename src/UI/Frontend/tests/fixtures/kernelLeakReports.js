// 内核泄漏报告的线上帧（/api/kernel-leaks/subscribe 的 value），模拟后端和解码器测试共用。
// 形状按后端第 1 期（codex/kernel-leak-report 9f4175fe）的 wire-example.json：枚举是字符串，名称、路径、产品名可为 null，
// 映像和服务信息各带自己的状态。持有者取自本机 10-27 的真实例子（GCUBridge 握着 1,464 个句柄、NahimicService 1,071 个），
// 其余几项摆出后端会发的其他形状：多个服务名、进程已结束、映像信息读不到、文件没有产品名。

const capturedAt = "2026-10-27T10:42:30+00:00";

const gcuBridge = {
  resourceKind: "ProcessHandles",
  currentValue: 1464,
  growthPerSecond: 0.4183,
  observationSeconds: 3480,
  sampleCount: 117,
  growth: 1458,
  holder: {
    processId: 5124,
    startKey: "134058917412345678",
    name: "GCUBridge",
    imagePath: "C:\\Program Files\\OEM\\机械革命控制中心\\AiStoneService\\GCUBridge.exe",
    productName: "GCUBridge",
    serviceNames: ["GCUBridge"],
    imageStatus: "Current",
    serviceStatus: "Current"
  }
};

const nahimic = {
  resourceKind: "ProcessHandles",
  currentValue: 1071,
  growthPerSecond: 0.3076,
  observationSeconds: 3480,
  sampleCount: 117,
  growth: 1069,
  holder: {
    processId: 4388,
    startKey: "134058917398765432",
    name: "NahimicService",
    imagePath: "C:\\WINDOWS\\System32\\NahimicService.exe",
    productName: "NahimicService",
    serviceNames: ["NahimicService"],
    imageStatus: "Current",
    serviceStatus: "Current"
  }
};

// 一个 svchost 承载多个服务。
const sharedServiceHost = {
  resourceKind: "ProcessHandles",
  currentValue: 2310,
  growthPerSecond: 0.1517,
  observationSeconds: 1890,
  sampleCount: 64,
  growth: 287,
  holder: {
    processId: 1876,
    startKey: "134058916901234567",
    name: "svchost",
    imagePath: "C:\\WINDOWS\\System32\\svchost.exe",
    productName: "Microsoft® Windows® Operating System",
    serviceNames: ["Appinfo", "ProfSvc", "Schedule", "Winmgmt"],
    imageStatus: "Current",
    serviceStatus: "Current"
  }
};

// 归因时进程已退出：后端不再给名称和路径，服务信息也读不到。
const endedHolder = {
  resourceKind: "ProcessHandles",
  currentValue: 1302,
  growthPerSecond: 0.2214,
  observationSeconds: 1200,
  sampleCount: 41,
  growth: 266,
  holder: {
    processId: 9140,
    startKey: "134058919987654321",
    name: null,
    imagePath: null,
    productName: null,
    serviceNames: [],
    imageStatus: "ProcessEnded",
    serviceStatus: "Unavailable"
  }
};

// 映像路径读不到（受保护进程）：服务表照常读到，没有对应服务。
const protectedHolder = {
  resourceKind: "ProcessHandles",
  currentValue: 1988,
  growthPerSecond: 0.1032,
  observationSeconds: 2610,
  sampleCount: 88,
  growth: 269,
  holder: {
    processId: 3312,
    startKey: "134058917005550001",
    name: null,
    imagePath: null,
    productName: null,
    serviceNames: [],
    imageStatus: "Unavailable",
    serviceStatus: "Current"
  }
};

// 文件不带产品名，也没有对应服务。
const noProductHolder = {
  resourceKind: "ProcessHandles",
  currentValue: 1136,
  growthPerSecond: 0.0911,
  observationSeconds: 1500,
  sampleCount: 51,
  growth: 137,
  holder: {
    processId: 11872,
    startKey: "134058921100000042",
    name: "tray-helper",
    imagePath: "D:\\Tools\\tray-helper\\tray-helper.exe",
    productName: null,
    serviceNames: [],
    imageStatus: "Current",
    serviceStatus: "Current"
  }
};

const report = (status, fields) => ({
  status, capturedAt, items: [], failure: "None", unavailableHolderCount: 0, ...fields
});

/** 场景名 → 后端帧。名字即 mock 场景 kernelLeakReport 的取值。 */
export const kernelLeakReportFrames = Object.freeze({
  // 观察足够长，没有疑似项。
  empty: report("Empty", {}),
  // 刚开始观察（窗口还不够判定）：后端同样发 Empty，线上与上一种没有区别。
  observing: report("Empty", { capturedAt: "2026-10-27T10:00:30+00:00" }),
  "unavailable-not-observed": report("Unavailable", { capturedAt: null, failure: "NotObserved" }),
  "unavailable-source": report("Unavailable", { failure: "SourceUnavailable", unavailableHolderCount: 214 }),
  "unavailable-capacity": report("Unavailable", { capturedAt: null, failure: "CapacityExceeded" }),
  "unavailable-invalid": report("Unavailable", { capturedAt: null, failure: "InvalidObservation" }),
  suspected: report("Suspected", { items: [gcuBridge, nahimic, sharedServiceHost, endedHolder, protectedHolder, noProductHolder] }),
  "suspected-unreadable": report("Suspected", { items: [gcuBridge, nahimic], unavailableHolderCount: 3 }),
  // 不合合同的帧（后端枚举里不对外发布的资源种类）：解码失败，按读取失败显示。
  invalid: report("Suspected", { items: [{ ...gcuBridge, resourceKind: "FileCachePoolBytes" }] })
});

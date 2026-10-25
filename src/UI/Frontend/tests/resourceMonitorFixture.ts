import type { ResourceMonitorWireSnapshot } from
  "../src/api/resourceBreakdownWire.ts";

export const capturedAt = "2026-08-22T15:20:30.000Z";

export function readyResourceMonitorWire(): ResourceMonitorWireSnapshot {
  return {
    version: 10,
    capturedAt,
    breakdown: {
      version: 8,
      capturedAt,
      softwareCatalog: [["software:test", "Test App", "other", "other"]],
      bars: [{
        metricId: "memory.usage",
        label: "内存占用",
        unit: "B",
        scaleMode: "capacity",
        totalValue: 64,
        capacityValue: 1024,
        totalSystemPercent: 6.25,
        software: [[
          0,
          64,
          6.25,
          1,
          125,
          [[
            42,
            "worker.exe",
            "C:\\Apps\\worker.exe",
            64,
            6.25,
            100,
            "USER",
            "x64",
            "process",
            125,
            "134325768300000000"
          ]]
        ]]
      }]
    }
  };
}

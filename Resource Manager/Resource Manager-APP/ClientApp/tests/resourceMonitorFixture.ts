import type { ResourceMonitorWireSnapshot } from
  "../src/api/resourceBreakdownWire.ts";

export const capturedAt = "2026-08-22T15:20:30.000Z";

export function readyResourceMonitorWire(): ResourceMonitorWireSnapshot {
  return {
    version: 8,
    capturedAt,
    breakdown: {
      version: 7,
      capturedAt,
      softwareCatalog: [["software:test", "Test App", "Other", "一般应用"]],
      bars: [{
        metricId: "memory.usage",
        label: "内存占用",
        unit: "B",
        scaleMode: "capacity",
        totalValue: 64,
        capacityValue: 1024,
        totalSystemPercent: 6.25,
        totalDisplay: "64 B / 1 KB",
        software: [[
          0,
          64,
          6.25,
          "64 B",
          1,
          125,
          [[
            42,
            "worker.exe",
            "C:\\Apps\\worker.exe",
            64,
            6.25,
            100,
            "64 B",
            "USER",
            "x64",
            "process",
            125,
            "134325768300000000"
          ]]
        ]]
      }]
    },
    table: {
      capturedAt,
      columns: [{
        id: "name",
        label: "名称",
        unit: "",
        visible: true,
        sortable: true,
        width: 260
      }],
      rows: [{
        id: "summary:total",
        parentId: null,
        depth: 0,
        kind: "summary",
        name: "总占用",
        status: "当前采样",
        softwareName: null,
        softwareId: null,
        processId: null,
        processStartKey: null,
        processCount: 0,
        processIds: [],
        processNames: [],
        executablePaths: [],
        impactScore: 0,
        values: {},
        sortKeys: {}
      }],
      sort: { columnId: "impact", direction: "desc" },
      viewMode: "software"
    }
  };
}

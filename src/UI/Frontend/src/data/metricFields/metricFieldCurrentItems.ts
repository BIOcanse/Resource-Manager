import type { PushValueItemPolicy } from "../../frontendRuntime/push/PushCurrentItems.ts";
import type { MetricFieldDevice, MetricFieldQuery } from "./metricFieldSource.ts";

export const metricFieldCurrentItems: PushValueItemPolicy<MetricFieldQuery, readonly MetricFieldDevice[]> = {
  absentMeansEmpty: () => true,
  includes: (query, key) => query.fields.includes((JSON.parse(key) as string[])[1]),
  isPending: (query, keys) => query.fields.some(field => !keys.has(JSON.stringify([null, field]))),
  split: (query, devices) => [...devices.flatMap(device => device.fields.map(field => ({
    key: JSON.stringify([device.deviceIdentity, field.name]), capturedAt: field.observedAt,
    value: [{ ...device, fields: [field] }]
  }))), ...query.fields.map(field => ({
    key: JSON.stringify([null, field]), capturedAt: null, value: []
  }))],
  combine: (_query, values) => {
    const devices = new Map<string, MetricFieldDevice>();
    for (const fragment of values) for (const device of fragment) {
      const previous = devices.get(device.deviceIdentity);
      devices.set(device.deviceIdentity, { ...device, fields: [...previous?.fields ?? [], ...device.fields] });
    }
    return [...devices.values()];
  }
};

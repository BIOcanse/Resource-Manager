import {
  requireArray, requireBoolean, requireNonEmptyString, requireNonNegativeSafeInteger,
  requireNullable, requireOneOf, requireRecord, ResponseDecodeError
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { DeviceTopologyIdentityFact, DeviceTopologyMachineIdentity } from "../../types.ts";

export function decodeMachineIdentity(value: unknown, path: string): DeviceTopologyMachineIdentity {
  const record = requireRecord(value, path);
  const evidencePath = `${path}.evidence`;
  const evidence = requireRecord(record.evidence, evidencePath);
  return {
    formFactor: requireOneOf(record.formFactor, `${path}.formFactor`,
      ["mobile_workstation", "laptop", "workstation", "desktop", "unknown"] as const),
    evidence: {
      chassisTypes: fact(evidence.chassisTypes, `${evidencePath}.chassisTypes`, (value, path) =>
        requireArray(value, path).map((item, index) => {
          const code = requireNonNegativeSafeInteger(item, `${path}[${index}]`);
          if (code > 127) throw new ResponseDecodeError(`${path}[${index}]`, "SMBIOS chassis type code");
          return code;
        })),
      systemBatteryPresent: fact(evidence.systemBatteryPresent, `${evidencePath}.systemBatteryPresent`, requireBoolean),
      cpuModels: fact(evidence.cpuModels, `${evidencePath}.cpuModels`, models),
      gpuModels: fact(evidence.gpuModels, `${evidencePath}.gpuModels`, models),
      gpuAttachments: fact(evidence.gpuAttachments, `${evidencePath}.gpuAttachments`, (value, path) =>
        requireArray(value, path).map((value, index) => {
          const itemPath = `${path}[${index}]`;
          const row = requireRecord(value, itemPath);
          return {
            model: requireNonEmptyString(row.model, `${itemPath}.model`),
            isDetachable: requireNullable(row.isDetachable, `${itemPath}.isDetachable`, requireBoolean),
            adapterTypeFlags: requireNullable(row.adapterTypeFlags, `${itemPath}.adapterTypeFlags`, (value, path) => {
              const flags = requireNonNegativeSafeInteger(value, path);
              if (flags > 0xffffffff) throw new ResponseDecodeError(path, "uint32 adapter type flags");
              return flags;
            }),
            attachmentKind: requireNullable(row.attachmentKind, `${itemPath}.attachmentKind`, (value, path) =>
              requireOneOf(value, path, ["graphics_dock"] as const)),
            evidenceSource: requireNullable(row.evidenceSource, `${itemPath}.evidenceSource`, requireNonEmptyString)
          };
        })),
      workstationComponents: requireArray(evidence.workstationComponents, `${evidencePath}.workstationComponents`)
        .map((item, index) => {
          const matchPath = `${evidencePath}.workstationComponents[${index}]`;
          const match = requireRecord(item, matchPath);
          return {
            component: requireOneOf(match.component, `${matchPath}.component`, ["cpu", "gpu"] as const),
            model: requireNonEmptyString(match.model, `${matchPath}.model`),
            ruleId: requireNonEmptyString(match.ruleId, `${matchPath}.ruleId`)
          };
        })
    }
  };
}

function models(value: unknown, path: string): string[] {
  return requireArray(value, path).map((item, index) => requireNonEmptyString(item, `${path}[${index}]`));
}

function fact<T>(value: unknown, path: string, decode: (value: unknown, path: string) => T): DeviceTopologyIdentityFact<T> {
  const record = requireRecord(value, path);
  const decoded = requireNullable(record.value, `${path}.value`, decode);
  const reason = requireNullable(record.reason, `${path}.reason`, requireNonEmptyString);
  if ((decoded === null) !== (reason !== null)) {
    throw new ResponseDecodeError(path, "available value with null reason, or null value with unavailable reason");
  }
  return { value: decoded, reason };
}

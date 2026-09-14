import {
  defineResponseDecoder,
  requireBoolean,
  requireNonEmptyString,
  requireRecord
} from "../../frontendRuntime/request/ResponseDecoder.ts";
import type { BackendStartupCapabilities } from "../../types.ts";

export const runtimeCapabilitiesDecoder = defineResponseDecoder<BackendStartupCapabilities>(
  "runtime.capabilities.v1",
  (value) => {
    const record = requireRecord(value);
    return Object.freeze({
      profileId: requireNonEmptyString(record.profileId, "$.profileId"),
      readOnly: requireBoolean(record.readOnly, "$.readOnly"),
      mutablePersistence: requireBoolean(record.mutablePersistence, "$.mutablePersistence"),
      legacyPersistenceImport: requireBoolean(record.legacyPersistenceImport, "$.legacyPersistenceImport"),
      gpuLaunchInterceptionReconciliation: requireBoolean(
        record.gpuLaunchInterceptionReconciliation,
        "$.gpuLaunchInterceptionReconciliation"),
      runtimeEffectOwners: requireBoolean(record.runtimeEffectOwners, "$.runtimeEffectOwners"),
      publicServiceCoordination: requireBoolean(
        record.publicServiceCoordination,
        "$.publicServiceCoordination"),
      optimizationRuntime: requireBoolean(record.optimizationRuntime, "$.optimizationRuntime"),
      sharedResourceOwnership: requireBoolean(
        record.sharedResourceOwnership,
        "$.sharedResourceOwnership")
    });
  });

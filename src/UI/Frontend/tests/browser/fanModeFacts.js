export function fanModeFacts(capabilityIds, { firmware = true, software = true, pointKind = "xy", allFans = false } = {}) {
  const reason = code => ({ code });
  return {
    firmwareCurveKind: firmware ? pointKind : null, softwareTakeoverCoversAllFans: allFans,
    fanManagementModes: [
      { id: "firmware-management", available: true, unavailableKind: null, unavailableReason: null, items: [] },
      { id: "firmware-write", available: firmware, unavailableKind: firmware ? null : "platform",
        unavailableReason: firmware ? null : reason("fan-firmware-curve-unsupported"), items: [{ capabilityId: "fan.curve", curveExecution: "firmware" }] },
      { id: "software-management", available: software, unavailableKind: software ? null : "platform",
        unavailableReason: software ? null : reason("fan-software-management-unsupported"),
        items: capabilityIds.map(capabilityId => ({ capabilityId, curveExecution: capabilityId === "fan.curve" ? "software" : null })) }
    ]
  };
}

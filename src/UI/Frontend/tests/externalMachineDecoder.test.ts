import assert from "node:assert/strict";
import { decodeExternalMachines } from "../src/features/deviceTopology/externalMachineDecoder.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";

const peer = {
  localLinkDeviceId: "USB4\\LOCAL_LINK\\1", peerIdentity: null, formFactor: "workstation", model: null,
  components: [{ component: "soc", model: null }],
  evidence: { source: "windows-usb4net", compatibleId: "USB4\\USB4NET", mediaConnectStateRaw: 1,
    peerPropertiesReason: "windows-peer-properties-unavailable" }
};
assert.equal(decodeExternalMachines(null, "$"), null);
assert.deepEqual(decodeExternalMachines([], "$"), []);
assert.deepEqual(decodeExternalMachines([peer], "$"), [peer]);
for (const invalid of [
  undefined,
  [{ ...peer, peerIdentity: undefined }],
  [{ ...peer, components: [] }],
  [{ ...peer, components: [{ component: "gpu", model: null }] }],
  [{ ...peer, components: [peer.components[0], peer.components[0]] }],
  [{ ...peer, formFactor: "graphics_dock" }],
  [{ ...peer, evidence: { ...peer.evidence, mediaConnectStateRaw: 0 } }],
  [{ ...peer, evidence: { ...peer.evidence, compatibleId: "USB4\\ROUTER" } }],
  [peer, peer]
]) assert.throws(() => decodeExternalMachines(invalid, "$"), ResponseDecodeError);
console.log("external machine decoder: null/empty, independent workstation with one unknown SoC, invalid evidence rejected");

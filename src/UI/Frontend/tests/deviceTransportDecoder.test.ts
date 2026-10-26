import assert from "node:assert/strict";
import { decodeTransport } from "../src/features/deviceTopology/transportDecoder.ts";
import { ResponseDecodeError } from "../src/frontendRuntime/request/ResponseDecoder.ts";

const evidence = [{ source: "setupapi-cfgmgr32", deviceId: "arbitrary-id", depth: 0,
  enumeratorName: "PCI", busTypeGuid: "c8ebdfb0-b510-11d0-80e5-00a0c92542e3", service: "stornvme", parentDeviceId: null }];
const known = { code: "nvme", reason: null, ruleId: "service-stornvme", evidence };
assert.deepEqual(decodeTransport(known, "$"), known);
const unknown = { code: null, reason: "unrecognized-source", ruleId: null, evidence };
// The consumer preserves authoritative null even when a raw service would tempt it to infer NVMe.
assert.deepEqual(decodeTransport(unknown, "$"), unknown);
for (const code of ["hd_audio", "bluetooth", "ucsi", "nvme", "ahci", "i2c_hid", "acpi", "pci", "usb", "usb4"])
  assert.equal(decodeTransport({ ...known, code }, "$").code, code);
for (const invalid of [undefined, null, { ...known, code: "PCI Express" }, { ...known, code: "unknown" },
  { ...known, reason: "unavailable" }, { ...known, ruleId: null }, { ...known, evidence: [] },
  { ...unknown, reason: null }, { ...unknown, ruleId: "inferred" },
  { ...known, evidence: [{ ...evidence[0], source: "device-name" }] },
  { ...known, evidence: [{ ...evidence[0], depth: 1 }] },
  { ...known, evidence: [{ ...evidence[0], busTypeGuid: "PCI" }] }])
  assert.throws(() => decodeTransport(invalid, "$"), ResponseDecodeError);
console.log("transport decoder: fixed codes, typed source evidence, authoritative null, invalid contracts rejected");

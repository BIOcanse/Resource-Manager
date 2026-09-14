import assert from "node:assert/strict";
import { resolveGpuDeviceName } from "../src/gpuScheduling/gpuDeviceName.ts";

assert.equal(
  resolveGpuDeviceName(0, " NVIDIA GeForce RTX 5090 ", "Microsoft Basic Render Driver"),
  "NVIDIA GeForce RTX 5090");
assert.equal(
  resolveGpuDeviceName(1, null, " AMD Radeon Graphics "),
  "AMD Radeon Graphics");
assert.equal(resolveGpuDeviceName(2, "", ""), "GPU2");

console.log("gpu device name tests passed");

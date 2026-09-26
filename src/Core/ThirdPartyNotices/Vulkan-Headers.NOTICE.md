# Vulkan-Headers

Resource Manager's native Vulkan placement layer uses the unmodified C/loader headers from KhronosGroup/Vulkan-Headers `v1.4.341` (tag object `0d3f509e57041fbd073a1ee84cd9ccd36d148446`). Source: https://github.com/KhronosGroup/Vulkan-Headers/tree/v1.4.341

The vendored subset includes the C core/platform/loader headers and the video definitions included by the core header. These are build-time inputs, not a bundled Vulkan runtime, graphics driver or SDK. Applicable notices remain in each original source; original repository license guidance and both Apache-2.0 and MIT texts accompany this notice. The loader-interface header is Apache-2.0; other selected headers carry their original SPDX terms. The Resource Manager project license does not replace those terms.

We gratefully acknowledge The Khronos Group, Valve, LunarG, and every Vulkan-Headers contributor. Their open specifications, maintained interfaces, and generous licensing make this independent project possible. Thank you for making this work accessible to individual developers.

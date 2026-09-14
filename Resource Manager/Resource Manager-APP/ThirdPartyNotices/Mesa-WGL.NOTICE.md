# Microsoft OpenGL ICD Declarations Maintained In Mesa

Resource Manager uses the unmodified `src/gallium/frontends/wgl/gldrv.h`
from Mesa commit `d10ec46ad5dfbb69e5f2c5f6aad0c7d8483760a5`.
Source mirror: https://github.com/chaotic-cx/mesa-mirror/blob/d10ec46ad5dfbb69e5f2c5f6aad0c7d8483760a5/src/gallium/frontends/wgl/gldrv.h

The original header SHA-256 is
`ADDE554BD382DFB583937C72DECF2F5E7DDA16F8E1A69AAD1218BE8C0ABD24F4`.
It is retained in `Native/GpuPlacementShim/third_party/mesa-wgl/gldrv.h`.
The generated `OpenGlCoreEntries.inc` retains its original copyright and
permission notice. These are compile-time declarations, not a bundled Mesa
renderer or a replacement graphics driver. The header's Microsoft MIT notice
governs these declarations; the project license does not replace it.

We gratefully thank Microsoft for publishing these interface declarations,
the Mesa contributors for maintaining this accessible reference, and MinHook's
authors and contributors for the interception foundation. Their work makes
this independent project's native graphics research and implementation possible.

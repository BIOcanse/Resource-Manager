# Native compiler runtime acknowledgements

Thank you to the Free Software Foundation and GCC/libstdc++ contributors,
MinGW-w64 and winpthreads contributors, Brecht Sanders and WinLibs, and the Zig
contributors. Their work makes this application's native Windows integration
possible. We retain their acknowledgements regardless of whether a particular
license requires them.

We also thank Hewlett-Packard and Silicon Graphics for the libstdc++ heritage
code, and Lockless Inc. for the code incorporated in winpthreads. Their complete
permission/attribution blocks are retained in `LibStdCpp.NOTICE.txt` and
`Winpthreads.Installed-Header-NOTICE.txt`, extracted without rewriting from the
installed toolchain's vector and pthread.h notice headers.

The 2026-09-13 beta uses GCC 15.2.0, WinLibs x86_64-ucrt-posix-seh r7, MinGW-w64
14.0.0 and Zig 0.16.0. GPU native helpers statically link their GCC/C++/thread
runtime dependencies. The runtime does not require a compiler installation or
its PATH. Windows system import libraries still resolve to installed Windows
components. Zig standard-library/runtime code is used by NativeCore and the
native adapter library.

Retained upstream license texts:

- `GCC.GPL-3.0.txt` and `GCC.Runtime-Exception.txt`: GCC runtime code, GPLv3 with
  the GCC Runtime Library Exception 3.1. The exception is additional permission
  for eligible compilation of independent modules, not relicensing all GCC code.
- `MinGW-w64.Runtime.txt`: complete runtime attribution and licenses.
- `MinGW-w64.LICENSE.txt`: original root license with component exceptions.
- `Winpthreads.LICENSE.txt`: complete winpthreads attribution and licenses.
- `Zig.LICENSE.txt`: original compiler distribution MIT license.

Sources, pinned to the toolchain releases:

- https://github.com/gcc-mirror/gcc/tree/releases/gcc-15.2.0
- https://github.com/mingw-w64/mingw-w64/tree/v14.0.0
- https://gcc.gnu.org/onlinedocs/libstdc++/manual/license.html
- https://www.gnu.org/licenses/gcc-exception-3.1.html
- https://winlibs.com/
- https://ziglang.org/

This package does not redistribute GCC, LLVM, MSBuild, Node.js or their complete
tool distributions. Their build-time contributions remain in our thanks list.

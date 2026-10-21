# Native runtimes in ResourceManager.ReadWriteCore

This SDK includes ResourceManager.NativeCore.dll, ResourceManager.AdlxBridge.dll
and ResourceManager.DumpAnalysis.exe. SubscriptionCore obtains them through its
ReadWriteCore dependency.

NativeCore uses Zig standard-library/runtime code. The retained original
license is Zig.LICENSE.txt, Copyright (c) Zig contributors.

AdlxBridge is a C bridge compiled with GCC. Its binary carries the compiler
marker GCC 15.2.0, MinGW-W64 x86_64-ucrt-posix-seh, Brecht Sanders r7.
Its build script selects -static-libgcc. GCC runtime licensing material is
retained in GCC.GPL-3.0.txt and GCC.Runtime-Exception.txt. The runtime exception
provides additional permission subject to its stated conditions.

MinGW-w64 runtime and component licensing material is retained in
MinGW-w64.LICENSE.txt and MinGW-w64.Runtime.txt. These original texts are
provided without rewriting. We thank the GCC, MinGW-w64, WinLibs and Zig
contributors for their work.

Windows system imports resolve to installed Windows components. The AMD
amdadlx64.dll driver library is loaded externally; it is not included in this
SDK. This SDK does not include the complete GCC or Zig compiler distributions.

DumpAnalysis is built from this project's C++ source using MinGW-w64/WinLibs
with static GCC runtime linkage. The same original GCC runtime exception and
MinGW-w64 licensing materials apply. GNU C++ library notices are retained in
LibStdCpp.NOTICE.txt alongside the GPL and Runtime Exception texts.
Its DbgEng interfaces come from the
toolchain's Windows platform headers. An existing Microsoft x64 DbgEng engine
and its companion libraries must be supplied explicitly; they are not bundled.

This notice describes only these three SDK binaries. It does not describe the
separate product's C++ GPU helpers, WebView2, WinUI, Vulkan or Mesa components.

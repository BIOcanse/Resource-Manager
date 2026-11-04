# Control Tree SDK pin

RM consumes Control Tree SDK 0.1.0-dev.1, source commit
079a66e26a41d1fd2fa33a69e9e3425cd2123e77. The source archive contains exactly
the files in the unmodified upstream manifest. The dependency manifest pins
both that manifest and the archive by length and SHA-256.

The local Zig build dependency restores the archive into its caller's build
cache using `build/scripts/build/Restore-ControlTreeSdk.ps1`. Restoration
checks the archive, member names, and every extracted file. NativeCore,
Desktop, and development export fixtures import the controller as `rm_control`
and compile it with ReleaseSafe assertions. `tests/Control` retains RM's
original controller scenarios and compile-time rejection tests.

`-Dcontrol-source-root` is for migration equivalence against a captured old
controller source. Ordinary product builds use the verified package.

RM still owns `src/Core/NativeCore/src/control/abi.zig` and its existing
`rm_control_*` exports. SDK dev.2 will migrate the managed host shell and
configuration tables to the generic SDK export layer and add run scopes;
dev.1 does not expose `control_tree_*` through RM's DLL.

# Native GPU session consumer probe

`GpuLaunchSessionProbe.cpp` includes the production broker client and runs finite resolve/report cases without launching a target, registering IFEO, injecting a DLL, or adjusting hardware. Its default console entry and the `RM_GPU_SESSION_DESKTOP_TEST` GUI-subsystem entry use the same argument decoder and consumer code. The GUI entry creates no window or console and permits the original WTS launcher to run it as the actual interactive user.

Build with the same static toolchain runtime contract as the delivered broker:

```powershell
g++ -std=c++17 -O2 -Wall -Wextra -municode -mwindows -static -DRM_GPU_SESSION_DESKTOP_TEST=1 -o <owned-output>/GpuLaunchSessionDesktopProbe.exe tests/Native/GpuLaunchSessionProbe.cpp -lshell32 -ladvapi32
```

Regular `GpuLaunchSessionTests` require `RESOURCE_MANAGER_GPU_LAUNCH_SESSION_TEST=1` and `RM_GPU_LAUNCH_SESSION_PROBE` pointing to that image. Both entries preserve redirected stdout for those tests.

The two SYSTEM cases separately require a real SYSTEM test host in Session 0, `RESOURCE_MANAGER_SYSTEM_GPU_SESSION_TEST=1`, `RM_SYSTEM_GPU_SESSION_PROBE`, and the actual desktop user's session/SID in `RM_SYSTEM_TARGET_SESSION`/`RM_SYSTEM_TARGET_SID`. Obtain the latter from the actual desktop identity, rather than the sandbox identity. `WindowsInteractiveUserSessionBroker` queries WTS and launches the probe under the standard user's token. The original process metadata ACL initializer and connection identity checks run; only GPU resolver/report effects use recording substitutes. The probe never receives a frontend API credential.

Use a unique owned pipe and a manual-only SYSTEM task with a two-minute limit and guaranteed removal. The valid case resolves and reports; the changed-backend-creation case resolves but cannot report. Child handles are retained, early exit produces its real exit code, and cleanup affects only that test's child. This does not certify full SCM workers, formal installed images, simultaneous users, or real IFEO interception.

`GpuLaunchBrokerPackagingTests` inspects both actual delivered EXEs for their GUI subsystem and bounded PE imports, rejecting undeclared non-Windows runtimes. A successful launch on a development machine whose PATH supplies the compiler's DLLs is insufficient packaging evidence.

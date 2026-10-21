# WTS desktop consumer fixture

This is a Windows GUI subsystem apphost with no window or application initialization. It links the existing FrontendSessionCredentialProbe program instead of duplicating its consumer code. The seven-argument mode connects an owned diagnostic pipe for stdin/stdout because WTS process creation does not inherit redirected handles. The credential pipe, compiled client methods and protected HTTP call are unchanged; credentials are never diagnostic output.

Build from the repository root:

```powershell
dotnet build tests/Fixtures/FrontendDesktopSessionProbe/FrontendDesktopSessionProbe.csproj -o .local/review-repairs-20261002/frontend-desktop-session-probe
```

The gated SystemSession tests in LoopbackApiAccessTests require a real SYSTEM process in Session 0, `RESOURCE_MANAGER_SYSTEM_DESKTOP_TEST=1`, `RM_SYSTEM_FRONTEND_PROBE`, and the actual interactive session/user SID in `RM_SYSTEM_TARGET_SESSION`/`RM_SYSTEM_TARGET_SID`. Prepare those values from the real desktop account, not the sandbox account. The production WindowsInteractiveUserSessionBroker queries WTS and launches the fixture under that user's token. Both launch methods, the changed-SID rejection, original pipe identity checks and the default store run without implementation substitutes; backend HTTP hosted services are removed to keep hardware/scheduling inactive.

The fixture is renamed only within a newly created test-output sibling directory so the original frontend resolver verifies its image. Tests refuse to overwrite an existing output directory and release the owned child before removing its copied files. The unique SYSTEM test task has no trigger, a two-minute execution cap and deletion on completion. It does not use installed product pipes or services. This evidence does not certify production session workers, formal installation, or simultaneous real user sessions.

# Compiled frontend consumer probe

This finite console fixture invokes the compiled NativeUi credential client by reflection. It never initializes the desktop application. The five-argument mode acquires a real pipe credential and calls the readonly HTTP scope. The optional sixth argument selects a desktop consumer case: `desktop`, `desktop-creation`, `desktop-limit`, `desktop-count`, or `desktop-oversize`.

Desktop cases invoke the compiled window capture and `BackendServiceSession.PublishDesktopAsync`. Only the owned backend path and the actual pipe reply are supplied through private fields; socket-owner verification remains active. The test places its apphost and dependencies in a new test-output sibling directory matching the production frontend path resolver. It refuses to overwrite an existing directory and removes only its own files afterward. The executable is a console probe with the expected image name, not the running product UI.

The normal and wrong-creation cases require an accessible interactive desktop and a real known foreground. Boundary cases use synthetic process rows solely to verify the typed count and HTTP byte limits; they do not establish process ownership or projected runtime states. No window is activated, moved or created, and no hardware is written. Credentials remain in memory and are absent from arguments and diagnostics.

Build from the repository root:

```powershell
dotnet build tests/Fixtures/FrontendSessionCredentialProbe/FrontendSessionCredentialProbe.csproj --no-restore -o .local/review-repairs-20261002/frontend-credential-probe
```

Set `RM_FRONTEND_HANDSHAKE_PROBE` to the resulting probe exe. Enable `RESOURCE_MANAGER_FRONTEND_HANDSHAKE_TEST=1` for credential tests, and separately `RESOURCE_MANAGER_DESKTOP_CONSUMER_TEST=1` for desktop tests. The latter must run with actual desktop access: a sandbox that returns unknown foreground is a failing precondition, not positive evidence. SYSTEM, WTS worker launch and different user accounts require separate validation.

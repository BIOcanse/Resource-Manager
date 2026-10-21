# SDK initialization failure probe

This finite child process executes the production managed ledger or scheduler
constructor with the production native DLL's contract, size, layout and capacity
checks. `tests/Native/AdapterInitializationFailureShim.cpp` forwards those calls
and returns failure only from initialization, after touching the allocated buffer.
The probe forces collection and finalization after each failed construction and
requires exactly 32 initialization calls before exiting successfully.

`NativeInitializationFailureTests` copies the fixture and its dependencies into a
unique test-owned directory. That directory contains the shim under the SDK's
canonical DLL name and the actual native DLL under a separate name. The existing
SDK resolver binds that local shim; installed files and global search paths are
unchanged. Each child has a 20-second watchdog and disables native error dialogs.
The test waits for its exit and then removes only its own directory.

Build the fixture with the existing adapter native output:

```powershell
dotnet build tests/Fixtures/NativeInitializationFailureProbe/NativeInitializationFailureProbe.csproj -p:SkipAdapterNativeBuild=true
g++ -shared -std=c++17 -O2 -static-libgcc -static-libstdc++ tests/Native/AdapterInitializationFailureShim.cpp -o .local/review-repairs-20261002/AdapterInitializationFailureShim.dll
$env:RESOURCE_MANAGER_NATIVE_INITIALIZATION_TEST = '1'
$env:RM_NATIVE_INITIALIZATION_PROBE = (Resolve-Path tests/Fixtures/NativeInitializationFailureProbe/bin/Debug/net10.0-windows/NativeInitializationFailureProbe.dll).Path
$env:RM_NATIVE_INITIALIZATION_SHIM = (Resolve-Path .local/review-repairs-20261002/AdapterInitializationFailureShim.dll).Path
dotnet test 'tests/App/Resource Manager-APP.Tests.csproj' --no-restore --filter 'FullyQualifiedName~NativeInitializationFailureTests'
```

Use a compatible Windows x64 C++ compiler. Without the explicit environment gate,
the two tests are skipped. This checks failure cleanup; it does not validate
hardware actions or certify every SDK lifecycle.

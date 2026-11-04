# Microsoft Detours v4.0.1

Source: <https://github.com/microsoft/Detours/tree/v4.0.1>

Resource Manager builds only the suspended-process import update path required by `DetourUpdateProcessWithDll`; it does not include the Detours runtime hook engine. Two narrowly scoped build-compatibility changes are carried in the vendored source:

- `creatwth.cpp` excludes helper-process and create-process wrappers when `DETOURS_MINIMAL_CREATE` is defined.
- `uimports.cpp` uses MinGW's named `IMAGE_IMPORT_DESCRIPTOR.u.OriginalFirstThunk` field when `NONAMELESSUNION` is active.

The import-table update algorithm is otherwise unchanged. The original Microsoft license is retained in `LICENSE.md`.

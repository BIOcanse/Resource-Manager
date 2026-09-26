#pragma once
#include "OpenGlWindowRoutes.h"
#include "OpenGlModuleLifetime.h"

namespace ResourceManagerOpenGl::Runtime
{
// Caller explicitly binds its bootstrap first; no context is created or rebound here.
inline WindowDispatchReference PrepareWindowDispatch(const WindowEntries& entries)
{
    const DWORD error = GetLastError();
    if (!current || pendingRelease) { SetLastError(ERROR_NOT_READY); return nullptr; }
    const auto drawable = drawables.Read(boundDc);
    if (!drawable || !drawable.target || !drawable.directory || drawable.target->module != current->module) {
        SetLastError(ERROR_NOT_READY); return nullptr;
    }
    WindowFunctions functions{};
    const PROC create = current->driver.getProcAddress("wglCreateContextAttribsARB");
    if (Callable(create)) std::memcpy(&functions.createAttributes, &create, sizeof(create));
    functions.arb = entries.arb; functions.ext = entries.ext;
    functions.setInterval = entries.setInterval; functions.getInterval = entries.getInterval;
    functions.makeRead = entries.makeRead; functions.readDc = entries.readDc;
    functions.choose = entries.choose; functions.ints = entries.ints; functions.floats = entries.floats;
    auto prepared = WindowDispatch::Create(current->module, functions);
    if (!prepared) return nullptr;
    const PROC addresses[]{Callback(functions.createAttributes), Callback(functions.arb), Callback(functions.ext),
        Callback(functions.setInterval), Callback(functions.getInterval), Callback(functions.makeRead), Callback(functions.readDc),
        Callback(functions.choose), Callback(functions.ints), Callback(functions.floats)};
    for (auto entry : addresses) if (entry && !PinEntryModule(entry)) return nullptr;
    SetLastError(error);
    return prepared;
}
}

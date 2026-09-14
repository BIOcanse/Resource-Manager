#pragma once
#include "OpenGlCallbackSource.h"

namespace ResourceManagerOpenGl
{
constexpr size_t MaximumCallbackPathCharacters = 32767;
constexpr size_t MaximumCallbackSourceBytes = 4 + 9 * 8 + 9 * (46 + MaximumCallbackPathCharacters * 2);
BOOL EncodeIcdCallbackSource(const IcdCallbackSource& source, std::vector<unsigned char>& output);
BOOL DecodeIcdCallbackSource(const unsigned char* bytes, size_t length, IcdCallbackSource& output);
}

#include "../backend/win32.h"

/* Resource names can be integer tags, and HICON is an opaque USER handle.
   Keep these Win32 casts in C: translated MAKEINTRESOURCEW and Zig alignCast
   would incorrectly require a real, aligned memory address in ReleaseSafe. */
HICON ResourceManagerLoadIconResource(HINSTANCE instance, WORD resource_id, int width, int height)
{
    return (HICON)LoadImageW(instance, MAKEINTRESOURCEW(resource_id), IMAGE_ICON, width, height, 0);
}

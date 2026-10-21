# EDID mode declaration layout and timing-code references

The Resource Manager C# parser is independently written. Factual established/DMT/VIC identifier mappings and EDID/CTA/DisplayID layouts were cross-checked against the following maintained implementations. No driver quirks, inferred-mode generation, repair, invalid-length clipping, or original parser algorithms are incorporated.

- Linux DRM, commit `2f2f7eeef8b0a1a9b9ca998231419873ccae8d4e`: `drivers/gpu/drm/drm_edid.c`, `drivers/gpu/drm/drm_displayid.c`, `drivers/gpu/drm/drm_displayid_internal.h`, `include/drm/drm_edid.h`.
  https://github.com/torvalds/linux/tree/2f2f7eeef8b0a1a9b9ca998231419873ccae8d4e
- LinuxTV v4l-utils / edid-decode, commit `3b4769d937f715cfa43c1a6064c3b1702dc98c14`: `utils/edid-decode/parse-base-block.cpp`, `parse-cta-block.cpp`, `parse-displayid-block.cpp`, `edid-decode.h`, `edid-decode.cpp` (each relevant file marks SPDX MIT). Retrieved from Google's upstream tracking mirror:
  https://android.googlesource.com/platform/external/v4l-utils/+/3b4769d937f715cfa43c1a6064c3b1702dc98c14/utils/edid-decode/

Attribution for the consulted mappings and layout material is retained here even where only factual numeric mappings were transcribed.

## Linux DRM copyright and permission

Copyright (c) 2006 Luc Verhaegen (quirks list)
Copyright (c) 2007-2008 Intel Corporation
Copyright 2010 Red Hat, Inc.
Copyright (C) 2006 Dennis Munsie
Copyright © 2014 Red Hat Inc.
Copyright © 2021 Intel Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sub license, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice (including the next paragraph) shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NON-INFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## edid-decode copyright and MIT permission

Copyright 2006-2012 Red Hat, Inc.
Copyright 2018-2020 Cisco Systems, Inc. and/or its affiliates. All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
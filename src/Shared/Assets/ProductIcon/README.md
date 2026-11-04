# Product icon

The approved 2026-10-31 split-ring design has no centre dot. The four SVG sources
are unchanged design inputs. Do not substitute the master for the tuned sizes.

| Output (px) | Source |
| --- | --- |
| 16 | icon-16.svg |
| 20, 24 | icon-24.svg |
| 32 | icon-32.svg |
| 40, 48, 64, 256 | icon.svg |

Regenerate using an already installed Playwright module and Microsoft Edge:

```text
node tools/assets/render-product-icon.cjs --playwright <installed-playwright-module-directory>
```

The renderer uses a transparent canvas at device scale 1. With the same Edge
version its outputs are deterministic. It writes eight PNGs, `product.ico`, and
the frontend public favicons. ICO entries below 256 use 32-bit DIBs with alpha
and transparency masks; the 256 entry is PNG-compressed. Generated files are
committed, so compilation and publication need no renderer or browser.

`product.rc` embeds icon group 1 in native executables; C# projects use
`ApplicationIcon`. Native resource builds must include this directory.

Desktop links `src/shell/product_icon.c` into both the resident and its icon
tests. That Win32 boundary preserves integer resource tags and opaque HICON
values without treating them as aligned Zig memory pointers.

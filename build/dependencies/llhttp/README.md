# llhttp source pin

The source pin is `release/v9.4.3`, commit
`0e815792b167a9bd8ace259b95b7da953776c288` from
https://github.com/nodejs/llhttp/tree/0e815792b167a9bd8ace259b95b7da953776c288.
`dependency-manifest.json` binds the delivered upstream files by length and
SHA-256. `.gitattributes` preserves their exact bytes. The MIT license is
included unmodified and must accompany the product's dependency notices.

RM compiles `src/api.c`, `src/http.c`, and the upstream-generated `src/llhttp.c`
with C99, the `include` directory, and libc. `rm-build.zig` is the NativeCore
build owner's importable recipe; H0 does not edit the shared NativeCore build.
There is no extra DLL, download, npm install, Node runtime or parser generation.
The upstream CMake file records its source list and version; RM does not invoke
it, and the rest of that CMake packaging project is not vendored.

Strict mode means `llhttp_init(HTTP_REQUEST)` and `lenient_flags == 0` throughout
the adapter's lifetime, including reset/reuse. Do not enable any lenient setter.
This release does not consume `LLHTTP_STRICT_MODE`; defining it proves nothing.
`tests/Native/http/llhttp_pin_test.c` verifies the pin, zero flags, byte-fragmented
valid requests and rejection of ambiguous/malformed framing. H-parse owns the
actual adapter and must enforce the same invariant there. HTTP upgrade rejection,
Host/Origin, duplicate security headers, routing, URL/JSON policy, limits and
resource lifetime remain RM's responsibility.

The version includes upstream's empty Transfer-Encoding rejection fix. The
old Kestrel framing oracle is kept separately: a parser difference must be
reviewed as a framing/security difference, never silently rewritten as business
equivalence. Updating this pin requires reviewing the release, replacing the
complete input set, regenerating hashes and running both fixture groups.

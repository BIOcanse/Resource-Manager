import { defineConfig } from "vite";
import solid from "vite-plugin-solid";
import { frontendBuildManifestPlugin } from "./build/frontendBuildManifest";
import { retryableLocaleImportsPlugin } from "./build/retryableLocaleImports";

export default defineConfig({
  plugins: [solid(), retryableLocaleImportsPlugin(), frontendBuildManifestPlugin()],
  build: {
    outDir: "../../Core/wwwroot",
    emptyOutDir: true,
    sourcemap: false
  },
  server: {
    port: 5173,
    strictPort: false,
    proxy: {
      "/api": "http://127.0.0.1:9321"
    }
  }
});

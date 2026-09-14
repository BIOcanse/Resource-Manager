import { defineConfig } from "vite";
import solid from "vite-plugin-solid";
import { frontendBuildManifestPlugin } from "./build/frontendBuildManifest";

export default defineConfig({
  plugins: [solid(), frontendBuildManifestPlugin()],
  build: {
    outDir: "../wwwroot",
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

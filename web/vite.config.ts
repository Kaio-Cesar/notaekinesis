import { fileURLToPath, URL } from "node:url";

import tailwindcss from "@tailwindcss/vite";
import vue from "@vitejs/plugin-vue";
import { defineConfig } from "vite";

const api = process.env.API_URL ?? "http://localhost:5657";

export default defineConfig({
  plugins: [vue(), tailwindcss()],
  resolve: {
    alias: { "@": fileURLToPath(new URL("./src", import.meta.url)) },
  },
  server: {
    port: Number(process.env.PORT ?? 5173),
    strictPort: true,
    // The API and the login redirect live on the server; keep the browser on one origin.
    proxy: {
      "/api": { target: api, changeOrigin: false },
      "/native-login": { target: api, changeOrigin: false },
    },
  },
});

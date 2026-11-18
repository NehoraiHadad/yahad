import { cloudflareTest } from "@cloudflare/vitest-plugin";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [
    cloudflareTest({
      wrangler: { configPath: "./wrangler.jsonc" },
      miniflare: {
        bindings: {
          ZONE_NAME: "home.example.com",
          ZONE_ID: "0123456789abcdef0123456789abcdef",
          CF_API_TOKEN: "test-token",
        },
      },
    }),
  ],
});

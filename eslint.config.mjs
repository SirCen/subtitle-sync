import { defineConfig, globalIgnores } from "eslint/config";
import nextVitals from "eslint-config-next/core-web-vitals";
import nextTs from "eslint-config-next/typescript";

const eslintConfig = defineConfig([
  ...nextVitals,
  ...nextTs,
  // Override default ignores of eslint-config-next.
  globalIgnores([
    // Default ignores of eslint-config-next:
    ".next/**",
    "out/**",
    "build/**",
    "next-env.d.ts",
    // Generated esbuild output for the Jellyfin plugin (issue #10). Minified
    // third-party code; linting it says nothing about this repository.
    "jellyfin-plugin/web/dist/**",
  ]),
]);

export default eslintConfig;

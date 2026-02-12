import { build } from "esbuild";

await build({
  entryPoints: ["build/index.js"],
  bundle: true,
  platform: "node",
  target: "node18",
  format: "esm",
  outfile: "dist/server.mjs",
  banner: {
    js: [
      "#!/usr/bin/env node",
      "import { createRequire } from 'module'; const require = createRequire(import.meta.url);",
    ].join("\n"),
  },
});

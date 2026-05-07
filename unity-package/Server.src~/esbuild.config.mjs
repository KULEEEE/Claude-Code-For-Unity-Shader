import { build } from "esbuild";

const banner = {
  js: [
    "#!/usr/bin/env node",
    "import { createRequire } from 'module'; const require = createRequire(import.meta.url);",
  ].join("\n"),
};

const common = {
  bundle: true,
  platform: "node",
  target: "node18",
  format: "esm",
  banner,
  external: ["@anthropic-ai/claude-agent-sdk"],
};

await build({
  ...common,
  entryPoints: ["build/headless.js"],
  outfile: "../Server~/headless.mjs",
});

await build({
  ...common,
  entryPoints: ["build/mcp-tools.js"],
  outfile: "../Server~/mcp-tools.mjs",
});

console.log("Built Server~/headless.mjs and Server~/mcp-tools.mjs");

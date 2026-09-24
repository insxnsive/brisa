import path from "node:path";
import fs from "node:fs";
import { fileURLToPath } from "node:url";
import { build } from "esbuild";

const root = path.dirname(fileURLToPath(import.meta.url));
const common = {
  absWorkingDir: root,
  bundle: true,
  platform: "node",
  target: "node22",
  format: "cjs",
  sourcemap: false,
  legalComments: "eof",
  logLevel: "info",
  plugins: [{
    name: "brisa-local-sources",
    setup(api) {
      const resolveFile = value => {
        for (const candidate of [value, `${value}.ts`, `${value}.js`, `${value}.mjs`, `${value}.cjs`, path.join(value, "index.ts"), path.join(value, "index.js")]) {
          try { if (fs.statSync(candidate).isFile()) return candidate; } catch {}
        }
        return value;
      };
      api.onResolve({ filter: /^\./ }, args => ({
        path: resolveFile(path.resolve(args.namespace === "brisa-source" ? path.dirname(args.importer) : args.resolveDir, args.path)),
        namespace: "brisa-source",
      }));
      api.onLoad({ filter: /.*/, namespace: "brisa-source" }, args => ({
        contents: fs.readFileSync(args.path, "utf8"),
        loader: args.path.endsWith(".ts") ? "ts" : "js",
        resolveDir: path.dirname(args.path),
      }));
    },
  }],
  banner: { js: "const __native_import_meta_url = require('node:url').pathToFileURL(__filename).href;" },
  define: { "import.meta.url": "__native_import_meta_url" },
};

await build({
  ...common,
  stdin: { contents: 'import "./src/index.ts";', resolveDir: root, sourcefile: "brisa-backend-entry.ts" },
  outfile: path.join(root, "dist", "backend.cjs"),
});
if (process.argv.includes("--fixture")) {
  await build({
    ...common,
    stdin: { contents: 'import "./tests/fixture-entry.mjs";', resolveDir: root, sourcefile: "brisa-backend-fixture-entry.ts" },
    outfile: path.join(root, "dist", "backend-fixture.cjs"),
  });
}

// Shared by the extractor and tools/dts-survey. It takes `ts` as a parameter, because each brings its own TypeScript 6.x.

import fs from "node:fs";
import path from "node:path";

/** The .d.ts files under `dir`, sorted, skipping node_modules. `only` keeps the named files; `skip` drops paths containing a substring. */
export function listDts(dir, { only, skip } = {}) {
  const out = [];
  const walk = (d) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const p = path.join(d, e.name);
      if (e.isDirectory()) {
        if (e.name !== "node_modules") walk(p);
      } else if (/\.d\.(m|c)?ts$/.test(e.name) && (!only || only.includes(e.name)) && !skip?.some((s) => p.includes(s))) {
        out.push(p);
      }
    }
  };
  walk(dir);
  return out.sort();
}

/**
 * A program over declaration files, with the DOM in scope and nothing from @types. `virtual` adds in-memory source
 * files, `{ [path]: text }`, with `/` separators.
 */
export function createProgram(ts, files, virtual = {}) {
  const options = {
    noEmit: true,
    skipLibCheck: true,
    target: ts.ScriptTarget.ESNext,
    module: ts.ModuleKind.NodeNext,
    moduleResolution: ts.ModuleResolutionKind.NodeNext,
    lib: ["lib.esnext.d.ts", "lib.dom.d.ts", "lib.dom.iterable.d.ts"],
    types: [],
    strict: true,
  };
  const host = ts.createCompilerHost(options);
  const { getSourceFile, fileExists } = host;
  host.fileExists = (f) => Object.hasOwn(virtual, f) || fileExists(f);
  host.getSourceFile = (f, language, ...rest) => (Object.hasOwn(virtual, f) ? ts.createSourceFile(f, virtual[f], language) : getSourceFile(f, language, ...rest));
  return ts.createProgram([...files, ...Object.keys(virtual)], options, host);
}

/** R19 step 2: the first property that every member declares with a distinct literal type (`type: "point"`), or null. */
export function discriminant(ts, checker, members) {
  const literal = ts.TypeFlags.StringLiteral | ts.TypeFlags.NumberLiteral | ts.TypeFlags.BooleanLiteral;
  for (const { name } of members[0].getProperties()) {
    const seen = new Set();
    const distinct = members.every((m) => {
      const prop = m.getProperty(name);
      const type = prop && checker.getTypeOfSymbol(prop);
      if (!type || !(type.flags & literal)) return false;
      const key = checker.typeToString(type);
      return !seen.has(key) && seen.add(key);
    });
    if (distinct) return name;
  }
  return null;
}

// dts-survey: measures which TypeScript type shapes a .d.ts -> C# generator
// actually has to deal with, per library.
//
// Usage:
//   node survey.mjs <node_modules dir> [--libs libraries.json] [--json out.json] [--md out.md]
//
// --libs lists the libraries to survey (default: libraries.json next to this script):
//   [{ "name": "...", "dir": "<path under node_modules>", "only": ["file.d.ts"], "skip": ["/substr/"] }]
//
// Every "API position" (property, accessor, parameter, return type, type alias,
// index signature) in each library's own .d.ts files is resolved with the
// TypeScript checker and put in exactly one bucket. Syntactic constructs that
// need type-level evaluation (mapped/conditional/template-literal types, etc.)
// are counted separately.

import { createRequire } from "node:module";
import fs from "node:fs";
import path from "node:path";
import { createProgram, discriminant, listDts } from "../../src/interoper-extractor/program.mjs";

const args = process.argv.slice(2);
const modulesDir = path.resolve(args[0] ?? "node_modules");
const opt = (name) => {
  const i = args.indexOf(name);
  return i >= 0 ? args[i + 1] : undefined;
};
const require = createRequire(path.join(modulesDir, "noop.js"));
const ts = require("typescript");

/** name, the directory under node_modules that counts as "the library", optional file filters. */
const libsFile = path.resolve(opt("--libs") ?? new URL("libraries.json", import.meta.url).pathname);
const libraries = JSON.parse(fs.readFileSync(libsFile, "utf8"));

const BUCKETS = [
  ["primitive", "string / number / boolean / bigint (incl. single literal)"],
  ["object", "single class / interface / object type"],
  ["array", "array or ReadonlyArray of anything"],
  ["tuple", "tuple type"],
  ["function", "function / callback type"],
  ["void", "void / undefined / never (returns)"],
  ["any", "any / unknown"],
  ["enum", "TS enum type"],
  ["typeParam", "generic type parameter"],
  ["literalUnion", "union of string/number literals only  -> C# enum or string-enum"],
  ["primitiveUnion", "union of different primitives (string | number)"],
  ["discriminatedUnion", "object union with a shared literal-typed tag (e.g. type: \"point\")"],
  ["objectUnion", "object union with NO usable tag"],
  ["mixedUnion", "primitive/literal mixed with object/array/function"],
  ["intersection", "A & B"],
  ["other", "anything else (index access, conditional left unresolved, ...)"],
];

const SYNTAX = {
  [ts.SyntaxKind.MappedType]: "mappedType",
  [ts.SyntaxKind.ConditionalType]: "conditionalType",
  [ts.SyntaxKind.TemplateLiteralType]: "templateLiteralType",
  [ts.SyntaxKind.IndexedAccessType]: "indexedAccessType",
  [ts.SyntaxKind.TypeQuery]: "typeofQuery",
  [ts.SyntaxKind.InferType]: "inferType",
  [ts.SyntaxKind.TupleType]: "tupleType",
  [ts.SyntaxKind.ThisType]: "thisType",
};
const UTILITY = new Set([
  "Partial", "Required", "Readonly", "Pick", "Omit", "Record", "Exclude", "Extract",
  "NonNullable", "ReturnType", "Parameters", "InstanceType", "Awaited", "ConstructorParameters",
]);

function surveyLibrary(lib) {
  const dir = path.join(modulesDir, lib.dir);
  if (!fs.existsSync(dir)) return null;
  const files = listDts(dir, lib);
  const program = createProgram(ts, files);
  const checker = program.getTypeChecker();
  const inLib = (sf) => (lib.only ? files.includes(sf.fileName) : sf.fileName.startsWith(dir + path.sep));

  const stats = {
    library: lib.name,
    files: 0,
    declarations: { classes: 0, interfaces: 0, typeAliases: 0, functions: 0, methods: 0, enums: 0 },
    positions: 0,
    buckets: Object.fromEntries(BUCKETS.map(([k]) => [k, 0])),
    nullable: 0, // positions whose type includes null and/or undefined (or ?)
    nullAndUndefined: 0, // both null AND undefined: three states
    promiseReturns: 0,
    callbackParams: 0,
    overloadedMethods: 0,
    asymmetricAccessors: 0, // get/set pairs with different types (e.g. setters that accept a plain-data bag)
    genericDeclarations: 0,
    indexSignatures: 0,
    syntax: Object.fromEntries([...Object.values(SYNTAX), "keyofOperator", "utilityTypeRef"].map((k) => [k, 0])),
    unionWidth: { max: 0, maxWhere: "", widths: [] },
    distinctUnions: {}, // bucket -> number of distinct checker types (= C# types to emit)
    examples: {},
  };

  const isNullish = (t) => (t.flags & (ts.TypeFlags.Null | ts.TypeFlags.Undefined | ts.TypeFlags.Void)) !== 0;
  const isPrimitive = (t) =>
    (t.flags &
      (ts.TypeFlags.StringLike | ts.TypeFlags.NumberLike | ts.TypeFlags.BooleanLike | ts.TypeFlags.BigIntLike | ts.TypeFlags.ESSymbolLike)) !== 0 &&
    !(t.flags & ts.TypeFlags.EnumLike);
  const isLiteral = (t) => (t.flags & (ts.TypeFlags.StringLiteral | ts.TypeFlags.NumberLiteral)) !== 0;
  const isFunctionType = (t) =>
    t.getCallSignatures().length > 0 && t.getProperties().length === 0;
  const isArrayLike = (t) => checker.isArrayType(t) || checker.isTupleType(t);

  function classify(type, where) {
    if (type.flags & (ts.TypeFlags.Any | ts.TypeFlags.Unknown)) return "any";
    if (type.flags & ts.TypeFlags.TypeParameter) return "typeParam";
    if (type.flags & (ts.TypeFlags.Void | ts.TypeFlags.Undefined | ts.TypeFlags.Never | ts.TypeFlags.Null)) return "void";
    if (type.flags & ts.TypeFlags.Boolean) return "primitive"; // `boolean` is itself the union true | false
    if (type.isUnion()) {
      let members = type.types.filter((t) => !isNullish(t));
      // boolean is `true | false` to the checker; fold it back.
      const bools = members.filter((t) => t.flags & ts.TypeFlags.BooleanLiteral);
      if (bools.length === 2) members = members.filter((t) => !(t.flags & ts.TypeFlags.BooleanLiteral)).concat([checker.getBooleanType()]);
      if (type.flags & ts.TypeFlags.EnumLiteral || (members.length > 0 && members.every((t) => t.flags & ts.TypeFlags.EnumLiteral))) return "enum";
      if (members.length === 0) return "void";
      if (members.length === 1) return classify(members[0], where);
      stats.unionWidth.widths.push(members.length);
      if (members.length > stats.unionWidth.max) {
        stats.unionWidth.max = members.length;
        stats.unionWidth.maxWhere = where;
      }
      if (members.every(isLiteral)) return "literalUnion";
      if (members.every(isPrimitive)) return "primitiveUnion";
      if (members.every((t) => !isPrimitive(t))) {
        const objs = members.filter((t) => t.flags & ts.TypeFlags.Object || t.flags & ts.TypeFlags.Intersection);
        if (objs.length === members.length && !members.some(isFunctionType) && !members.some(isArrayLike)) {
          return discriminant(ts, checker, members) ? "discriminatedUnion" : "objectUnion";
        }
      }
      return "mixedUnion";
    }
    if (type.flags & ts.TypeFlags.EnumLike) return "enum";
    if (isPrimitive(type)) return "primitive";
    if (type.isIntersection()) return "intersection";
    if (checker.isTupleType(type)) return "tuple";
    if (checker.isArrayType(type)) return "array";
    if (type.flags & ts.TypeFlags.Object) return isFunctionType(type) ? "function" : "object";
    return "other";
  }

  function record(typeNode, node, where, { optional = false } = {}) {
    let type;
    if (typeNode) type = checker.getTypeFromTypeNode(typeNode);
    else if (node) type = checker.getTypeAtLocation(node);
    if (!type) return;
    stats.positions++;
    const bucket = classify(type, where);
    stats.buckets[bucket]++;
    if (bucket.endsWith("Union")) (seenUnions[bucket] ??= new Set()).add(stripNullish(type).id);
    const nullish = type.isUnion() && type.types.filter(isNullish);
    const hasNull = nullish && nullish.some((t) => t.flags & ts.TypeFlags.Null);
    const hasUndef = optional || (nullish && nullish.some((t) => t.flags & (ts.TypeFlags.Undefined | ts.TypeFlags.Void)));
    if (hasNull || hasUndef) stats.nullable++;
    if (hasNull && hasUndef) stats.nullAndUndefined++;
    const ex = (stats.examples[bucket] ??= []);
    if (ex.length < 4 && typeNode) {
      const text = typeNode.getText().replace(/\s+/g, " ");
      if (text.length < 160) ex.push(`${where}: ${text}`);
    }
    return { type, bucket };
  }

  const seenUnions = {};
  // `T | null` and `T | undefined` are the same C# union, so key on the type without nullish members.
  const stripNullish = (t) => (t.isUnion() ? checker.getNonNullableType(t) : t);

  const nameOf = (n) => (n.name && ts.isIdentifier(n.name) ? n.name.text : n.name?.getText?.() ?? "?");

  function visit(node, owner) {
    const kind = node.kind;
    if (SYNTAX[kind]) stats.syntax[SYNTAX[kind]]++;
    if (ts.isTypeOperatorNode(node) && node.operator === ts.SyntaxKind.KeyOfKeyword) stats.syntax.keyofOperator++;
    if (ts.isTypeReferenceNode(node) && ts.isIdentifier(node.typeName) && UTILITY.has(node.typeName.text)) stats.syntax.utilityTypeRef++;

    if (ts.isClassDeclaration(node)) stats.declarations.classes++;
    if (ts.isInterfaceDeclaration(node)) stats.declarations.interfaces++;
    if (ts.isEnumDeclaration(node)) stats.declarations.enums++;
    if ((ts.isClassDeclaration(node) || ts.isInterfaceDeclaration(node) || ts.isTypeAliasDeclaration(node) || ts.isFunctionDeclaration(node) || ts.isMethodDeclaration(node) || ts.isMethodSignature(node)) && node.typeParameters?.length) {
      stats.genericDeclarations++;
    }
    if (ts.isIndexSignatureDeclaration(node)) {
      stats.indexSignatures++;
      record(node.type, node, `${owner}[index]`);
    }

    const ownerName = node.name && (ts.isClassDeclaration(node) || ts.isInterfaceDeclaration(node) || ts.isModuleDeclaration(node)) ? nameOf(node) : owner;

    if (ts.isTypeAliasDeclaration(node)) {
      stats.declarations.typeAliases++;
      record(node.type, node, `type ${nameOf(node)}`);
    } else if (ts.isPropertyDeclaration(node) || ts.isPropertySignature(node)) {
      record(node.type, node, `${owner}.${nameOf(node)}`, { optional: !!node.questionToken });
    } else if (ts.isGetAccessorDeclaration(node)) {
      record(node.type, node, `${owner}.${nameOf(node)} (get)`);
      const setter = node.parent.members?.find((m) => ts.isSetAccessorDeclaration(m) && nameOf(m) === nameOf(node));
      if (setter && node.type && setter.parameters[0]?.type) {
        const g = checker.getTypeFromTypeNode(node.type);
        const s = checker.getTypeFromTypeNode(setter.parameters[0].type);
        if (!checker.isTypeAssignableTo(s, g) || !checker.isTypeAssignableTo(g, s)) stats.asymmetricAccessors++;
      }
    } else if (ts.isSetAccessorDeclaration(node)) {
      const p = node.parameters[0];
      if (p) record(p.type, p, `${owner}.${nameOf(node)} (set)`);
    } else if (ts.isFunctionLike(node) && !ts.isFunctionTypeNode(node) && !ts.isConstructorTypeNode(node) && !ts.isGetAccessor(node) && !ts.isSetAccessor(node)) {
      if (ts.isFunctionDeclaration(node)) stats.declarations.functions++;
      if (ts.isMethodDeclaration(node) || ts.isMethodSignature(node)) stats.declarations.methods++;
      const label = `${owner}.${node.name ? nameOf(node) : ts.SyntaxKind[node.kind]}`;
      for (const p of node.parameters) {
        const r = record(p.type, p, `${label}(${nameOf(p)})`, { optional: !!p.questionToken || !!p.initializer });
        if (r && (r.bucket === "function" || (r.type.isUnion() && r.type.types.some(isFunctionType)))) stats.callbackParams++;
      }
      if (node.type) {
        const r = record(node.type, node, `${label} => return`);
        if (r) {
          const sym = r.type.getSymbol?.();
          if (sym && sym.name === "Promise") stats.promiseReturns++;
        }
      }
    }

    // Overloads: methods / functions with >1 declaration of the same name in one container.
    if ((ts.isClassDeclaration(node) || ts.isInterfaceDeclaration(node)) && node.members) {
      const counts = new Map();
      for (const m of node.members) {
        if ((ts.isMethodDeclaration(m) || ts.isMethodSignature(m)) && m.name) {
          const n = m.name.getText();
          counts.set(n, (counts.get(n) ?? 0) + 1);
        }
      }
      for (const c of counts.values()) if (c > 1) stats.overloadedMethods++;
    }

    ts.forEachChild(node, (c) => visit(c, ownerName));
  }

  for (const sf of program.getSourceFiles()) {
    if (!inLib(sf)) continue;
    stats.files++;
    visit(sf, path.basename(sf.fileName).replace(/\.d\.(m|c)?ts$/, ""));
  }
  for (const [k, set] of Object.entries(seenUnions)) stats.distinctUnions[k] = set.size;
  return stats;
}

const quantile = (xs, q) => {
  if (!xs.length) return "-";
  const s = [...xs].sort((a, b) => a - b);
  return s[Math.min(s.length - 1, Math.floor(q * s.length))];
};
const pct = (n, d) => (d ? ((100 * n) / d).toFixed(1) + "%" : "-");

const results = [];
for (const lib of libraries) {
  const t0 = Date.now();
  const r = surveyLibrary(lib);
  if (!r) {
    console.error(`skip ${lib.name} (not installed)`);
    continue;
  }
  console.error(`${lib.name}: ${r.positions} positions in ${r.files} files (${Date.now() - t0} ms)`);
  results.push(r);
}

for (const out of [opt("--json"), opt("--md")]) if (out) fs.mkdirSync(path.dirname(path.resolve(out)), { recursive: true });

if (opt("--json")) {
  const slim = results.map((r) => ({ ...r, unionWidth: { max: r.unionWidth.max, maxWhere: r.unionWidth.maxWhere, count: r.unionWidth.widths.length } }));
  fs.writeFileSync(opt("--json"), JSON.stringify(slim, null, 2));
}

// Markdown summary
const unionKeys = ["literalUnion", "primitiveUnion", "discriminatedUnion", "objectUnion", "mixedUnion"];
let md = "";
md += "| Library | Positions | Nullable | null **and** undefined | Literal union (enum) | Primitive union | Discriminated obj union | Untagged obj union | Mixed union | **Needs a real union** | Intersection | Callback params | Promise returns | Asym. get/set | Overloaded methods |\n";
md += "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|\n";
for (const r of results) {
  const b = r.buckets;
  const real = b.primitiveUnion + b.discriminatedUnion + b.objectUnion + b.mixedUnion;
  md += `| ${r.library} | ${r.positions} | ${pct(r.nullable, r.positions)} | ${pct(r.nullAndUndefined, r.positions)} | ${pct(b.literalUnion, r.positions)} | ${pct(b.primitiveUnion, r.positions)} | ${pct(b.discriminatedUnion, r.positions)} | ${pct(b.objectUnion, r.positions)} | ${pct(b.mixedUnion, r.positions)} | **${pct(real, r.positions)}** | ${pct(b.intersection, r.positions)} | ${r.callbackParams} | ${r.promiseReturns} | ${r.asymmetricAccessors} | ${r.overloadedMethods} |\n`;
}
md += "\n| Library | distinct literal unions | distinct primitive unions | distinct discriminated | distinct untagged object | distinct mixed | median / p90 members per union |\n";
md += "|---|---:|---:|---:|---:|---:|---:|\n";
for (const r of results) {
  const d = r.distinctUnions;
  md += `| ${r.library} | ${d.literalUnion ?? 0} | ${d.primitiveUnion ?? 0} | ${d.discriminatedUnion ?? 0} | ${d.objectUnion ?? 0} | ${d.mixedUnion ?? 0} | ${quantile(r.unionWidth.widths, 0.5)} / ${quantile(r.unionWidth.widths, 0.9)} |\n`;
}
md += "\n| Library | Generic decls | Index signatures | Mapped | Conditional | Template literal | keyof | Indexed access | typeof | Utility type refs | any/unknown | Widest union (members) |\n";
md += "|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|\n";
for (const r of results) {
  const s = r.syntax;
  md += `| ${r.library} | ${r.genericDeclarations} | ${r.indexSignatures} | ${s.mappedType} | ${s.conditionalType} | ${s.templateLiteralType} | ${s.keyofOperator} | ${s.indexedAccessType} | ${s.typeofQuery} | ${s.utilityTypeRef} | ${pct(r.buckets.any, r.positions)} | ${r.unionWidth.max} (\`${r.unionWidth.maxWhere}\`) |\n`;
}
if (opt("--md")) fs.writeFileSync(opt("--md"), md);
console.log(md);

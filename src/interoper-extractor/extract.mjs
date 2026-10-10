// Interoper extractor: reads a package's .d.ts files and its Custom Elements Manifest with the TypeScript checker
// and writes IR v1 (docs/ir.md).
//
//   node extract.mjs <package dir> [--profile profile.json] [--out ir.json]
//
// The profile is read only for the event-map property (`events.mapProperty`, R24); everything else in it is the generator's.

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import ts from "typescript";
import { createProgram, discriminant, listDts } from "./program.mjs";

const F = ts.SymbolFlags;
const T = ts.TypeFlags;
const PRIMITIVES = [
  [T.Any, "any"], [T.Unknown, "unknown"], [T.String, "string"], [T.Number, "number"], [T.Boolean, "boolean"],
  [T.BigInt, "bigint"], [T.ESSymbolLike, "symbol"], [T.Void, "void"], [T.Undefined, "undefined"], [T.Null, "null"],
  [T.Never, "never"], [T.NonPrimitive, "object"],
];

/** Drops undefined values and empty arrays, so absent means "none" and the JSON stays small. */
const compact = (o) => Object.fromEntries(Object.entries(o).filter(([, v]) => v !== undefined && !(Array.isArray(v) && !v.length)));

export function extract(packageDir, { eventMap } = {}) {
  const dir = path.resolve(packageDir);
  const pkg = JSON.parse(fs.readFileSync(path.join(dir, "package.json"), "utf8"));
  const files = listDts(dir);
  const manifest = readManifest(dir, pkg);
  const program = createProgram(ts, files, Object.fromEntries([...manifest.types.values()].map((t) => [t.file, t.text])));
  const checker = program.getTypeChecker();

  const own = new Set(files);
  const inPackage = (sym) => sym.declarations?.some((d) => own.has(path.resolve(d.getSourceFile().fileName)));

  // The module specifier of a declaration file: its package's name plus the path of the JS file it describes.
  // Undefined for the platform's own typings (lib.dom.d.ts).
  const specifiers = new Map();
  function moduleOf(decl) {
    const sf = decl.getSourceFile();
    const file = path.resolve(sf.fileName);
    if (!specifiers.has(file)) specifiers.set(file, isPlatform(sf) ? undefined : specifierOf(file));
    return specifiers.get(file);
  }
  const isPlatform = (node) => program.isSourceFileDefaultLibrary(node.getSourceFile());

  // --- Declarations ---

  function declaration(sym, decl) {
    const name = nameOf(sym);
    const base = { name, export: name !== sym.name ? sym.name : undefined, doc: doc(sym) };
    if (sym.flags & F.Class) return classDecl(sym, base);
    if (sym.flags & F.Enum) return enumDecl(sym, base);
    if (sym.flags & F.Interface) {
      const type = checker.getDeclaredTypeOfSymbol(sym);
      return compact({ kind: "interface", ...base, typeParameters: typeParams(type.typeParameters), extends: checker.getBaseTypes(type).map((t) => typeRef(t)).filter((t) => t.kind === "ref"), ...structure(type) });
    }
    if (sym.flags & F.TypeAlias) {
      return compact({ kind: "alias", ...base, typeParameters: typeParams(decl.typeParameters?.map((p) => checker.getTypeAtLocation(p.name))), type: typeRef(checker.getTypeFromTypeNode(decl.type), decl.type, { expandAlias: true }) });
    }
    if (sym.flags & F.Function) return compact({ kind: "function", name, export: base.export, signatures: signatures(checker.getTypeOfSymbol(sym), ts.SignatureKind.Call) });
    if (sym.flags & F.Variable) {
      return compact({ kind: "variable", ...base, readonly: (ts.getCombinedNodeFlags(decl) & ts.NodeFlags.Const) !== 0 || undefined, type: valueType(sym, decl.type) });
    }
    return undefined; // re-exports and namespaces aren't in IR v1
  }

  function classDecl(sym, base) {
    const type = checker.getDeclaredTypeOfSymbol(sym);
    const statics = checker.getTypeOfSymbol(sym);
    const decl = sym.declarations.find(ts.isClassLike);
    const [superclass] = checker.getBaseTypes(type);
    const mapProp = eventMap && type.getProperty(eventMap);
    const implemented = decl?.heritageClauses?.find((h) => h.token === ts.SyntaxKind.ImplementsKeyword)?.types ?? [];
    return compact({
      kind: "class",
      ...base,
      abstract: hasModifier(decl, ts.ModifierFlags.Abstract),
      typeParameters: typeParams(type.typeParameters),
      extends: superclass && typeRef(superclass),
      implements: implemented.map((n) => typeRef(checker.getTypeFromTypeNode(n), n)),
      constructors: signatures(statics, ts.SignatureKind.Construct),
      members: [...members(type), ...members(statics, true)],
      events: mapProp && checker.getPropertiesOfType(checker.getTypeOfSymbol(mapProp)).map((e) => compact({ name: e.name, type: valueType(e), doc: doc(e) })),
    });
  }

  function enumDecl(sym, base) {
    const items = sym.declarations.filter(ts.isEnumDeclaration).flatMap((d) => d.members);
    return compact({
      kind: "enum",
      ...base,
      members: items.map((m) => compact({ name: checker.getSymbolAtLocation(m.name).name, value: checker.getConstantValue(m), doc: doc(checker.getSymbolAtLocation(m.name)) })),
    });
  }

  // --- Members and signatures ---

  /**
   * Checker-resolved members. Those a named base declares stay with that base (follow `extends`). Members of an
   * anonymous base or a mapped type are kept, as inherited, unless the platform declares them (DOM types are JsAny).
   */
  function members(type, isStatic = false) {
    const owners = type.symbol?.declarations ?? [];
    const bases = type.objectFlags & ts.ObjectFlags.ClassOrInterface ? checker.getBaseTypes(type).filter(isNamed) : [];
    return checker.getPropertiesOfType(type).flatMap((p) => {
      const decls = p.declarations ?? []; // none for a mapped type's synthetic keys (Record<"x" | "y", T>)
      if (p.name === eventMap || p.name === "prototype" || (decls.length && decls.every(isPlatform))) return [];
      const inherited = decls.length > 0 && !decls.some((d) => owners.includes(d.parent));
      return inherited && (isStatic || bases.some((b) => b.getProperty(p.name))) ? [] : [member(p, inherited, isStatic)];
    });
  }

  function member(sym, inherited, isStatic) {
    const decl = sym.valueDeclaration ?? sym.declarations?.[0];
    const common = {
      name: sym.name,
      optional: (sym.flags & F.Optional) !== 0 || undefined,
      static: isStatic || undefined,
      abstract: hasModifier(decl, ts.ModifierFlags.Abstract),
      inherited: inherited || undefined,
    };
    if (sym.flags & F.Method) {
      // A method's docs are on its signatures, one per overload.
      return compact({ kind: "method", ...common, signatures: signatures(checker.getNonNullableType(checker.getTypeOfSymbol(sym)), ts.SignatureKind.Call) });
    }
    const getter = sym.declarations?.find(ts.isGetAccessor);
    const setter = sym.declarations?.find(ts.isSetAccessor);
    // An accessor pair whose setter takes another type (§5). The setter's node is trusted only when the getter's is.
    const readType = checker.getTypeOfSymbol(sym);
    const setNode = getter?.type && setter?.parameters[0]?.type;
    const setType = setNode && checker.getTypeFromTypeNode(getter.type) === readType && checker.getTypeFromTypeNode(setNode);
    return compact({
      kind: "property",
      ...common,
      doc: inherited ? undefined : doc(sym), // an inherited member's docs stay with its declaration
      readonly: (getter && !setter) || hasModifier(decl, ts.ModifierFlags.Readonly),
      type: valueType(sym, (getter ?? decl)?.type, common.optional),
      writeType: setType && setType !== readType ? typeRef(setType, setNode) : undefined,
    });
  }

  /** Overloads in declaration order, each with its own JSDoc. */
  function signatures(type, kind) {
    const construct = kind === ts.SignatureKind.Construct;
    return checker.getSignaturesOfType(type, kind).map((sig) => compact({
      typeParameters: construct ? undefined : typeParams(sig.typeParameters),
      params: sig.parameters.map((p) => {
        const d = p.valueDeclaration;
        const optional = (d && checker.isOptionalParameter(d)) || undefined;
        return compact({ name: p.name, type: valueType(p, d?.type, optional), optional, rest: (d && !!d.dotDotDotToken) || undefined });
      }),
      returns: construct ? undefined : typeRef(checker.getReturnTypeOfSignature(sig), sig.declaration?.type),
      doc: doc(sig),
    }));
  }

  function typeParams(list) {
    return list?.map((tp) => {
      const d = tp.symbol.declarations?.find(ts.isTypeParameterDeclaration);
      const at = (n) => n && typeRef(checker.getTypeFromTypeNode(n), n);
      return compact({ name: tp.symbol.name, constraint: at(d?.constraint), default: at(d?.default) });
    });
  }

  /** The members, index signatures and call/construct signatures of an interface or object type. */
  function structure(type) {
    return {
      members: members(type),
      indexSignatures: checker.getIndexInfosOfType(type).map((i) => compact({ key: typeRef(i.keyType), type: typeRef(i.type), readonly: i.isReadonly || undefined })),
      callSignatures: signatures(type, ts.SignatureKind.Call),
      constructSignatures: signatures(type, ts.SignatureKind.Construct),
    };
  }

  // --- Type references ---

  /**
   * The type of a property, parameter or event. The declared type node is used when it resolves to the same type
   * (it keeps declaration order and alias names); otherwise, e.g. for members of a generic instantiation or a mapped
   * type, the checker's resolved type is used. `optional` drops the `undefined` that `?` adds.
   */
  function valueType(sym, node, optional) {
    const resolved = checker.getTypeOfSymbol(sym);
    const declared = node && checker.getTypeFromTypeNode(node);
    const parts = (t) => (t.isUnion() ? t.types : [t]).filter((x) => !(x.flags & T.Undefined));
    const same = declared && (declared === resolved || (optional && sameSet(parts(declared), parts(resolved))));
    return same ? typeRef(declared, node, { optional }) : typeRef(resolved, undefined, { optional });
  }

  const stack = new Set();

  /**
   * One type as an IR type reference. `node`, if it resolves to `type`, guides the shape: union members keep
   * their written order and named aliases (R6), instead of the checker's flattened, id-ordered list.
   */
  function typeRef(type, node, { optional = false, expandAlias = false } = {}) {
    while (node && ts.isParenthesizedTypeNode(node)) node = node.type;
    if (node && ts.isThisTypeNode(node)) return { kind: "this" }; // the checker resolves it to the class it's read on
    if (node && checker.getTypeFromTypeNode(node) !== type) node = undefined;
    const f = type.flags;

    if (!expandAlias) {
      // An alias of an existing type (`type Options = Partial<…>`) isn't on the type, only on the node that names it.
      const named = node && ts.isTypeReferenceNode(node) && checker.getSymbolAtLocation(node.typeName);
      const alias = named && named.flags & F.Alias ? checker.getAliasedSymbol(named) : named;
      if (alias && alias.flags & F.TypeAlias && inPackage(alias)) return ref(alias, node.typeArguments?.map((n) => checker.getTypeFromTypeNode(n)), node);
      if (type.aliasSymbol && inPackage(type.aliasSymbol)) return ref(type.aliasSymbol, type.aliasTypeArguments, node);
    }
    if (f & T.EnumLike && type.symbol) return ref(f & T.EnumLiteral && !(f & T.Union) ? type.symbol.parent ?? type.symbol : type.symbol);
    if (f & (T.StringLiteral | T.NumberLiteral)) return { kind: "literal", value: type.value };
    if (f & T.BooleanLiteral) return { kind: "literal", value: type === checker.getTrueType() };
    for (const [flag, name] of PRIMITIVES) if (f & flag) return { kind: "primitive", name };
    if (f & T.TypeParameter) {
      // `this` is a type parameter whose symbol is the class itself.
      return type.symbol.flags & (F.Class | F.Interface) ? { kind: "this" } : { kind: "typeParameter", name: type.symbol.name };
    }
    if (type.isUnion()) return union(type, node, optional);
    if (type.isIntersection() && !type.types.every((t) => t.flags & T.Object)) {
      // `string & {}` (R10) or a part that's a type parameter; an intersection of object types is resolved (R11).
      const types = node && ts.isIntersectionTypeNode(node) ? node.types.map((n) => typeRef(checker.getTypeFromTypeNode(n), n)) : type.types.map((t) => typeRef(t));
      return { kind: "intersection", types };
    }
    if (f & (T.Object | T.Intersection)) return object(type, node);
    return unresolved(type);
  }

  function union(type, node, optional) {
    let parts = node && ts.isUnionTypeNode(node)
      ? node.types.map((n) => [checker.getTypeFromTypeNode(n), n])
      : type.types.map((t) => [t]);
    if (optional) parts = parts.filter(([t]) => !(t.flags & T.Undefined));
    // The checker spells `boolean` as `true | false`; fold it back.
    const bools = parts.filter(([t]) => t.flags & T.BooleanLiteral);
    if (bools.length === 2) parts = parts.filter((p) => p !== bools[1]).map((p) => (p === bools[0] ? [checker.getBooleanType()] : p));
    if (parts.length === 1) return typeRef(parts[0][0], parts[0][1]);
    const records = parts.map(([t]) => t).filter((t) => !(t.flags & (T.Null | T.Undefined)));
    const tagged = records.length > 1 && records.every((t) => t.flags & T.Object && !checker.isArrayLikeType(t) && !t.getCallSignatures().length);
    return compact({
      kind: "union",
      types: parts.map(([t, n]) => typeRef(t, n)),
      discriminant: (tagged && discriminant(ts, checker, records)) || undefined, // R19
    });
  }

  function object(type, node) {
    if (checker.isArrayType(type)) {
      const element = node && (ts.isArrayTypeNode(node) ? node.elementType : ts.isTypeOperatorNode(node) && ts.isArrayTypeNode(node.type) ? node.type.elementType : undefined);
      return { kind: "array", element: typeRef(checker.getTypeArguments(type)[0], element) };
    }
    if (checker.isTupleType(type)) {
      const flags = type.target.elementFlags;
      const nodes = node && ts.isTupleTypeNode(node) ? node.elements.map((e) => (ts.isNamedTupleMember(e) ? e.type : e)) : [];
      const required = flags.filter((x) => x & ts.ElementFlags.Required).length;
      return compact({
        kind: "tuple",
        elements: checker.getTypeArguments(type).map((t, i) => typeRef(t, nodes[i], { optional: (flags[i] & ts.ElementFlags.Optional) !== 0 })),
        required: required < flags.length ? required : undefined,
        rest: (flags.at(-1) & ts.ElementFlags.Variable) !== 0 || undefined,
      });
    }
    if (isNamed(type)) {
      const arity = type.target?.typeParameters?.length ?? 0;
      return ref(type.symbol, type.objectFlags & ts.ObjectFlags.Reference ? checker.getTypeArguments(type).slice(0, arity) : undefined, node);
    }
    if (stack.has(type)) return unresolved(type); // an anonymous type that contains itself
    stack.add(type);
    const shape = structure(type);
    stack.delete(type);
    const [call] = shape.callSignatures;
    if (call && shape.callSignatures.length === 1 && !shape.members.length && !shape.indexSignatures.length) return { kind: "function", ...call };
    return compact({ kind: "object", ...shape });
  }

  /** A named type: `module` says where it's declared, and is absent for the platform's own (Promise, HTMLElement). */
  function ref(sym, args, node) {
    const argNodes = node && ts.isTypeReferenceNode(node) ? node.typeArguments ?? [] : [];
    const decl = sym.declarations?.find(moduleOf);
    return compact({ kind: "ref", name: nameOf(sym), module: decl && moduleOf(decl), args: args?.map((a, i) => typeRef(a, argNodes[i])) });
  }

  /** R12: a type the checker can't resolve to something concrete (`keyof this`, `this["$events"][K]`). */
  const unresolved = (type) => ({ kind: "unresolved", text: checker.typeToString(type) });

  // --- JSDoc ---

  /** Its own JSDoc only: without the checker, TypeScript doesn't copy an overridden member's docs from its base. */
  function doc(symOrSig) {
    const text = (parts) => ts.displayPartsToString(parts).trim();
    const tags = symOrSig.getJsDocTags();
    const tag = (...names) => tags.filter((t) => names.includes(t.name)).map((t) => text(t.text));
    const d = compact({
      summary: text(symOrSig.getDocumentationComment()) || undefined,
      default: tag("default", "defaultValue")[0],
      deprecated: tag("deprecated")[0], // "" when it has no message
      since: tag("since")[0],
      see: tag("see"),
    });
    return Object.keys(d).length ? d : undefined;
  }

  // --- Custom elements (design §2) ---

  /** An element: what the manifest adds to its class. Attributes link to the class's properties, which the IR already has. */
  function element({ tag, decl, type }) {
    return compact({
      tag,
      class: manifestType(type),
      doc: manifestDoc(decl),
      attributes: decl.attributes?.map((a) => compact({ name: a.name, property: a.fieldName, type: manifestType(a.type), doc: manifestDoc(a) })),
      events: decl.events?.map((e) => {
        const type = manifestType(e.type);
        const node = manifestNode(e.type);
        // A CustomEvent<T> gives T; a type that isn't an Event is the detail's own (some manifests write it that way).
        const detail = type?.kind === "ref" && type.name === "CustomEvent" && !type.module ? type.args[0]
          : node && !checker.isTypeAssignableTo(checker.getTypeFromTypeNode(node), eventType) ? type : undefined;
        return compact({ name: e.name, detail, type: detail ? undefined : type, doc: manifestDoc(e) });
      }),
      slots: decl.slots?.map((s) => compact({ name: s.name, doc: manifestDoc(s) })),
    });
  }
  const eventType = checker.getDeclaredTypeOfSymbol(checker.resolveName("Event", undefined, F.Type, false));

  /** A manifest type text's node in its virtual module: undefined when it has none, null when the module has errors. */
  function manifestNode(type) {
    const sf = manifest.types.has(type) && program.getSourceFile(manifest.types.get(type).file);
    if (!sf) return undefined;
    if (program.getSyntacticDiagnostics(sf).length || program.getSemanticDiagnostics(sf).length) return null;
    return sf.statements.find(ts.isTypeAliasDeclaration).type;
  }

  /** A manifest type text as an IR type, or `unresolved` when the checker can't resolve it. */
  function manifestType(type) {
    const node = manifestNode(type);
    return node ? typeRef(checker.getTypeFromTypeNode(node), node) : node === null ? { kind: "unresolved", text: type.text } : undefined;
  }

  // --- Modules ---

  const modules = [];
  for (const sf of program.getSourceFiles()) {
    const moduleSym = own.has(path.resolve(sf.fileName)) && checker.getSymbolAtLocation(sf);
    if (!moduleSym) continue;
    const declarations = checker.getExportsOfModule(moduleSym)
      .map((sym) => [sym, sym.declarations?.find((d) => d.getSourceFile() === sf)])
      .filter(([, d]) => d)
      .sort(([, a], [, b]) => a.pos - b.pos)
      .map(([sym, d]) => declaration(sym, d))
      .filter(Boolean);
    if (declarations.length) modules.push({ specifier: moduleOf(sf), declarations });
  }
  modules.sort((a, b) => (a.specifier < b.specifier ? -1 : 1));
  return compact({ version: 1, package: { name: pkg.name, version: pkg.version }, modules, elements: manifest.elements.map(element) });
}

/**
 * The elements of the package's Custom Elements Manifest (`customElements` in package.json), sorted by tag. Each type
 * text the IR needs becomes a virtual module next to the package, for the checker: it imports the names the text's
 * `references` point at and exports the text as `__type`. `types` maps a manifest type to `{ file, text }`.
 */
function readManifest(dir, pkg) {
  const elements = [];
  const types = new Map();
  if (!pkg.customElements) return { elements, types };
  const { modules = [] } = JSON.parse(fs.readFileSync(path.join(dir, pkg.customElements), "utf8"));
  const defined = new Map(modules.flatMap((m) => (m.exports ?? []).filter((e) => e.kind === "custom-element-definition")
    .map((e) => [`${e.declaration.module ?? m.path}#${e.declaration.name}`, e.name])));
  for (const m of modules) {
    for (const decl of m.declarations ?? []) {
      const tag = decl.tagName ?? defined.get(`${m.path}#${decl.name}`);
      if (tag) elements.push({ tag, decl, type: { text: decl.name, references: [{ name: decl.name, module: m.path }] } });
    }
  }
  elements.sort((a, b) => (a.tag < b.tag ? -1 : 1));

  const root = dir.split(path.sep).join("/");
  const add = (type) => {
    if (!type?.text) return;
    const from = (r) => (r.package && r.package !== "global:" ? [r.package, r.module].filter(Boolean).join("/") : !r.package && r.module && `./${r.module}`);
    const imports = (type.references ?? []).filter(from).map((r) => `import type { ${r.name} } from ${JSON.stringify(from(r))};\n`);
    types.set(type, { file: `${root}/__element-type-${types.size}.ts`, text: `${[...new Set(imports)].join("")}export type __type = ${type.text};\n` });
  };
  for (const { decl, type } of elements) {
    add(type);
    for (const a of decl.attributes ?? []) if (!a.fieldName) add(a.type);
    for (const e of decl.events ?? []) add(e.type);
  }
  return { elements, types };
}

/** A manifest entry's docs: its description (or summary), default and deprecation. */
function manifestDoc(entry) {
  const d = compact({ summary: entry.description || entry.summary || undefined, default: entry.default, deprecated: entry.deprecated === true ? "" : entry.deprecated || undefined });
  return Object.keys(d).length ? d : undefined;
}

/** The nearest package.json with a name decides the specifier: `<name>/<path of the .js next to the .d.ts>`. */
function specifierOf(file) {
  for (let d = path.dirname(file); d !== path.dirname(d); d = path.dirname(d)) {
    const json = path.join(d, "package.json");
    const name = fs.existsSync(json) && JSON.parse(fs.readFileSync(json, "utf8")).name;
    if (name) return `${name}/${path.relative(d, file).split(path.sep).join("/").replace(/\.d\.([mc]?)ts$/, ".$1js")}`;
  }
  return undefined;
}

/** A declaration's own name: the symbol of a default export is named `default` (its `export`, ir.md). */
const nameOf = (sym) => sym.declarations?.map((d) => ts.getNameOfDeclaration(d)).find((n) => n && ts.isIdentifier(n))?.text ?? sym.name;
const isNamed = (type) => type.symbol && type.symbol.flags & (F.Class | F.Interface) && !(type.objectFlags & (ts.ObjectFlags.Anonymous | ts.ObjectFlags.Mapped));
const hasModifier = (decl, flag) => (decl && (ts.getCombinedModifierFlags(decl) & flag) !== 0) || undefined;
const sameSet = (a, b) => a.length === b.length && a.every((x) => b.includes(x));

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  const opt = (name) => (args.includes(name) ? args[args.indexOf(name) + 1] : undefined);
  if (!args[0] || args[0].startsWith("--")) {
    console.error("usage: node extract.mjs <package dir> [--profile profile.json] [--out ir.json]");
    process.exit(2);
  }
  const profile = opt("--profile") ? JSON.parse(fs.readFileSync(opt("--profile"), "utf8")) : {};
  const json = JSON.stringify(extract(args[0], { eventMap: profile.events?.mapProperty }), null, 2) + "\n";
  if (opt("--out")) fs.writeFileSync(opt("--out"), json);
  else process.stdout.write(json);
}

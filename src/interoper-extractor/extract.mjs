// Interoper extractor: reads packages' .d.ts files and their Custom Elements Manifests with the TypeScript checker
// and writes IR v1 (docs/ir.md).
//
//   node extract.mjs <package dir>... [--profile profile.json] [--out ir.json]
//
// The first package is the IR's `package`; the others' modules, and those of unlisted packages whose declarations the
// listed ones reference, say which package they're in.
// The profile is read only for the event-map property (`events.mapProperty`, R24); everything else in it is the generator's.
// `dotnet interoper generate` runs it (design §5); this form is for development.

import fs from "node:fs";
import { createRequire } from "node:module";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { createProgram, discriminant, listDts } from "./program.mjs";

const ts = loadTypeScript();

/**
 * TypeScript 6.x (design §4): the package folder in `INTEROPER_TYPESCRIPT` (the CLI passes the consumer's), else the
 * extractor's own (repo development).
 */
function loadTypeScript() {
  const require = createRequire(import.meta.url);
  const dir = process.env.INTEROPER_TYPESCRIPT;
  const loaded = require(dir ? path.resolve(dir) : "typescript");
  if (!/^6\./.test(loaded.version)) {
    throw new Error(`TypeScript ${loaded.version} found; the extractor needs 6.x: run \`npm install -D typescript@6.0.3\``);
  }
  return loaded;
}

const F = ts.SymbolFlags;
const T = ts.TypeFlags;
const PRIMITIVES = [
  [T.Any, "any"], [T.Unknown, "unknown"], [T.String, "string"], [T.Number, "number"], [T.Boolean, "boolean"],
  [T.BigInt, "bigint"], [T.ESSymbolLike, "symbol"], [T.Void, "void"], [T.Undefined, "undefined"], [T.Null, "null"],
  [T.Never, "never"], [T.NonPrimitive, "object"],
];

/** Drops undefined values and empty arrays, so absent means "none" and the JSON stays small. */
const compact = (o) => Object.fromEntries(Object.entries(o).filter(([, v]) => v !== undefined && !(Array.isArray(v) && !v.length)));

/** `packageDirs` is one package's folder or a list of them; the first is the IR's `package`. */
export function extract(packageDirs, { eventMap } = {}) {
  // By real path, so a package reached through a link (a workspace, pnpm) is the same files as the one listed.
  const dirs = [packageDirs].flat().map((d) => fs.realpathSync(d));
  packages.clear(); // package.json files are read once per run
  const pkg = readPackage(dirs[0]);
  const files = dirs.flatMap((d) => listDts(d));
  const manifests = dirs.map((d) => readManifest(d, readPackage(d)));
  const manifestTypes = new Map(manifests.flatMap((m) => [...m.types]));
  const virtual = Object.fromEntries([...manifestTypes.values()].map((t) => [t.file, t.text]));
  const program = createProgram(ts, files, virtual);
  const checker = program.getTypeChecker();

  const own = new Set(files);
  const listedNames = new Set(dirs.map((d) => readPackage(d).name));

  // A declaration file's module: its specifier, package and whether it's internal (ir.md). Undefined for the platform's
  // own typings (lib.dom.d.ts) and the manifests' virtual modules.
  const infos = new Map();
  function infoOf(sf) {
    const file = path.resolve(sf.fileName);
    if (!infos.has(file)) infos.set(file, isPlatform(sf) || Object.hasOwn(virtual, sf.fileName) ? undefined : moduleInfo(file));
    return infos.get(file);
  }
  const moduleOf = (decl) => infoOf(decl.getSourceFile())?.specifier;
  const isPlatform = (node) => program.isSourceFileDefaultLibrary(node.getSourceFile());

  /**
   * The named class or interface a base is, or for an intersection its first constituent that is a named, exported
   * class: a mixin's base `typeof Marker & typeof Draggable` is `Marker`, in either order, and so is TypeScript's own
   * `{ new (...args: any[]): {...} } & typeof Marker`. The mixin's members are listed as inherited.
   */
  const namedBase = (type) => (isNamed(type) ? type : exportedClasses(type)[0]);
  /** An intersection base's named, exported classes: its named base, then its mixins (`Draggable`, when exported). */
  const exportedClasses = (type) => (type.isIntersection() ? type.types.filter((t) => isNamed(t) && t.symbol.flags & F.Class && isExported(t.symbol)) : []);
  /** Whether its module exports a symbol, under any name. */
  const isExported = (sym) => sym.declarations?.length > 0 && exportsOf(sym.declarations[0].getSourceFile()).has(sym);

  const fileExports = new Map();
  /**
   * The declarations a file exports, each with its declaring node and the name it's exported under (ir.md `export`).
   * An export of a declaration elsewhere in the file (`export default Pin;`, `export { Pin as Marker }`) is an alias:
   * the declaration is kept once, under its own name when the file exports that too. A re-export of another file's
   * declaration (`export * from`, `export { X } from`) is that file's.
   */
  function exportsOf(sf) {
    if (fileExports.has(sf)) return fileExports.get(sf);
    const result = new Map();
    const moduleSym = checker.getSymbolAtLocation(sf);
    for (const sym of moduleSym ? checker.getExportsOfModule(moduleSym) : []) {
      const target = sym.flags & F.Alias ? checker.getAliasedSymbol(sym) : sym;
      const d = target.declarations?.find((d) => d.getSourceFile() === sf);
      if (d && (!result.has(target) || sym.name === nameOf(target))) result.set(target, { d, exported: sym.name });
    }
    fileExports.set(sf, result);
    return result;
  }

  let reexports;
  /**
   * Where a declaration of an internal module is imported from (ir.md `importFrom`): the first module, by specifier,
   * that an export reaches and that re-exports it as a value (not `export type`), with the name it's exported under
   * there when that isn't its own, and its `file` as a module's: such a module declares nothing, so it isn't listed.
   */
  function importFrom(sym, decl) {
    if (!infoOf(decl.getSourceFile())?.internal) return undefined;
    if (!reexports) {
      reexports = new Map();
      const reachable = program.getSourceFiles().filter((sf) => infoOf(sf) && !infoOf(sf).internal).sort((a, b) => (infoOf(a).specifier < infoOf(b).specifier ? -1 : 1));
      for (const sf of reachable) {
        const moduleSym = checker.getSymbolAtLocation(sf);
        for (const e of moduleSym ? checker.getExportsOfModule(moduleSym) : []) {
          if (typeOnly(e, sf)) continue;
          const target = e.flags & F.Alias ? checker.getAliasedSymbol(e) : e;
          if (!reexports.has(target) && target.declarations?.length && target.declarations.every((d) => d.getSourceFile() !== sf)) {
            reexports.set(target, { module: infoOf(sf).specifier, file: infoOf(sf).file, name: e.name });
          }
        }
      }
    }
    const found = reexports.get(sym);
    return found && compact({ module: found.module, export: found.name !== nameOf(sym) ? found.name : undefined, file: found.file });
  }

  /**
   * Whether `e`, an export of `sf`, exports nothing at run time: an alias with a type-only link in its chain (`export
   * type { X } from`, `export { type X }`, `import type { X }` then `export { X }`), or a symbol that came through
   * `export *` when no value `export *` reaches it (only `export type * from`).
   */
  function typeOnly(e, sf) {
    return e.flags & F.Alias ? aliasTypeOnly(e) : !starReaches(e, sf, new Set());
  }
  function aliasTypeOnly(alias) {
    for (let s = alias; s && s.flags & F.Alias; s = checker.getImmediateAliasedSymbol(s)) {
      if (s.declarations?.some((d) => ts.isTypeOnlyImportOrExportDeclaration(d))) return true;
    }
    return false;
  }
  /** Whether a value `export * from` of `sf` reaches `sym`: from the file that declares it, or as a value from there. */
  function starReaches(sym, sf, seen) {
    if (seen.has(sf)) return false;
    seen.add(sf);
    for (const st of sf.statements) {
      if (!ts.isExportDeclaration(st) || st.exportClause || !st.moduleSpecifier || st.isTypeOnly) continue;
      const m = checker.getSymbolAtLocation(st.moduleSpecifier);
      const from = m?.declarations?.find(ts.isSourceFile);
      if (!from) continue;
      if (sym.declarations?.some((d) => d.getSourceFile() === from)) return true;
      const inner = checker.getExportsOfModule(m).find((x) => x === sym || (x.flags & F.Alias && checker.getAliasedSymbol(x) === sym));
      if (inner && (inner === sym ? starReaches(sym, from, seen) : !aliasTypeOnly(inner))) return true;
    }
    return false;
  }

  // --- Declarations ---

  /** `exported` is the name the module exports `sym` under (ir.md `export`). */
  function declaration(sym, decl, exported = sym.name) {
    const name = nameOf(sym);
    const base = { name, export: name !== exported ? exported : undefined, importFrom: importFrom(sym, decl), doc: doc(sym) };
    if (sym.flags & F.Class) return classDecl(sym, base);
    if (sym.flags & F.Enum) return enumDecl(sym, base);
    if (sym.flags & F.Interface) {
      const type = checker.getDeclaredTypeOfSymbol(sym);
      return compact({ kind: "interface", ...base, typeParameters: typeParams(type.typeParameters), extends: checker.getBaseTypes(type).map((t) => typeRef(t)).filter((t) => t.kind === "ref"), ...structure(type) });
    }
    if (sym.flags & F.TypeAlias) {
      return compact({ kind: "alias", ...base, typeParameters: typeParams(decl.typeParameters?.map((p) => checker.getTypeAtLocation(p.name))), type: typeRef(checker.getTypeFromTypeNode(decl.type), decl.type, { expandAlias: true }) });
    }
    if (sym.flags & F.Function) return compact({ kind: "function", name, export: base.export, importFrom: base.importFrom, signatures: signatures(checker.getTypeOfSymbol(sym), ts.SignatureKind.Call) });
    if (sym.flags & F.Variable) {
      return compact({ kind: "variable", ...base, readonly: (ts.getCombinedNodeFlags(decl) & ts.NodeFlags.Const) !== 0 || undefined, type: valueType(sym, decl.type) });
    }
    return undefined; // a namespace (`declare namespace`) isn't in IR v1
  }

  function classDecl(sym, base) {
    const type = checker.getDeclaredTypeOfSymbol(sym);
    const statics = checker.getTypeOfSymbol(sym);
    const decl = sym.declarations.find(ts.isClassLike);
    const [baseType] = checker.getBaseTypes(type);
    const superclass = baseType && (namedBase(baseType) ?? baseType);
    const mapProp = eventMap && type.getProperty(eventMap);
    const implemented = decl?.heritageClauses?.find((h) => h.token === ts.SyntaxKind.ImplementsKeyword)?.types ?? [];
    return compact({
      kind: "class",
      ...base,
      abstract: hasModifier(decl, ts.ModifierFlags.Abstract),
      typeParameters: typeParams(type.typeParameters),
      extends: superclass && typeRef(superclass),
      mixins: baseType && !isNamed(baseType) ? exportedClasses(baseType).slice(1).map((t) => typeRef(t)) : undefined,
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
   * anonymous base, a mixin or a mapped type are kept, as inherited, unless the platform declares them (DOM members aren't bound).
   * A member keyed by a symbol, or named like a type-only marker (`@types`), is left out.
   */
  function members(type, isStatic = false) {
    const owners = type.symbol?.declarations ?? [];
    const bases = type.objectFlags & ts.ObjectFlags.ClassOrInterface ? checker.getBaseTypes(type).map(namedBase).filter(Boolean) : [];
    return checker.getPropertiesOfType(type).flatMap((p) => {
      const decls = p.declarations ?? []; // none for a mapped type's synthetic keys (Record<"x" | "y", T>)
      if (p.name === eventMap || p.name === "prototype" || (decls.length && decls.every(isPlatform))) return [];
      if (String(p.escapedName).startsWith("__@")) return []; // keyed by a symbol ([Symbol.iterator]), which has no name to bind
      if (p.name.startsWith("@")) return []; // a type-only marker ("@types"), which no object has
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
    // The checker's members, not the written ones: nested unions and union aliases flattened, repeats dropped (R6).
    const records = type.types.filter((t) => !(t.flags & (T.Null | T.Undefined)));
    // Object types, and intersections of them (`Props & { type: "heat" }`), which TypeScript doesn't flag as objects.
    const tagged = records.length > 1 && records.every((t) => t.flags & (T.Object | T.Intersection) && !checker.isArrayLikeType(t) && !t.getCallSignatures().length);
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
    // R11: an intersection's named parts (`Props` in `Props & { type: "poly" }`), which its record is named after.
    const bases = type.isIntersection() ? type.types.filter(isNamed).map((t) => typeRef(t)) : undefined;
    return compact({ kind: "object", bases, ...shape });
  }

  /**
   * A named type: `module` says where it's declared, and is absent for the platform's own (Promise, HTMLElement). A
   * declaration of a package that isn't listed is added to the IR, under its own module.
   */
  function ref(sym, args, node) {
    const argNodes = node && ts.isTypeReferenceNode(node) ? node.typeArguments ?? [] : [];
    const decl = sym.declarations?.find(moduleOf);
    // Another copy of a listed package (one a dependency installed for itself) has the same modules: they're listed.
    if (decl && !own.has(path.resolve(decl.getSourceFile().fileName)) && !listedNames.has(infoOf(decl.getSourceFile()).package)) reached.add(sym);
    return compact({ kind: "ref", name: nameOf(sym), module: decl && moduleOf(decl), args: args?.map((a, i) => typeRef(a, argNodes[i])) });
  }
  /** Whether a package declares a symbol (an alias of the platform's, such as `Partial`, is resolved in place). */
  const inPackage = (sym) => sym.declarations?.some(moduleOf);
  /** The declarations of unlisted packages that the IR references, in the order they're met. */
  const reached = new Set();

  /** R12: a type the checker can't resolve to something concrete (`keyof this`, `this["$events"][K]`). */
  const unresolved = (type) => ({ kind: "unresolved", text: checker.typeToString(type) });

  // --- JSDoc ---

  /** Its own JSDoc only: without the checker, TypeScript doesn't copy an overridden member's docs from its base. */
  function doc(symOrSig) {
    const text = (parts = []) => parts.map(linkPart).join("").trim();
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
    const sf = manifestTypes.has(type) && program.getSourceFile(manifestTypes.get(type).file);
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

  // Every export of the listed packages' files, then the custom elements, then what they reach in other packages.
  const listed = new Map();
  for (const sf of program.getSourceFiles()) {
    if (own.has(path.resolve(sf.fileName))) listed.set(sf, [...exportsOf(sf)].map(([sym, { d, exported }]) => ({ d, decl: declaration(sym, d, exported) })));
  }
  const elements = manifests.flatMap((m) => m.elements).sort((a, b) => (a.tag < b.tag ? -1 : 1)).map(element);
  for (const sym of reached) { // a Set iterates what's added while it does, so this follows references transitively
    const sf = sym.declarations.find(moduleOf).getSourceFile();
    const entry = exportsOf(sf).get(sym);
    if (!entry) continue; // not exported: only its references are in the IR
    if (!listed.has(sf)) listed.set(sf, []);
    listed.get(sf).push({ d: entry.d, decl: declaration(sym, entry.d, entry.exported) });
  }
  const modules = [];
  for (const [sf, entries] of listed) {
    const declarations = entries.filter((e) => e.decl).sort((a, b) => a.d.pos - b.d.pos).map((e) => e.decl);
    const { specifier, package: name, file, internal } = infoOf(sf);
    if (declarations.length) modules.push(compact({ specifier, package: name !== pkg.name ? name : undefined, file, internal, declarations }));
  }
  modules.sort((a, b) => (a.specifier < b.specifier ? -1 : 1));
  return compact({ version: 1, package: { name: pkg.name, version: pkg.version }, modules, elements });
}

/**
 * The elements of the package's Custom Elements Manifest (`customElements` in package.json). Each type
 * text the IR needs becomes a virtual module next to the package, for the checker: it imports the names the text's
 * `references` point at and exports the text as `__type`. `types` maps a manifest type to `{ file, text }`. A module
 * path of the package itself is imported through its own `exports` when one reaches it (`components/zoom-label` as
 * `<name>/components/zoom-label`, for `"./components/*"`), and otherwise as a file of the package by its full name
 * (`dist/index.js`, or `dist/legend` as `./dist/legend.js` or `./dist/legend/index.js`, whichever has its .d.ts);
 * the checker then follows re-exports to the declaring module.
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

  const root = dir.split(path.sep).join("/");
  const map = pkg.exports === undefined ? undefined : exportsMap(pkg.exports);
  const own = (module) => {
    const subpath = module.replace(/^\.?\//, "").replace(/\/$/, "");
    if (map && typeof resolveExport(map, `./${subpath}`) === "string") return `${pkg.name}/${subpath}`;
    // A file of the package, by its full name, as an ES module imports it: the path, its .js, or its folder's index.js.
    const file = [subpath, `${subpath}.js`, `${subpath}/index.js`].find((f) => /\.[mc]?js$/.test(f) && fs.existsSync(path.join(dir, f.replace(/\.([mc]?)js$/, ".d.$1ts"))));
    return `./${file ?? subpath}`;
  };
  const add = (type) => {
    if (!type?.text) return;
    const from = (r) => (r.package && r.package !== "global:" ? [r.package, r.module].filter(Boolean).join("/") : !r.package && r.module && own(r.module));
    const imports = (type.references ?? []).filter(from).map((r) => `import type { ${r.name} } from ${JSON.stringify(from(r))};\n`);
    // An ES module whatever the package's `type`, so imports resolve with the `import` condition.
    types.set(type, { file: `${root}/__element-type-${types.size}.mts`, text: `${[...new Set(imports)].join("")}export type __type = ${type.text};\n` });
  };
  for (const { decl, type } of elements) {
    add(type);
    for (const a of decl.attributes ?? []) add(a.type);
    for (const e of decl.events ?? []) add(e.type);
  }
  return { elements, types };
}

/**
 * A JSDoc display part, with each `{@link}` (or `@linkcode`, `@linkplain`) spelled `{@link Name}` or `{@link Name text}`:
 * TypeScript's own `displayPartsToString` runs a link's name into its text (`{@link Xtext}`).
 */
function linkPart(part, i, parts) {
  switch (part.kind) {
    case "link": return part.text.startsWith("{") ? "{@link " : part.text;
    case "linkName": return part.text.trim();
    case "linkText": return (parts[i - 1].kind === "linkName" ? " " : "") + part.text.trim();
    default: return part.text;
  }
}

/** A manifest entry's docs: its description (or summary), default and deprecation. */
function manifestDoc(entry) {
  const d = compact({ summary: entry.description || entry.summary || undefined, default: entry.default, deprecated: entry.deprecated === true ? "" : entry.deprecated || undefined });
  return Object.keys(d).length ? d : undefined;
}

const packages = new Map();
/** A folder's package.json, or undefined; cached for the run. */
function readPackage(dir) {
  const json = path.join(dir, "package.json");
  if (!packages.has(json)) packages.set(json, fs.existsSync(json) ? JSON.parse(fs.readFileSync(json, "utf8")) : undefined);
  return packages.get(json);
}

/**
 * A declaration file's module (ir.md): the nearest package.json with a name is its package, and its specifier is how
 * that package's `exports` reach the file (`@acme/widgets/legend`). Without `exports` it's `<name>/<path of the .js
 * next to the .d.ts>`, and so it is when no export reaches the file, which is then internal. It's internal too, under
 * its export's subpath, when the exports that reach it are for its typings only and there's no .js next to them.
 * `file` is, for import maps, the JS file a browser loads for the subpath (its JS conditions' target, else that `.js`),
 * when that isn't the subpath itself.
 */
function moduleInfo(file) {
  for (let d = path.dirname(file); d !== path.dirname(d); d = path.dirname(d)) {
    const pkg = readPackage(d);
    if (!pkg?.name) continue;
    const dts = `./${path.relative(d, file).split(path.sep).join("/")}`;
    const js = dts.replace(/\.d\.([mc]?)ts$/, ".$1js");
    const map = pkg.exports === undefined ? undefined : exportsMap(pkg.exports);
    // Of the exports that reach it, one that loads JS in a browser, else the first. With neither a JS target nor a .js
    // next to the typings, that export is for the typings only: the module keeps its subpath, but nothing can import
    // it, so it's internal.
    const subpaths = map ? subpathsOf(map, [js, dts]) : [js];
    const withJs = map ? subpaths.find((s) => typeof resolveExport(map, s, JS_CONDITIONS) === "string") : undefined;
    const subpath = withJs ?? subpaths[0];
    const typingsOnly = map !== undefined && subpath !== undefined && withJs === undefined && !fs.existsSync(path.join(d, js));
    const real = withJs !== undefined ? resolveExport(map, withJs, JS_CONDITIONS) : js;
    return compact({ specifier: pkg.name + (subpath ?? js).slice(1), package: pkg.name,
      file: subpath !== undefined && !typingsOnly && real !== subpath ? real.slice(2) : undefined, internal: subpath === undefined || typingsOnly || undefined });
  }
  return undefined;
}

/** An `exports` field as a map of subpaths: a string, an array or a map of conditions is the `"."` export. */
const exportsMap = (exports) => (exports && typeof exports === "object" && !Array.isArray(exports) && Object.keys(exports).some((k) => k.startsWith(".")) ? exports : { ".": exports });

/**
 * The subpaths of an `exports` map that reach a file (its `.js` or its `.d.ts`, `files`), in order: the exact keys
 * whose target is the file, then the `*` patterns, each in the map's order. A subpath counts only when resolving it
 * forward gives the file: a pattern with a longer prefix, or a null target, can shadow it.
 */
function subpathsOf(map, files) {
  const entries = Object.entries(map).map(([key, value]) => [key, target(value)]).filter(([, t]) => typeof t === "string");
  const candidates = [
    ...entries.filter(([key, t]) => !key.includes("*") && files.includes(t)).map(([key]) => key),
    ...entries.filter(([key, t]) => key.includes("*") && t.includes("*")).flatMap(([key, t]) => {
      const [prefix, suffix] = [t.slice(0, t.indexOf("*")), t.slice(t.indexOf("*") + 1)];
      const file = files.find((f) => f.length > prefix.length + suffix.length && f.startsWith(prefix) && f.endsWith(suffix));
      return file ? [key.replace("*", file.slice(prefix.length, file.length - suffix.length))] : [];
    }),
  ];
  return candidates.filter((subpath) => files.includes(resolveExport(map, subpath)));
}

/**
 * What a subpath resolves to through an `exports` map, as Node resolves it: its exact key, or else the pattern whose
 * prefix is the longest (then the longest key), with `*` replaced; null or undefined when nothing is exported there.
 */
function resolveExport(map, subpath, conditions = CONDITIONS) {
  if (!subpath.includes("*") && Object.hasOwn(map, subpath)) return target(map[subpath], conditions);
  let best;
  for (const key of Object.keys(map)) {
    const star = key.indexOf("*");
    if (star < 0 || key.indexOf("*", star + 1) >= 0) continue;
    const [prefix, suffix] = [key.slice(0, star), key.slice(star + 1)];
    if (subpath.length < key.length || !subpath.startsWith(prefix) || !subpath.endsWith(suffix)) continue;
    if (!best || prefix.length > best.prefix.length || (prefix.length === best.prefix.length && key.length > best.key.length)) best = { key, prefix, suffix };
  }
  const t = best && target(map[best.key], conditions);
  return typeof t === "string" ? t.replaceAll("*", subpath.slice(best.prefix.length, subpath.length - best.suffix.length)) : t;
}

/** The conditions TypeScript resolves an `import`'s typings with, and those a browser loads its JS with (import maps). */
const CONDITIONS = new Set(["types", "import", "default"]);
const JS_CONDITIONS = new Set(["browser", "import", "default"]);

/**
 * What an export's value resolves to with the given conditions (by default as TypeScript resolves an `import`): a
 * string; a condition object's first condition, in its own order, that is one of them and resolves, nested ones too;
 * an array's first entry that resolves to a string. Null excludes it, and undefined means none applies.
 */
function target(value, conditions = CONDITIONS) {
  if (typeof value === "string" || value === null) return value;
  if (Array.isArray(value)) return value.map((v) => target(v, conditions)).find((t) => typeof t === "string");
  if (typeof value !== "object") return undefined;
  for (const [condition, nested] of Object.entries(value)) {
    const t = conditions.has(condition) ? target(nested, conditions) : undefined;
    if (t !== undefined) return t;
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
  const valueOptions = ["--profile", "--out"];
  const dirs = args.filter((a, i) => !a.startsWith("--") && !valueOptions.includes(args[i - 1]));
  if (!dirs.length) {
    console.error("usage: node extract.mjs <package dir>... [--profile profile.json] [--out ir.json]");
    process.exit(2);
  }
  const profile = opt("--profile") ? JSON.parse(fs.readFileSync(opt("--profile"), "utf8")) : {};
  const json = JSON.stringify(extract(dirs, { eventMap: profile.events?.mapProperty }), null, 2) + "\n";
  if (opt("--out")) fs.writeFileSync(opt("--out"), json);
  else process.stdout.write(json);
}

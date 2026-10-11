# IR v1

The extractor's output and the generator's input ([design §4](design.md#4-generator-architecture)): one JSON document for one or more packages, language-neutral and versioned. It records what the typings and the Custom Elements Manifest say, resolved by the TypeScript checker ([mapping §7](mapping.md#7-what-the-extractor-must-resolve-with-the-checker)). It makes no C# decisions: classification, naming and the profile are the generator's.

```bash
dotnet interoper generate @acme/maps @acme/map-widgets --api async --namespace Acme.Maps --out Generated/AcmeMaps   # writes Generated/AcmeMaps/.interoper/ir.json
```

`dotnet interoper generate` runs the extractor and caches its IR ([design §5](design.md#5-consumer-workflow-and-packages)). For development, run it directly:

```bash
node src/interoper-extractor/extract.mjs fixtures/demo-maps --profile fixtures/demo-maps/profile.json --out ir.json
node src/interoper-extractor/extract.mjs node_modules/@acme/maps node_modules/@acme/map-widgets --out ir.json
```

It takes the folders of the packages to list; the first is the IR's `package`. Folders are taken by their real path, so a package linked into `node_modules` (a workspace, pnpm) is the same files as when it's reached through the link, and another copy of a listed package (one a dependency installed for itself) adds nothing: its modules are listed already. The profile is read only for its event-map property (`events.mapProperty`). The fixtures' IRs are committed as golden files, which are the generator's test input: [`fixtures/demo-maps/demo-maps.ir.json`](../fixtures/demo-maps/demo-maps.ir.json), and [`fixtures/demo-widgets/demo-widgets.ir.json`](../fixtures/demo-widgets/demo-widgets.ir.json), what demo-widgets adds to it (its modules and elements) when both are extracted from a `node_modules` that has them, demo-maps first. After an intended change, regenerate them with `UPDATE_GOLDEN=1 node --test src/interoper-extractor/test/extract.test.mjs` and review the diff.

## Conventions

- **Deterministic.** Modules are sorted by specifier and elements by tag, whatever their package, declarations follow source order, members follow the checker's order (own, then inherited), and union members follow the order they're written in. Two runs give byte-identical output.
- **Absent means none.** Empty lists, `false` flags and missing docs are left out.

## Document

```jsonc
{ "version": 1, "package": { "name": "@acme/demo-maps", "version": "0.0.0" },
  "modules": [{ "specifier": "@acme/demo-maps/geometry.js", "declarations": [ … ] },
              { "specifier": "@acme/demo-widgets/legend", "package": "@acme/demo-widgets", "file": "dist/legend.js", "declarations": [ … ] },
              { "specifier": "@acme/demo-widgets/dist/support/entry.js", "package": "@acme/demo-widgets", "internal": true, "declarations": [ … ] }],
  "elements": [{ "tag": "demo-map", "class": { "kind": "ref", "name": "DemoMapElement", "module": "@acme/demo-maps/demo-map.js" }, … }] }
```

- **Packages.** `package` is the first package listed. A module of any other package says its `package`: a listed one, or a package that isn't listed but declares something a listed one references.
- **Specifiers.** A module's package is the nearest `package.json` with a name, and its specifier is how that package's `exports` reach the file: `"./legend": { "types": "./dist/legend.d.ts", "import": "./dist/legend.js" }` gives `@acme/demo-widgets/legend`. A target is resolved as TypeScript resolves an import, with the conditions `types`, `import` and `default`: a string; a condition object's first of those conditions that resolves, nested ones too, falling through one that doesn't; an array's first entry that does; `null` excludes. It matches the `.d.ts` itself or the `.js` next to it. The keys whose target matches are tried, the exact ones first, then the `*` patterns, each in the order they're written, and a key counts only when resolving it forward, as Node does (its exact key, or else the pattern with the longest prefix), gives the file back: a longer pattern or a `null` can shadow it. A string `exports`, an array, or a map of conditions is the `"."` export, the package's name alone. Without `exports` a specifier is the package's name plus the path of the `.js` (`@acme/demo-maps/layers/Layer.js`), and so is one that no export reaches, which also has `"internal": true`: it can't be imported by its specifier ([design §3.5](design.md#35-module-loading)). A module of an export has `file`, for import maps ([design §3.5](design.md#35-module-loading)), when the JS a browser loads for its subpath isn't at that subpath: the path in the package of its export's target with the conditions `browser`, `import` and `default` (in the condition object's own order, as Node takes them), or of the `.js` next to the `.d.ts` when no JS target applies. `dist/legend.js` for `@acme/demo-widgets/legend`; `esm/a.js` for `"./a.js": { "types": "./a.d.ts", "import": "./esm/a.js" }`. A mirror such as demo-maps' `"./*.js": "./*.js"` has none, and nor has a module without `exports` or an internal one, whose specifier is its file's path.
- **What's listed.** Every exported declaration of a listed package's files, under the module that declares it. A declaration of an unlisted package is listed when the IR references it, transitively, so a dependency brings only the declarations its dependents reach. The platform's own types (the DOM's, `Promise`) are never listed, and references to them have no `module`.

## Declarations

Every declaration has `kind`, `name` and an optional `doc` (a function's are on its signatures).

- `name` is the declaration's own name, also for a default export, and references (`ref`) use it.
- `export` is the name the module exports it under, when that differs: `"default"` for `export default class TileLayer`. An export of a declaration elsewhere in the file (`export default Pin;`, `export { Pin as Marker }`) is that declaration, listed once: with no `export` when the module also exports it under its own name. A re-export of another file's declarations (`export * from`, `export { X } from`) isn't listed again: they're the declaring module's, and so are references through it, the manifest's included. A namespace (`declare namespace`, `export * as ns`) isn't in IR v1.
- `importFrom` is where a declaration of an internal module is imported from, when a module an export reaches re-exports it (the common `"exports": { ".": "./index.js" }` with `export * from "./impl.js"`): `{ module, export?, file? }`, the first such module by specifier, the name it's exported under there when that isn't `name`, and its `file` as a module's, since it declares nothing and so isn't listed (`dist/index.js` for `@acme/demo-widgets`). A declaration of an internal module without it can't be imported.

| `kind` | Fields |
|---|---|
| `class` | `abstract`, `typeParameters`, `extends` (one type), `implements`, `constructors` (signatures), `members`, `events` |
| `interface` | `typeParameters`, `extends` (named bases), `members`, `indexSignatures`, `callSignatures`, `constructSignatures` |
| `alias` | `typeParameters`, `type` (resolved: `Partial<Pick<…>>` becomes an `object`, R12) |
| `enum` | `members`: `{ name, value, doc }` |
| `function` | `signatures` |
| `variable` | `type`, `readonly` (`const`) |

- **Members** are `{ kind: "property", name, type, writeType?, readonly?, optional?, static?, abstract?, inherited?, doc? }` or `{ kind: "method", name, signatures, optional?, static?, abstract?, inherited? }`, whose docs are on its signatures.
  - `writeType` is the setter's type when an accessor pair's types differ (§5).
  - Members a named base declares are listed on that base only: follow `extends`. Members of an anonymous base or a mapped type are listed, with `inherited`. Members the platform declares (the DOM's) are left out. So are members keyed by a symbol (`[Symbol.iterator]()`), which have no name to bind.
  - A mixin's base, an intersection (`class Pin extends PinBase`, where `PinBase: typeof Marker & typeof Draggable`), is its first part that is a named, exported class, in any order and next to anonymous parts (`{ new (...args: any[]): {…} } & typeof Marker`): `extends` is `Marker`, and the other parts' members are listed with `inherited`.
- **Events** (R24) are `{ name, type, doc? }`, the checker-resolved properties of the event-map property's type on that class. The event-map property itself isn't a member.
- **Signatures** are `{ typeParameters?, params, returns, doc? }`, one per overload (R23), each with its own JSDoc. Constructors have no `returns`. A parameter is `{ name, type, optional?, rest? }`; a rest parameter's type is its array.
- **Type parameters** are `{ name, constraint?, default? }`.
- **Index signatures** are `{ key, type, readonly? }`.
- **Docs** are `{ summary?, default?, deprecated?, since?, see? }`, from the declaration's own JSDoc (an override doesn't copy its base's): `see` is a list, and `deprecated` is `""` when the tag has no message. Line breaks stay, and a link (`{@link}`, `{@linkcode}`, `{@linkplain}`) is spelled `{@link Name}` or `{@link Name text}`.
- An `optional` member or parameter's `type` doesn't include the `undefined` that `?` adds.

## Elements

The custom elements of the listed packages' Custom Elements Manifests (`customElements` in `package.json`), for the Blazor components ([design §2](design.md#2-what-we-generate)). An element is a tag plus what the manifest adds to its class; the class's properties and methods are its declaration's members.

```jsonc
{ "tag": "demo-map", "class": { "kind": "ref", "name": "DemoMapElement", "module": "@acme/demo-maps/demo-map.js" },
  "doc": { … }, "attributes": [ … ], "events": [ … ], "slots": [ … ] }
```

- An element's tag is its declaration's `tagName`, or the name of a `custom-element-definition` export of it.
- **Attributes** are `{ name, property?, type?, doc? }`. `property` names the class member the attribute reflects, which has the property's type; `type` is the manifest's type for the attribute, which can be narrower (a `string` attribute over a union property).
- **Events** are `{ name, detail?, type?, doc? }`. A `CustomEvent<T>` gives `detail` (T), and so does a type that isn't an `Event`, because some manifests give the detail's type; any other event type is `type`. Neither means a plain `Event`.
- **Slots** are `{ name, doc? }`; the default slot's name is `""`.
- **Docs** come from the manifest: `summary` is its `description` (or `summary`), `default` an attribute's default (a JS expression), `deprecated` as for JSDoc.
- **Types** are the manifest's type texts, resolved by the checker in the scope of the names their `references` point at, so they're the same IR types as the typings'. A manifest module path of the package itself is resolved as an import of the package's own `exports` when one reaches it (`components/zoom-label` is `@acme/demo-widgets/components/zoom-label` when `"./components/*"` exports `./dist/components/*/index.js`), and otherwise as the package's file (`dist/index.js`). A reference to a module that re-exports the name (an `index.js` with `export * from "./legend.js"`) resolves to the declaring module. A text that doesn't resolve is `unresolved`. `class` is resolved the same way, from the declaration's name and module; the generator reports an element whose class is `unresolved` (IOP2007, [mapping R28](mapping.md)).

## Types

| `kind` | Fields | Example |
|---|---|---|
| `primitive` | `name`: `string`, `number`, `boolean`, `bigint`, `symbol`, `void`, `undefined`, `null`, `never`, `any`, `unknown`, `object` | |
| `literal` | `value` (string, number or boolean) | `"point"`, `2` |
| `ref` | `name`, `module` (absent for the platform's own types), `args` | `Point`, `Promise<void>`, `Collection<Marker>` |
| `typeParameter` | `name` | `T` |
| `this` | | `clone(): this` |
| `array` | `element` | `number[]`, `readonly T[]` |
| `tuple` | `elements`, `required` (when some are optional), `rest` (the last element repeats) | `[number, number]` |
| `union` | `types`, `discriminant` (R19) | `Style` on `type` |
| `intersection` | `types`, when a part isn't an object type | `string & {}` (R10), `T & Named` |
| `object` | `members`, `indexSignatures`, `callSignatures`, `constructSignatures` | `{ zoom: number }`, resolved mapped types and intersections (R11) |
| `function` | a signature's fields: an object with one call signature and nothing else | `(marker: Marker) => string` |
| `unresolved` | `text` (the checker's spelling), for IOP2002 | `keyof this`, `this["$events"][K]` |

- A union keeps named members as written: `FlyToTarget` is `Shape | [number, number] | Camera`, not the checker's flattened list. The generator flattens it (R6). `boolean` is a `primitive`, never `true | false`.
- A `discriminant` is the first property that every non-nullish member declares with a distinct literal type.
- Aliases are `ref`s when a package declares them, a dependency included. The platform's, such as `Partial`, are resolved in place.

# IR v1

The IR is the extractor's output and the generator's input ([design §4](design.md#4-generator-architecture)). It is one versioned JSON document for one or more packages. It records what the typings and the Custom Elements Manifest say, resolved by the TypeScript checker. It makes no C# decisions; classification, naming and the profile belong to the generator.

`dotnet interoper generate` runs the extractor and caches the IR in `<out>/.interoper/ir.json` ([design §5](design.md#5-consumer-workflow-and-packages)). To run the extractor directly:

```bash
node src/interoper-extractor/extract.mjs fixtures/demo-maps --profile fixtures/demo-maps/profile.json --out ir.json
node src/interoper-extractor/extract.mjs node_modules/@acme/maps node_modules/@acme/map-widgets --out ir.json
```

It takes the folders of the packages to list. The first one is the IR's `package`. Folders are resolved to their real path, so a linked package is read once. The profile is read only for `events.mapProperty`.

The fixtures' IRs are committed as golden files, and the generator's tests read them:

- [`fixtures/demo-maps/demo-maps.ir.json`](../fixtures/demo-maps/demo-maps.ir.json)
- [`fixtures/demo-widgets/demo-widgets.ir.json`](../fixtures/demo-widgets/demo-widgets.ir.json): what demo-widgets adds when both are extracted together, demo-maps first.

After an intended change, regenerate them with `UPDATE_GOLDEN=1 node --test src/interoper-extractor/test/extract.test.mjs` and review the diff.

## Conventions

- Output is deterministic. Modules are sorted by specifier and elements by tag. Declarations follow source order. Members follow the checker's order, own then inherited. Union members keep their written order.
- Absent means none. Empty lists, `false` flags and missing docs are left out.

## Document

```jsonc
{ "version": 1, "package": { "name": "@acme/demo-maps", "version": "0.0.0" },
  "modules": [{ "specifier": "@acme/demo-maps/geometry.js", "declarations": [ … ] },
              { "specifier": "@acme/demo-widgets/legend", "package": "@acme/demo-widgets", "file": "dist/legend.js", "declarations": [ … ] },
              { "specifier": "@acme/demo-widgets/dist/support/entry.js", "package": "@acme/demo-widgets", "internal": true, "declarations": [ … ] }],
  "elements": [{ "tag": "demo-map", "class": { "kind": "ref", "name": "DemoMapElement", "module": "@acme/demo-maps/demo-map.js" }, … }] }
```

### Packages

`package` is the first package listed. A module of any other package, listed or only referenced, has its own `package` field.

### Specifiers

A module's package is the nearest `package.json` with a name. Its specifier is how that package's `exports` reach the file. For example, `"./legend": { "types": "./dist/legend.d.ts", "import": "./dist/legend.js" }` gives `@acme/demo-widgets/legend`.

Export targets are resolved as TypeScript resolves an import, with the conditions `types`, `import` and `default`:

- A string is the target.
- A condition object uses the first of those conditions that resolves, including nested ones.
- An array uses its first entry that resolves.
- `null` excludes the path.

A target matches the `.d.ts` itself or the `.js` next to it. Exact keys are tried first, then `*` patterns, each in written order. A key only counts if resolving it forward, as Node does, gives the same file back. So a longer pattern or a `null` can shadow it. A string, array or condition-map `exports` is the `"."` export, the package name alone.

When several exports reach a file, the specifier is the first one with a JS target (`browser`, `import` or `default`), or else the first one.

A module without `exports`, or one no export reaches, gets the package name plus the path of its `.js` (`@acme/demo-maps/layers/Layer.js`). If no export reaches it, it also has `"internal": true`, because it can't be imported by that specifier ([design §3.5](design.md#35-module-loading)).

A types-only export with no `.js` next to it (`"./base": { "types": "./base.d.ts" }`) keeps the export's subpath (`@acme/extra/base`), so namespaces and profile keys stay stable. It is also `internal`, since no JS loads it.

### File

A module reached by an export has `file` when the JS a browser loads isn't at the specifier's subpath. Import maps use it ([design §3.5](design.md#35-module-loading)). It is the export's target under `browser`, `import` or `default`, in the order the condition object lists them, or the `.js` next to the `.d.ts` when there is no JS target. Examples:

- `dist/legend.js` for `@acme/demo-widgets/legend`.
- `esm/a.js` for `"./a.js": { "types": "./a.d.ts", "import": "./esm/a.js" }`.

A mirror export such as demo-maps' `"./*.js": "./*.js"` has no `file`. Neither does a module without `exports` or an internal module, whose specifier is already its file's path.

### What's listed

Every exported declaration in a listed package's files is listed, under the module that declares it. A declaration from an unlisted package is listed only when the IR references it, directly or through other listed declarations. Platform types (the DOM's, `Promise`) are never listed, and references to them have no `module`.

## Declarations

Every declaration has `kind`, `name` and an optional `doc`. A function's docs are on its signatures.

- `name` is the declaration's own name, also for a default export. References (`ref`) use it.
- `export` is the name the module exports it under, when that differs. It is `"default"` for `export default class TileLayer`. An export of a declaration elsewhere in the same file (`export default Pin;`, `export { Pin as Marker }`) points to that declaration, listed once. It has no `export` when the module also exports it under its own name.
- Re-exports of another file (`export * from`, `export { X } from`) aren't listed again. The declarations belong to the declaring module, and so do references through the re-export, including the manifest's. Namespaces (`declare namespace`, `export * as ns`) aren't in IR v1.
- `importFrom` says where to import a declaration of an internal module from. It is set when a module that an export reaches re-exports it as a value, as in `"exports": { ".": "./index.js" }` with `export * from "./impl.js"`. Type-only re-exports (`export type`, `export { type X }`, `import type` then `export`) don't count. Its shape is `{ module, export?, file? }`: the first such module by specifier, the export name there when it isn't `name`, and that module's `file`. A declaration of an internal module without `importFrom` can't be imported.

| `kind` | Fields |
|---|---|
| `class` | `abstract`, `typeParameters`, `extends` (one type), `mixins` (named classes), `implements`, `constructors` (signatures), `members`, `events` |
| `interface` | `typeParameters`, `extends` (named bases), `members`, `indexSignatures`, `callSignatures`, `constructSignatures` |
| `alias` | `typeParameters`, `type` (resolved: `Partial<Pick<…>>` becomes an `object`, R12) |
| `enum` | `members`: `{ name, value, doc }` |
| `function` | `signatures` |
| `variable` | `type`, `readonly` (`const`) |

### Members

A member is one of:

- `{ kind: "property", name, type, writeType?, readonly?, optional?, static?, abstract?, inherited?, doc? }`
- `{ kind: "method", name, signatures, optional?, static?, abstract?, inherited? }`, with docs on its signatures.

`writeType` is the setter's type when an accessor pair's types differ ([mapping §5](mapping.md#5-members)).

Members declared by a named base are listed on that base only, so follow `extends`. Members of an anonymous base or a mapped type are listed with `inherited`. Platform members (the DOM's), symbol-keyed members (`[Symbol.iterator]()`) and members whose name starts with `@` (type-only markers such as `"@setterTypes"`, which no object has) are left out.

A mixin's base is an intersection, as in `class Pin extends PinBase` where `PinBase: typeof Marker & typeof Draggable`. Its `extends` is the first part that is a named, exported class, wherever it sits in the intersection. Here that's `Marker`. The other parts' members are listed with `inherited`. The other parts that are named, exported classes are its `mixins`, in order: `Draggable` isn't exported, so `Pin` has none, and `TileLayer` (`typeof Layer & typeof Refreshable`) has `Refreshable`.

### Other fields

- Events (R24) are `{ name, type, doc? }`: the properties of the class's event-map property type, resolved by the checker. The event-map property itself isn't a member.
- Signatures are `{ typeParameters?, params, returns, doc? }`, one per overload (R23), each with its own JSDoc. Constructors have no `returns`. A parameter is `{ name, type, optional?, rest? }`. A rest parameter's type is its array.
- Type parameters are `{ name, constraint?, default? }`.
- Index signatures are `{ key, type, readonly? }`.
- Docs are `{ summary?, default?, deprecated?, since?, see? }`, from the declaration's own JSDoc. An override doesn't copy its base's. `see` is a list, and `deprecated` is `""` when the tag has no message. Line breaks are kept. Links (`{@link}`, `{@linkcode}`, `{@linkplain}`) are written `{@link Name}` or `{@link Name text}`.
- An `optional` member or parameter's `type` leaves out the `undefined` that `?` adds.

## Elements

These are the custom elements from the listed packages' Custom Elements Manifests (`customElements` in `package.json`). They become Blazor components (R28). An element is a tag plus what the manifest adds to its class. The class's properties and methods are on its declaration.

```jsonc
{ "tag": "demo-map", "class": { "kind": "ref", "name": "DemoMapElement", "module": "@acme/demo-maps/demo-map.js" },
  "doc": { … }, "attributes": [ … ], "events": [ … ], "slots": [ … ] }
```

- The tag is the declaration's `tagName`, or the name of a `custom-element-definition` export of it.
- Attributes are `{ name, property?, type?, doc? }`. `property` names the class member the attribute reflects. `type` is the manifest's type for the attribute, which can be narrower than the property's, such as a `string` attribute over a union property.
- Events are `{ name, detail?, type?, doc? }`. A `CustomEvent<T>` gives `detail` as T. So does a type that isn't an `Event`, because some manifests give the detail type directly. Any other event type goes in `type`. Neither field means a plain `Event`.
- Slots are `{ name, doc? }`. The default slot's name is `""`.
- Docs come from the manifest. `summary` is its `description` (or `summary`), `default` is an attribute's default as a JS expression, and `deprecated` works as for JSDoc.
- Types are the manifest's type strings, resolved by the checker through the names their `references` point at, so they are the same IR types as the typings'. A manifest module path resolves through the package's `exports` when one reaches it, and otherwise as a file in the package. A reference to a module that re-exports the name resolves to the declaring module. Text that doesn't resolve is `unresolved`. `class` is resolved the same way. The generator skips an element whose class is `unresolved` (IOP2007, [R28](mapping.md#6-naming)).

## Types

| `kind` | Fields | Example |
|---|---|---|
| `primitive` | `name`: `string`, `number`, `boolean`, `bigint`, `symbol`, `void`, `undefined`, `null`, `never`, `any`, `unknown`, `object` | |
| `literal` | `value` (string, number or boolean) | `"point"`, `2` |
| `ref` | `name`, `module` (absent for platform types), `args` | `Point`, `Promise<void>`, `Collection<Marker>` |
| `typeParameter` | `name` | `T` |
| `this` | | `clone(): this` |
| `array` | `element` | `number[]`, `readonly T[]` |
| `tuple` | `elements`, `required` (when some are optional), `rest` (the last element repeats) | `[number, number]` |
| `union` | `types`, `discriminant` (R19) | `Style` on `type` |
| `intersection` | `types`, when a part isn't an object type | `string & {}` (R10), `T & Named` |
| `object` | `members`, `indexSignatures`, `callSignatures`, `constructSignatures` | `{ zoom: number }`, resolved mapped types and intersections (R11) |
| `function` | a signature's fields, for an object with one call signature and nothing else | `(marker: Marker) => string` |
| `unresolved` | `text` (the checker's spelling). The generator skips it (R12) | `keyof this`, `this["$events"][K]` |

- A union keeps named members as written. `FlyToTarget` is `Shape | [number, number] | Camera`, not the checker's flattened list. The generator flattens it (R6). `boolean` is a `primitive`, never `true | false`.
- A `discriminant` is the first property that every non-nullish member declares with a distinct literal type. A member may be an object type or an intersection of them.
- Aliases declared by a package, including a dependency, are `ref`s. Platform aliases such as `Partial` are resolved in place.

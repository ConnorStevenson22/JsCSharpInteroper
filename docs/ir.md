# IR v1

The extractor's output and the generator's input ([design §4](design.md#4-generator-architecture)): one JSON document per package, language-neutral and versioned. It records what the typings and the Custom Elements Manifest say, resolved by the TypeScript checker ([mapping §7](mapping.md#7-what-the-extractor-must-resolve-with-the-checker)). It makes no C# decisions: classification, naming and the profile are the generator's.

```bash
node src/interoper-extractor/extract.mjs fixtures/demo-maps --profile fixtures/demo-maps/profile.json --out ir.json
```

The profile is read only for its event-map property (`events.mapProperty`). The fixture's IR is committed as a golden file, [`fixtures/demo-maps/demo-maps.ir.json`](../fixtures/demo-maps/demo-maps.ir.json), which is the generator's test input; after an intended change, regenerate it with `UPDATE_GOLDEN=1 node --test src/interoper-extractor/test/extract.test.mjs` and review the diff.

## Conventions

- **Deterministic.** Modules are sorted by specifier, elements by tag, declarations follow source order, members follow the checker's order (own, then inherited), and union members follow the order they're written in. Two runs give byte-identical output.
- **Absent means none.** Empty lists, `false` flags and missing docs are left out.

## Document

```jsonc
{ "version": 1, "package": { "name": "@acme/demo-maps", "version": "0.0.0" },
  "modules": [{ "specifier": "@acme/demo-maps/geometry.js", "declarations": [ … ] }],
  "elements": [{ "tag": "demo-map", "class": { "kind": "ref", "name": "DemoMapElement", "module": "@acme/demo-maps/demo-map.js" }, … }] }
```

A **specifier** is the nearest named `package.json` plus the path of the `.js` next to the `.d.ts`. Only exported declarations are listed, under the module that declares them.

## Declarations

Every declaration has `kind`, `name` and an optional `doc` (a function's are on its signatures).

- `name` is the declaration's own name, also for a default export, and references (`ref`) use it.
- `export` is the name the module exports it under, when that differs: `"default"` for `export default class TileLayer`. An export of a declaration elsewhere in the file (`export default Pin;`, `export { Pin as Marker }`) is that declaration, listed once: with no `export` when the module also exports it under its own name. Re-exports of another file's declarations are left out.

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
  - Members a named base declares are listed on that base only: follow `extends`. Members of an anonymous base or a mapped type are listed, with `inherited`. Members the platform declares (the DOM's) are left out.
  - A mixin's base, an intersection that starts with a named class (`class Pin extends PinSuperclass`, where `PinSuperclass: typeof Marker & typeof Draggable`), is that class: `extends` is `Marker`, and the other parts' members are listed with `inherited`.
- **Events** (R24) are `{ name, type, doc? }`, the checker-resolved properties of the event-map property's type on that class. The event-map property itself isn't a member.
- **Signatures** are `{ typeParameters?, params, returns, doc? }`, one per overload (R23), each with its own JSDoc. Constructors have no `returns`. A parameter is `{ name, type, optional?, rest? }`; a rest parameter's type is its array.
- **Type parameters** are `{ name, constraint?, default? }`.
- **Index signatures** are `{ key, type, readonly? }`.
- **Docs** are `{ summary?, default?, deprecated?, since?, see? }`, from the declaration's own JSDoc (an override doesn't copy its base's): `see` is a list, and `deprecated` is `""` when the tag has no message.
- An `optional` member or parameter's `type` doesn't include the `undefined` that `?` adds.

## Elements

The custom elements of the package's Custom Elements Manifest (`customElements` in `package.json`), for the Blazor components ([design §2](design.md#2-what-we-generate)). An element is a tag plus what the manifest adds to its class; the class's properties and methods are its declaration's members.

```jsonc
{ "tag": "demo-map", "class": { "kind": "ref", "name": "DemoMapElement", "module": "@acme/demo-maps/demo-map.js" },
  "doc": { … }, "attributes": [ … ], "events": [ … ], "slots": [ … ] }
```

- An element's tag is its declaration's `tagName`, or the name of a `custom-element-definition` export of it.
- **Attributes** are `{ name, property?, type?, doc? }`. `property` names the class member the attribute reflects, which has the type; `type` is given only when there's no `property`.
- **Events** are `{ name, detail?, type?, doc? }`. A `CustomEvent<T>` gives `detail` (T), and so does a type that isn't an `Event`, because some manifests give the detail's type; any other event type is `type`. Neither means a plain `Event`.
- **Slots** are `{ name, doc? }`; the default slot's name is `""`.
- **Docs** come from the manifest: `summary` is its `description` (or `summary`), `default` an attribute's default (a JS expression), `deprecated` as for JSDoc.
- **Types** are the manifest's type texts, resolved by the checker in the scope of the names their `references` point at, so they're the same IR types as the typings'. A text that doesn't resolve is `unresolved`. `class` is resolved the same way, from the declaration's name and module.

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
- Aliases are `ref`s only when this package declares them. Others, such as `Partial` or another package's aliases, are resolved in place.

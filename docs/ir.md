# IR v1

The extractor's output and the generator's input ([design §4](design.md#4-generator-architecture)): one JSON document per package, language-neutral and versioned. It records what the typings say, resolved by the TypeScript checker ([mapping §7](mapping.md#7-what-the-extractor-must-resolve-with-the-checker)). It makes no C# decisions: classification, naming and the profile are the generator's.

```bash
node src/interoper-extractor/extract.mjs fixtures/demo-maps --profile fixtures/demo-maps/profile.json --out ir.json
```

The profile is read only for its event-map property (`events.mapProperty`).

## Conventions

- **Deterministic.** Modules are sorted by specifier, declarations follow source order, members follow the checker's order (own, then inherited), and union members follow the order they're written in. Two runs give byte-identical output.
- **Absent means none.** Empty lists, `false` flags and missing docs are left out.

## Document

```jsonc
{ "version": 1, "package": { "name": "@acme/demo-maps", "version": "0.0.0" },
  "modules": [{ "specifier": "@acme/demo-maps/geometry.js", "declarations": [ … ] }] }
```

A **specifier** is the nearest named `package.json` plus the path of the `.js` next to the `.d.ts`. Only exported declarations are listed, under the module that declares them.

## Declarations

Every declaration has `kind`, `name` and an optional `doc`.

| `kind` | Fields |
|---|---|
| `class` | `abstract`, `typeParameters`, `extends` (one type), `implements`, `constructors` (signatures), `members`, `events` |
| `interface` | `typeParameters`, `extends` (named bases), `members`, `indexSignatures`, `callSignatures`, `constructSignatures` |
| `alias` | `typeParameters`, `type` (resolved: `Partial<Pick<…>>` becomes an `object`, R12) |
| `enum` | `members`: `{ name, value, doc }` |
| `function` | `signatures` |
| `variable` | `type`, `readonly` (`const`) |

- **Members** are `{ kind: "property", name, type, writeType?, readonly?, optional?, static?, abstract?, inherited?, doc? }` or `{ kind: "method", name, signatures, optional?, static?, abstract?, inherited?, doc? }`.
  - `writeType` is the setter's type when an accessor pair's types differ (§5).
  - Members a named base declares are listed on that base only: follow `extends`. Members of an anonymous base or a mapped type are listed, with `inherited`. Members the platform declares (the DOM's) are left out.
- **Events** (R24) are `{ name, type, doc? }`, the checker-resolved properties of the event-map property's type on that class. The event-map property itself isn't a member.
- **Signatures** are `{ typeParameters?, params, returns, doc? }`, one per overload (R23), each with its own JSDoc. Constructors have no `returns`. A parameter is `{ name, type, optional?, rest? }`; a rest parameter's type is its array.
- **Type parameters** are `{ name, constraint?, default? }`.
- **Index signatures** are `{ key, type, readonly? }`.
- **Docs** are `{ summary?, default?, deprecated?, since?, see? }`: `see` is a list, and `deprecated` is `""` when the tag has no message.
- An `optional` member or parameter's `type` doesn't include the `undefined` that `?` adds.

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
| `intersection` | `types`; the generator flattens their members (R11) | `string & {}` (R10) |
| `object` | `members`, `indexSignatures`, `callSignatures`, `constructSignatures` | `{ zoom: number }`, resolved mapped types |
| `function` | a signature's fields: an object with one call signature and nothing else | `(marker: Marker) => string` |
| `unresolved` | `text` (the checker's spelling), for IOP2002 | `keyof this`, `this["$events"][K]` |

- A union keeps named members as written: `FlyToTarget` is `Shape | [number, number] | Camera`, not the checker's flattened list. The generator flattens it (R6). `boolean` is a `primitive`, never `true | false`.
- A `discriminant` is the first property that every non-nullish member declares with a distinct literal type.
- Aliases are `ref`s only when this package declares them. Others, such as `Partial` or another package's aliases, are resolved in place.

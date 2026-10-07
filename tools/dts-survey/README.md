# dts-survey

Measures which TypeScript type shapes a `.d.ts` → C# generator actually has to handle, per library.

Each library's own declaration files are loaded into the TypeScript **checker** (not just parsed). Every API position (property, accessor, parameter, return type, type alias, index signature) is resolved and put in exactly one bucket: primitive, object, enum, literal union, primitive/mixed/discriminated/untagged union, intersection, callback, `any`, and so on. Positions that need type-level evaluation (mapped, conditional, indexed-access, `keyof`, utility types) are counted too.

```bash
npm install          # ~870 MB: pulls the surveyed libraries at the pinned versions
npm run survey       # writes results/survey.json and results/survey.md
```

Or point it at any `node_modules` that already has the libraries and TypeScript 6.x:

```bash
node survey.mjs /path/to/node_modules --json out.json --md out.md
```

To survey a different library, add an entry to the `libraries` array at the top of `survey.mjs`.

Notes:

- TypeScript **6.x** is used deliberately: TypeScript 7.0 ships no stable programmatic API yet.
- "Discriminated" means every member of an object union has a shared property with a distinct literal type. The checker finds it, so a generator can emit the classifier attributes mechanically.
- "Distinct" union counts treat `T | null` and `T | undefined` as the same shape as `T`, so they approximate how many C# union types a generator would emit.
- Results from the October 2026 run are in `results/` and discussed in [`docs/report.md`](../../docs/report.md).

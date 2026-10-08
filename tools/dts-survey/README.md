# dts-survey

Measures which TypeScript type shapes a `.d.ts` → C# generator actually has to handle, per library.

Each library's own declaration files are loaded into the TypeScript **checker** (not just parsed). Every API position (property, accessor, parameter, return type, type alias, index signature) is resolved and put in exactly one bucket: primitive, object, enum, literal union, primitive/mixed/discriminated/untagged union, intersection, callback, `any`, and so on. Positions that need type-level evaluation (mapped, conditional, indexed-access, `keyof`, utility types) are counted too.

```bash
npm install          # ~800 MB: pulls the libraries in libraries.json at pinned versions
npm run survey       # writes results/survey.json and results/survey.md (not committed)
```

Or point it at any `node_modules` that already has the libraries and TypeScript 6.x, with your own list:

```bash
node survey.mjs /path/to/node_modules --libs /path/to/my-libraries.json --json out.json --md out.md
```

`libraries.json` entries look like `{ "name": "...", "dir": "<path under node_modules>", "only": ["file.d.ts"], "skip": ["/substring/"] }`. To survey a library that must not live in this repo, keep its entry in a config file outside the repo and pass it with `--libs`.

Notes:

- TypeScript **6.x** is used deliberately: TypeScript 7.0 ships no stable programmatic API yet.
- "Discriminated" means every member of an object union has a shared property with a distinct literal type. The checker finds it, so the generator can emit a value-based classification switch ([mapping R19](../../docs/mapping.md#4-reading-union-values-back-classification)).
- "Distinct" union counts treat `T | null` and `T | undefined` as the same shape as `T`, so they approximate how many C# union types a generator would emit.
- Results aren't committed, because `survey.json` quotes snippets of third-party typings. The October 2026 numbers are summarized in [`docs/mapping.md` §9](../../docs/mapping.md#9-where-these-rules-come-from).

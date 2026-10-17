# dts-survey

Counts which TypeScript type shapes a `.d.ts` to C# generator has to handle, per library. A run over 12 real libraries (151,353 API positions) found that 68–91% of positions map directly, 2.4–12.5% need a real union, and 4–24% need judgement. Unions are small (median 2–4 members). The hard part is telling their cases apart, which is why [mapping R19](../../docs/mapping.md#4-reading-union-values-back-classification) exists.

It loads each library's declaration files into the TypeScript checker. Every API position (property, accessor, parameter, return type, type alias, index signature) is resolved and counted in one bucket: primitive, object, enum, literal union, the kinds of union, intersection, callback, `any`, and so on. It also counts positions that need type-level evaluation, such as mapped, conditional and indexed-access types, `keyof` and utility types.

```bash
npm install          # ~800 MB: the libraries in libraries.json, at pinned versions
npm run survey       # writes results/survey.json and results/survey.md
```

Or point it at a `node_modules` that already has the libraries and TypeScript 6.x, with your own list:

```bash
node survey.mjs /path/to/node_modules --libs /path/to/my-libraries.json --json out.json --md out.md
```

An entry in `libraries.json` looks like `{ "name": "...", "dir": "<path under node_modules>", "only": ["file.d.ts"], "skip": ["/substring/"] }`. To survey a library that must not live in this repo, keep its entry in a file outside the repo and pass it with `--libs`.

Notes:

- It shares its program setup and discriminant check with the extractor (`src/interoper-extractor/program.mjs`), so run it from a full clone.
- A union is "discriminated" when every member is an object with a shared property of a distinct literal type.
- "Distinct" union counts treat `T | null` and `T | undefined` as `T`. They roughly match how many C# union types a generator would emit.
- Results aren't committed, because they quote third-party typings.

# TypeScript → C# mapping rules

What the generator emits for each TypeScript construct.

- The extractor and generator ([plan](plan.md) phase 2) implement these rules.
- The fixture library (`fixtures/demo-maps`) exercises each one.
- Rules are numbered (**R1**, **R2**, …) so tests, issues and diagnostics can cite them.
- Where a rule is backed by an experiment, it links the [evidence](design.md#8-evidence).

Two generator inputs change the output everywhere: the **mode** (`--api sync|async`, see the [design](design.md)) and the consumer's optional **profile**.

---

## 1. The value model

Verified ([design §8](design.md#8-evidence)).

**R1. `undefined` is a type.** The runtime defines `Undefined`: a sealed class with a private constructor and one instance, `Undefined.Value`. Users can't construct it.

**R2. Every TypeScript union becomes a generated non-boxing `[Union]` struct, never the `union` keyword.**
- The struct's cases are the mapped union members plus a nullable bottom case, `Undefined?`.
- `default` is `undefined`: tag 0, and `Value` returns `Undefined.Value`.
- The `null` literal compiles through a generated `implicit operator X(Undefined? value)` and means JS `null`.
- Each case has `TryGetValue(out T)`, plus `HasValue`.

Why not the `union` keyword: its `default` is `null`, and it boxes value types.

```csharp
// type ColorInput = string | number[] | RGBA;   used as  color?: ColorInput | null
[Union] public readonly struct ColorInput : IUnion
{
    public ColorInput(string value) ...
    public ColorInput(double[] value) ...
    public ColorInput(Rgba value) ...
    public ColorInput(Undefined? value) ...               // null ref = JS null, singleton = undefined
    public static implicit operator ColorInput(Undefined? value) => new(value);
    public object? Value => _tag switch { 0 => Undefined.Value, 1 => null, _ => _ref };
    // HasValue, TryGetValue(out string), TryGetValue(out double[]), TryGetValue(out Rgba), TryGetValue(out Undefined)
}
```

| You write | It means | On the wire |
|---|---|---|
| *(nothing)* / `default` | `undefined` | property omitted |
| `= null` | JS `null` | `null` |
| `= "red"` / `= new double[] {…}` / `= new Rgba {…}` | value | value |
| `= Undefined.Value` | explicit `undefined` | omitted |

The compiler enforces exhaustiveness. A `switch` that misses `Undefined` warns CS8509; one that misses `null` warns CS8655.

The nullable bottom case is what makes the compiler demand the `null` arm. With a non-nullable `Undefined` case, the `null` arm silently isn't required.

**R3. Non-union types in optional or nullable positions use `Opt<T>`.** It has the same three states and the same conversions (`Opt<double> z = 5; z = null; z = default;`). Required, non-null positions stay plain C# types (`double`).

**R4. A union of live classes that covers every concrete subclass of an abstract base maps to the base.** The base is generated as a `closed class` (implicitly abstract; writing `abstract` as well is error CS9384). A switch over the subclasses is exhaustive, including through `Opt<Base>`. Unions that cover only *some* subclasses stay unions.

**R5. Collapse after mapping, then de-duplicate.** Example: `number[]` and `[number, number, number]` both map to `double[]`, so they become one case.

**R6. Flatten nested unions.** A union inside a union gets no single-step implicit conversion from the inner cases. So the generator flattens: `type A = B | C; type B = X | Y` becomes cases `X, Y, C`. Case order is declaration order, so output is deterministic.

---

## 2. Type mapping

| TypeScript | C# | Rule / notes |
|---|---|---|
| `number` | `double` | **R7.** The profile can override per member (`int`, `long`) |
| `string`, `boolean` | `string`, `bool` | |
| `bigint` | `System.Numerics.BigInteger` | |
| `Date` | `DateTimeOffset` | |
| `T[]`, `readonly T[]`, `Array<T>` (data) | `T[]` | |
| tuple, homogeneous numeric | `double[]` | R5 applies |
| tuple, heterogeneous | `ValueTuple` (`(string, double)`) | |
| `Record<string, T>`, `{ [k: string]: T }` | `Dictionary<string, T>` | Known members next to an index signature stay typed |
| `Promise<T>` | `Task<T>` | **R8.** In both modes |
| `void` (return) | `void` (sync) / `Task` (async) | |
| string-literal union | `enum`, with a generated name map | **R9.** See R25 for names |
| open-ended literal union (`"a" \| "b" \| (string & {})`) or more than 64 members | extensible enum: `readonly record struct` wrapping a string, with static known values | **R10.** |
| number-literal union | `enum` with explicit values | |
| TS `enum` | C# `enum` | |
| any other union | generated union (R2) | |
| `T \| null`, `T \| undefined`, `prop?: T` | union with bottom case (R2), or `Opt<T>` (R3) | |
| class (entity) | live class | §3 |
| class (value-like, per profile) | live class + `XSnapshot` record | §3 |
| interface with only data members | data record (keeps the interface's name) | §3 |
| intersection of data bags (`A & { type: "x" }`) | one flattened record of the checker-resolved members | **R11.** |
| intersection used as a mixin | C# interfaces on the live class | |
| generic `T` | C# generic where it's reified at runtime (`Collection<T>`); otherwise the constraint, or `JsAny` | |
| `this` (return type) | the declaring type | |
| `keyof`, indexed access, mapped, conditional, `typeof` | the type the checker resolves | **R12.** Unresolvable → `JsAny` + diagnostic IOP2002 |
| `any`, `unknown`, `object` | `JsAny`: an opaque handle with untyped get/set/call | Diagnostic IOP2001 |
| DOM element types (`HTMLElement`, `HTMLDivElement`) | `ElementReference` | **R13.** So Blazor `@ref`s can be passed directly |
| other DOM types (`MouseEvent`, `ImageData`) | `JsAny` until a DOM binding exists (WebIDL front end, see [plan](plan.md) "Later") | |
| `AbortSignal` (parameter) | `CancellationToken` | **R14.** Bridged by the runtime |
| `ArrayBuffer`, typed arrays | `byte[]` / `T[]` | Binary transfer is a later optimisation |
| function type | delegate (`Action<…>` / `Func<…>`) | §5, R21 |

---

## 3. Entities and values (by reference or by value)

The central decision for every class and interface.

**R15. Default classification.**
- A `class` is an **entity**: a live object with identity, behind a handle.
- An interface whose members are all data (no methods) is a **data record**.

The profile overrides either way. Getting this wrong is the classic failure mode: a JSON copy of an entity loses identity and methods, and some libraries' `toJSON()` produces a different schema altogether.

**R16. Value-like classes have two shapes.** The profile marks classes value-like; typical examples are geometry, symbols and colors. Such a class gets:
- a live class (`Point`), used when you hold a handle;
- an `XSnapshot` record (`PointSnapshot`), used inside snapshots, constructor bags and data-in/data-out module functions.

**R17. Snapshot depth.** Snapshot records copy value-like members deeply, as their snapshot records. Entity members appear as live objects inside the snapshot.

**R18. Every live class has an `XSnapshot` record.** It carries the class's data members, all optional. One type serves as:
- what `Snapshot()`/`SnapshotAsync()` returns;
- the constructor input for `Create`/`CreateAsync`;
- the patch for `Apply`/`ApplyAsync`. Unset members are undefined (R2), so a patch is just a partially filled record.

A generated `XSnapshot.Diff(before, after)` produces the minimal patch.

---

## 4. Reading union values back (classification)

When JS returns a value for a union position, the runtime must pick the case.

**R19. Classification order:**
1. **Handle with a class id** (`{"$h": 12, "$t": 7}`) → the generated registry maps the id to the most-derived live type, then picks the case that type converts to.
2. **Object with a discriminant.** The extractor finds a property that every member declares with a distinct literal type (`type: "point"`). The generator emits a switch on that property's *value*.
3. **Object without a discriminant.** The generator computes each case's *required* member names. If those sets separate the cases, it emits a structural check. Otherwise the union is **ambiguous**: diagnostic IOP2003, with the profile supplying a classifier. Optional members don't count, because TypeScript makes most distinguishing members optional.
4. **Primitives** by JSON token kind (string / number / bool / array). Two cases with the same token kind (e.g. `string` and a string enum) are told apart by value where possible; otherwise IOP2003.

**R20. Writing** serializes the active case. Data records include their discriminant as a constant property, so JS receives `{ "type": "point", … }`.

---

## 5. Members

| JS member | `--api sync` | `--api async` |
|---|---|---|
| property `x: T` | `T X { get; set; }` | `Task<T> GetXAsync()`, `Task SetXAsync(T value)` |
| read-only property | `T X { get; }` | `Task<T> GetXAsync()` |
| getter type ≠ setter type | `TGet X { get; }` + `void SetX(TSet value)` | `Task<TGet> GetXAsync()`, `Task SetXAsync(TSet value)` |
| method `foo(a): R` | `R Foo(A a)` | `Task<R> FooAsync(A a)` |
| method returning `Promise<R>` | `Task<R> FooAsync(A a)` | `Task<R> FooAsync(A a)` |
| constructor `new X(props)` | `static X Create(JsScope scope, XSnapshot props)` | `static Task<X> CreateAsync(JsScope scope, XSnapshot props)` |
| event (from an event map) | `IDisposable OnClick(Action<ClickEvent>, EventOptions?)` | `Task<IAsyncDisposable> OnClickAsync(Action<ClickEvent>, EventOptions?)` |
| snapshot / patch | `XSnapshot Snapshot()`, `void Apply(XSnapshot)` | `Task<XSnapshot> SnapshotAsync()`, `Task ApplyAsync(XSnapshot)` |
| module function | static method on a static class per module, same per-mode shape | |

Sync mode needs .NET and JS on one thread, so it runs on WebAssembly only and fails fast elsewhere (verified, [design §8](design.md#8-evidence)).

**R21. Callbacks** (function-typed parameters) become delegates registered in the runtime's callback registry, owned by the scope.
- A callback whose *return value* JS uses synchronously (e.g. a formatter `(v) => string`) works in sync mode.
- It *can't* work in async mode on Server or Hybrid. The generator emits IOP2005, and the profile can supply a JS-side implementation instead.

**R22. Optional parameters** use the value model: `Opt<T> p = default` (or a union `= default`), and `default` is undefined. Omitted trailing arguments are omitted on the JS side too. Rest parameters become `params T[]`.

**R23. Overloads** map one to one. If two TypeScript overloads collapse to the same C# signature after mapping, they merge into one method with a union or enum parameter (IOP2004).

**R24. Events** come from the checker-resolved event map, including maps declared through a phantom property and an indexed access on `this`. `EventOptions` carry JS-side behaviour that must happen before the event reaches .NET (`PreventDefault`, `StopPropagation`), plus payload projection (`Fields`). Server and Hybrid can't call `preventDefault()` in time from .NET.

---

## 6. Naming

**R25. Literal values → enum member names.** Split on non-alphanumerics and PascalCase each part (`"simple-marker"` → `SimpleMarker`). A leading digit gets an underscore (`"3d"` → `_3d`); the empty string becomes `Empty`. Duplicates after normalization get a numeric suffix and IOP2006. The profile can rename.

**R26. Members** are PascalCased. Async mode appends `Async`.
- A JS member that collides with a generated member (`getZoom()` next to `zoom` in async mode) or with an `object` member (`toString()`, `equals()`) is renamed (`JsToString`, `JsEquals`) with IOP2006.
- C# keywords can't collide after PascalCasing.

**R27. Types** keep the TypeScript name in PascalCase. The namespace is the consumer's root namespace plus the module's folder path. Anonymous unions are named after their owner: `Feature.shape` becomes `FeatureShape`, and its setter-only wider type becomes `FeatureShapeInput`.

---

## 7. What the extractor must resolve with the checker

A syntax walk isn't enough. The extractor asks the TypeScript checker for *resolved* members and records them in the IR:

- utility types (`Partial`, `Pick`, `Omit`, `Record`), `keyof`, indexed access, `typeof`, mapped types, and conditional types where they resolve;
- event maps declared through phantom properties;
- inherited members, accessor get/set pairs (with each side's type), overloads;
- discriminants (R19);
- JSDoc: summary, `@default`, `@deprecated` → `[Obsolete]`, `@since`, `@see`;
- the module specifier for every declaration.

---

## 8. Diagnostics

Every fallback is reported with a code so consumers can see and tune what didn't map cleanly. Initial set:

| Code | Meaning |
|---|---|
| IOP2001 | `any` / `unknown` / `object` mapped to `JsAny` |
| IOP2002 | Type the checker couldn't resolve, mapped to `JsAny` |
| IOP2003 | Ambiguous union; no generated classifier. Profile override needed |
| IOP2004 | Overloads merged after mapping |
| IOP2005 | Synchronous-result callback not supported in async mode |
| IOP2006 | Name changed to avoid a collision |

---

## 9. Where these rules come from

A survey of 12 real libraries (151,353 API positions, measured with [`tools/dts-survey`](../tools/dts-survey) in October 2026) found:

- 68–91% of positions map directly;
- 2.4–12.5% need a real union;
- 4–24% need judgement (`any`, callbacks, intersections, computed types);
- literal unions reach 6.2% of positions in one library, one of them with 2,631 members;
- up to 20% of positions accept both `null` and `undefined`;
- one library has 1,553 accessors whose getter and setter types differ;
- unions are small (median 2–4 members); the hard part is *classifying* them, not their size.

The quality of a library's typings matters more than its size.

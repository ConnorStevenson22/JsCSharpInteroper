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
- Each case has `TryGetValue(out T)`, plus `HasValue`, `IsUndefined` and `IsNull`.
- The struct implements the runtime's `IJsUnion<TSelf>`, which requires the `null` conversion and static `WriteValue`/`ReadValue` for the cases. The runtime's `JsJson` helpers handle undefined and null once for every union and `Opt<T>`. Everything else (tag, constructors, `TryGetValue`, equality) is generated per union.

Why not the `union` keyword: its `default` is `null`, and it boxes value types.

```csharp
// type ColorInput = string | number[] | RGBA;   used as  color?: ColorInput | null
[Union] public readonly struct ColorInput : IJsUnion<ColorInput>
{
    public ColorInput(string value) ...
    public ColorInput(double[] value) ...
    public ColorInput(Rgba value) ...
    public ColorInput(Undefined? value) ...               // null ref = JS null, singleton = undefined
    public static implicit operator ColorInput(Undefined? value) => new(value);
    public object? Value => _tag switch { 0 => Undefined.Value, 1 => null, _ => _ref };
    // HasValue, IsUndefined, IsNull, TryGetValue(out string), TryGetValue(out double[]), TryGetValue(out Rgba), TryGetValue(out Undefined)
    // static WriteValue(Utf8JsonWriter, ColorInput), static ReadValue(ref Utf8JsonReader)   (R19, R20)
}
```

| You write | It means | On the wire |
|---|---|---|
| *(nothing)* / `default` | `undefined` | property omitted |
| `= null` | JS `null` | `null` |
| `= "red"` / `= new double[] {…}` / `= new Rgba {…}` | value | value |
| `= Undefined.Value` | explicit `undefined` | omitted |

Where a value can't be omitted (array elements, arguments), undefined is `{"$u":1}` ([design §3.4](design.md#34-protocol)), in both directions: the `JsJson` readers accept it too.

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
| string-literal union | `enum`, with a generated name map (`BasemapNames`: `ToJs`, `FromJs`, `Write`, `Read`) | **R9.** See R25 for names |
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
| generic `T` | C# generic over entities (`where T : JsObject`) where it's reified at runtime (`Collection<T>`); a constrained type parameter isn't mapped yet (IOP2007) | See below |
| `this` (return type) | the declaring type | |
| `keyof`, indexed access, mapped, conditional, `typeof` | the type the checker resolves | **R12.** Unresolvable → `JsAny` + diagnostic IOP2002 |
| `any`, `unknown`, `object` | `JsAny`: an opaque handle with untyped get/set/call | Diagnostic IOP2001 |
| DOM element types (`HTMLElement`, `HTMLDivElement`) | `ElementReference` | **R13.** So Blazor `@ref`s can be passed directly |
| other DOM types (`MouseEvent`, `ImageData`) | `JsAny` until a DOM binding exists (WebIDL front end, see [plan](plan.md) "Later") | |
| `AbortSignal` (parameter) | `CancellationToken` | **R14.** Bridged by the runtime |
| `ArrayBuffer`, typed arrays | `byte[]` / `T[]` | Binary transfer is a later optimisation |
| function type | delegate (`Action<…>` / `Func<…>`) | §5, R21 |

**Generics.** A type parameter without a constraint stands for an entity (`where T : JsObject`), read with the scope like any entity; a constrained one isn't mapped yet (IOP2007).
- A class the profile lists in `collections` is a live generic class (`Collection<T> : Emitter where T : JsObject` in the fixture). It has no `Create`, since its owner makes it (its constructor isn't a bag, IOP2007), and no registry entry, since the registry can't make an instance of an unknown type argument: what reads one passes `JsScope.Read(ref reader, create)` a factory (`static (realm, handle) => new Collection<Marker>(realm, handle)`), and identity still holds (R19). So a collection is read only through its collection-typed members (`MapView.markers`), never as a `JsObject` or through a base. A use with a type argument that isn't an entity (`Collection<number>`) is skipped (IOP2007).
- A generic data interface is a generic record (`CollectionChangeEvent<T>`), with the same constraint.

---

## 3. Entities and values (by reference or by value)

The central decision for every class and interface.

**R15. Default classification.**
- A `class` is an **entity**: a live object with identity, behind a handle.
- An interface whose members are all data (no methods) is a **data record**. So is an interface an event names as its payload: its methods are left out (R24).

The profile overrides either way. Getting this wrong is the classic failure mode: a JSON copy of an entity loses identity and methods, and some libraries' `toJSON()` produces a different schema altogether.

**R16. Value-like classes have two shapes.** The profile marks classes value-like; typical examples are geometry, symbols and colors. Such a class gets:
- a live class (`Point`), used when you hold a handle: what `Create` returns, and `this`-typed results (`Shape Clone(scope)`);
- an `XSnapshot` record (`PointSnapshot`), used wherever a type names the class: properties, arguments and results (read with `byValue`), inside snapshots and event payloads, constructor bags and data-in/data-out module functions.

Sent to JS, a snapshot record of a value-like class arrives as an instance: the writer adds its concrete class id (`$c`, [design §3.4](design.md#34-protocol)) and the runtime calls `new Point(bag)`. The bag keeps its constants, such as the discriminant (R20). Data records stay plain objects. The snapshots of an abstract value-like base form a `closed record` hierarchy (`ShapeSnapshot`), read by the discriminant (R19 step 2).

**R17. Snapshot depth.** Snapshot records carry data members only, copying value-like members deeply as their snapshot records. Entity members are not in snapshots, because reading one needs a scope: read them with their scoped getter (`GetSelected(scope)`).
- **Scoped records.** A data record (not a snapshot) may hold entities, directly, in arrays or through a type parameter (`{ added: T[] }`). It's *scoped*: its reader is `Read(ref reader, JsScope scope)`, which reads each handle with the scope, so what returns or delivers one takes a scope (§5). A union can't have an entity or a scoped record as a case yet, since its reader takes no scope (IOP2007).

**R18. Every live class has an `XSnapshot` record.** It carries the class's writable data members, all optional, each with its getter's type where the setter's differs (`MarkerSnapshot.Position` is a `PointSnapshot`). Read-only members are read with their getters. One type serves as:
- what `Snapshot()`/`SnapshotAsync()` returns;
- the constructor input for `Create`/`CreateAsync`;
- the patch for `Apply`/`ApplyAsync`. Unset members are undefined (R2), so a patch is just a partially filled record.

A generated `XSnapshot.Diff(before, after)` produces the minimal patch.

Every snapshot record has `WriteMembers` (the members alone: the bag for `create`, the patch for `apply`) and `Read`; a value-like one also has `Write`, which wraps the members as a value with `$c` and the discriminant. The bag and the patch leave both out, because `apply` assigns every member it gets and a discriminant is read-only.

---

## 4. Reading union values back (classification)

When JS returns a value for a union position, the runtime must pick the case.

**R19. Classification order:**
1. **Handle with a class id** (`{"$h": 12, "$t": 7}`) → the generated registry maps the id to the most-derived live type, then picks the case that type converts to.
2. **Object with a discriminant.** The extractor finds a property that every member declares with a distinct literal type (`type: "point"`). The generator emits a switch on that property's *value*.
3. **Object without a discriminant.** The generator computes each case's *required* member names. If those sets separate the cases, it emits a structural check. Otherwise the union is **ambiguous**: diagnostic IOP2003, with the profile supplying a classifier. Optional members don't count, because TypeScript makes most distinguishing members optional.
4. **Primitives** by JSON token kind (string / number / bool / array). Two cases with the same token kind (e.g. `string` and a string enum) are told apart by value where possible; otherwise IOP2003.

**R20. Writing** serializes the active case. A member typed with one literal (`type: "point"`) is a constant: not a property, but always written, so JS receives `{ "type": "point", … }`.

---

## 5. Members

| JS member | `--api sync` | `--api async` |
|---|---|---|
| property `x: T` | `T X { get; set; }` | `Task<T> GetXAsync()`, `Task SetXAsync(T value)` |
| read-only property | `T X { get; }` | `Task<T> GetXAsync()` |
| getter type ≠ setter type | `TGet X { get; }` + `void SetX(TSet value)` | `Task<TGet> GetXAsync()`, `Task SetXAsync(TSet value)` |
| method `foo(a): R` | `R Foo(A a)` | `Task<R> FooAsync(A a)` |
| property or method returning an entity or a scoped record (R17), or taking a callback (R21) | `R GetX(JsScope scope)` (+ `void SetX(T value)`), `R Foo(JsScope scope, A a)` | `Task<R> GetXAsync(JsScope scope)`, `Task<R> FooAsync(JsScope scope, A a)` |
| callback property (R21) | `void SetX(JsScope scope, Func<A, R>? value)` | `Task SetXAsync(JsScope scope, Action<A>? value)` |
| method returning `Promise<R>` | `Task<R> FooAsync(A a)` | `Task<R> FooAsync(A a)` |
| constructor `new X(props)` | `static X Create(JsScope scope, XSnapshot props)` | `static Task<X> CreateAsync(JsScope scope, XSnapshot props)` |
| event (from an event map) | `IDisposable OnClick(JsScope scope, Action<ClickEvent> handler, EventOptions? options = null)` | `Task<IAsyncDisposable> OnClickAsync(JsScope scope, Action<ClickEvent> handler, EventOptions? options = null)` |
| event stream | — | `IAsyncEnumerable<ClickEvent> OnClickStreamAsync(JsScope scope, EventOptions? options = null, CancellationToken cancellationToken = default)` |
| property watch, for every property of a class with the profile's watch method (its own or a base's) | `IDisposable WatchZoom(JsScope scope, Action<double>)` | `Task<IAsyncDisposable> WatchZoomAsync(JsScope scope, Action<double>)` |
| property watch stream | — | `IAsyncEnumerable<double> WatchZoomStreamAsync(JsScope scope, CancellationToken cancellationToken = default)` |
| snapshot / patch | `XSnapshot Snapshot()`, `void Apply(XSnapshot)` | `Task<XSnapshot> SnapshotAsync()`, `Task ApplyAsync(XSnapshot)` |
| module function | static method on a static class per module, same per-mode shape, with a leading `JsRealm realm`, or `JsScope scope` when it returns an entity or a scoped record or takes a callback (`static Hit LastHit(JsScope scope)`) | |

- **A live result belongs to the scope the caller passes** ([design §3.2](design.md#32-handles-identity-and-lifetime)), so members that return an entity, or take a callback the scope will own (R21), take that `JsScope` first; in sync mode such a property is `GetX(scope)` plus `SetX(value)`. Value-like classes are values (R16), so `PointSnapshot Center { get; set; }` needs no scope.
- **Events and watches** belong to the scope too: it owns the subscription and the listener, and disposing the result releases both sooner (R24).
- **Discriminants** (`type: "point"`) aren't members of live classes: the live type already says it.

Sync mode needs .NET and JS on one thread, so it runs on WebAssembly only and fails fast elsewhere (verified, [design §8](design.md#8-evidence)).

**R21. Callbacks** (function-typed parameters) become delegates registered in the runtime's callback registry, owned by the scope, so the member takes a `JsScope` first (§5).
- **Shape.** A callback returning `void` is an `Action<…>`, any other a `Func<…, R>`, with at most 4 parameters (more is IOP2007). The generated member registers it with `scope.Callback(handler, read1, …, write)` and sends `{"$f":id}`: each argument is read with its reader and the scope, which owns the handles in it, and a missing one is undefined; a `Func`'s result is written back with `write`, an `Action`'s is undefined. A named function alias (`type Formatter = (m: Marker) => string`) has no C# type of its own; its uses are delegates.
- **Lifetime.** Each call registers a new callback, which lives until its scope is disposed: setting a property to `null` or to another delegate doesn't release the previous one, so use a short-lived scope for repeated calls. A callback JS calls after its scope is disposed is dropped, and JS gets undefined. Every handle in its arguments is owned by its scope as it arrives, including ones no reader consumes (arguments past the delegate's arity, members a record skips). In async mode on Server and Hybrid, JS doesn't wait for .NET, so a void callback can run after the member's `Task` has completed.
- **Properties.** A callback property is setter-only, `SetX(scope, value)`, since JS can't hand the function back; an optional or nullable one takes `null` to clear it, and it has no watch. A read-only one, or a callback anywhere but a parameter or a property (in a data record, say), is skipped (IOP2007).
- A callback whose *return value* JS uses synchronously (e.g. a formatter `(v) => string`) works in sync mode.
- It *can't* work in async mode on Server or Hybrid: JS has moved on before the result arrives. The member is skipped in async mode with IOP2005, and the profile can supply a JS-side implementation instead.

**R22. Optional parameters** use the value model: `Opt<T> p = default` (or a union `= default`), and `default` is undefined. Omitted trailing arguments are omitted on the JS side too.
- A rest parameter `...points: Point[]` becomes `params PointSnapshot[] points`, written as one argument per element (a spread). The optional parameters before it are written while a later one is defined or it has elements.
- An optional trailing parameter or a rest parameter whose type isn't supported yet is dropped, with IOP2007; a rest parameter's type must be an array. `...markers: Marker[]` is `params Marker[] markers`, a spread of handles. A required one skips the member.

**R23. Overloads** map one to one. If two TypeScript overloads collapse to the same C# signature after mapping, they merge into one method with a union or enum parameter (IOP2004).

**R24. Events** come from the checker-resolved event map, including maps declared through a phantom property and an indexed access on `this`.
- **`EventOptions`** (`Interoper.Runtime`) carry what JS must do before the event reaches .NET, because Server and Hybrid can't call `preventDefault()` in time from .NET: `PreventDefault` and `StopPropagation` call the event's methods of those names when it has them, so library events with them work as DOM events do. `Fields` names the payload's top-level members to send. They travel with the `subscribe` op ([design §3.4](design.md#34-protocol)); watches take none.
- **Default projection.** When the payload is a data record, the generated member passes the record's wire members, constants included, as the default fields, and `options.Fields` replaces them. What the record doesn't have (DOM values, methods, live objects) never crosses the wire, so payloads stay small and leak no handles. A member that isn't sent is `default` in the record (null for a reference). Any other payload is sent whole.
- **Payload interfaces.** An interface an event names directly as its payload (not through an alias or a union), and its bases, become data records with their methods left out, one IOP2007 per method, instead of being skipped whole.
- **Streams** (async mode). `OnXStreamAsync` and `WatchXStreamAsync` (§5) subscribe when the enumeration first moves, and release the subscription when it ends: on `break`, cancellation or disposal. Disposing the scope ends its open streams normally, after what's already queued. JS can't be paused, so there's no backpressure: values queue without bound until they're read, and projection keeps them small.

---

## 6. Naming

**R25. Literal values → enum member names.** Split on non-alphanumerics and PascalCase each part (`"simple-marker"` → `SimpleMarker`). A leading digit gets an underscore (`"3d"` → `_3d`); the empty string becomes `Empty`. Duplicates after normalization get a numeric suffix and IOP2006. The profile can rename.

**R26. Members** are PascalCased. Async mode appends `Async`.
- A JS member that collides with a generated member (`getZoom()` next to `zoom` in async mode) or with an `object` member (`toString()`, `equals()`) is renamed (`JsToString`, `JsEquals`) with IOP2006.
- A member with the C# name and parameter types of a member an emitted base declares (for a property, its name) is `new`, since it hides that one (CS0108): `TileLayer Clone(JsScope scope)` over `Layer Clone(JsScope scope)`, and every subclass's `Snapshot()`. Other parameter types make an overload. Across kinds the name alone decides: a property hides a base's methods of its name, and a method hides a base's property of its name. A collision with a base class is never a rename, only `new`.
- C# keywords can't collide after PascalCasing.

**R27. Types** keep the TypeScript name in PascalCase.
- **Namespaces.** A type's namespace is the consumer's root namespace plus its module's folders in the package, each PascalCased: `layers/TileLayer.js` gives `Acme.Maps.Layers`. Its file's path mirrors the namespace (`Layers/TileLayer.cs`). The registry `{Package}.cs` and the metadata module stay at the root.
- **References.** Generated code names a type in its own namespace plainly, and a type in another namespace by its `global::`-qualified name (`global::Acme.Maps.Geometry.Extent`). In `Acme.Maps.Geometry.Support`, say, `Geometry.Extent` would otherwise mean a member of the class `Acme.Maps.Geometry.Geometry`.

Anonymous unions are named after their owner: `Feature.shape` becomes `FeatureShape`, and its setter-only wider type becomes `FeatureShapeInput`. A method's result is named after the method (`Feature.moveTo` → `FeatureMoveTo`), a parameter's after the method and the parameter (`FeatureMoveToTarget`), and an event's payload after the event (`FeatureClick`). When two generated types would share a path, the registry keeps its name and a module's static class takes the next free one (`Measure2`), since nothing refers to it, and so does one whose full name is a namespace's; any other later type is skipped, and so is a type with the full name of a namespace (`Layers` next to the `layers/` folder). A skipped type's users are not removed with it, so they don't compile until the profile renames it. Classes of one name in different namespaces get class id constants prefixed with their namespace below the root (`LayersPointId`), whatever the order of the modules; a class in the root namespace keeps `PointId`. All of these are IOP2006, with the type's full name (or the module, for its static class) as the subject.

---

## 7. What the extractor must resolve with the checker

A syntax walk isn't enough. The extractor asks the TypeScript checker for *resolved* members and records them in the [IR](ir.md):

- utility types (`Partial`, `Pick`, `Omit`, `Record`), `keyof`, indexed access, `typeof`, mapped types, and conditional types where they resolve;
- event maps declared through phantom properties;
- inherited members, accessor get/set pairs (with each side's type), overloads;
- discriminants (R19);
- JSDoc: summary, `@default`, `@deprecated` → `[Obsolete]`, `@since`, `@see`;
- the module specifier for every declaration.

---

## 8. Diagnostics

Every fallback is reported with a code so consumers can see and tune what didn't map cleanly. The generator never throws for a shape it can't map: it finds them while selecting what to emit, skips them with what uses them, and reports each. Initial set:

| Code | Meaning |
|---|---|
| IOP2001 | `any` / `unknown` / `object` mapped to `JsAny` |
| IOP2002 | Type the checker couldn't resolve, mapped to `JsAny` |
| IOP2003 | Skipped: an ambiguous union (R19), whose cases can't be told apart, and what uses it. Profile override needed |
| IOP2004 | Overloads merged after mapping |
| IOP2005 | Skipped in async mode: a member whose callback's result JS uses synchronously (R21) |
| IOP2006 | Name changed to avoid a collision (R25, R26); a module's static class renamed, or a type skipped, because its path or full name is taken or is a namespace's; a class id prefixed because classes in other namespaces share its name (R27) |
| IOP2007 | Skipped: a declaration, member or optional parameter the generator can't map (yet), or that uses one, or a class outside the selection |

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

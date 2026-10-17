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
- **Cases outside the selection.** A union drops a case that is a class outside the selection, or an alias of one, reported as IOP2007 with the union as the subject; the union aliases in it are flattened first (R6), so their cases count one by one. With one case left, the union is that case: `type Owner = Layer | TileLayer | Folder`, with only `Layer` selected, is `Layer`. With none left, what uses it is skipped (IOP2007). With two or more, it reads its other cases (R19), and its `ReadValue` throws `JsonException` on a handle (`{"$h":…,"$t":0}`), which is how JS sends an instance of a class outside the selection; so do the enum and primitive readers, should one get a handle. The reader has no scope to own that handle, so the JS object stays referenced for the transport's lifetime.
  - *Reading a dropped case through a narrowed union.* JS sends whatever it has. When the narrowed type is an entity (`Layer`), the handle of an instance of a class outside the selection has no live type, and the read throws (`No live type…`, as for class id 0). When it's a primitive or an enum, a handle throws `JsonException`. When it's a record, the reader takes the handle's object as the record: its members aren't on it, so the required ones keep their placeholders (null or zero) and the optional ones are undefined; no error is raised.
  - *Selection decides the type.* Narrowing depends on what's selected, so widening the selection later can turn a member's C# type from the one case (`Layer`) into the union struct (`Owner`). Code written against the narrowed type then needs changes; the IOP2007 for each dropped case says where.
- **A union that is only written**, a parameter's or a setter's wider type, may have a case that needs a scope to be read: an entity, or a record that holds one (R17). Writing needs none. So may one that dropped a case and can't tell the others apart (R19). Nothing reads it, so it skips R19 and its `ReadValue` throws. Where the same union would be read (a getter, a result, a record member), the member is skipped (IOP2007). A union struct isn't generic, so no union, written or read, has a case that names a type parameter (`T | string`, §2).
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

**R6. Flatten nested unions.** A union inside a union gets no single-step implicit conversion from the inner cases. So the generator flattens: `type A = B | C; type B = X | Y` becomes cases `X, Y, C`. Case order is declaration order, so output is deterministic. Literal unions flattened into one may share values (`"red" | "amber"` and `"amber" | "blue"`): each value is one enum member.

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
| inline object type, including an intersection of data bags (`A & { type: "x" }`), which the checker resolves to one | a data record named after its owner | **R11.** See below |
| intersection used as a mixin | C# interfaces on the live class | |
| class whose base is a mixin's (`extends PinBase`, where `PinBase: typeof Marker & typeof Draggable`) | a live class deriving from the named class (`Pin : Marker`), with the mixin's members as its own | The extractor resolves it ([ir.md](ir.md#declarations)) |
| generic `T` | C# generic over entities (`where T : JsObject`) where it's reified at runtime (`Collection<T>`); a class's or interface's constrained type parameter is its constraint, and a default (`Tagged<T = any>`) is ignored | See below |
| `this` (return type) | the declaring type | |
| `keyof`, indexed access, mapped, conditional, `typeof` | the type the checker resolves | **R12.** Unresolvable → skipped, with what uses it (IOP2007) |
| `any`, `unknown`, `object` | `JsAny`: the value's JSON, untyped. Plain objects and arrays are copies; other objects are handles, which the scope that read it owns, so it's read with a scope (R17) and isn't in snapshots. `any` and `unknown` are `Opt<JsAny>`, since they include null and undefined (R3); `object` is `JsAny`. `meta.Read(JsJson.ReadNumber)` or `meta.Read(scope.Read<Marker>)` reads it as a type; `JsAny.Parse(json)` and `JsAny.From(value, write)` make one | Diagnostic IOP2001. Not a union case |
| DOM element types (`HTMLElement`, `HTMLDivElement`) | `ElementReference`, so Blazor `@ref`s can be passed directly | **R13.** Written only; see below |
| other DOM types (`MouseEvent`, `ImageData`) | none until a DOM binding exists (WebIDL front end, see [plan](plan.md) "Later") | Skipped, with what uses it (IOP2007) |
| `AbortSignal` (parameter) | `CancellationToken` | **R14.** Bridged by the runtime |
| `ArrayBuffer`, typed arrays | `byte[]` / `T[]` | Binary transfer is a later optimisation |
| function type | delegate (`Action<…>` / `Func<…>`) | §5, R21 |

**R11. Inline object types** are data records like named ones (R15), named after their owner (R27): `Feature.size: { w: number }` is `FeatureSize`. In a union, each inline case is named after the union and its tag's value: the discriminant's (R19), or else a member every inline case has with a string literal of its own, so `set shape(v: (PolyProps & { type: "poly" }) | (DotProps & { type: "dot" }))` gives `FeatureShapeInput` with the cases `FeatureShapeInputPoly` and `FeatureShapeInputDot` whether the IR names `type` as the discriminant or not. Without a tag a case is named by its position in the union as written, dropped cases counted and `null` and `undefined` not (`FeatureShapeCase2`), reported as IOP2006. An optional member it can't map is left out (IOP2007, as `Feature.shape.legs`), and a required one skips what uses it; that holds wherever the inline record is, a callback's argument (`Feature.each(cb)(e).legs`) or result included, and a case of a union alias flattened into another union (R6), which is trimmed in its place (`Spot.rack` for `type Spot = Pin | {…}`); a callback alias's are trimmed once, for all its uses (`Visit(e).legs`), and keep its name (`VisitE`). One with a method or a call signature isn't data, and neither is one with a type parameter of a generic type (`interface Box<T> { meta: { item: T } }`), since the record isn't generic: either is skipped (IOP2007). A name that is taken, by an emitted declaration (`FeatureSize` next to `Feature.size`; a union alias narrowed to one case isn't one), a snapshot, the registry, a module's static class, an anonymous union or enum, a component, or another inline record, gets a numeric suffix (`FeatureSize2`), reported as IOP2006.

**R13. DOM elements** (`Element`, `HTMLElement`, `SVGElement` and their `HTML…Element` / `SVG…Element` subtypes) are `ElementReference`, written by `JsElement.Write` (`Interoper.Blazor`) as `{"$e":"<id>"}`. JS finds the element by Blazor's capture attribute (`[_bl_<id>]`), so the reference must come from an `@ref` after its first render, or the call fails with `JsException` (`InteroperError`); an empty one throws before anything is sent. The capture attribute is a detail of Blazor's renderer, pinned with the SDK: a Blazor upgrade must rerun the component tests ([design §6](design.md#6-testing)).
- **Written only.** JS can't hand an element back as an `ElementReference`, so a DOM-typed property is setter-only (`SetContainer(Opt<ElementReference>)`, R21's shape), has no watch and isn't in snapshots, and a read-only one is skipped. A DOM result (a method's, a read-only property's, an event payload's) is skipped (IOP2007), and so is a union with a DOM case.
- **Records.** A data record with a DOM member (`MapViewOptions.container`) has no `Read`, since it's only written; one that's read is skipped with what uses it (IOP2007).
- **Custom elements** are live classes like any other: their class id is found by their tag (R28), so `Element` of a component is the element's live object. That holds for an abstract element class too, which the registry makes ([design §3.2](design.md#32-handles-identity-and-lifetime)).

**Generics.** A type parameter without a constraint stands for an entity (`where T : JsObject`), read with the scope like any entity. A default (`Tagged<T = any>`) doesn't matter, since C# always has the type argument.
- A class's or interface's type parameter with a constraint stands for its constraint, and is gone with its type arguments: `class View<V extends LayerView = LayerView> { layerView: V }` is a plain `View` whose `LayerView` is a `LayerView`, `View<TileLayerView>` is `View`, and `interface InputEvent<E extends Input>` is a plain `InputEvent` record. A value read is still its most-derived live type (R19), and a value written may be wider than TypeScript allows, which JS accepts. One whose constraint names the type's own type parameters (`T extends Box<T>`), or that a signature or callback in the type declares again, a profile collection's, and a method's or function's type parameter aren't mapped yet (IOP2007).
- A class the profile lists in `collections` is a live generic class (`Collection<T> : Emitter where T : JsObject` in the fixture). It has no `Create`, since its owner makes it (its constructor isn't a bag, IOP2007), and no registry entry, since the registry can't make an instance of an unknown type argument: what reads one passes `JsScope.Read(ref reader, create)` a factory (`static (realm, handle) => new Collection<Marker>(realm, handle)`), and identity still holds (R19). So a collection is read only through its collection-typed members (`MapView.markers`), never as a `JsObject` or through a base. A use with a type argument that isn't an entity (`Collection<number>`) is skipped (IOP2007).
- A generic data interface is a generic record (`CollectionChangeEvent<T>`), with the same constraint.
- A use of a generic union alias is the union it stands for, with the type arguments in place of the type parameters, as if it were written there: `OneOrMany<Marker>`, where `type OneOrMany<T> = T | T[]`, is `Marker | Marker[]`, an anonymous union named after its owner (R27). So is a use of a generic alias of one (`type Same<V> = OneOrMany<V>`), and a missing type argument is its parameter's default. The alias itself has no type (IOP2007). A recursive one (`type Tree<T> = T | Tree<T>[]`) stops expanding after a few levels and is skipped, with what uses it; any other generic alias (of an object type, say) isn't mapped yet (IOP2007).

---

## 3. Entities and values (by reference or by value)

The central decision for every class and interface.

**R15. Default classification.**
- A `class` is an **entity**: a live object with identity, behind a handle.
- An interface whose members are all data (no methods) is a **data record**. So is an interface an event names as its payload: its methods are left out (R24).
- A data record's optional member that can't be mapped is left out of the record (IOP2007), so the record, and the unions it's a case of, still map: `interface TrackProps { stops: number[][]; legs?: [number, string][] }` is `TrackProps` with `Stops` only. A required one skips the record, with what uses it. Inline records do the same (R11).
- An interface's iterator methods (results of `Iterator`, `IterableIterator`, `Generator` and their kin, such as `values()`; the extractor leaves out `[Symbol.iterator]()` and any other member keyed by a symbol) are left out of it (IOP2007), so an interface that is otherwise data is still a data record. A class's are skipped like any member it can't map.

The profile overrides either way. Getting this wrong is the classic failure mode: a JSON copy of an entity loses identity and methods, and some libraries' `toJSON()` produces a different schema altogether.

**R16. Value-like classes have two shapes.** The profile marks classes value-like; typical examples are geometry, symbols and colors. Such a class gets:
- a live class (`Point`), used when you hold a handle: what `Create` returns, and `this`-typed results (`Shape Clone(scope)`);
- an `XSnapshot` record (`PointSnapshot`), used wherever a type names the class: properties, arguments and results (read with `byValue`), inside snapshots and event payloads, constructor bags and data-in/data-out module functions.

Sent to JS, a snapshot record of a value-like class arrives as an instance: the writer adds its concrete class id (`$c`, [design §3.4](design.md#34-protocol)) and the runtime calls `new Point(bag)`. The bag keeps its constants, such as the discriminant (R20). Data records stay plain objects. The snapshots of an abstract value-like base form a `closed record` hierarchy (`ShapeSnapshot`), read by the discriminant (R19 step 2).

**R17. Snapshot depth.** Snapshot records carry data members only, copying value-like members deeply as their snapshot records. Entity members are not in snapshots, because reading one needs a scope: read them with their scoped getter (`GetSelected(scope)`).
- **Scoped records.** A data record (not a snapshot) may hold entities, directly, in arrays or through a type parameter (`{ added: T[] }`). It's *scoped*: its reader is `Read(ref reader, JsScope scope)`, which reads each handle with the scope, so what returns or delivers one takes a scope (§5). A union's reader takes no scope, so only a union that is only written can have an entity or a scoped record as a case (R2).

**R18. Every live class has an `XSnapshot` record.** It carries the class's writable data members, all optional, each with its getter's type where the setter's differs (`MarkerSnapshot.Position` is a `PointSnapshot`). Read-only members are read with their getters. A member a subclass redeclares is in its snapshot once: under a closed base (R16) it stays the base record's when its type is the same, and is a `new` property of the subclass's record when it isn't (R26). Then the base record's property of that name is hidden, and the subclass's `WriteMembers` doesn't write it: `with` on a base-typed reference (`ShapeSnapshot s = tile; s with { Size = … }`) sets the hidden one, which isn't sent. One type serves as:
- what `Snapshot()`/`SnapshotAsync()` returns;
- the constructor input for `Create`/`CreateAsync`;
- the patch for `Apply`/`ApplyAsync`. Unset members are undefined (R2), so a patch is just a partially filled record.

**Diff.** Every snapshot record except a closed base (`ShapeSnapshot`; its subclasses' records have one) has a generated `static XSnapshot Diff(XSnapshot before, XSnapshot after)`: the patch from `before` to `after`.
- A member is in the patch when `after` defines it (it isn't undefined) and it differs from `before`'s.
- Members compare with the runtime's `JsDiff.Same`: `EqualityComparer`, but arrays compare element by element, recursively, also inside an `Opt<T>` or a union case. A nested record uses its own equality, so an array inside one compares by reference, and the member goes into the patch even when it hasn't changed. That's harmless: `apply` sets it to the value it has.
- **A patch can't unset a member**, since undefined means "leave it as it is". To clear one, set it to `null` in `after`.

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
| … whose setter type can't be mapped | the read-only property (IOP2007 for the setter) | the same |
| method `foo(a): R` | `R Foo(A a)` | `Task<R> FooAsync(A a)` |
| property or method returning an entity or a scoped record (R17), or taking a callback (R21) | `R GetX(JsScope scope)` (+ `void SetX(T value)`), `R Foo(JsScope scope, A a)` | `Task<R> GetXAsync(JsScope scope)`, `Task<R> FooAsync(JsScope scope, A a)` |
| callback property (R21) | `void SetX(JsScope scope, Func<A, R>? value)` | `Task SetXAsync(JsScope scope, Action<A>? value)` |
| method returning `Promise<R>` | `Task<R> FooAsync(A a)` | `Task<R> FooAsync(A a)` |
| constructor `new X(props)` | `static X Create(JsScope scope, XSnapshot props)` | `static Task<X> CreateAsync(JsScope scope, XSnapshot props)` |
| event (from an event map) | `IDisposable OnClick(JsScope scope, Action<ClickEvent> handler, EventOptions? options = null)` | `Task<IAsyncDisposable> OnClickAsync(JsScope scope, Action<ClickEvent> handler, EventOptions? options = null)` |
| event without a payload (`void`, `undefined`, `null`) | `IDisposable OnTick(JsScope scope, Action handler, EventOptions? options = null)` | `Task<IAsyncDisposable> OnTickAsync(…same…)` |
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
- **Lifetime.** Each call registers a new callback, which lives until its scope is disposed: setting a property to `null` or to another delegate doesn't release the previous one, so use a short-lived scope for repeated calls. A callback JS calls after its scope is disposed is dropped, and JS gets undefined. Every handle in its arguments is owned by its scope as it arrives, including ones no reader consumes (arguments past the delegate's arity, members a record skips); a dropped callback's aren't released, though, and stay in JS for the transport's lifetime. In async mode on Server and Hybrid, JS doesn't wait for .NET, so a void callback can run after the member's `Task` has completed.
- **Properties.** A callback property is setter-only, `SetX(scope, value)`, since JS can't hand the function back; an optional or nullable one takes `null` to clear it, and it has no watch. A read-only one, or a callback anywhere but a parameter or a property (in a data record, say), is skipped (IOP2007).
- A callback whose *return value* JS uses synchronously (e.g. a formatter `(v) => string`) works in sync mode.
- It *can't* work in async mode on Server or Hybrid: JS has moved on before the result arrives. The member is skipped in async mode with IOP2005, and the profile can supply a JS-side implementation instead.

**R22. Optional parameters** use the value model: `Opt<T> p = default` (or a union `= default`), and `default` is undefined. Omitted trailing arguments are omitted on the JS side too.
- A rest parameter `...points: Point[]` becomes `params PointSnapshot[] points`, written as one argument per element (a spread). The optional parameters before it are written while a later one is defined or it has elements.
- An optional trailing parameter or a rest parameter whose type isn't supported yet is dropped, with IOP2007; a rest parameter's type must be an array. `...markers: Marker[]` is `params Marker[] markers`, a spread of handles. A required one skips the member.

**R23. Overloads** map one to one. If two TypeScript overloads collapse to the same C# signature after mapping, they would merge into one method with a union or enum parameter (IOP2004, not reported yet: the generator doesn't merge overloads).

**R24. Events** come from the checker-resolved event map, including maps declared through a phantom property and an indexed access on `this`.
- **`EventOptions`** (`Interoper.Runtime`) carry what JS must do before the event reaches .NET, because Server and Hybrid can't call `preventDefault()` in time from .NET: `PreventDefault` and `StopPropagation` call the event's methods of those names when it has them, so library events with them work as DOM events do. `Fields` names the payload's top-level members to send. They travel with the `subscribe` op ([design §3.4](design.md#34-protocol)); watches take none.
- **Default projection.** When the payload is a data record, named or inline (R11), the generated member passes the record's wire members, constants included, as the default fields, and `options.Fields` replaces them. What the record doesn't have (DOM values, methods, live objects) never crosses the wire, so payloads stay small and leak no handles. A member that isn't sent is `default` in the record (null for a reference). Any other payload is sent whole.
- **Payload interfaces.** An interface an event names directly as its payload (not through an alias or a union), and its bases, become data records with their methods left out, one IOP2007 per method, instead of being skipped whole. A member they can't map is left out too, required or not (IOP2007, as an optional one is from any record, R15), since a payload is only read: `defer: <T>(op: () => Promise<T>) => Promise<T>` on an input event's base doesn't skip the event.
- **Subscriptions.** The profile's subscribe and watch methods return an object with `dispose()`, or `remove()` as some libraries name it; releasing the subscription calls whichever it has ([design §3.4](design.md#34-protocol)).
- **No payload.** An event whose payload is `void`, `undefined` or `null` has a parameterless `Action` handler, sends no fields (`fields: []`) unless the options name some, and has no stream.
- **Streams** (async mode). `OnXStreamAsync` and `WatchXStreamAsync` (§5) subscribe when the enumeration first moves, and release the subscription when it ends: on `break`, cancellation or disposal. Disposing the scope ends its open streams normally, after what's already queued. JS can't be paused, so there's no backpressure: values queue without bound until they're read, and projection keeps them small.

---

## 6. Naming

**R25. Literal values → enum member names.** Split on non-alphanumerics and PascalCase each part (`"simple-marker"` → `SimpleMarker`). A leading digit gets an underscore (`"3d"` → `_3d`). A value of symbols only spells them, the longest first: `<` `LessThan`, `<=` `LessThanOrEqual`, `>` `GreaterThan`, `>=` `GreaterThanOrEqual`, `=` and `==` `Equal`, `!=` `NotEqual`, `+` `Plus`, `-` `Minus`, `*` `Star`, `/` `Slash`, `%` `Percent`, and any other character its code (`&` → `U0026`), so `"--"` is `MinusMinus`. Only the empty string becomes `Empty`. (Before, any value of symbols only was `Empty`, `Empty2`, …, so such enum members are renamed: `"--"` was `Empty`.) Duplicates after normalization get a numeric suffix and IOP2006, reported once for each distinct rename, with the first enum that has it: an anonymous union that many owners repeat is an enum per owner (`…, and in 2 other enums`). The profile can rename.

**R26. Members** are PascalCased. Async mode appends `Async`.
- A JS member that collides with a generated member (`getZoom()` next to `zoom` in async mode) or with an `object` member (`toString()`, `equals()`) is renamed (`JsToString`, `JsEquals`) with IOP2006.
- A member with the C# name and parameter types of a member an emitted base declares (for a property, its name) is `new`, since it hides that one (CS0108): `TileLayer Clone(JsScope scope)` over `Layer Clone(JsScope scope)`, and every subclass's `Snapshot()`. A generic base's parameter types have the subclass's type arguments in them, so `Route : Collection<Marker>` redeclaring the `change` event has a `new OnChange…`. Other parameter types make an overload. Across kinds the name alone decides: a property hides a base's methods of its name, and a method hides a base's property of its name. A collision with a base class is never a rename, only `new`.
- C# keywords can't collide after PascalCasing.

**R27. Types** keep the TypeScript name in PascalCase.
- **Namespaces.** A type's namespace is the consumer's root namespace plus its module's folders in the package, each PascalCased: `layers/TileLayer.js` gives `Acme.Maps.Layers`. A module of a package other than the IR's first ([ir.md](ir.md#document)) has that package's name without its scope first: `@acme/widgets/legend` gives `Acme.Maps.Widgets`, and `@acme/widgets/dist/support/entry.js` gives `Acme.Maps.Widgets.Dist.Support`. Its file's path mirrors the namespace (`Layers/TileLayer.cs`). The registry `{Package}.cs` and the metadata module stay at the root, named after the first package.
- **References.** Generated code names a type in its own namespace plainly, and a type in another namespace by its `global::`-qualified name (`global::Acme.Maps.Figure.Frame`). In `Acme.Maps.Figure.Parts`, say, `Figure.Frame` would otherwise mean a member of the class `Acme.Maps.Figure.Figure`. The runtime's and .NET's types are always qualified (`global::Interoper.Opt<double>`, `global::System.Threading.Tasks.Task`), and generated files have no usings, so a generated type or member named `JsJson`, `Opt` or `Task` hides none of them. A member named like a type it reads, but not of exactly that type (`Opt<Legend> Legend`), would hide that type in its record's reader (CS0120), so the reader names it qualified, a scoped record's (R17) included.
- **Sibling modules.** Modules of one folder can declare types of one name (`ops/a.js` and `ops/b.js` each export `Options`). The first in IR order keeps it; a later one is prefixed with its module's name (`BOptions`), and its users use that name, so both compile. Names that differ only in case count as one name (`SVGOptions` in `a.js`, `SvgOptions` in `b.js` gives `BSvgOptions`).
- **Case.** Generated files must be distinct where the file system ignores case (Windows, macOS), so a type's path counts as taken when another differs from it only in case, and so does a name an inline record, an anonymous union or a component (R28) would take. A type and a namespace that differ only in case don't clash (`SVG.cs` beside an `Svg/` folder; the registry `Svg` beside a `SVG/` folder): one is a file, the other a folder, and C# tells them apart.

Anonymous unions and inline records (R11) are named after their owner: `Feature.shape` becomes `FeatureShape`, and its setter-only wider type becomes `FeatureShapeInput`. A method's result is named after the method (`Feature.moveTo` → `FeatureMoveTo`), a parameter's after the method and the parameter (`FeatureMoveToTarget`), and an event's payload after the event (`FeatureClick`). The registry is named after the package without its scope (`Maps`); when a namespace has that name (`@acme/widgets` with a `widgets/` folder gives `Acme.Widgets`), it takes the next free one (`Widgets2`). A namespace that a declared type has the full name of (`Marker` next to the `marker/` folder) takes the next free name (`Acme.Maps.Marker2`, its folder `Marker2/`), and so do the namespaces in it, so the type keeps its name; a type and a namespace that differ only in case don't clash. When two generated types would share a path, the registry keeps its name and a module's static class takes the next free one (`Measure2`), since nothing refers to it, and so does one whose full name is a namespace's; any other later type is skipped (an anonymous type named like a declared one, say, or like a namespace). A skipped type's users are not removed with it, so they don't compile. Classes of one name in different namespaces get class id constants prefixed with their namespace below the root (`LayersPointId`), whatever the order of the modules; a class in the root namespace keeps `PointId`. All of these are IOP2006, with the type's full name (or the module, for its static class and a sibling's renamed type) as the subject.

**R28. Components.** Each custom element in the package's custom elements manifest gets a Razor component: a plain C# class `{Component}.cs` (`public sealed partial class DemoMap : JsComponentBase`) with a hand-written `BuildRenderTree`, no `.razor` file. Its name is the tag in PascalCase (`demo-map` → `DemoMap`), in the element class's namespace. On a clash, often with the element class itself (`class DemoMap extends HTMLElement`), it's `{Name}Component` (`DemoMapComponent`), the convention, which isn't reported. So does one that differs only in case (R27): class `AcmeSVGLabel`, tag `acme-svg-label`, gives `AcmeSvgLabelComponent`; when that is taken too (an inline record's name counts, `DemoMap.component`'s), the next free number (`DemoMapComponent2`), reported as IOP2006. An element whose class isn't one the IR declares (the extractor couldn't resolve the manifest's name, [ir.md](ir.md#elements)) has no component (IOP2007).
- **Parameters.** Each attribute and each writable property of the element class is a `[Parameter]` of its plain mapped type (`double Zoom`, `Basemap Basemap`, `PointSnapshot Center`), without `Opt`. Something is rendered or set **only when the parent supplied it**: the component records the supplied names in `SetParametersAsync`. An attribute without a property must have a primitive or string-enum type, or it's skipped (IOP2007).
- **Attributes and properties.** A parameter of a primitive or string-enum type is rendered as an HTML attribute (invariant culture; a bool is present or absent), so it works in prerendering and static SSR. So is an attribute whose property has another type (a union, say) when the manifest gives the attribute itself a `string`, `number` or `boolean` type: the parameter has the manifest's type. Any other is set on the live element after the first interactive render, and again whenever it changes (`JsDiff.Same`, R18), with the `set` op; a null one is JS null when its type has null, and undefined when it's only optional.
- **Content.** `RenderFragment? ChildContent` is the default slot, and each named slot of the manifest is a `RenderFragment?` parameter (`Toolbar`) rendered in `<div slot="toolbar" style="display:contents">`. `AdditionalAttributes` captures unmatched attributes, and `Element` (`DemoMapElement?`) is the element's live object once interactive, fetched with the `wrap` op; `OnElementReady` (`EventCallback<DemoMapElement>`) is raised once, when it is and the supplied properties are set. In async mode the component loads the element's module first (`load`), so the element is defined; sync mode preloads it with the registry's other modules. A component disposed meanwhile stops quietly (`JsComponentBase.IsDisposed`, `ElementAsync`).
- **Events.** Each manifest event is an `EventCallback<TDetail>` (`OnDemoMapClick`), or `EventCallback` when the detail is void or null. The detail is read from the event's `detail`, projected to the record's wire members as R24's default projection does. An inline object detail is an inline record (R11) named after the event (`DemoMapZoomChangeDetail`), in the element class's namespace, and drops and renames as R11 says. Events are Blazor custom events registered by `interoper.js` when the realm starts, once per name: elements that share an event name share its fields, all of theirs, or the whole detail when one of them has no projection. `{Package}Events.cs` declares their `[EventHandler]` attributes (`ondemo-map-click`, with `JsEventArgs`) on a partial class `EventHandlers` in the root namespace, the only class Razor looks for them on, so hand-written markup can use `@ondemo-map-click` once it imports the namespace and a realm is started; a type of that name in the root namespace takes it, and the attributes are skipped (IOP2006). The registration turns an entity in a projected detail into a handle as the event is created, before any .NET code runs: the component's reader takes it into its scope, but hand-written markup that never reads the detail leaves it in JS for the transport's lifetime. The element class's event map isn't emitted: the component's events replace it.
- **`@bind`.** An event pairs with a property P when its name ends in `-{kebab(P)}-change` or `{P}Change` and its detail has a member P of P's type; the longest such P wins (`max-zoom-change` binds `maxZoom` over `zoom`). The component then also has `EventCallback<T> PChanged`, invoked with `detail.P`, so `demo-map-zoom-change` gives `@bind-Zoom`. A change event that echoes a value the component itself rendered or set (`JsEchoes`) raises the event's callback but not `PChanged`: otherwise an echo of an older value, arriving after a newer one was sent (two quick changes on Server), would set the parent back, and the two would chase each other.
- **Prerendering.** Attributes and slots render; events, properties and `Element` wait for interactivity.
- **Modes.** Both: the element is fetched through the async seam in either mode, and properties are set with the mode's send (`SendAsync` or `Send`).

**R29. Docs.** A declaration's JSDoc ([ir.md](ir.md#declarations)) becomes its XML docs, the same in both modes: on a live class, each property (both `GetXAsync` and `SetXAsync` in async mode), each overload of a method or module function, `Create` (the constructor's), each event, each record and its members (a snapshot record's members too), each enum or union struct, and a TS enum's members. A component (R28) has its element's manifest docs, and its parameters, events and slots theirs; a parameter without an attribute doc has its property's.
- The summary is `<summary>`; `@default`, `@since`, `@deprecated` and `@see` are lines of `<remarks>` (`Default: 0`, `Since: 0.2`, `Deprecated: …`, `See: …`). `@deprecated` isn't `[Obsolete]`: its CS0618 would break a consumer's build under `TreatWarningsAsErrors`.
- Text is XML-escaped, without control characters but tab; `{@link X}` and `` `code` `` become `<c>X</c>`, `{@link X text}` (or `X | text`) its text, and a URL's link `<see href>`. Each line of the text is a `///` line.
- No doc, no comment: never an empty `<summary>`. Undocumented members stay so, so a project that compiles the bindings with `GenerateDocumentationFile` turns CS1591 off for them, as the golden slice does.

---

## 7. What the extractor must resolve with the checker

A syntax walk isn't enough. The extractor asks the TypeScript checker for *resolved* members and records them in the [IR](ir.md):

- utility types (`Partial`, `Pick`, `Omit`, `Record`), `keyof`, indexed access, `typeof`, mapped types, and conditional types where they resolve;
- event maps declared through phantom properties;
- inherited members, accessor get/set pairs (with each side's type), overloads;
- discriminants (R19);
- JSDoc: summary, `@default`, `@deprecated`, `@since`, `@see` (R29);
- the module specifier for every declaration.

---

## 8. Diagnostics

Every fallback is reported with a code so consumers can see and tune what didn't map cleanly. The generator never throws for a shape it can't map: it finds them while selecting what to emit, skips them with what uses them, and reports each. Initial set:

| Code | Meaning |
|---|---|
| IOP2001 | `any` / `unknown` / `object` mapped to `JsAny`, for each member, parameter or result that has it |
| IOP2002 | Not reported: a type the checker couldn't resolve is skipped (IOP2007, R12) |
| IOP2003 | Skipped: an ambiguous union (R19), whose cases can't be told apart, and what uses it. Profile override needed |
| IOP2004 | Not reported yet: overloads merged after mapping (R23) |
| IOP2005 | Skipped in async mode: a member whose callback's result JS uses synchronously (R21) |
| IOP2006 | Name changed to avoid a collision (R25, R26); an inline record renamed, or an inline union case named by its position (R11); a component renamed past `{Name}Component` (`DemoMapComponent2`), or the custom events' handlers skipped (R28); the registry, a module's static class or a sibling module's type renamed, or a namespace renamed because a type has its name, or a type skipped, because its path or full name is taken or is a namespace's; a class id prefixed because classes in other namespaces share its name (R27). Enum member renames (R25) are reported last, once each |
| IOP2007 | Skipped: a declaration, member or optional parameter the generator can't map (yet), or that uses one, or a class outside the selection; a setter whose type can't be mapped, its getter kept (§5); a data record's or inline record's optional member, a callback's argument's included, or an inline record with a type parameter (R15, R11); an interface's iterator method (R15); a union case outside the selection, or a union with none left (R2); an element whose class isn't the IR's (R28); or the `Create`, module functions and component of a declaration that can't be imported: an internal module's that nothing re-exports ([design §3.5](design.md#35-module-loading)) |
| IOP3001 | Analyzer (Info): two or more consecutive awaited writes to live objects outside `await using (scope.BeginBatch())`, each a round trip on Server ([design §3.3](design.md#33-batching-async-mode)). A write is an awaited statement calling an instance method of a `JsObject`-derived type that returns `Task`, with or without `ConfigureAwait`; reads return `Task<T>` and aren't counted. A run is consecutive statements of a block, a switch section or the top-level statements, and is reported once, on its second statement. The batch can also be a local that `BeginBatch()` initialised (`await using (batch)`). A lambda or local function isn't in its method's batch, since it may run later. Info, so builds with `TreatWarningsAsErrors` don't break; raise it with `dotnet_diagnostic.IOP3001.severity = warning` in `.editorconfig` |

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

# TypeScript → C# mapping rules

What the generator emits for each TypeScript construct. Rules are numbered (R1–R29) so tests and diagnostics can cite them. [PATTERNS.md](../fixtures/demo-maps/PATTERNS.md) shows where the fixture library exercises each one. The mode (`--api sync|async`) and the consumer's optional profile change the output everywhere.

## 1. The value model

**R1. `undefined` is a type.** The runtime defines `Undefined`, a sealed class with one instance, `Undefined.Value`. Users can't construct it.

**R2. Every TypeScript union becomes a generated `[Union]` struct** that doesn't box. We don't use the C# `union` keyword, because its `default` is `null` and it boxes value types.

- The cases are the mapped union members plus a nullable bottom case, `Undefined?`.
- `default` is `undefined`. The `null` literal converts through a generated `implicit operator X(Undefined? value)` and means JS `null`.
- Each case has `TryGetValue(out T)`. The struct also has `HasValue`, `IsUndefined` and `IsNull`.
- The struct implements the runtime's `IJsUnion<TSelf>`, with static `WriteValue`/`ReadValue`. The runtime's `JsJson` helpers handle undefined and null for every union and `Opt<T>`.
- The nullable bottom case makes the compiler demand a `null` arm. A switch that misses `Undefined` warns CS8509, and one that misses `null` warns CS8655 ([design §8](design.md#8-constraints)).
- Reference cases share one field. A `number`, `boolean` or enum case (`number | string`, `number | number[]`) is kept in a `double` field of its own, and an extensible enum (R10) or DOM element (R13) case as its string, so no case is boxed. Only `Value` boxes.
- The string literals of a union with other types are one enum case: the alias's enum when they are exactly a literal-union alias's (`LengthUnit` in `unit?: LengthUnit | number`, emitted even though R6 flattens it), else an anonymous one named after the union (`StrokeStyleDashLiteral` in `dash?: "solid" | "dotted" | number[]`). A number or boolean literal next to other types isn't mapped yet (IOP2007). A union of literals only is an enum, not a struct (R9, R10).

```csharp
// type ColorInput = string | number[] | RGBA;   used as  color?: ColorInput | null
[Union] public readonly struct ColorInput : IJsUnion<ColorInput>
{
    public ColorInput(string value) ...
    public ColorInput(double[] value) ...
    public ColorInput(Rgba value) ...
    public ColorInput(Undefined? value) ...   // null = JS null, Undefined.Value = undefined
    public static implicit operator ColorInput(Undefined? value) => new(value);
    // HasValue, IsUndefined, IsNull, TryGetValue(out ...), WriteValue, ReadValue
}
```

| You write | It means | On the wire |
|---|---|---|
| nothing, or `default` | `undefined` | property omitted |
| `= null` | JS `null` | `null` |
| `= "red"`, `= new Rgba {…}` | value | value |
| `= Undefined.Value` | explicit `undefined` | omitted |

Where a value can't be omitted (array elements, arguments), undefined is `{"$u":1}` in both directions ([design §3.4](design.md#34-protocol)).

Cases outside the selection, and platform types:

- A case that is a class outside the selection (or an alias of one) is dropped, with IOP2007. So is a platform type that isn't mapped (`MouseEvent`, R14): `toWorld(p: ScreenPoint | MouseEvent)` takes a `ScreenPoint`. Nested union aliases are flattened first (R6).
- One case left: the union is that case. `type Owner = Layer | TileLayer | Folder` with only `Layer` selected is `Layer`. Widening the selection later can turn it back into the union struct, so code written against it may need changes.
- No cases left: what uses it is skipped (IOP2007).
- Two or more left: it is read as usual (R19). Its `ReadValue` throws `JsonException` if JS sends a handle, which is how an instance of an unselected class arrives.
- Read through a union narrowed to one case: an entity case reads an unselected subclass as its nearest selected ancestor (R19), and throws if there is none. A primitive or enum case throws `JsonException`. A record case reads the handle's object as a record whose members are all missing, without error.

A union that is only written (a parameter, or a setter's wider type) may have cases that need a scope to read: an entity, or a scoped record (R17). It may also have a DOM element case (R13), or cases R19 can't tell apart. Its `ReadValue` throws. The same union in a getter or a result skips that member (IOP2007). A data record keeps a member typed with a union that has an entity or scoped record case (`JumpOptions.target: Point | Marker | Camera`): the record writes it, its `Read` leaves it undefined, and it doesn't make the record scoped. An event payload drops such a member instead (R24). A union struct isn't generic, so no case can name a type parameter.

**R3. Non-union types in optional or nullable positions use `Opt<T>`.** It has the same three states and conversions (`Opt<double> z = 5; z = null; z = default;`). Required, non-null positions stay plain (`double`). Read it with a pattern (`z is double d`, `z is Undefined`) or `TryGetValue(out var d)`, which has no `Undefined` overload so `var` works.

An array or tuple element that can be null or undefined is `Opt<T>` too: `(Marker | null)[]` is `Opt<Marker>[]`, and `Array<number | undefined>` is `Opt<double>[]`. An element that maps to a union struct stays that struct (R2). Entity elements are read with a scope (R17). Undefined elements are `{"$u":1}` on the wire.

**R4. A union of live classes that covers every concrete subclass of an abstract base maps to the base.** The base is a `closed class` (implicitly abstract), so a switch over the subclasses is exhaustive, also through `Opt<Base>`. A union that covers only some subclasses stays a union.

**R5. Collapse after mapping, then de-duplicate.** `number[]` and `[number, number, number]` both map to `double[]`, so they become one case.

**R6. Flatten nested unions.** C# gives no one-step conversion from an inner union's cases, so `type A = B | C; type B = X | Y` gives the cases `X, Y, C`. Cases keep declaration order. Literal unions flattened together that share a value get one enum member for it.

## 2. Type mapping

| TypeScript | C# | Notes |
|---|---|---|
| `number` | `double` | **R7.** Always `double` |
| `string`, `boolean` | `string`, `bool` | |
| `T[]`, `readonly T[]`, `Array<T>` | `T[]` | A nullable element is `Opt<T>[]` (R3) |
| homogeneous numeric tuple | `double[]` | R5 |
| `Promise<T>` | `Task<T>` | **R8.** In both modes |
| `void` (return) | `void` (sync), `Task` (async) | |
| string-literal union | `enum` plus a name map (`BasemapNames`: `ToJs`, `FromJs`, `Write`, `Read`) | **R9.** Names by R25 |
| open-ended literal union (`"a" \| (string & {})`), or more than 64 members | `readonly record struct` wrapping a string, with static known values | **R10** |
| number-literal union | `enum` with explicit values | |
| TS `enum` | C# `enum` | |
| any other union | union struct | R2 |
| `T \| null`, `T \| undefined`, `prop?: T` | union with bottom case (R2), or `Opt<T>` (R3) | |
| class | live class | §3 |
| value-like class (per profile) | live class plus `XSnapshot` record | §3 |
| interface with only data members | data record with the same name | §3 |
| inline object type, including an intersection of data bags | data record named after its owner, one per structure | **R11** |
| class extending a mixin's base (`PinBase: typeof Marker & typeof Draggable`) | derives from the named class (`Pin : Marker`), with the mixin's members as its own, and implements each exported mixin's interface (§5) | [ir.md](ir.md#declarations) |
| generic `T` | generic over entities (`where T : JsObject`), or its constraint | See Generics below |
| `this` (return type) | the declaring type | |
| `keyof`, indexed access, mapped, conditional, `typeof` | the type the checker resolves | **R12.** Unresolvable: skipped with its users (IOP2007) |
| `any`, `unknown`, `object` | `JsAny` (`Opt<JsAny>` for `any` and `unknown`) | IOP2001. See below |
| DOM element types (`HTMLElement`) | `ElementReference` | **R13.** Written only |
| function type | delegate (`Action<…>`, `Func<…>`) | R21 |
| `AbortSignal` | not mapped yet | **R14.** IOP2007 |
| `bigint`, `Date`, heterogeneous tuples, `Record<string, T>` and index signatures, intersections used as mixins, `ArrayBuffer`, typed arrays, other DOM and platform types (`MouseEvent`, `Blob`) | not mapped yet | Skipped with their users (IOP2007); a union drops them as cases (R2) |

`JsAny` holds the value's JSON, untyped. Plain objects and arrays are copies. Other objects are handles owned by the scope that read them, so a `JsAny` is read with a scope (R17) and isn't in snapshots. It is not a union case. Read it as a type with `meta.Read(JsJson.ReadNumber)` or `meta.Read(scope.Read<Marker>)`. Make one with `JsAny.Parse(json)` or `JsAny.From(value, write)`.

**R11. Inline object types** are data records like named ones (R15), named after their owner (R27): `Feature.size: { w: number }` is `FeatureSize`.

- In a union, each inline case is named after the union and its tag value. The tag is the discriminant (R19), or else a member every inline case has with its own string literal. So `(PolyProps & { type: "poly" }) | (DotProps & { type: "dot" })` on the setter of `Feature.shape` gives `FeatureShapeInputPoly` and `FeatureShapeInputDot`.
- Without a tag, a case is named by its position in the union as written (`FeatureShapeCase2`), with IOP2006. `null` and `undefined` don't count toward the position.
- An intersection of one named type with string constants is named after the type and the constants' values, whatever its owner: `GlowOptions & { kind: "glow" }` is `GlowOptionsGlow`. It is in the type's namespace if that namespace has emitted types, and otherwise in its owner's. The IR's `bases` names the type ([ir.md](ir.md#types)).
- Structurally identical anonymous records, unions and enums are one type, which every use names: the same members in the same order, with the same types, optionality and constants. Docs don't count, and neither does whether a constant is optional, since it is always written (R20). The type takes the shortest of the names its owners would give it, in that owner's namespace; a tie goes to the name, then the namespace, in ordinal order. `Marker.position`'s setter and `MarkerOptions.position` share `MarkerPositionInput`. Sharing isn't reported.
- Members it can't map follow R15: an optional one is left out (IOP2007), a required one skips the record and its users. This holds wherever the record appears, including callback arguments and results. For a callback alias, the record keeps the alias's name (`VisitE`).
- An inline type with a method, a call signature or a type parameter of an enclosing generic type isn't data. It is skipped (IOP2007).
- If the name is taken (by a declaration, snapshot, registry, module class, other anonymous type or component), it gets a numeric suffix (`FeatureSize2`) and IOP2006.

**R13. DOM elements** (`Element`, `HTMLElement`, `SVGElement` and their subtypes) are `ElementReference`, written by `JsElement.Write` (`Interoper.Blazor`) as `{"$e":"<id>"}`. JS finds the element by Blazor's capture attribute, so the reference must come from an `@ref` after the first render. Otherwise the call throws `JsException`. The capture attribute is a Blazor internal, so a Blazor upgrade must rerun the component tests ([design §6](design.md#6-testing)).

- JS can't hand an element back, so a DOM-typed property is setter-only (`host: HTMLElement | null` is `SetHost(Opt<ElementReference>)`), with no watch and no snapshot member. A read-only one is skipped. DOM results and event payloads are skipped (IOP2007).
- A union with a DOM element case is only written (R2). A property of that type is setter-only (`container: HTMLElement | string | null` is `SetContainer(MapViewContainer)`). Where it would be read, the member is skipped (IOP2007).
- A data record with a DOM member, or a member of a union with one, has no `Read`. If something reads it, that user is skipped (IOP2007). A member that is an array of such a union or record isn't mapped, even when the record is only written.
- Custom elements are live classes like any other, found by their tag (R28). This includes abstract element classes.

Generics: an unconstrained type parameter is an entity (`where T : JsObject`). A default (`Tagged<T = any>`) is ignored, since C# always has the type argument.

- A class's or interface's constrained type parameter is replaced by its constraint, and the type loses its type arguments: `class Scene<V extends Overlay>` is a plain `Scene` whose `V` members are `Overlay`. Values read are still their most-derived type (R19). A constraint that names the type's own parameters (`T extends Box<T>`), a profile collection's constrained parameter, and a method's or function's type parameters aren't mapped yet (IOP2007).
- A class listed in the profile's `collections` is a live generic class (`Collection<T> : Emitter where T : JsObject`). It has no `Create` and no registry entry. Its owner makes it, and readers pass a factory to `JsScope.Read(ref reader, create)`. So it is read only through members typed with it (`MapView.markers`), never as a `JsObject` or a base. A use whose type argument isn't an entity (`Collection<number>`) is skipped (IOP2007).
- A generic data interface is a generic record (`CollectionChangeEvent<T>`) with the same constraint.
- A use of a generic union alias is expanded in place: `OneOrMany<Marker>`, where `type OneOrMany<T> = T | T[]`, is the anonymous union `Marker | Marker[]` named after its owner. Missing type arguments take their defaults. The alias itself has no type. A recursive alias stops after a few levels and is skipped. Other generic aliases aren't mapped yet (IOP2007).

## 3. Entities and values (by reference or by value)

The central decision for every class and interface.

**R15. Default classification.**

- A `class` is an entity: a live object with identity, behind a handle.
- An interface whose members are all data is a data record. So is an interface an event names as its payload, minus its methods (R24). Any other interface with methods isn't mapped yet (IOP2007).
- A data record's optional member that can't be mapped is left out (IOP2007), so the record still maps. A required one skips the record and its users.
- An interface's iterator methods (`values()` returning `Iterator` and kin) are left out (IOP2007). Symbol-keyed members never reach the IR.

The profile's `valueClasses` marks classes as value-like (R16). Getting this wrong is the classic failure: a JSON copy of an entity loses identity and methods.

**R16. Value-like classes have two shapes.** The profile marks classes as value-like (geometry, colors). Such a class gets:

- a live class (`Point`), used when you hold a handle: what `Create` returns, and `this`-typed results;
- a snapshot record (`PointSnapshot`), used everywhere else the type is named: properties, arguments, results, other snapshots, event payloads, constructor bags and module functions.

Sent to JS, a value-like snapshot arrives as an instance. The writer adds its class id (`$c`, [design §3.4](design.md#34-protocol)) and the runtime calls `new Point(bag)`. The bag keeps constants such as the discriminant (R20). Plain data records stay plain objects. The snapshots of an abstract value-like base form a `closed record` hierarchy (`ShapeSnapshot`), read by the discriminant.

**R17. Snapshot depth.** Snapshots hold data members only. Value-like members are copied deeply as their snapshots. Entity members are left out, since reading one needs a scope; use the scoped getter (`GetSelected(scope)`).

A data record (not a snapshot) may hold entities, directly, in arrays or through a type parameter. Only emitted classes count: a member typed with a class outside the selection is dropped or skipped (R2), so it makes nothing scoped. Such a record is *scoped*: its reader is `Read(ref reader, JsScope scope)`, so whatever returns it takes a scope (§5). Union readers take no scope, so only a write-only union can have an entity or scoped record case (R2).

**R18. Every live class has an `XSnapshot` record.** It holds the class's writable data members, all optional, typed as the getter where getter and setter differ. One type serves as:

- what `Snapshot()` / `SnapshotAsync()` returns;
- the constructor input for `Create` / `CreateAsync`;
- the patch for `Apply` / `ApplyAsync`. Unset members are undefined, so a patch is a partly filled record.

A member a subclass redeclares appears once. Under a closed base (R16) it stays on the base record if its type is unchanged, and is a `new` property on the subclass record if not (R26). In that case the subclass's `WriteMembers` doesn't write the hidden base property.

Every snapshot record has `Write` (the whole object, with its constants; a value-like one's adds `$c`), `WriteMembers` (the bag for `create`, the patch for `apply`) and `Read`.

System.Text.Json: every snapshot record, and every data record that reads without a scope (not scoped, R17; no DOM member, R13; not generic), implements `IJsRecord<T>` and has `[JsonConverter(typeof(JsRecordConverter<T>))]`. So app types that hold them serialize in their wire format, with no reflection over the record. A value-like snapshot keeps its `$c`, which `Read` ignores.

Chosen members: every snapshot record with members other than constants has a nested static class `Members` with one `JsMembers<XSnapshot>` per member (`LayerSnapshot.Members.Visible`), combined with `|`. They name members without strings:

- `SnapshotAsync(members)` reads only those members, in one round trip. The rest are undefined, so the result works with `Diff` and `ApplyAsync`.
- A class with the profile's watch method also has `WatchAsync(scope, members, handler)`, one subscription for all of them. After each JS turn in which any changed, the handler gets one partial snapshot of those members as they are then, so a burst is one message ([design §3.4](design.md#34-protocol)). `Diff` against the previous one gives the patch. Async mode adds `WatchStreamAsync`.
- They are overloads, not `new` (R26), so a `Layer` reference to a `FeatureLayer` reads and watches `Layer` members.
- A closed base's members also name its discriminant, which its `Read` needs (R19 step 2).
- A member named `Members` renames the class `JsMembers` (IOP2006).

```csharp
var settings = await layer.SnapshotAsync(LayerSnapshot.Members.Visible | LayerSnapshot.Members.Opacity);
await using var watch = await layer.WatchAsync(scope, LayerSnapshot.Members.Visible | LayerSnapshot.Members.Opacity, now => settings = now);
```

`Diff`: every snapshot record except a closed base has `static XSnapshot Diff(XSnapshot before, XSnapshot after)`, which returns the patch from `before` to `after`.

- A member is in the patch when `after` defines it and it differs from `before`.
- Members compare with `JsDiff.Same`: normal equality, except arrays compare element by element (also inside `Opt<T>` or a union). Nested records use their own equality, so an array inside one may produce a harmless extra patch entry.
- A patch can't unset a member, since undefined means "leave it". To clear one, set it to `null`.

## 4. Reading union values back (classification)

When JS returns a value for a union position, the runtime must pick the case.

**R19. Classification order:**

1. **Handle with a class id** (`{"$h": 12, "$t": 7}`). The generated registry maps the id to the most-derived live type, and the case is the one that type converts to. An object whose class isn't in the metadata (undeclared, or outside the selection) gets the id of its nearest known ancestor: the runtime walks up the prototype chain, reading at each level the class-name property, or else the prototype's own `constructor` when it is a loaded module's export. So a plain class with no class-name property, as an app's own module has, is still known. A variant (§5) is known too, so an unselected class that adds a mixin reads as its nearest emitted ancestor with the mixin's interface. With none, the id is 0, and reading it as an entity throws.
2. **Object with a discriminant.** The extractor finds a property every member declares with a distinct literal type (`type: "point"`), intersections (`Props & { type: "heat" }`) included. The generator switches on its value. A nullable union that holds one union (`(A | B) | null`) uses that union's.
3. **Object without a discriminant.** If the sets of *required* member names separate the cases, the generator emits a structural check. Otherwise the union is ambiguous and is skipped (IOP2003). Optional members don't count, because typings make most of them optional.
4. **Primitives** by JSON token kind: a string or string enum is a JSON string, a `number` or number enum a JSON number, a `boolean` `true` or `false`. Two cases with the same kind (`number` and a number enum) are ambiguous (IOP2003).

**R20. Writing** serializes the active case. A member typed with a single literal (`type: "point"`) is a constant. It isn't a C# property, but it is always written, so JS receives `{ "type": "point", … }`.

## 5. Members

| JS member | `--api sync` | `--api async` |
|---|---|---|
| property `x: T` | `T X { get; set; }` | `Task<T> GetXAsync()`, `Task SetXAsync(T value)` |
| read-only property | `T X { get; }` | `Task<T> GetXAsync()` |
| getter type ≠ setter type | `TGet X { get; }` + `void SetX(TSet value)` | `GetXAsync()`, `SetXAsync(TSet value)` |
| … setter type can't be mapped | the read-only property (IOP2007 for the setter) | same |
| method `foo(a): R` | `R Foo(A a)` | `Task<R> FooAsync(A a, CancellationToken cancellationToken = default)` |
| returns an entity or scoped record (R17), or takes a callback (R21) | `R GetX(JsScope scope)` (+ `void SetX(T value)`), `R Foo(JsScope scope, A a)` | `GetXAsync(JsScope scope)`, `FooAsync(JsScope scope, A a)` |
| callback property (R21) | `void SetX(JsScope scope, Func<A, R>? value)` | `Task SetXAsync(JsScope scope, …)` |
| method returning `Promise<R>` | `Task<R> FooAsync(A a, CancellationToken cancellationToken = default)` | same |
| constructor `new X(props)` | `static X Create(JsScope scope, XSnapshot props)` | `static Task<X> CreateAsync(JsScope scope, XSnapshot props)` |
| event | `IDisposable OnClick(JsScope scope, Action<ClickEvent> handler, EventOptions? options = null)` | `Task<IAsyncDisposable> OnClickAsync(…same…)` |
| event without a payload | `IDisposable OnTick(JsScope scope, Action handler, …)` | `Task<IAsyncDisposable> OnTickAsync(…)` |
| event stream | none | `IAsyncEnumerable<ClickEvent> OnClickStreamAsync(JsScope scope, EventOptions? options = null, CancellationToken cancellationToken = default)` |
| property watch (classes with the profile's watch method, own or inherited) | `IDisposable WatchZoom(JsScope scope, Action<double>)` | `Task<IAsyncDisposable> WatchZoomAsync(…)` |
| property watch stream | none | `IAsyncEnumerable<double> WatchZoomStreamAsync(JsScope scope, CancellationToken cancellationToken = default)` |
| snapshot / patch | `XSnapshot Snapshot()`, `void Apply(XSnapshot)` | `SnapshotAsync()`, `ApplyAsync(XSnapshot)` |
| snapshot of some members (R18) | `XSnapshot Snapshot(JsMembers<XSnapshot> members)` | `SnapshotAsync(members)` |
| watch of some members (R18; classes with the profile's watch method) | `IDisposable Watch(JsScope scope, JsMembers<XSnapshot> members, Action<XSnapshot> handler)` | `Task<IAsyncDisposable> WatchAsync(…)`, `IAsyncEnumerable<XSnapshot> WatchStreamAsync(JsScope scope, JsMembers<XSnapshot> members, CancellationToken cancellationToken = default)` |
| module function | static method on a static class per module, with a leading `JsRealm realm` (or `JsScope scope` when the rules above need one) | same, async |

- A setter whose wider type takes the constructor bag of an entity class the getter returns also takes that class, as libraries that build an instance from the bag (autocasting) do: `get theme(): Theme`, `set theme(v: ThemeOptions | string)` is `SetTheme(ViewThemeInput)`, whose cases are `ThemeOptions`, `string` and `Theme`. The union is only written (R2).
- A method or module function that returns a `Task` takes `CancellationToken cancellationToken = default` last, except after a rest parameter (R22). Cancelling stops the wait, not the JS call, which runs to the end. A result that arrives later is still read, so the caller's scope owns its handles.
- A name that already ends in `Async` keeps the one suffix: `loadAsync()` is `LoadAsync`.
- Live results, events and watches belong to the scope the caller passes ([design §3.2](design.md#32-handles-identity-and-lifetime)). Disposing a returned subscription releases it sooner. Value-like classes are values (R16), so `PointSnapshot Center { get; set; }` needs no scope.
- Discriminants aren't members of live classes, since the live type already says it.
- An exported mixin class (`Refreshable` in `TileLayerBase: typeof Layer & typeof Refreshable`) is an interface, `IRefreshable : IJsObject`, never a live class. Its properties, methods and events are default members, and each emitted class with the mixin implements it, so code holding a `Layer` can test `layer is IRefreshable r` and call `r.RefreshAsync()`. A member that names `this` is left to the classes. Only the mixins of emitted classes get one, and only when members are left.
- A class outside the selection that adds one of those mixins to its base (an unselected `ImageLayer : Layer` that is refreshable) is a variant when emitted members name its nearest emitted ancestor (`View.layers` names `Layer`), since only then can JS hand one to .NET: it gets a class id, and the registry reads its objects as a private subclass of its nearest emitted ancestor that implements the interfaces it adds. Its metadata entry is its runtime class name and `of`, the ancestor's class id, whose entry it shares. A subclass that adds no mixin has none: JS finds its base (R19 step 1).
- Static members and exported variables (`export const`) aren't mapped yet (IOP2007).

**R21. Callbacks** become delegates in the runtime's callback registry, owned by the scope.

- A callback returning `void` is an `Action<…>`, any other a `Func<…, R>`. A callback with more than 4 parameters, a rest parameter or type parameters, or one that returns a Promise, isn't mapped yet (IOP2007). The member registers it with `scope.Callback(handler, read1, …, write)` and sends `{"$f":id}`. Arguments are read with the scope. A named function alias has no C# type; its uses are delegates.
- Each call registers a new callback that lives until its scope is disposed. Replacing or clearing a callback property doesn't release the old one, so use a short-lived scope for repeated calls. A callback called after its scope is disposed is dropped and returns undefined to JS.
- In async mode on Server and Hybrid, JS doesn't wait for .NET, so a void callback can run after the member's `Task` completes.
- A callback property is setter-only, `SetX(scope, value)`, with `null` to clear and no watch. A read-only callback property, or a callback anywhere other than a parameter or property, is skipped (IOP2007).
- A callback whose return value JS uses synchronously (a formatter `(v) => string`) works in sync mode only. In async mode the member is skipped with IOP2005.

**R22. Optional parameters** are `Opt<T> p = default` (or a union `= default`). Omitted trailing arguments are omitted on the JS side too.

- A rest parameter `...points: Point[]` becomes `params PointSnapshot[] points`, sent as a spread. `...markers: Marker[]` is `params Marker[] markers`.
- An optional or rest parameter whose type isn't supported is dropped (IOP2007). A required one skips the member.

**R23. Overloads** map one to one. Overloads that collapse to the same C# signature would be merged (IOP2004), but the generator doesn't merge them yet.

**R24. Events** come from the checker-resolved event map, including maps declared through a phantom property and indexed access on `this`.

- `EventOptions` (`Interoper.Runtime`) say what JS does before the event reaches .NET, because Server and Hybrid can't call `preventDefault()` in time. `PreventDefault` and `StopPropagation` call those methods when the event has them. `Fields` names the payload members to send. Watches take no options.
- When the payload is a data record, the default fields are the record's wire members, and `options.Fields` replaces them. Anything not sent is `default` in the record. Other payloads are sent whole.
- An interface named directly as a payload, and its bases, become data records with their methods left out (one IOP2007 each). Members they can't map are left out too, required or not, since a payload is only read.
- The profile's subscribe and watch methods return an object with `dispose()` or `remove()`. Releasing calls whichever it has.
- An event whose payload is `void`, `undefined` or `null` has a parameterless `Action`, sends no fields and has no stream.
- Streams (async mode) subscribe on the first `MoveNext` and unsubscribe on `break`, cancellation or disposal. Disposing the scope ends them after what's queued. There is no backpressure.

## 6. Naming

**R25. Literal values → enum member names.** Split on non-alphanumerics and PascalCase each part (`"simple-marker"` → `SimpleMarker`). A leading digit gets an underscore (`"3d"` → `_3d`). Symbols are spelled out, longest first:

| Symbol | Name | Symbol | Name |
|---|---|---|---|
| `<` | `LessThan` | `<=` | `LessThanOrEqual` |
| `>` | `GreaterThan` | `>=` | `GreaterThanOrEqual` |
| `=`, `==` | `Equal` | `!=` | `NotEqual` |
| `+` | `Plus` | `-` | `Minus` |
| `*` | `Star` | `/` | `Slash` |
| `%` | `Percent` | other | its code (`&` → `U0026`) |

So `"--"` is `MinusMinus`. The empty string is `Empty`. Duplicates get a numeric suffix and IOP2006, reported once per distinct rename.

**R26. Members** are PascalCased. Async mode appends `Async`.

- A member that collides with a generated member (`getZoom()` next to `zoom` in async mode) or an `object` member (`toString()`) is renamed (`JsToString`) with IOP2006.
- A member that hides a base member with the same name and parameter types is `new` (`TileLayer Clone(JsScope)` over `Layer Clone(JsScope)`, and every subclass's `Snapshot()`). A generic base's parameter types use the subclass's type arguments. Different parameter types make an overload. Between a property and a method, the name alone decides. A collision with a base is never a rename.

**R27. Types** keep the TypeScript name in PascalCase.

- Namespaces. The consumer's root namespace plus the module's folders, PascalCased: `layers/TileLayer.js` gives `Acme.Maps.Layers`. A module of another package than the IR's first adds that package's name without scope: `@acme/widgets/legend` gives `Acme.Maps.Widgets`. File paths mirror namespaces (`Layers/TileLayer.cs`). The registry `{Package}.cs` and the metadata module stay at the root.
- References. Generated code names types in its own namespace plainly and all others with `global::`. Runtime and .NET types are always qualified and generated files have no usings, so a generated name like `Opt` or `Task` hides nothing. A record reader qualifies a type that one of its members is named after.
- Sibling modules. When modules in one folder export the same name (`ops/a.js` and `ops/b.js` each export `Options`), the first in IR order keeps it and later ones get their module's name as a prefix (`BOptions`). Names that differ only in case count as the same.
- Case. Two generated paths that differ only in case clash, since Windows and macOS ignore case. A file and a folder that differ only in case don't.
- Anonymous types are named after their owner: `Feature.shape` gives `FeatureShape`, its setter's wider type `FeatureShapeInput`, a method result `FeatureMoveTo`, a parameter `FeatureMoveToTarget`, an event payload `FeatureClick`. Identical ones share the shortest of those names, and a tagged intersection takes its named part's (R11). An anonymous union or enum whose name a declaration or another type has takes a numeric suffix, like an inline record (R11): `Dock.breakpoint: DockBreakpoint | boolean` is `DockBreakpoint2`.
- The registry is named after the package without scope (`Maps`), or the next free name (`Widgets2`) if a namespace has it.
- A namespace with the full name of a declared type (`Marker` next to a `marker/` folder) takes the next free name (`Acme.Maps.Marker2`), so the type keeps its name.
- Clashes. When two generated types would share a path, the registry keeps its name, and a module's static class takes the next free one (`Measure2`). Any other later type is skipped, and so is a type with a namespace's full name. A skipped type's users are not removed, so they won't compile.
- Class ids. Classes with the same name in different namespaces get id constants prefixed with their namespace below the root (`LayersPointId`). A root class keeps `PointId`.

All renames and skips here are IOP2006.

**R28. Components.** Each custom element in the package's custom elements manifest gets a Razor component, written as a plain C# class `{Component}.cs` (`public sealed partial class DemoMap : JsComponentBase`) with no `.razor` file.

- Name. The tag in PascalCase (`demo-map` → `DemoMap`), in the element class's namespace. If that's taken, often by the element class itself, it's `{Name}Component`, which isn't reported. If that's taken too, the next number (`DemoMapComponent2`) with IOP2006. An element whose class the IR doesn't declare has no component (IOP2007).
- Parameters. Each attribute and writable property is a `[Parameter]` of its plain mapped type, without `Opt`. Only parameters the parent supplied are rendered or set. An attribute without a property must have a primitive or string-enum type, or it's skipped (IOP2007).
- Attributes and properties. Primitive and string-enum parameters render as HTML attributes (invariant culture; a bool is present or absent), so they work in prerendering and static SSR. So does an attribute the manifest types as `string`, `number` or `boolean` even when its property is wider. Everything else is set on the live element after the first interactive render, and again when it changes (`JsDiff.Same`). A null value is JS null if the type allows null, else undefined.
- Content. `ChildContent` is the default slot. Each named slot is a `RenderFragment?` parameter rendered in `<div slot="toolbar" style="display:contents">`. `AdditionalAttributes` captures unmatched attributes. `Element` is the live element once interactive, and `OnElementReady` fires once it is ready and the supplied properties are set.
- Events. Each manifest event is an `EventCallback<TDetail>` (`OnDemoMapClick`), or `EventCallback` with no detail. The detail is projected like R24. An inline detail is an inline record named after the event (`DemoMapZoomChangeDetail`). `interoper.js` registers the Blazor custom events when the realm starts. `{Package}Events.cs` declares their `[EventHandler]` attributes on a partial `EventHandlers` class in the root namespace, so hand-written markup can use `@ondemo-map-click`. If that class name is taken, the attributes are skipped (IOP2006). The element class's own event map isn't emitted.
- `@bind`. An event pairs with property P when its name ends in `-{kebab(P)}-change` or `{P}Change` and its detail has a member P of P's type. The longest P wins. The component then has `EventCallback<T> PChanged`, so `demo-map-zoom-change` gives `@bind-Zoom`. Echoes of values the component itself set don't raise `PChanged`, so the parent and element don't fight over stale values.
- Prerendering. Attributes and slots render. Events, properties and `Element` wait for interactivity.
- Modes. In async mode the component loads the element's module first. Sync mode preloads it with the registry's other modules.

**R29. Docs.** A declaration's JSDoc ([ir.md](ir.md#declarations)) becomes XML docs, the same in both modes. That covers live class members (both async accessors), each overload, `Create`, events, records and their members, enums, union structs and TS enum members. A component gets its element's manifest docs.

- The summary is `<summary>`. `@default`, `@since`, `@deprecated` and `@see` become lines of `<remarks>`. `@deprecated` isn't `[Obsolete]`, because CS0618 would break builds under `TreatWarningsAsErrors`.
- Text is XML-escaped. `{@link X}` and `` `code` `` become `<c>X</c>`, `{@link X text}` becomes its text, and a URL becomes `<see href>`.
- No doc means no comment, never an empty `<summary>`. Projects that set `GenerateDocumentationFile` should turn off CS1591 for the bindings.

## 7. What the extractor must resolve with the checker

A syntax walk isn't enough. The extractor asks the TypeScript checker for resolved types: utility, mapped, conditional and indexed-access types, `keyof` and `typeof`, event maps declared through phantom properties, inherited members, accessor pairs, overloads, discriminants (R19) and JSDoc (R29). [ir.md](ir.md) describes the result.

## 8. Diagnostics

The generator never throws for a shape it can't map. It skips the shape along with whatever uses it, and reports a code.

| Code | Meaning |
|---|---|
| IOP2001 | `any`, `unknown` or `object` mapped to `JsAny` |
| IOP2002 | Not reported. An unresolvable type is skipped with IOP2007 (R12) |
| IOP2003 | Skipped: an ambiguous union (R19) and its users |
| IOP2004 | Not reported yet: overloads merged after mapping (R23) |
| IOP2005 | Skipped in async mode: a callback whose result JS uses synchronously (R21) |
| IOP2006 | A name changed or a type skipped to avoid a collision (R11, R18, R25–R28) |
| IOP2007 | Skipped: something the generator can't map yet, or that uses it. Includes classes and union cases outside the selection (R2), dropped record members (R11, R15), iterator methods, and declarations that can't be imported because no public module re-exports them ([design §3.5](design.md#35-module-loading)) |
| IOP3001 | Analyzer, Info: two or more consecutive awaited writes to live objects outside `await using (scope.BeginBatch())`. Each is a round trip on Server ([design §3.3](design.md#33-batching-async-mode)). Raise it with `dotnet_diagnostic.IOP3001.severity = warning` |
